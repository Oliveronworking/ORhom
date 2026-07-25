using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ORhom;

internal sealed class LocalAudioCaptureService
{
    private readonly AppLogger _logger;

    public LocalAudioCaptureService(AppLogger logger)
    {
        _logger = logger;
    }

    public LocalAudioCaptureSession Start(
        string? preferredDeviceId,
        string? preferredDeviceName,
        TimeSpan maximumDuration,
        EventHandler<LocalAudioCaptureUnexpectedlyStoppedEventArgs>? unexpectedlyStoppedHandler = null)
    {
        MMDevice? selectedDevice = null;
        MMDevice? nameFallbackDevice = null;
        var useLegacyNameFallback = string.IsNullOrWhiteSpace(preferredDeviceId);
        var nameFallbackMatchCount = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (!string.IsNullOrWhiteSpace(preferredDeviceId) &&
                    device.ID.Equals(preferredDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    if (selectedDevice is null)
                    {
                        selectedDevice = device;
                    }
                    else
                    {
                        device.Dispose();
                    }
                }
                else if (useLegacyNameFallback &&
                         !string.IsNullOrWhiteSpace(preferredDeviceName) &&
                         device.FriendlyName.Equals(preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    nameFallbackMatchCount++;
                    if (nameFallbackDevice is null)
                    {
                        nameFallbackDevice = device;
                    }
                    else
                    {
                        device.Dispose();
                    }
                }
                else
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            selectedDevice?.Dispose();
            nameFallbackDevice?.Dispose();
            throw new LocalAudioCaptureException(
                "Die aktiven Windows-Mikrofone konnten nicht abgefragt werden. Bitte Windows-Audiodienst, Geräteverbindung und Mikrofonberechtigung prüfen.",
                ex);
        }

        if (useLegacyNameFallback && nameFallbackMatchCount > 1)
        {
            nameFallbackDevice?.Dispose();
            throw new LocalAudioCaptureException(
                $"Mehrere aktive Mikrofone heißen „{preferredDeviceName}“. Bitte das gewünschte Gerät in den Einstellungen erneut auswählen.");
        }

        if (useLegacyNameFallback)
        {
            selectedDevice = nameFallbackDevice;
        }

        if (selectedDevice is null)
        {
            throw new LocalAudioCaptureException(
                "Das eingestellte Mikrofon ist nicht verbunden oder nicht aktiv.");
        }

        LocalAudioCaptureSession? session = null;
        try
        {
            session = new LocalAudioCaptureSession(
                selectedDevice,
                maximumDuration,
                _logger);
            if (unexpectedlyStoppedHandler is not null)
            {
                session.UnexpectedlyStopped += unexpectedlyStoppedHandler;
            }

            session.Start();
            return session;
        }
        catch (UnauthorizedAccessException ex)
        {
            DisposeFailedStart(session, selectedDevice);
            throw new LocalAudioCaptureException(
                "Windows hat den Mikrofonzugriff verweigert. Bitte den Mikrofonzugriff für Desktop-Apps in den Datenschutzeinstellungen erlauben.",
                ex);
        }
        catch (Exception ex) when (ex is not LocalAudioCaptureException)
        {
            DisposeFailedStart(session, selectedDevice);
            throw new LocalAudioCaptureException(
                "Das Mikrofon konnte nicht lokal gestartet werden. Bitte Gerät und Windows-Mikrofonberechtigung prüfen.",
                ex);
        }
    }

    private static void DisposeFailedStart(
        LocalAudioCaptureSession? session,
        MMDevice selectedDevice)
    {
        if (session is not null)
        {
            session.Dispose();
        }
        else
        {
            selectedDevice.Dispose();
        }
    }

}

internal sealed class LocalAudioCaptureSession : IDisposable
{
    private const int TargetSampleRate = 16000;

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private MemoryStream _capturedBytes = new();
    private readonly object _bufferLock = new();
    private readonly object _unexpectedStopLock = new();
    private readonly TaskCompletionSource<Exception?> _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _maximumDuration;
    private readonly AppLogger _logger;
    private readonly System.Diagnostics.Stopwatch _duration = new();
    private int _stopRequested;
    private int _limitReported;
    private EventHandler<LocalAudioCaptureUnexpectedlyStoppedEventArgs>? _unexpectedlyStopped;
    private LocalAudioCaptureUnexpectedlyStoppedEventArgs? _unexpectedStop;
    private bool _disposed;

