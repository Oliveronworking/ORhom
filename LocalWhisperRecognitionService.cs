using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace ChatGptDictationBridge;

internal sealed class LocalWhisperRecognitionService : IDisposable
{
    public const string Language = "de";

    internal const string InitialPrompt =
        "OpenAI Flow, ChatGPT, Codex, GitHub, Repository, PowerShell, .NET, JSON, " +
        "Vulkan, Flash Attention, Whisper Large V3 Turbo, Zwischenablage.";

    private const int SignalFrameSamples = 320;
    private const int MinimumConsecutiveActiveSignalFrames = 3;
    private const float MinimumSignalFramePeak = 0.002f;
    private const double MinimumSignalFrameRms = 0.0005d;
    private const float MinimumContentSampleMagnitude = 0.00001f;
    private const int SilenceTrimPaddingSamples = 4800;
    private const int WarmupSampleRate = 16000;
    private const float WarmupFrequency = 220f;
    private const float WarmupAmplitude = 0.03f;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Rx7700XtVulkanDeviceRegex = new(
        @"ggml_vulkan:\s*(?<index>\d+)\s*=\s*.*RX\s*7700\s*XT",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly LocalWhisperModelManager _modelManager;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly ConcurrentQueue<string> _nativeInitializationMessages = new();
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private IDisposable? _nativeLogSubscription;
    private volatile bool _vulkanBackendConfirmed;
    private volatile bool _warmupCompleted;
    private int _rx7700XtDeviceIndex = -1;
    private bool _disposed;

    public LocalWhisperRecognitionService(
        LocalWhisperModelManager modelManager,
        AppLogger logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    public bool IsReady =>
        _warmupCompleted &&
        _processor is not null &&
        _vulkanBackendConfirmed;

    public async Task EnsureReadyAsync(
        IProgress<LocalWhisperModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            var modelPath = await _modelManager
                .EnsureModelAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            await Task.Run(
                () => InitializeWhisper(modelPath),
                cancellationToken).ConfigureAwait(false);
            await WarmUpWhisperAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeWhisperObjects();
            throw;
        }
        catch (LocalWhisperModelException)
        {
            throw;
        }
        catch (LocalWhisperRecognitionException)
        {
            DisposeWhisperObjects();
            throw;
        }
        catch (Exception ex)
        {
            DisposeWhisperObjects();
            throw new LocalWhisperRecognitionException(
                "whisper.cpp konnte das deutsche Modell nicht über Vulkan laden. Bitte den aktuellen AMD-Adrenalin-Treiber sowie Microsoft Visual C++ 2015–2022 Redistributable (x64) installieren und OpenAI Flow neu starten.",
                ex);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<string> TranscribeGermanAsync(
        float[] mono16KhzSamples,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady)
        {
            throw new LocalWhisperRecognitionException(
                "Das lokale deutsche Sprachmodell ist noch nicht bereit.");
        }

        if (mono16KhzSamples.Length < 1600)
        {
            throw new LocalWhisperRecognitionException(
                "Die Aufnahme war zu kurz. Bitte den Hotkey drücken, kurz sprechen und erneut stoppen.");
        }

        var originalSampleCount = mono16KhzSamples.Length;
        var preparedSamples = PrepareSamplesForTranscription(mono16KhzSamples);
        if (!ContainsAudibleSignal(preparedSamples))
        {
            _logger.Info($"Local transcription skipped because the recording contains no audible signal. Samples={originalSampleCount}.");
            return string.Empty;
        }

        if (preparedSamples.Length != originalSampleCount)
        {
            _logger.Info($"Local recording edge silence trimmed. OriginalSamples={originalSampleCount} PreparedSamples={preparedSamples.Length} PaddingMs=300.");
        }

        await _processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processor = _processor;
            if (processor is null || !_vulkanBackendConfirmed)
            {
                throw new LocalWhisperRecognitionException(
                    "Das lokale deutsche Sprachmodell wurde zwischenzeitlich entladen. Bitte erneut versuchen.");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var text = new StringBuilder();
            await foreach (var segment in processor
                               .ProcessAsync(preparedSamples, cancellationToken)
                               .ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            var normalized = WhitespaceRegex.Replace(text.ToString(), " ").Trim();
            _logger.Info($"Local German transcription completed. DurationMs={stopwatch.Elapsed.TotalMilliseconds:F0} Samples={preparedSamples.Length} OriginalSamples={originalSampleCount} TextLength={normalized.Length} Language='{Language}'.");
            return normalized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalWhisperRecognitionException(
                "Die lokale deutsche Transkription ist fehlgeschlagen. Bitte erneut versuchen oder den ChatGPT-Browser-Fallback auswählen.",
                ex);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var hadLoadedResources = _processor is not null ||
                                         _factory is not null ||
                                         _nativeLogSubscription is not null;
                DisposeWhisperObjects();
                if (hadLoadedResources)
                {
                    _logger.Info("Local whisper.cpp model unloaded; verified model cache remains on disk.");
                }
            }
            finally
            {
                _processingGate.Release();
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initializationGate.Wait();
        try
        {
            _processingGate.Wait();
            try
            {
                DisposeWhisperObjects();
                _modelManager.Dispose();
            }
            finally
            {
                _processingGate.Release();
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private void InitializeWhisper(string modelPath)
    {
        DisposeWhisperObjects();
        _warmupCompleted = false;
        _vulkanBackendConfirmed = false;
        _rx7700XtDeviceIndex = -1;
        while (_nativeInitializationMessages.TryDequeue(out _))
        {
        }

        RuntimeOptions.LibraryPath = null;
        if (RuntimeOptions.LoadedLibrary is { } loadedLibrary &&
            loadedLibrary != RuntimeLibrary.Vulkan)
        {
            throw new LocalWhisperRecognitionException(
                $"Es ist bereits die Whisper-Laufzeit {loadedLibrary} statt Vulkan aktiv. Bitte OpenAI Flow neu starten.");
        }

        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan];
        _nativeLogSubscription = LogProvider.AddLogger(OnNativeLog);

        WhisperFactory? factory = null;
        WhisperProcessor? processor = null;
        try
        {
            var selectedGpuDevice = 0;
            (factory, processor) = CreateWhisperProcessor(modelPath, selectedGpuDevice);
            var rx7700XtDevice = Volatile.Read(ref _rx7700XtDeviceIndex);
            if (rx7700XtDevice > 0)
            {
                processor.Dispose();
                processor = null;
                factory.Dispose();
                factory = null;
                _vulkanBackendConfirmed = false;
                selectedGpuDevice = rx7700XtDevice;
                _logger.Info($"RX 7700 XT detected as Vulkan device {selectedGpuDevice}; reloading the model on that GPU.");
                (factory, processor) = CreateWhisperProcessor(
                    modelPath,
                    selectedGpuDevice);
            }

            if (RuntimeOptions.LoadedLibrary != RuntimeLibrary.Vulkan ||
                !_vulkanBackendConfirmed)
            {
                throw new LocalWhisperRecognitionException(
                    "Die Vulkan-Laufzeit wurde geladen, aber whisper.cpp hat kein Vulkan-GPU-Backend aktiviert. Bitte AMD-Treiber aktualisieren und prüfen, ob die RX 7700 XT in Windows verfügbar ist.");
            }

            rx7700XtDevice = Volatile.Read(ref _rx7700XtDeviceIndex);
            if (rx7700XtDevice >= 0 && selectedGpuDevice != rx7700XtDevice)
            {
                throw new LocalWhisperRecognitionException(
                    $"Die RX 7700 XT wurde als Vulkan-Gerät {rx7700XtDevice} erkannt, aber whisper.cpp verwendet Gerät {selectedGpuDevice}. Bitte OpenAI Flow neu starten.");
            }

            _factory = factory;
            _processor = processor;
            factory = null;
            processor = null;
            _logger.Info($"Local whisper.cpp initialized; native warm-up pending. Runtime={RuntimeOptions.LoadedLibrary} GpuDevice={selectedGpuDevice} Language='{Language}' Model='{Path.GetFileName(modelPath)}' RuntimeInfo='{CollapseForLog(WhisperFactory.GetRuntimeInfo())}'.");
            if (rx7700XtDevice < 0)
            {
                _logger.Info($"Vulkan backend is active, but the native device log did not explicitly contain 'RX 7700 XT'. GPU device {selectedGpuDevice} is being used.");
            }
        }
        finally
        {
            processor?.Dispose();
            factory?.Dispose();
        }
    }

    private async Task WarmUpWhisperAsync(CancellationToken cancellationToken)
    {
        var processor = _processor;
        if (processor is null || !_vulkanBackendConfirmed)
        {
            throw new LocalWhisperRecognitionException(
                "Das lokale deutsche Sprachmodell konnte nicht für die erste Transkription vorbereitet werden.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var discardedSegments = 0;
        try
        {
            var warmupSamples = CreateWarmupSamples();
            discardedSegments = await DrainWarmupAsync(
                    processor.ProcessAsync(warmupSamples, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            _warmupCompleted = true;
            _logger.Info($"Local whisper.cpp native warm-up completed. DurationMs={stopwatch.Elapsed.TotalMilliseconds:F0} DiscardedSegments={discardedSegments}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalWhisperRecognitionException(
                "Das lokale deutsche Sprachmodell konnte nicht vollständig aufgewärmt werden. Bitte erneut versuchen.",
                ex);
        }
    }

    private void OnNativeLog(WhisperLogLevel level, string? message)
    {
        var collapsed = CollapseForLog(message);
        if (collapsed.Length == 0)
        {
            return;
        }

        if (ConfirmsVulkanBackend(collapsed))
        {
            _vulkanBackendConfirmed = true;
        }

        if (FindRx7700XtVulkanDevice(collapsed) is { } rx7700XtDevice)
        {
            Volatile.Write(ref _rx7700XtDeviceIndex, rx7700XtDevice);
        }

        if (_nativeInitializationMessages.Count < 200)
        {
            _nativeInitializationMessages.Enqueue(collapsed);
        }

        if (level is WhisperLogLevel.Error or WhisperLogLevel.Warning ||
            collapsed.Contains("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"whisper.cpp [{level}]: {collapsed}");
        }
    }

    private void DisposeWhisperObjects()
    {
        _warmupCompleted = false;
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _nativeLogSubscription?.Dispose();
        _nativeLogSubscription = null;
        _vulkanBackendConfirmed = false;
        _rx7700XtDeviceIndex = -1;
    }

    private static (WhisperFactory Factory, WhisperProcessor Processor) CreateWhisperProcessor(
        string modelPath,
        int gpuDevice)
    {
        var factory = WhisperFactory.FromPath(
            modelPath,
            new WhisperFactoryOptions
            {
                UseGpu = true,
                UseFlashAttention = true,
                GpuDevice = gpuDevice
            });
        try
        {
            var processor = factory.CreateBuilder()
                .WithLanguage(Language)
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .WithNoContext()
                .WithPrompt(InitialPrompt)
                .WithTemperature(0f)
                .WithGreedySamplingStrategy()
                .Build();
            return (factory, processor);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static string CollapseForLog(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex.Replace(value, " ").Trim();

    internal static bool ConfirmsVulkanBackend(string message) =>
        message.Contains("using Vulkan", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Vulkan0 backend", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("whisper_model_load: Vulkan", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("total size", StringComparison.OrdinalIgnoreCase);

    internal static int? FindRx7700XtVulkanDevice(string message)
    {
        var match = Rx7700XtVulkanDeviceRegex.Match(message);
        return match.Success &&
               int.TryParse(match.Groups["index"].Value, out var index)
            ? index
            : null;
    }

    internal static bool ContainsAudibleSignal(ReadOnlySpan<float> samples)
    {
        return ContainsConsecutiveAudibleFrames(samples);
    }

    internal static float[] PrepareSamplesForTranscription(float[] samples)
    {
        if (samples.Length == 0)
        {
            return samples;
        }

        var containsNonFiniteSample = false;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                containsNonFiniteSample = true;
                break;
            }
        }

        var start = 0;
        var endExclusive = samples.Length;
        if (TryFindContentBounds(
                samples,
                out var firstContentSample,
                out var lastContentSampleExclusive))
        {
            start = Math.Max(0, firstContentSample - SilenceTrimPaddingSamples);
            endExclusive = Math.Min(
                samples.Length,
                lastContentSampleExclusive + SilenceTrimPaddingSamples);
        }

        if (!containsNonFiniteSample && start == 0 && endExclusive == samples.Length)
        {
            return samples;
        }

        var prepared = new float[endExclusive - start];
        for (var sourceIndex = start; sourceIndex < endExclusive; sourceIndex++)
        {
            var sample = samples[sourceIndex];
            prepared[sourceIndex - start] = float.IsFinite(sample) ? sample : 0f;
        }

        return prepared;
    }

    internal static float[] CreateWarmupSamples()
    {
        var samples = new float[WarmupSampleRate];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = WarmupAmplitude * MathF.Sin(
                2 * MathF.PI * WarmupFrequency * index / WarmupSampleRate);
        }

        return samples;
    }

    internal static async Task<int> DrainWarmupAsync<T>(
        IAsyncEnumerable<T> segments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var discardedSegments = 0;
        await foreach (var _ in segments
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            discardedSegments++;
        }

        return discardedSegments;
    }

    private static bool TryFindContentBounds(
        ReadOnlySpan<float> samples,
        out int firstContentSample,
        out int lastContentSampleExclusive)
    {
        firstContentSample = -1;
        lastContentSampleExclusive = -1;
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if (!float.IsFinite(sample) ||
                Math.Abs(sample) < MinimumContentSampleMagnitude)
            {
                continue;
            }

            if (firstContentSample < 0)
            {
                firstContentSample = index;
            }

            lastContentSampleExclusive = index + 1;
        }

        return firstContentSample >= 0;
    }

    private static bool ContainsConsecutiveAudibleFrames(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return false;
        }

        var consecutiveActiveFrames = 0;
        for (var offset = 0; offset < samples.Length; offset += SignalFrameSamples)
        {
            var frameLength = Math.Min(SignalFrameSamples, samples.Length - offset);
            double sumOfSquares = 0;
            var peak = 0f;
            for (var index = 0; index < frameLength; index++)
            {
                var sample = samples[offset + index];
                if (!float.IsFinite(sample))
                {
                    continue;
                }

                peak = Math.Max(peak, Math.Abs(sample));
                sumOfSquares += sample * (double)sample;
            }

            var rms = Math.Sqrt(sumOfSquares / frameLength);
            if (peak >= MinimumSignalFramePeak && rms >= MinimumSignalFrameRms)
            {
                consecutiveActiveFrames++;
                if (consecutiveActiveFrames >= MinimumConsecutiveActiveSignalFrames)
                {
                    return true;
                }
            }
            else
            {
                consecutiveActiveFrames = 0;
            }
        }

        return false;
    }
}

internal sealed class LocalWhisperRecognitionException : Exception
{
    public LocalWhisperRecognitionException(string message)
        : base(message)
    {
    }

    public LocalWhisperRecognitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
