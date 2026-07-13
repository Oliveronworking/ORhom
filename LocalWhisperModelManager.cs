using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChatGptDictationBridge;

internal sealed record LocalWhisperModelDescriptor(
    string Id,
    string FileName,
    Uri DownloadUri,
    string Revision,
    long ExpectedLength,
    string Sha256)
{
    public static LocalWhisperModelDescriptor LargeV3Turbo { get; } = new(
        "whisper-large-v3-turbo",
        "ggml-large-v3-turbo.bin",
        new Uri(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/" +
            "5359861c739e955e79d9a303bcbc70fb988958b1/" +
            "ggml-large-v3-turbo.bin",
            UriKind.Absolute),
        "5359861c739e955e79d9a303bcbc70fb988958b1",
        1_624_555_275,
        "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69");
}

internal enum LocalWhisperModelProgressStage
{
    CheckingCache,
    VerifyingCache,
    Downloading,
    Ready
}

internal readonly record struct LocalWhisperModelProgress(
    LocalWhisperModelProgressStage Stage,
    long BytesProcessed,
    long TotalBytes,
    bool FromCache = false)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesProcessed * 100d / TotalBytes, 0d, 100d);
}

internal enum LocalWhisperModelFailure
{
    InvalidConfiguration,
    Network,
    HttpStatus,
    Storage,
    Integrity
}

internal sealed class LocalWhisperModelException : Exception
{
    public LocalWhisperModelException(
        LocalWhisperModelFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public LocalWhisperModelFailure Failure { get; }
}

/// <summary>
/// Makes the pinned local Whisper model available without ever exposing a
/// partially downloaded or unverified file as the final model.
/// </summary>
internal sealed class LocalWhisperModelManager : IDisposable
{
    private const int BufferSize = 128 * 1024;
    private const int ProcessLockRetryDelayMilliseconds = 100;
    private const int VerificationSchemaVersion = 1;
    private static readonly TimeSpan DefaultDownloadIdleTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _downloadIdleTimeout;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _disposeState;