    public LocalAudioCaptureSession(
        MMDevice device,
        TimeSpan maximumDuration,
        AppLogger logger)
    {
        _device = device;
        _maximumDuration = maximumDuration;
        _logger = logger;
        DeviceName = device.FriendlyName;
        DeviceId = device.ID;
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler? RecordingLimitReached;

    public event EventHandler<LocalAudioCaptureUnexpectedlyStoppedEventArgs>? UnexpectedlyStopped
    {
        add
        {
            if (value is null)
            {
                return;
            }

            LocalAudioCaptureUnexpectedlyStoppedEventArgs? priorStop;
            lock (_unexpectedStopLock)
            {
                _unexpectedlyStopped += value;
                priorStop = _unexpectedStop;
            }

            if (priorStop is not null)
            {
                InvokeUnexpectedStopHandler(value, priorStop);
            }
        }
        remove
        {
            lock (_unexpectedStopLock)
            {
                _unexpectedlyStopped -= value;
            }
        }
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public bool IsRecording => Volatile.Read(ref _stopRequested) == 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture.StartRecording();
        _duration.Start();
        _logger.Info($"Local microphone capture started. DeviceId='{DeviceId}' Format='{_capture.WaveFormat}'.");
    }

    public async Task<LocalAudioCaptureResult> StopAndGetSamplesAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync(cancellationToken);

        var sourceFormat = _capture.WaveFormat;
        float[] samples;
        using (var capturedBytes = DetachCapturedBytes())
        {
            if (!capturedBytes.TryGetBuffer(out var bytes))
            {
                throw new LocalAudioCaptureException(
                    "Der lokale Audiopuffer konnte nicht verarbeitet werden.");
            }

            samples = await Task.Run(
                () => ConvertToMono16Khz(bytes, sourceFormat, cancellationToken),
                cancellationToken);
        }

        var duration = TimeSpan.FromSeconds(samples.Length / (double)TargetSampleRate);
        _logger.Info($"Local microphone capture completed. DurationMs={duration.TotalMilliseconds:F0} Samples={samples.Length} SourceFormat='{sourceFormat}'.");
        return new LocalAudioCaptureResult(samples, duration, DeviceId, DeviceName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequestStop();

        Exception? recordingError;
        try
        {
            recordingError = await _stopped.Task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new LocalAudioCaptureException(
                "Das Mikrofon hat die Aufnahme nicht rechtzeitig beendet.",
                ex);
        }

        if (recordingError is not null)
        {
            throw new LocalAudioCaptureException(
                "Beim lokalen Aufnehmen ist ein Mikrofonfehler aufgetreten.",
                recordingError);
        }
    }

    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        _duration.Stop();
        try
        {
            _capture.StopRecording();
        }
        catch (Exception ex)
        {
            _stopped.TrySetResult(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RequestStop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _device.Dispose();
        lock (_bufferLock)
        {
            _capturedBytes.Dispose();
        }

        lock (_unexpectedStopLock)
        {
            _unexpectedlyStopped = null;
        }
    }

    internal static float[] ConvertToMono16Khz(
        byte[] bytes,
        WaveFormat sourceFormat,
        CancellationToken cancellationToken = default) =>
        ConvertToMono16Khz(
            new ArraySegment<byte>(bytes),
            sourceFormat,
            cancellationToken);

    private static float[] ConvertToMono16Khz(
        ArraySegment<byte> bytes,
        WaveFormat sourceFormat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Count == 0)
        {
            return [];
        }

        var usableFormat = sourceFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : sourceFormat;
        using var rawBytes = new MemoryStream(
            bytes.Array!,
            bytes.Offset,
            bytes.Count,
            writable: false,
            publiclyVisible: false);
        using var rawStream = new RawSourceWaveStream(rawBytes, usableFormat);
        ISampleProvider samples = rawStream.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new DownmixToMonoSampleProvider(samples, cancellationToken);
        }

        if (samples.WaveFormat.SampleRate != TargetSampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);
        }

        var estimatedLength = Math.Max(
            1024,
            (int)Math.Min(
                int.MaxValue,
                Math.Ceiling(
                    bytes.Count /
                    (double)Math.Max(usableFormat.AverageBytesPerSecond, 1) *
                    TargetSampleRate)));
        var result = new float[estimatedLength];
        var resultLength = 0;
        var buffer = new float[TargetSampleRate];
        int read;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = samples.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var requiredLength = checked(resultLength + read);
            if (requiredLength > result.Length)
            {
                var grownLength = Math.Min(
                    Array.MaxLength,
                    Math.Max((long)requiredLength, result.Length * 2L));
                if (grownLength < requiredLength)
                {
                    throw new LocalAudioCaptureException(
                        "Die lokale Aufnahme ist zu groß, um sie im Arbeitsspeicher zu verarbeiten.");
                }

                Array.Resize(ref result, (int)grownLength);
            }

            buffer.AsSpan(0, read).CopyTo(result.AsSpan(resultLength));
            resultLength = requiredLength;
        }

        if (resultLength == 0)
        {
            return [];
        }

        if (resultLength != result.Length)
        {
            Array.Resize(ref result, resultLength);
        }

