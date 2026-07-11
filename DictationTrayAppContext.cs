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
    private readonly ChromeProfileDiscovery _chromeProfileDiscovery;
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
    private bool _queuedStopRequested;
    private bool _exitInProgress;

    public DictationTrayAppContext()
    {
        var baseDirectory = AppContext.BaseDirectory;
        _logger = new AppLogger(Path.Combine(baseDirectory, "logs"));
        _applicationIcon = LoadApplicationIcon();
        _settings = AppSettings.Load(Path.Combine(baseDirectory, "settings.json"), _logger);
        _chromeProfileDiscovery = new ChromeProfileDiscovery();
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
        _hotkeyWindow.ToggleReleased += OnToggleReleased;
        _hotkeyWindow.EscapePressed += (_, _) => _ = AbortRecordingAsync();
        _hotkeyWindow.CreateControl();
        _settingsForm = new SettingsForm(_settings, _audioInputDevices, _chromeProfileDiscovery, ApplySettingsAsync);
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
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profil, Mikrofon & Hotkey einstellen", null, (_, _) => OpenSettings()));
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
            if (_status == AppStatus.Starting)
            {
                _queuedStopRequested = true;
                _logger.Info("Toggle hotkey queued a stop while dictation is starting.");
                _ = StopAfterQueuedRequestAsync();
                return;
            }

            _logger.Info($"Toggle hotkey ignored while an operation is already running. Status={_status}");
            ShowMessage("Das aktuelle Diktat wird noch verarbeitet.");
            return;
        }

        try
        {
            if (_status == AppStatus.Idle)
            {
                _queuedStopRequested = false;
                await BeginWebDictationAsync();
            }
            else if (_status == AppStatus.Recording)
            {
                _queuedStopRequested = false;
                await FinishWebDictationAsync();
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void OnToggleReleased(object? sender, HotkeyReleasedEventArgs e)
    {
        if (!HybridPushToTalkPolicy.ShouldStopOnRelease(
                _settings.EnableHybridPushToTalk,
                e.HeldForMs,
                _settings.PushToTalkHoldThresholdMs))
        {
            return;
        }

        _queuedStopRequested = true;
        _logger.Info($"Push-to-talk release detected. HeldForMs={e.HeldForMs}");
        _ = StopAfterQueuedRequestAsync();
    }

    private async Task StopAfterQueuedRequestAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            if (!_queuedStopRequested || _status != AppStatus.Recording)
            {
                _queuedStopRequested = false;
                return;
            }

            _queuedStopRequested = false;
            await FinishWebDictationAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task BeginWebDictationAsync()
    {
        var activeMicrophones = _audioInputDevices.GetActiveMicrophones();
        if (!activeMicrophones.Contains(_settings.PreferredMicrophoneName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Info("Recording blocked because the configured microphone is not active.");
            ShowMessage($"Das eingestellte Mikrofon „{_settings.PreferredMicrophoneName}“ ist nicht verbunden oder nicht aktiv.");
            return;
        }

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
            RestoreTargetFocus(target);
            if (!startResult.IsTerminationConfirmed &&
                startResult.ChatWindow != IntPtr.Zero &&
                NativeMethods.IsWindow(startResult.ChatWindow))
            {
                _session = new RecordingSession(
                    target,
                    startResult.ChatWindow,
                    startResult.Input);
                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
                ShowMessage("Die Aufnahme konnte nicht bestätigt oder sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

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
        void RestoreFocus() => RestoreTargetFocus(session.Target);

        var stopResult = await _dictationController.StopDictationAsync(
            session.ChatWindow,
            RestoreFocus);
        StopAudioDucking();
        ChatGptDictationReadResult? readResult = null;
        var terminationConfirmed = stopResult.IsTerminationConfirmed;
        var recoveryCompletedWithoutDestructiveCleanup =
            stopResult.IsTerminationConfirmed && stopResult.CanRecoverText;

        if (!stopResult.Ok)
        {
            SetStatus(AppStatus.ReadingText);
            try
            {
                if (stopResult.CanRecoverText)
                {
                    readResult = await _dictationController.ReadDictatedTextAsync(
                        session.ChatWindow,
                        restoreTargetFocus: RestoreFocus);
                    _logger.Info($"Dictation recovery read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={readResult.Text.Trim().Length} Stable={readResult.IsStable} ClipboardRestoreFailed={readResult.ClipboardRestoreFailed}");
                }
            }
            finally
            {
                if (stopResult.RequiresDeferredCleanup)
                {
                    var cleanup = await _dictationController.CompleteDeferredStopCleanupAsync(
                        session.ChatWindow);
                    terminationConfirmed = cleanup.IsTerminationConfirmed;
                    recoveryCompletedWithoutDestructiveCleanup =
                        cleanup == RecordingFailureCleanupResult.Recoverable;
                }
            }

            var recoveredText = readResult?.Text.Trim() ?? string.Empty;
            var hasSafeRecoveredText = recoveredText.Length > 0 &&
                                       !AutomationHelpers.IsUnsafeCapturedText(recoveredText);
            if (RecoveryTextPolicy.CanUseNormalPastePath(
                    recoveredText,
                    readResult?.IsStable == true,
                    recoveryCompletedWithoutDestructiveCleanup))
            {
                _logger.Info($"Stop transition recovered as a normal completion. TextLength={recoveredText.Length}");
                PasteCompletedDictation(
                    session,
                    recoveredText,
                    readResult!.ClipboardRestoreFailed);
                return;
            }

            RestoreTargetFocus(session.Target);
            if (!terminationConfirmed)
            {
                _history.Add(
                    hasSafeRecoveredText ? recoveredText : string.Empty,
                    hasSafeRecoveredText
                        ? DictationHistoryOutcomes.FailureRecovered
                        : DictationHistoryOutcomes.StopFailed);
                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
                ShowMessage(hasSafeRecoveredText
                    ? "Das Mikrofon konnte nicht sicher beendet werden. Der bisherige Text liegt im Verlauf. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen."
                    : "Das Mikrofon konnte nicht sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

            _history.Add(
                hasSafeRecoveredText ? recoveredText : string.Empty,
                hasSafeRecoveredText
                    ? DictationHistoryOutcomes.FailureRecovered
                    : DictationHistoryOutcomes.StopFailed);
            ShowMessage(readResult?.ClipboardRestoreFailed == true
                ? "Die Aufnahme wurde beendet, aber die vorherige Zwischenablage konnte bei der Textrettung nicht wiederhergestellt werden."
                : hasSafeRecoveredText
                    ? "Die Transkription wurde nicht sicher fertig. Der letzte Textstand liegt im Verlauf."
                    : stopResult.Message);
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.ReadingText);
        readResult = await _dictationController.ReadDictatedTextAsync(
            session.ChatWindow,
            session.ChatInput,
            restoreTargetFocus: RestoreFocus);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length} Stable={readResult.IsStable} ClipboardRestoreFailed={readResult.ClipboardRestoreFailed}");
        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text) || !readResult.IsStable)
        {
            var recoveredIncompleteText = text.Length > 0 && !AutomationHelpers.IsUnsafeCapturedText(text);
            _history.Add(
                recoveredIncompleteText ? text : string.Empty,
                recoveredIncompleteText
                    ? DictationHistoryOutcomes.FailureRecovered
                    : DictationHistoryOutcomes.TranscriptionFailed);
            RestoreTargetFocus(session.Target);
            ShowMessage(readResult.ClipboardRestoreFailed
                ? "Die Transkription wurde abgebrochen, weil die vorherige Zwischenablage nicht wiederhergestellt werden konnte. Ein letzter Textstand liegt gegebenenfalls im Verlauf oder Clipboard."
                : recoveredIncompleteText
                    ? "Die Transkription wurde nicht sicher fertig. Der letzte Textstand liegt im Verlauf."
                    : "Nach dem Stoppen wurde kein Text transkribiert.");
            ResetToIdle();
            return;
        }

        PasteCompletedDictation(session, text, readResult.ClipboardRestoreFailed);
    }

    private void PasteCompletedDictation(
        RecordingSession session,
        string text,
        bool readClipboardRestoreFailed)
    {
        SetStatus(AppStatus.Pasting);
        var historyEntryId = _history.Add(text, DictationHistoryOutcomes.Transcribed);
        var pasteResult = _pasteService.PasteIntoTarget(text, session.Target, _settings);
        _history.UpdateOutcome(
            historyEntryId,
            pasteResult.Succeeded ? DictationHistoryOutcomes.Pasted : DictationHistoryOutcomes.PasteFailed);
        _logger.Info($"Dictation paste completed. Success={pasteResult.Succeeded} TextLength={text.Length} ClipboardRestoreOutcome={pasteResult.ClipboardRestoreOutcome} TextIsOnClipboard={pasteResult.TextIsOnClipboard}");
        if (!pasteResult.Succeeded)
        {
            var copied = pasteResult.TextIsOnClipboard ||
                         (pasteResult.AllowClipboardFallback && ClipboardHelper.TrySetText(text, _logger));
            ShowMessage(pasteResult.ClipboardRestoreOutcome == ClipboardRestoreOutcome.Failed ||
                        readClipboardRestoreFailed
                ? copied
                    ? "Text konnte nicht eingefügt werden und die vorherige Zwischenablage nicht wiederhergestellt werden. Das Diktat liegt im Verlauf und aktuell auch im Clipboard."
                    : "Text konnte nicht eingefügt werden; außerdem ließ sich die vorherige Zwischenablage nicht wiederherstellen. Das Diktat liegt sicher im Verlauf."
                : copied
                    ? "Text konnte nicht eingefügt werden. Er liegt in der Zwischenablage und im Verlauf – Strg+V fügt ihn ein."
                    : "Text konnte nicht eingefügt werden. Er wurde im Diktierverlauf gesichert.");
        }
        else if (pasteResult.ClipboardRestoreOutcome == ClipboardRestoreOutcome.Failed ||
                 readClipboardRestoreFailed)
        {
            ShowMessage("Text wurde eingefügt, aber die vorherige Zwischenablage konnte nicht wiederhergestellt werden.");
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
            void RestoreFocus() => RestoreTargetFocus(session.Target);

            var stopResult = await _dictationController.StopDictationAsync(
                session.ChatWindow,
                RestoreFocus);
            StopAudioDucking();
            var textWasRecovered = false;
            var recoveryClipboardRestoreFailed = false;
            var terminationConfirmed = stopResult.IsTerminationConfirmed;
            try
            {
                if (stopResult.Ok)
                {
                    SetStatus(AppStatus.ReadingText);
                    var readResult = await _dictationController.ReadDictatedTextAsync(
                        session.ChatWindow,
                        session.ChatInput,
                        restoreTargetFocus: RestoreFocus);
                    recoveryClipboardRestoreFailed = readResult.ClipboardRestoreFailed;
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
                else if (stopResult.CanRecoverText)
                {
                    SetStatus(AppStatus.ReadingText);
                    var recovery = await TryRecoverTextToHistoryAsync(
                        session.ChatWindow,
                        DictationHistoryOutcomes.CancelledRecovered,
                        stopResult.RequiresDeferredCleanup ? 2500 : null,
                        RestoreFocus);
                    textWasRecovered = recovery.TextRecovered;
                    recoveryClipboardRestoreFailed = recovery.ClipboardRestoreFailed;
                    if (!textWasRecovered)
                    {
                        _history.Add(string.Empty, DictationHistoryOutcomes.CancelledWithoutText);
                    }
                }
                else
                {
                    _history.Add(string.Empty, DictationHistoryOutcomes.CancelledWithoutText);
                }
            }
            finally
            {
                if (stopResult.RequiresDeferredCleanup)
                {
                    var cleanup = await _dictationController.CompleteDeferredStopCleanupAsync(
                        session.ChatWindow);
                    terminationConfirmed = cleanup.IsTerminationConfirmed;
                }
            }

            RestoreTargetFocus(session.Target);
            if (!terminationConfirmed)
            {
                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
                ShowMessage("Das Mikrofon konnte nicht sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

            ShowMessage(recoveryClipboardRestoreFailed
                ? "Aufnahme abgebrochen, aber die vorherige Zwischenablage konnte bei der Textrettung nicht wiederhergestellt werden."
                : textWasRecovered
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

        ShowMessage($"ChatGPT wurde im Chrome-Profil {_settings.ChromeProfileDirectory} geöffnet.");
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

    private async Task<SettingsApplyResult> ApplySettingsAsync(string microphoneName, string hotkey, ChromeProfileInfo chromeProfile)
    {
        if (_status != AppStatus.Idle)
        {
            return SettingsApplyResult.Fail("Bitte zuerst die laufende Aufnahme beenden.");
        }

        var previousExecutablePath = _settings.ChromeExecutablePath;
        var previousUserDataDirectory = _settings.ChromeUserDataDir;
        var previousProfileDirectory = _settings.ChromeProfileDirectory;
        void RestorePreviousChromeProfile()
        {
            _settings.ChromeExecutablePath = previousExecutablePath;
            _settings.ChromeUserDataDir = previousUserDataDirectory;
            _settings.ChromeProfileDirectory = previousProfileDirectory;
        }

        _settings.ChromeExecutablePath = chromeProfile.ChromeExecutablePath;
        _settings.ChromeUserDataDir = chromeProfile.UserDataDirectory;
        _settings.ChromeProfileDirectory = chromeProfile.DirectoryName;
        var profileValidation = _chromeProfileLauncher.ValidateConfiguredProfile();
        if (!profileValidation.IsValid)
        {
            RestorePreviousChromeProfile();
            return SettingsApplyResult.Fail($"Das Chrome-Profil ist nicht verfügbar: {profileValidation.FailureReason}");
        }

        var activeMicrophones = _audioInputDevices.GetActiveMicrophones();
        if (!activeMicrophones.Contains(microphoneName, StringComparer.OrdinalIgnoreCase))
        {
            RestorePreviousChromeProfile();
            return SettingsApplyResult.Fail("Das ausgewählte Mikrofon ist nicht mehr verbunden.");
        }

        var microphoneResult = await _microphoneConfigurator.ApplyAsync(microphoneName);
        if (!microphoneResult.Ok)
        {
            RestorePreviousChromeProfile();
            return SettingsApplyResult.Fail(microphoneResult.Message);
        }

        var pageReset = await _dictationController.ResetChatGptPageAsync(_settingsForm.Handle);
        if (!pageReset.Ok)
        {
            RestorePreviousChromeProfile();
            return SettingsApplyResult.Fail("Mikrofon gespeichert, aber ChatGPT konnte nicht vorbereitet werden. Bitte ChatGPT Profil öffnen und Anmeldung prüfen.");
        }

        if (!_hotkeyWindow.TryUpdateToggleHotkey(hotkey, out var hotkeyFailure))
        {
            RestorePreviousChromeProfile();
            return SettingsApplyResult.Fail(hotkeyFailure);
        }

        _settings.PreferredMicrophoneName = microphoneName;
        _settings.ToggleHotkey = hotkey;
        _settings.SetupCompleted = true;
        _settings.Save(_logger);
        _recordingOverlay.SetToggleHotkey(hotkey);
        _logger.Info($"Settings applied from UI. ChromeProfileDirectory='{chromeProfile.DirectoryName}' MicrophoneName='{microphoneName}' ToggleHotkey='{hotkey}'.");
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

    private async Task<DictationRecoveryResult> TryRecoverTextToHistoryAsync(
        IntPtr chatWindow,
        string outcome,
        int? timeoutOverrideMs = null,
        Action? restoreTargetFocus = null)
    {
        var readResult = await _dictationController.ReadDictatedTextAsync(
            chatWindow,
            timeoutOverrideMs: timeoutOverrideMs,
            restoreTargetFocus: restoreTargetFocus);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation recovery read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length}");
        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text))
        {
            return new DictationRecoveryResult(false, readResult.ClipboardRestoreFailed);
        }

        _history.Add(text, outcome);
        return new DictationRecoveryResult(true, readResult.ClipboardRestoreFailed);
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

    private void RestoreTargetFocus(FocusTarget target)
    {
        _pasteService.RestoreTargetFocus(target);
    }

    private void ResetToIdle()
    {
        StopAudioDucking();
        _session = null;
        _queuedStopRequested = false;
        _hotkeyWindow.SetEscapeEnabled(false);
        SetStatus(AppStatus.Idle);
        _overlayTargetWindow = IntPtr.Zero;
    }

    private void StopAudioDucking()
    {
        _audioDuckingTimer.Stop();
        _audioDucking.Restore();
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
        ShowMessage($"Chrome-Profil {_settings.ChromeProfileDirectory} nicht gefunden. Bitte OpenAI Flow öffnen und ein Profil auswählen.");
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

    private async void Exit(object? sender, EventArgs e)
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        _hotkeyWindow.SetToggleEnabled(false);
        var terminationConfirmed = true;
        await _operationLock.WaitAsync();
        try
        {
            var session = _session;
            if (session is not null && _status != AppStatus.Idle)
            {
                SetStatus(AppStatus.Stopping);
                StopAudioDucking();
                terminationConfirmed = await _dictationController.TerminateRecordingForShutdownAsync(
                    session.ChatWindow);
                if (terminationConfirmed)
                {
                    _session = null;
                    _hotkeyWindow.SetEscapeEnabled(false);
                }
            }
        }
        catch (Exception ex)
        {
            terminationConfirmed = false;
            _logger.Error("Application exit recording cleanup failed.", ex);
        }
        finally
        {
            _operationLock.Release();
        }

        if (!terminationConfirmed)
        {
            _exitInProgress = false;
            _hotkeyWindow.SetToggleEnabled(true);
            _hotkeyWindow.SetEscapeEnabled(true);
            SetStatus(AppStatus.Recording);
            ShowMessage("OpenAI Flow bleibt geöffnet, weil das Mikrofon nicht sicher beendet werden konnte. Bitte F8 drücken oder das ChatGPT-Fenster schließen.");
            return;
        }

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

    private sealed record DictationRecoveryResult(
        bool TextRecovered,
        bool ClipboardRestoreFailed);
}