    public LocalWhisperModelManager(
        string modelDirectory,
        HttpClient? httpClient = null,
        LocalWhisperModelDescriptor? descriptor = null,
        TimeSpan? downloadIdleTimeout = null)
    {
        Descriptor = descriptor ?? LocalWhisperModelDescriptor.LargeV3Turbo;
        ValidateDescriptor(Descriptor);

        _downloadIdleTimeout = downloadIdleTimeout ?? DefaultDownloadIdleTimeout;
        if (_downloadIdleTimeout <= TimeSpan.Zero ||
            _downloadIdleTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.InvalidConfiguration,
                "Das Zeitlimit für den lokalen Modelldownload ist ungültig.");
        }

        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.InvalidConfiguration,
                "Der lokale Whisper-Modellordner ist nicht konfiguriert.");
        }

        ModelDirectory = Path.GetFullPath(modelDirectory);
        ModelPath = Path.Combine(ModelDirectory, Descriptor.FileName);
        PartialPath = ModelPath + ".partial";
        VerificationPath = ModelPath + ".verified.json";
        ProcessLockPath = ModelPath + ".lock";

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    public LocalWhisperModelDescriptor Descriptor { get; }

    public string ModelDirectory { get; }

    public string ModelPath { get; }

    public string PartialPath { get; }

    public string VerificationPath { get; }

    internal string ProcessLockPath { get; }

    public async Task<string> EnsureModelAsync(
        IProgress<LocalWhisperModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var operationToken = operationCancellation.Token;
        await _gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await EnsureModelCoreAsync(progress, operationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        _gate.Wait();
        try
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
        finally
        {
            _lifetimeCancellation.Dispose();
            _gate.Dispose();
        }
    }

    private async Task<string> EnsureModelCoreAsync(
        IProgress<LocalWhisperModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(ModelDirectory);
            progress?.Report(new LocalWhisperModelProgress(
                LocalWhisperModelProgressStage.CheckingCache,
                0,
                Descriptor.ExpectedLength));

            using var processLock = await AcquireProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);

            if (await TryUseCachedModelAsync(progress, cancellationToken).ConfigureAwait(false))
            {
                TryDeleteBestEffort(PartialPath);
                progress?.Report(new LocalWhisperModelProgress(
                    LocalWhisperModelProgressStage.Ready,
                    Descriptor.ExpectedLength,
                    Descriptor.ExpectedLength,
                    FromCache: true));
                return ModelPath;
            }

            DeleteFileRequired(PartialPath, "unvollständigen Modelldownload");
            EnsureSufficientDiskSpace();
            await DownloadAndVerifyAsync(progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new LocalWhisperModelProgress(
                LocalWhisperModelProgressStage.Ready,
                Descriptor.ExpectedLength,
                Descriptor.ExpectedLength));
            return ModelPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.Network,
                "Der Download des lokalen Whisper-Modells hat zu lange gedauert. Bitte Internetverbindung, Proxy und Firewall prüfen.",
                ex);
        }
        catch (LocalWhisperModelException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.Network,
                "Das lokale Whisper-Modell konnte nicht heruntergeladen werden. Bitte Internetverbindung, Proxy und Firewall prüfen.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StorageException(ex);
        }
    }

    private async Task<bool> TryUseCachedModelAsync(
        IProgress<LocalWhisperModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ModelPath))
        {
            TryDeleteBestEffort(VerificationPath);
            return false;
        }

        var modelInfo = new FileInfo(ModelPath);
        if (modelInfo.Length != Descriptor.ExpectedLength)
        {
            DeleteInvalidCache();
            return false;
        }

        var sidecar = await TryReadSidecarAsync(cancellationToken).ConfigureAwait(false);
        if (SidecarMatches(sidecar, modelInfo))
        {
            return true;
        }

        progress?.Report(new LocalWhisperModelProgress(
            LocalWhisperModelProgressStage.VerifyingCache,
            0,
            Descriptor.ExpectedLength));
        var cachedHash = await ComputeSha256Async(
                ModelPath,
                LocalWhisperModelProgressStage.VerifyingCache,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeHashEquals(cachedHash, Descriptor.Sha256))
        {
            DeleteInvalidCache();
            return false;
        }

        await WriteSidecarAsync(modelInfo).ConfigureAwait(false);
        return true;
    }

    private async Task DownloadAndVerifyAsync(
        IProgress<LocalWhisperModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partialMoved = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Descriptor.DownloadUri);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LocalWhisperModelException(
                    LocalWhisperModelFailure.HttpStatus,
                    $"Der vertrauenswürdige Modellserver hat den Download mit HTTP {(int)response.StatusCode} ({response.StatusCode}) abgelehnt. Bitte später erneut versuchen.");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is { } length && length != Descriptor.ExpectedLength)
            {
                throw IntegrityException(
                    $"Der Modellserver meldet {length} statt {Descriptor.ExpectedLength} Bytes.");
            }

            progress?.Report(new LocalWhisperModelProgress(
                LocalWhisperModelProgressStage.Downloading,
                0,
                Descriptor.ExpectedLength));

            long received = 0;
            string downloadedHash;
            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             PartialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long lastProgressBytes = 0;
                var progressStopwatch = Stopwatch.StartNew();
                try
                {
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await ReadWithIdleTimeoutAsync(
                                    source,
                                    buffer.AsMemory(0, BufferSize),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (IOException ex)
                        {
                            throw new LocalWhisperModelException(
                                LocalWhisperModelFailure.Network,
                                "Die Netzwerkverbindung wurde während des Modelldownloads unterbrochen. Bitte erneut versuchen.",
                                ex);
                        }
                        if (read == 0)
                        {
                            break;
                        }

                        received += read;
                        if (received > Descriptor.ExpectedLength)
                        {
                            throw IntegrityException("Der Modelldownload ist größer als erwartet.");
                        }

                        await destination
                            .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        if (Descriptor.ExpectedLength <= 8 * 1024 * 1024 ||
                            received == Descriptor.ExpectedLength ||
                            received - lastProgressBytes >= 4 * 1024 * 1024 &&
                            progressStopwatch.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report(new LocalWhisperModelProgress(
                                LocalWhisperModelProgressStage.Downloading,
                                received,
                                Descriptor.ExpectedLength));
                            lastProgressBytes = received;
                            progressStopwatch.Restart();
                        }
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                    downloadedHash = Convert.ToHexString(hash.GetHashAndReset())
                        .ToLowerInvariant();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (received != Descriptor.ExpectedLength)
            {
                throw IntegrityException(
                    $"Der Modelldownload enthält {received} statt {Descriptor.ExpectedLength} Bytes.");
            }

            if (!FixedTimeHashEquals(downloadedHash, Descriptor.Sha256))
            {
                throw IntegrityException("Die SHA-256-Prüfsumme des Modelldownloads stimmt nicht.");
            }

            File.Move(PartialPath, ModelPath, overwrite: true);
            partialMoved = true;
            await WriteSidecarAsync(new FileInfo(ModelPath)).ConfigureAwait(false);
        }
        finally
        {
            if (!partialMoved)
            {
                TryDeleteBestEffort(PartialPath);
            }
        }
    }

    private async Task<string> ComputeSha256Async(
        string path,
        LocalWhisperModelProgressStage stage,
        IProgress<LocalWhisperModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long processed = 0;
        long lastProgressBytes = 0;
        var progressStopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                processed += read;
                if (Descriptor.ExpectedLength <= 8 * 1024 * 1024 ||
                    processed == Descriptor.ExpectedLength ||
                    processed - lastProgressBytes >= 4 * 1024 * 1024 &&
                    progressStopwatch.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new LocalWhisperModelProgress(
                        stage,
                        processed,
                        Descriptor.ExpectedLength));
                    lastProgressBytes = processed;
                    progressStopwatch.Restart();
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<ModelVerificationSidecar?> TryReadSidecarAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(VerificationPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                VerificationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<ModelVerificationSidecar>(
                    stream,
                    SidecarJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            // The model itself will be hashed instead of trusting an unreadable
            // verification shortcut.
            return null;
        }
    }

    private async Task WriteSidecarAsync(FileInfo modelInfo)
    {
        modelInfo.Refresh();
        var sidecar = new ModelVerificationSidecar(
            VerificationSchemaVersion,
            Descriptor.Id,
            Descriptor.FileName,
            Descriptor.Revision,
            Descriptor.ExpectedLength,
            Descriptor.Sha256.ToLowerInvariant(),
            modelInfo.CreationTimeUtc.Ticks,
            modelInfo.LastWriteTimeUtc.Ticks);
        var temporaryPath = VerificationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        sidecar,
                        SidecarJsonOptions)
                    .ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, VerificationPath, overwrite: true);
        }
        finally
        {
            TryDeleteBestEffort(temporaryPath);
        }
    }

    private bool SidecarMatches(ModelVerificationSidecar? sidecar, FileInfo modelInfo)
    {
        if (sidecar is null)
        {
            return false;
        }

        modelInfo.Refresh();
        return sidecar.SchemaVersion == VerificationSchemaVersion &&
               string.Equals(sidecar.ModelId, Descriptor.Id, StringComparison.Ordinal) &&
               string.Equals(sidecar.FileName, Descriptor.FileName, StringComparison.Ordinal) &&
               string.Equals(sidecar.Revision, Descriptor.Revision, StringComparison.Ordinal) &&
               sidecar.ExpectedLength == Descriptor.ExpectedLength &&
               FixedTimeHashEquals(sidecar.Sha256, Descriptor.Sha256) &&
               sidecar.CreationTimeUtcTicks == modelInfo.CreationTimeUtc.Ticks &&
               sidecar.LastWriteTimeUtcTicks == modelInfo.LastWriteTimeUtc.Ticks;
    }

    private void DeleteInvalidCache()
    {
        DeleteFileRequired(ModelPath, "ungültigen Modellcache");
        DeleteFileRequired(VerificationPath, "ungültigen Modell-Prüfnachweis");
    }

    private void DeleteFileRequired(string path, string description)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.Storage,
                $"OpenAI Flow konnte den {description} nicht entfernen. Bitte Zugriffsrechte prüfen und erneut versuchen.",
                ex);
        }
    }

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide the original download or cancellation error.
        }
    }

    private LocalWhisperModelException StorageException(Exception exception) => new(
        LocalWhisperModelFailure.Storage,
        $"Das lokale Whisper-Modell konnte nicht unter „{ModelDirectory}“ gespeichert werden. Bitte freien Speicherplatz und Zugriffsrechte prüfen.",
        exception);

    private static LocalWhisperModelException IntegrityException(string detail) => new(
        LocalWhisperModelFailure.Integrity,
        $"Der Download des lokalen Whisper-Modells ist unvollständig oder beschädigt. {detail} Bitte erneut versuchen.");

    private static bool FixedTimeHashEquals(string? actual, string? expected)
    {
        if (actual is null || expected is null || actual.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual.ToLowerInvariant()),
            System.Text.Encoding.ASCII.GetBytes(expected.ToLowerInvariant()));
    }

    private static void ValidateDescriptor(LocalWhisperModelDescriptor descriptor)
    {
        var hasSafeFileName = !string.IsNullOrWhiteSpace(descriptor.FileName) &&
                              Path.GetFileName(descriptor.FileName)
                                  .Equals(descriptor.FileName, StringComparison.Ordinal) &&
                              descriptor.FileName.IndexOfAny(
                                  [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
        var hasValidHash = descriptor.Sha256 is { Length: 64 } &&
                           descriptor.Sha256.All(Uri.IsHexDigit);
        if (string.IsNullOrWhiteSpace(descriptor.Id) ||
            !hasSafeFileName ||
            descriptor.DownloadUri is not { IsAbsoluteUri: true } ||
            !descriptor.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(descriptor.Revision) ||
            descriptor.ExpectedLength <= 0 ||
            !hasValidHash)
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.InvalidConfiguration,
                "Die Sicherheitsdaten des lokalen Whisper-Modells sind ungültig.");
        }
    }

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        Timeout = TimeSpan.FromHours(2)
    };

    private async Task<FileStream> AcquireProcessLockAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    ProcessLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(ProcessLockRetryDelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw StorageException(ex);
            }
        }
    }

    private async ValueTask<int> ReadWithIdleTimeoutAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        idleCancellation.CancelAfter(_downloadIdleTimeout);
        try
        {
            return await source.ReadAsync(buffer, idleCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested &&
                  idleCancellation.IsCancellationRequested)
        {
            throw new LocalWhisperModelException(
                LocalWhisperModelFailure.Network,
                "Der Modellserver hat während des Downloads zu lange keine Daten geliefert. Bitte Internetverbindung, Proxy und Firewall prüfen und erneut versuchen.",
                ex);
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 32 or 33;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(nameof(LocalWhisperModelManager));
        }
    }

    private void EnsureSufficientDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(ModelDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var required = checked(Descriptor.ExpectedLength + 256L * 1024 * 1024);
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
            {
                throw new LocalWhisperModelException(
                    LocalWhisperModelFailure.Storage,
                    $"Für das lokale Sprachmodell werden mindestens {required / (1024d * 1024 * 1024):F1} GB freier Speicher benötigt. Auf Laufwerk {drive.Name} ist nicht genug Platz.");
            }
        }
        catch (LocalWhisperModelException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StorageException(ex);
        }
    }

    private sealed record ModelVerificationSidecar(
        int SchemaVersion,
        string ModelId,
        string FileName,
        string Revision,
        long ExpectedLength,
        string Sha256,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks);
}