        return result;
    }

    private MemoryStream DetachCapturedBytes()
    {
        lock (_bufferLock)
        {
            var capturedBytes = _capturedBytes;
            _capturedBytes = new MemoryStream();
            return capturedBytes;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_bufferLock)
        {
            _capturedBytes.Write(e.Buffer, 0, e.BytesRecorded);
        }

        if (_duration.Elapsed < _maximumDuration ||
            Interlocked.Exchange(ref _limitReported, 1) != 0)
        {
            return;
        }

        RequestStop();
        RecordingLimitReached?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var stopWasRequested = Interlocked.Exchange(ref _stopRequested, 1) != 0;
        _duration.Stop();
        _stopped.TrySetResult(e.Exception);
        if (stopWasRequested)
        {
            return;
        }

        var args = new LocalAudioCaptureUnexpectedlyStoppedEventArgs(e.Exception);
        EventHandler<LocalAudioCaptureUnexpectedlyStoppedEventArgs>? handlers;
        lock (_unexpectedStopLock)
        {
            _unexpectedStop ??= args;
            handlers = _unexpectedlyStopped;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<LocalAudioCaptureUnexpectedlyStoppedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            InvokeUnexpectedStopHandler(handler, args);
        }
    }

    internal sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private const double ActiveChannelEnergyRatio = 0.0001d;
        private const double NegativeCorrelationThreshold = -0.25d;

        private readonly ISampleProvider _source;
        private readonly CancellationToken _cancellationToken;
        private readonly double[] _channelEnergies;
        private readonly double[] _dominantCorrelations;
        private readonly bool[] _activeChannels;
        private readonly float[] _channelPolarities;
        private float[] _sourceBuffer = [];

        public DownmixToMonoSampleProvider(
            ISampleProvider source,
            CancellationToken cancellationToken)
        {
            _source = source;
            _cancellationToken = cancellationToken;
            _channelEnergies = new double[source.WaveFormat.Channels];
            _dominantCorrelations = new double[source.WaveFormat.Channels];
            _activeChannels = new bool[source.WaveFormat.Channels];
            _channelPolarities = new float[source.WaveFormat.Channels];
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var channels = _source.WaveFormat.Channels;
            var required = checked(count * channels);
            if (_sourceBuffer.Length < required)
            {
                _sourceBuffer = new float[required];
            }

            var sourceRead = _source.Read(_sourceBuffer, 0, required);
            _cancellationToken.ThrowIfCancellationRequested();
            var frames = sourceRead / channels;
            if (frames == 0)
            {
                return 0;
            }

            Array.Clear(_channelEnergies);
            for (var frame = 0; frame < frames; frame++)
            {
                if ((frame & 1023) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = _sourceBuffer[frame * channels + channel];
                    _channelEnergies[channel] += sample * (double)sample;
                }
            }

            var dominantChannel = 0;
            for (var channel = 1; channel < channels; channel++)
            {
                if (_channelEnergies[channel] > _channelEnergies[dominantChannel])
                {
                    dominantChannel = channel;
                }
            }

            var dominantEnergy = _channelEnergies[dominantChannel];
            if (dominantEnergy <= double.Epsilon)
            {
                Array.Clear(buffer, offset, frames);
                return frames;
            }

            Array.Clear(_dominantCorrelations);
            for (var frame = 0; frame < frames; frame++)
            {
                var dominantSample = _sourceBuffer[frame * channels + dominantChannel];
                for (var channel = 0; channel < channels; channel++)
                {
                    _dominantCorrelations[channel] +=
                        dominantSample * (double)_sourceBuffer[frame * channels + channel];
                }
            }

            var activeChannelCount = 0;
            var minimumActiveEnergy = dominantEnergy * ActiveChannelEnergyRatio;
            for (var channel = 0; channel < channels; channel++)
            {
                var isActive = _channelEnergies[channel] >= minimumActiveEnergy;
                _activeChannels[channel] = isActive;
                if (!isActive)
                {
                    _channelPolarities[channel] = 1f;
                    continue;
                }

                activeChannelCount++;
                var normalizedCorrelation = _dominantCorrelations[channel] /
                                            Math.Sqrt(dominantEnergy * _channelEnergies[channel]);
                _channelPolarities[channel] =
                    normalizedCorrelation < NegativeCorrelationThreshold ? -1f : 1f;
            }

            for (var frame = 0; frame < frames; frame++)
            {
                if ((frame & 1023) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    if (_activeChannels[channel])
                    {
                        sum += _sourceBuffer[frame * channels + channel] *
                               _channelPolarities[channel];
                    }
                }

                buffer[offset + frame] = sum / activeChannelCount;
            }

            return frames;
        }
    }

    private void InvokeUnexpectedStopHandler(
        EventHandler<LocalAudioCaptureUnexpectedlyStoppedEventArgs> handler,
        LocalAudioCaptureUnexpectedlyStoppedEventArgs args)
    {
        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected local microphone stop handler failed.", ex);
        }
    }
}

internal sealed record LocalAudioCaptureResult(
    float[] Samples,
    TimeSpan Duration,
    string DeviceId,
    string DeviceName);

internal sealed class LocalAudioCaptureUnexpectedlyStoppedEventArgs : EventArgs
{
    public LocalAudioCaptureUnexpectedlyStoppedEventArgs(Exception? error)
    {
        Error = error;
    }

    public Exception? Error { get; }
}

internal sealed class LocalAudioCaptureException : Exception
{
    public LocalAudioCaptureException(string message)
        : base(message)
    {
    }

    public LocalAudioCaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
