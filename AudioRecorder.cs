using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace ChatGptDictationBridge;

internal sealed class AudioRecorder : IDisposable
{
    private readonly AppLogger _logger;
    private readonly object _gate = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private Stopwatch? _stopwatch;
    private string? _filePath;
    private TaskCompletionSource<AudioRecordingResult>? _stopped;

    public AudioRecorder(AppLogger logger)
    {
        _logger = logger;
    }

    public bool IsRecording { get; private set; }

    public void Start(AppSettings settings)
    {
        lock (_gate)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("Audio recorder is already recording.");
            }

            var folder = Path.IsPathRooted(settings.AudioTempFolder)
                ? settings.AudioTempFolder
                : Path.Combine(AppContext.BaseDirectory, settings.AudioTempFolder);
            Directory.CreateDirectory(folder);

            _filePath = Path.Combine(folder, $"dictation-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            _writer = new WaveFileWriter(_filePath, _waveIn.WaveFormat);
            _stopwatch = Stopwatch.StartNew();
            _stopped = new TaskCompletionSource<AudioRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;
                _waveIn.StartRecording();
                IsRecording = true;
                _logger.Info($"Audio recording started. File='{_filePath}'");
            }
            catch
            {
                CleanupRecorder();
                throw;
            }
        }
    }

    public Task<AudioRecordingResult> StopAsync()
    {
        WaveInEvent waveIn;
        Task<AudioRecordingResult> stoppedTask;

        lock (_gate)
        {
            if (!IsRecording || _waveIn is null || _stopped is null)
            {
                throw new InvalidOperationException("Audio recorder is not recording.");
            }

            waveIn = _waveIn;
            stoppedTask = _stopped.Task;
        }

        _logger.Info("Audio recording stop requested.");
        waveIn.StopRecording();
        return stoppedTask;
    }

    public void DeleteTemporaryFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.Info("Temporary audio file deleted.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Temporary audio file could not be deleted.", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CleanupRecorder();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            _writer?.Flush();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        TaskCompletionSource<AudioRecordingResult>? stopped;
        string? filePath;
        TimeSpan duration;

        lock (_gate)
        {
            stopped = _stopped;
            filePath = _filePath;
            duration = _stopwatch?.Elapsed ?? TimeSpan.Zero;
            CleanupRecorder();
        }

        if (e.Exception is not null)
        {
            stopped?.TrySetException(e.Exception);
            _logger.Error("Audio recording failed.", e.Exception);
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            stopped?.TrySetException(new InvalidOperationException("Audio file path was not set."));
            return;
        }

        _logger.Info($"Audio recording stopped. DurationMs={(long)duration.TotalMilliseconds}");
        stopped?.TrySetResult(new AudioRecordingResult(filePath, duration));
    }

    private void CleanupRecorder()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
        }

        _writer?.Dispose();
        _stopwatch?.Stop();

        _waveIn = null;
        _writer = null;
        _stopwatch = null;
        _filePath = null;
        _stopped = null;
        IsRecording = false;
    }
}
