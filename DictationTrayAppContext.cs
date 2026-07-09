using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal sealed class DictationTrayAppContext : ApplicationContext
{
    private readonly AppLogger _logger;
    private readonly Icon _applicationIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly FocusTracker _focusTracker;
    private readonly PasteService _pasteService;
    private readonly AppSettings _settings;
    private readonly ChromeProfileLauncher _chromeProfileLauncher;
    private readonly ChromeMicrophoneConfigurator _microphoneConfigurator;
    private readonly AudioInputDeviceService _audioInputDevices;
    private readonly ChatGptDictationController _dictationController;
    private readonly AudioDuckingService _audioDucking;
    private readonly System.Windows.Forms.Timer _audioDuckingTimer;
    private readonly RecordingOverlayForm _recordingOverlay;
    private readonly SettingsForm _settingsForm;
    private readonly DictationHistoryStore _history;
    private readonly DictationHistoryForm _historyForm;
    private AppStatus _status = AppStatus.Idle;
    private RecordingSession? _session;
    private IntPtr _overlayTargetWindow;

    public DictationTrayAppContext()
    {
        var baseDirectory = AppContext.BaseDirectory;
        _logger = new AppLogger(Path.Combine(baseDirectory, "logs"));
        _applicationIcon = LoadApplicationIcon();
        _settings = AppSettings.Load(Path.Combine(baseDirectory, "settings.json"), _logger);
        _chromeProfileLauncher = new ChromeProfileLauncher(_settings, _logger);
        _microphoneConfigurator = new ChromeMicrophoneConfigurator(_chromeProfileLauncher, _logger);
        _audioInputDevices = new AudioInputDeviceService(_logger);
        _dictationController = new ChatGptDictationController(_settings, _logger, _chromeProfileLauncher);
        _audioDucking = new AudioDuckingService(_settings, _logger);
        _audioDuckingTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _audioDuckingTimer.Tick += (_, _) => _audioDucking.Refresh();
        _recordingOverlay = new RecordingOverlayForm(_settings.RecordingOverlayBottomOffsetPx, _settings.ToggleHotkey);
        _focusTracker = new FocusTracker(_logger);
        _pasteService = new PasteService(_logger);
        var historyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAIFlow");
        _history = new DictationHistoryStore(Path.Combine(historyDirectory, "dictation-history.json"), _logger);
        _historyForm = new DictationHistoryForm(_history) { Icon = _applicationIcon };
        _logger.Info("Application started.");
        var initialProfileValidation = _chromeProfileLauncher.ValidateConfiguredProfile();

        _hotkeyWindow = new HotkeyWindow(_settings, _logger);
        _hotkeyWindow.TogglePressed += (_, _) => _ = ToggleAsync();
        _hotkeyWindow.EscapePressed += (_, _) => _ = AbortRecordingAsync();
        _hotkeyWindow.CreateControl();
        _settingsForm = new SettingsForm(_settings, _audioInputDevices, ApplySettingsAsync);
        _settingsForm.Icon = _applicationIcon;
        _settingsForm.VisibleChanged += (_, _) => _hotkeyWindow.SetToggleEnabled(!_settingsForm.Visible);

        _statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Aufnahme abbrechen", null, (_, _) => _ = AbortRecordingAsync()));
        menu.Items.Add(new ToolStripMenuItem("Diktierverlauf (letzte 10)", null, (_, _) => OpenHistory()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("ChatGPT Profil öffnen", null, (_, _) => _ = OpenChatGptProfileAsync())
        {
            Enabled = _settings.OpenChatGptProfileVisibleForSetup
        });
        menu.Items.Add(new ToolStripMenuItem("Mikrofon & Hotkey einstellen", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profil prüfen", null, (_, _) => CheckChromeProfile()));
        menu.Items.Add(new ToolStripMenuItem("ChatGPT Diagnose speichern", null, (_, _) => _ = WriteChatGptDiagnosticsAsync())
        {
            Enabled = _settings.EnableChatGptInputDiagnostics
        });
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profilordner öffnen", null, (_, _) => OpenChromeProfileDirectory()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("OpenAI Flow öffnen", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Konfigurationsdatei öffnen", null, (_, _) => OpenPath(_settings.SettingsPath)));
        menu.Items.Add(new ToolStripMenuItem("Logs öffnen", null, (_, _) => OpenPath(_logger.LogPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, Exit));

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Visible = true,
            Text = "OpenAI Flow Dictation - Idle",
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        if (!initialProfileValidation.IsValid)
        {
            if (_settings.WarnIfConfiguredChromeProfileUnavailable)
            {
                ShowConfiguredProfileUnavailable();
            }
        }
        else if (_settings.SetupCompleted)
        {
            QueueChatGptStartupPreparation();
        }

        if (!_settings.SetupCompleted)
        {
            _settingsForm.ShowAndActivate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _historyForm.ClosePermanently();
            _historyForm.Dispose();
            _settingsForm.ClosePermanently();
            _settingsForm.Dispose();
            _hotkeyWindow.Dispose();
            _recordingOverlay.Dispose();
            _audioDuckingTimer.Stop();
            _audioDuckingTimer.Dispose();
            _audioDucking.Dispose();
            _dictationController.Dispose();
            _operationLock.Dispose();
            _applicationIcon.Dispose();
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
                await BeginWebDictationAsync();
            }
            else if (_status == AppStatus.Recording)
            {
                await FinishWebDictationAsync();
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task BeginWebDictationAsync()
    {
        var target = _focusTracker.Capture(_settings);
        _overlayTargetWindow = target.WindowHandle;
        if (target.IsPasswordField)
        {
            _logger.Info("Recording blocked because target is a password field.");
            ShowMessage("Ziel ist ein Passwortfeld. Aufnahme wurde nicht gestartet.");
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Starting);
        var startResult = await _dictationController.StartDictationAsync(target.WindowHandle);
        if (!startResult.Ok)
        {
            RestoreTargetAndClipboard(target);
            ShowMessage(startResult.Message);
            ResetToIdle();
            return;
        }

        _session = new RecordingSession(target, startResult.ChatWindow, startResult.Input);
        _audioDucking.Begin();
        _audioDuckingTimer.Start();
        _hotkeyWindow.SetEscapeEnabled(true);
        SetStatus(AppStatus.Recording);
        _logger.Info($"Recording session started. TargetClass='{target.WindowClass}' ChatWindow=0x{startResult.ChatWindow.ToInt64():X}");

        if (_settings.RestoreTargetAfterStart)
        {
            _pasteService.RestoreTargetFocus(target);
        }
    }

    private async Task FinishWebDictationAsync()
    {
        var session = _session;
        if (session is null)
        {
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Stopping);
        var stopResult = await _dictationController.StopDictationAsync(session.ChatWindow);
        if (!stopResult.Ok)
        {
            SetStatus(AppStatus.ReadingText);
            var textWasRecovered = await TryRecoverTextToHistoryAsync(
                session.ChatWindow,
                DictationHistoryOutcomes.FailureRecovered);
            if (!textWasRecovered)
            {
                _history.Add(string.Empty, DictationHistoryOutcomes.StopFailed);
            }

            RestoreTargetAndClipboard(session.Target);
            ShowMessage(textWasRecovered
                ? "Die Aufnahme konnte nicht sauber beendet werden, aber der Text wurde im Verlauf gerettet."
                : stopResult.Message);
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.ReadingText);
        var readResult = await _dictationController.ReadDictatedTextAsync(session.ChatWindow);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length}");
        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text))
        {
            _history.Add(string.Empty, DictationHistoryOutcomes.TranscriptionFailed);
            RestoreTargetAndClipboard(session.Target);
            ShowMessage("Nach dem Stoppen wurde kein Text transkribiert.");
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Pasting);
        var historyEntryId = _history.Add(text, DictationHistoryOutcomes.Transcribed);
        var pasteSucceeded = _pasteService.PasteIntoTarget(text, session.Target, _settings);
        _history.UpdateOutcome(
            historyEntryId,
            pasteSucceeded ? DictationHistoryOutcomes.Pasted : DictationHistoryOutcomes.PasteFailed);
        _logger.Info($"Dictation paste completed. Success={pasteSucceeded} TextLength={text.Length}");
        if (!pasteSucceeded)
        {
            ShowMessage("Text wurde gelesen, aber konnte nicht ins Ziel eingefügt werden.");
        }

        ResetToIdle();
    }

    private async Task AbortRecordingAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var session = _session;
            if (_status != AppStatus.Recording || session is null)
            {
                return;
            }

            _logger.Info("Recording abort requested.");
            SetStatus(AppStatus.Stopping);
            var stopResult = await _dictationController.StopDictationAsync(session.ChatWindow);
            var textWasRecovered = false;
            if (stopResult.Ok)
            {
                SetStatus(AppStatus.ReadingText);
                var readResult = await _dictationController.ReadDictatedTextAsync(session.ChatWindow);
                var text = readResult.Text.Trim();
                if (text.Length > 0 && !AutomationHelpers.IsUnsafeCapturedText(text))
                {
                    _history.Add(text, DictationHistoryOutcomes.CancelledRecovered);
                    textWasRecovered = true;
                }
                else
                {
                    _history.Add(string.Empty, DictationHistoryOutcomes.CancelledWithoutText);
                }
            }
            else
            {
                SetStatus(AppStatus.ReadingText);
                textWasRecovered = await TryRecoverTextToHistoryAsync(
                    session.ChatWindow,
                    DictationHistoryOutcomes.CancelledRecovered);
                if (!textWasRecovered)
                {
                    _history.Add(string.Empty, DictationHistoryOutcomes.CancelledWithoutText);
                }
            }

            RestoreTargetAndClipboard(session.Target);
            ShowMessage(textWasRecovered
                ? "Aufnahme abgebrochen. Der gesprochene Text wurde im Verlauf gesichert."
                : "Aufnahme abgebrochen. Es konnte kein Text gesichert werden.");
            ResetToIdle();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void QueueChatGptStartupPreparation()
    {
        if (!_settings.PrepareChatGptOnStartup || !_settings.LaunchChatGptIfMissing)
        {
            return;
        }

        _ = Task.Run(PrepareChatGptOnStartupAsync);
    }

    private async Task PrepareChatGptOnStartupAsync()
    {
        await Task.Delay(800);
        try
        {
            var foregroundBeforeLaunch = NativeMethods.GetForegroundWindow();
            var result = await _dictationController.PrepareBackgroundWindowAsync(IntPtr.Zero);
            if (!result.Ok)
            {
                _logger.Info($"ChatGPT startup preparation failed. Failure={result.Failure}");
                return;
            }

            if (foregroundBeforeLaunch != IntPtr.Zero && NativeMethods.IsWindow(foregroundBeforeLaunch))
            {
                NativeMethods.SetForegroundWindow(foregroundBeforeLaunch);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT startup preparation failed.", ex);
        }
    }

    private async Task OpenChatGptProfileAsync()
    {
        var result = await _dictationController.OpenConfiguredProfileAsync(IntPtr.Zero);
        if (!result.Ok)
        {
            ShowMessage(result.Message);
            return;
        }

        ShowMessage("ChatGPT wurde im konfigurierten Chrome-Profil Profile 3 geöffnet.");
    }

    private void CheckChromeProfile()
    {
        var validation = _dictationController.ValidateConfiguredProfile();
        if (validation.IsValid)
        {
            _logger.Info("Chrome profile check completed successfully.");
            ShowMessage($"Konfiguriertes Chrome-Profil {_settings.ChromeProfileDirectory} wurde gefunden.");
            return;
        }

        _logger.Info($"Chrome profile check failed. Reason={validation.FailureReason}");
        ShowConfiguredProfileUnavailable();
    }

    private async Task<SettingsApplyResult> ApplySettingsAsync(string microphoneName, string hotkey)
    {
        if (_status != AppStatus.Idle)
        {
            return SettingsApplyResult.Fail("Bitte zuerst die laufende Aufnahme beenden.");
        }

        var activeMicrophones = _audioInputDevices.GetActiveMicrophones();
        if (!activeMicrophones.Contains(microphoneName, StringComparer.OrdinalIgnoreCase))
        {
            return SettingsApplyResult.Fail("Das ausgewählte Mikrofon ist nicht mehr verbunden.");
        }

        var microphoneResult = await _microphoneConfigurator.ApplyAsync(microphoneName);
        if (!microphoneResult.Ok)
        {
            return SettingsApplyResult.Fail(microphoneResult.Message);
        }

        var pageReset = await _dictationController.ResetChatGptPageAsync(_settingsForm.Handle);
        if (!pageReset.Ok)
        {
            return SettingsApplyResult.Fail("Mikrofon gespeichert, aber ChatGPT konnte nicht vorbereitet werden. Bitte ChatGPT Profil öffnen und Anmeldung prüfen.");
        }

        if (!_hotkeyWindow.TryUpdateToggleHotkey(hotkey, out var hotkeyFailure))
        {
            return SettingsApplyResult.Fail(hotkeyFailure);
        }

        _settings.PreferredMicrophoneName = microphoneName;
        _settings.ToggleHotkey = hotkey;
        _settings.SetupCompleted = true;
        _settings.Save(_logger);
        _recordingOverlay.SetToggleHotkey(hotkey);
        _logger.Info($"Settings applied from UI. MicrophoneName='{microphoneName}' ToggleHotkey='{hotkey}'.");
        ShowMessage($"OpenAI Flow läuft jetzt mit {hotkey} im Hintergrund.");
        return SettingsApplyResult.Success(microphoneName, hotkey);
    }

    private void OpenSettings()
    {
        if (_status != AppStatus.Idle)
        {
            ShowMessage("Bitte zuerst die laufende Aufnahme beenden.");
            return;
        }

        _settingsForm.ShowAndActivate();
    }

    private void OpenHistory()
    {
        _historyForm.ShowAndActivate();
    }

    private async Task<bool> TryRecoverTextToHistoryAsync(IntPtr chatWindow, string outcome)
    {
        var readResult = await _dictationController.ReadDictatedTextAsync(chatWindow);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation recovery read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length}");
        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text))
        {
            return false;
        }

        _history.Add(text, outcome);
        return true;
    }

    private async Task WriteChatGptDiagnosticsAsync()
    {
        if (!_settings.EnableChatGptInputDiagnostics)
        {
            ShowMessage("ChatGPT-Diagnose ist in den Einstellungen deaktiviert.");
            return;
        }

        var window = _session?.ChatWindow ?? _dictationController.KnownChatWindow;
        await _dictationController.DiagnoseChatGptUiAsync(window);
        ShowMessage("ChatGPT Diagnose wurde ins Log geschrieben.");
    }

    private void OpenChromeProfileDirectory()
    {
        var validation = _dictationController.ValidateConfiguredProfile();
        if (!validation.IsValid)
        {
            ShowConfiguredProfileUnavailable();
            return;
        }

        OpenPath(validation.ProfileDirectoryPath);
    }

    private void RestoreTargetAndClipboard(FocusTarget target)
    {
        _pasteService.RestoreTargetFocus(target);
        _pasteService.RestoreClipboard(target, _settings);
    }

    private void ResetToIdle()
    {
        _audioDuckingTimer.Stop();
        _audioDucking.Restore();
        _session = null;
        _hotkeyWindow.SetEscapeEnabled(false);
        SetStatus(AppStatus.Idle);
        _overlayTargetWindow = IntPtr.Zero;
    }

    private void SetStatus(AppStatus status)
    {
        _status = status;
        _statusItem.Text = $"Status: {status}";
        _notifyIcon.Text = $"OpenAI Flow Dictation - {status}";
        if (_settings.ShowRecordingOverlay)
        {
            if (status == AppStatus.Idle)
            {
                _recordingOverlay.HideOverlay();
            }
            else
            {
                _recordingOverlay.ShowStatus(status, _overlayTargetWindow);
            }
        }
        _logger.Info($"Status changed: {status}");
    }

    private void ShowMessage(string message)
    {
        _notifyIcon.BalloonTipTitle = "OpenAI Flow Dictation";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void ShowConfiguredProfileUnavailable()
    {
        ShowMessage($"Konfiguriertes Chrome-Profil {_settings.ChromeProfileDirectory} nicht gefunden. Bitte settings.json prüfen.");
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
            ShowMessage("Datei konnte nicht geöffnet werden.");
        }
    }

    private void Exit(object? sender, EventArgs e)
    {
        _logger.Info("Application exiting.");
        ExitThread();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
            return Icon.ExtractAssociatedIcon(executablePath) ?? (Icon)SystemIcons.Application.Clone();
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    private sealed record RecordingSession(
        FocusTarget Target,
        IntPtr ChatWindow,
        AutomationElement? ChatInput);
}
