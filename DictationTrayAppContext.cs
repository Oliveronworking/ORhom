using System.Diagnostics;
using System.IO;

namespace ChatGptDictationBridge;

internal sealed class DictationTrayAppContext : ApplicationContext
{
    private readonly AppLogger _logger;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly FocusTracker _focusTracker;
    private readonly AudioRecorder _audioRecorder;
    private readonly TranscriptionService _transcriptionService;
    private readonly PasteService _pasteService;
    private readonly AppSettings _settings;
    private AppStatus _status = AppStatus.Idle;
    private RecordingSession? _session;
    private CancellationTokenSource? _transcriptionCancellation;

    public DictationTrayAppContext()
    {
        var baseDirectory = AppContext.BaseDirectory;
        _logger = new AppLogger(Path.Combine(baseDirectory, "logs"));
        _settings = AppSettings.Load(Path.Combine(baseDirectory, "settings.json"), _logger);
        _focusTracker = new FocusTracker(_logger);
        _audioRecorder = new AudioRecorder(_logger);
        _transcriptionService = new TranscriptionService(_settings, _logger);
        _pasteService = new PasteService(_logger);
        _logger.Info("Application started.");

        _statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Aufnahme abbrechen", null, (_, _) => _ = AbortRecordingAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Einstellungen oeffnen", null, (_, _) => OpenPath(_settings.SettingsPath)));
        menu.Items.Add(new ToolStripMenuItem("Logs oeffnen", null, (_, _) => OpenPath(_logger.LogPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, Exit));

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "OpenAI Flow Dictation - Idle",
            ContextMenuStrip = menu
        };

        _hotkeyWindow = new HotkeyWindow(_settings, _logger);
        _hotkeyWindow.TogglePressed += (_, _) => _ = ToggleAsync();
        _hotkeyWindow.EscapePressed += (_, _) => _ = AbortRecordingAsync();
        _hotkeyWindow.CreateControl();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _transcriptionCancellation?.Cancel();
            _transcriptionCancellation?.Dispose();
            _audioRecorder.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _hotkeyWindow.Dispose();
            _operationLock.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task ToggleAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_status == AppStatus.Idle)
            {
                BeginRecording();
            }
            else if (_status == AppStatus.Recording)
            {
                await FinishRecordingAsync();
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void BeginRecording()
    {
        var target = _focusTracker.Capture(_settings);
        if (target.IsPasswordField)
        {
            _logger.Info("Recording blocked because target is a password field.");
            ShowMessage("Ziel ist ein Passwortfeld. Aufnahme wurde nicht gestartet.");
            SetTemporaryError();
            return;
        }

        try
        {
            _audioRecorder.Start(_settings);
            _session = new RecordingSession(target);
            _hotkeyWindow.SetEscapeEnabled(true);
            SetStatus(AppStatus.Recording);
        }
        catch (Exception ex)
        {
            _logger.Error("Audio recording could not be started.", ex);
            _pasteService.RestoreClipboard(target, _settings);
            ShowMessage("Mikrofonaufnahme konnte nicht gestartet werden.");
            SetTemporaryError();
        }
    }

    private async Task FinishRecordingAsync()
    {
        var session = _session;
        if (session is null)
        {
            ResetToIdle();
            return;
        }

        AudioRecordingResult? recording = null;
        try
        {
            SetStatus(AppStatus.Transcribing);
            recording = await _audioRecorder.StopAsync();

            _transcriptionCancellation?.Dispose();
            _transcriptionCancellation = new CancellationTokenSource();
            var text = await _transcriptionService.TranscribeAsync(recording.FilePath, _settings, _transcriptionCancellation.Token);
            text = text.Trim();
            _logger.Info($"Transcription completed. TextLength={text.Length}");

            if (text.Length == 0)
            {
                ShowMessage("Kein diktierter Text erkannt.");
                ResetToIdle();
                return;
            }

            SetStatus(AppStatus.Pasting);
            if (!_pasteService.PasteIntoTarget(text, session.Target, _settings))
            {
                ShowMessage("Text konnte nicht eingefuegt werden.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Transcription canceled.");
            ShowMessage("Aufnahme abgebrochen.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Error("Dictation workflow failed.", ex);
            ShowMessage("OpenAI API-Key fehlt. Setze OPENAI_API_KEY oder openAIApiKey in settings.json.");
            SetTemporaryError();
            return;
        }
        catch (Exception ex)
        {
            _logger.Error("Dictation workflow failed.", ex);
            ShowMessage("Diktat fehlgeschlagen. Details stehen im Log.");
            SetTemporaryError();
            return;
        }
        finally
        {
            if (recording is not null)
            {
                _audioRecorder.DeleteTemporaryFile(recording.FilePath);
            }

            if (_status != AppStatus.Error)
            {
                ResetToIdle();
            }
        }
    }

    private async Task AbortRecordingAsync()
    {
        if (_status is not (AppStatus.Recording or AppStatus.Transcribing or AppStatus.Pasting))
        {
            return;
        }

        if (!await _operationLock.WaitAsync(0))
        {
            if (_status == AppStatus.Transcribing)
            {
                _logger.Info("Abort requested while transcription is running.");
                _transcriptionCancellation?.Cancel();
            }

            return;
        }

        try
        {
            _logger.Info("Abort requested.");
            _transcriptionCancellation?.Cancel();

            if (_audioRecorder.IsRecording)
            {
                AudioRecordingResult? recording = null;
                try
                {
                    recording = await _audioRecorder.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error("Stopping aborted recording failed.", ex);
                }
                finally
                {
                    _audioRecorder.DeleteTemporaryFile(recording?.FilePath);
                }
            }

            _pasteService.RestoreClipboard(_session?.Target, _settings);
            ShowMessage("Aufnahme abgebrochen.");
            ResetToIdle();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void ResetToIdle()
    {
        _session = null;
        _hotkeyWindow.SetEscapeEnabled(false);
        SetStatus(AppStatus.Idle);
    }

    private void SetTemporaryError()
    {
        SetStatus(AppStatus.Error);
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_notifyIcon.Visible)
            {
                ResetToIdle();
            }
        };
        timer.Start();
    }

    private void SetStatus(AppStatus status)
    {
        _status = status;
        _statusItem.Text = $"Status: {status}";
        _notifyIcon.Text = $"OpenAI Flow Dictation - {status}";
        _logger.Info($"Status changed: {status}");
    }

    private void ShowMessage(string message)
    {
        _notifyIcon.BalloonTipTitle = "OpenAI Flow Dictation";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(2500);
    }

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error($"Could not open path: {path}", ex);
            ShowMessage("Datei konnte nicht geoeffnet werden.");
        }
    }

    private void Exit(object? sender, EventArgs e)
    {
        _logger.Info("Application exiting.");
        ExitThread();
    }

    private sealed record RecordingSession(FocusTarget Target);
}
