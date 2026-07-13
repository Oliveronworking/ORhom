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

    private const int SignalFrameSamples = 320;
    private const int MinimumActiveSignalFrames = 3;
    private const float MinimumSignalFramePeak = 0.002f;
    private const double MinimumSignalFrameRms = 0.0005d;

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
    private int _rx7700XtDeviceIndex = -1;
    private bool _disposed;

    public LocalWhisperRecognitionService(
        LocalWhisperModelManager modelManager,
        AppLogger logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    public bool IsReady => _processor is not null && _vulkanBackendConfirmed;

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

        if (!ContainsAudibleSignal(mono16KhzSamples))
        {
            _logger.Info($"Local transcription skipped because the recording contains no audible signal. Samples={mono16KhzSamples.Length}.");
            return string.Empty;
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

            var text = new StringBuilder();
            await foreach (var segment in processor
                               .ProcessAsync(mono16KhzSamples, cancellationToken)
                               .ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            var normalized = WhitespaceRegex.Replace(text.ToString(), " ").Trim();
            _logger.Info($"Local German transcription completed. Samples={mono16KhzSamples.Length} TextLength={normalized.Length} Language='{Language}'.");
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
            _logger.Info($"Local whisper.cpp ready. Runtime={RuntimeOptions.LoadedLibrary} GpuDevice={selectedGpuDevice} Language='{Language}' Model='{Path.GetFileName(modelPath)}' RuntimeInfo='{CollapseForLog(WhisperFactory.GetRuntimeInfo())}'.");
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
        if (samples.IsEmpty)
        {
            return false;
        }

        var activeFrames = 0;
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
                activeFrames++;
                if (activeFrames >= MinimumActiveSignalFrames)
                {
                    return true;
                }
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
