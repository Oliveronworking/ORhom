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
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationTokenSource _startupCancellation = new();
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
    private Task _startupPreparationTask = Task.CompletedTask;
    private CancellationTokenSource? _dictationReadCancellation;

    public DictationTrayAppContext(
        AppSettings settings,
        AppLogger logger,
        ChromeProfileDiscovery chromeProfileDiscovery)
    {
        _logger = logger;
        _applicationIcon = LoadApplicationIcon();
        _settings = settings;
        _chromeProfileDiscovery = chromeProfileDiscovery;
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
            _startupCancellation.Cancel();
            _lifetimeCancellation.Cancel();
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
            if (_startupPreparationTask.IsCompleted)
            {
                _startupCancellation.Dispose();
                _lifetimeCancellation.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private async Task ToggleAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            if (_status is AppStatus.Stopping or AppStatus.ReadingText &&
                _dictationReadCancellation is not null)
            {
                _dictationReadCancellation.Cancel();
                _logger.Info($"Dictation processing cancellation requested. Status={_status}");
                ShowMessage("Die laufende Verarbeitung wird abgebrochen; der ChatGPT-Entwurf bleibt erhalten.");
                return;
            }

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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _dictationReadCancellation = cancellation;
        try
        {
            await FinishWebDictationCoreAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StopAudioDucking();
            var session = _session;
            if (session is not null)
            {
                RestoreTargetFocus(session.Target);
                var recordingInactive = await _dictationController.ConfirmRecordingInactiveAsync(
                    session.ChatWindow,
                    session.ChatInput);
                if (recordingInactive)
                {
                    ResetToIdle();
                }
                else
                {
                    _hotkeyWindow.SetEscapeEnabled(true);
                    SetStatus(AppStatus.Recording);
                }
            }
            else
            {
                ResetToIdle();
            }

            _logger.Info("Dictation stop/transcription processing was cancelled; the ChatGPT composer was preserved.");
            if (!_exitInProgress && !_lifetimeCancellation.IsCancellationRequested)
            {
                ShowMessage("Verarbeitung abgebrochen. Der ChatGPT-Entwurf bleibt erhalten; bitte F8 erneut drücken oder das ChatGPT-Profil öffnen.");
            }
        }
        finally
        {
            if (ReferenceEquals(_dictationReadCancellation, cancellation))
            {
                _dictationReadCancellation = null;
            }
        }
    }

    private async Task FinishWebDictationCoreAsync(CancellationToken cancellationToken)
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
            RestoreFocus,
            cancellationToken);
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
                        restoreTargetFocus: RestoreFocus,
                        cancellationToken: cancellationToken);
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
                await PasteCompletedDictationAsync(
                    session,
                    recoveredText,
                    readResult!.ClipboardRestoreFailed,
                    readResult.Input);
                return;
            }

            RestoreTargetFocus(session.Target);
            if (!terminationConfirmed)
            {
                var recoveredTextPersisted = hasSafeRecoveredText &&
                                             _history.TryAdd(
                                                 recoveredText,
                                                 DictationHistoryOutcomes.FailureRecovered,
                                                 out _);
                if (!hasSafeRecoveredText)
                {
                    _ = _history.TryAdd(
                        string.Empty,
                        DictationHistoryOutcomes.StopFailed,
                        out _);
                }

                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
                ShowMessage(recoveredTextPersisted
                    ? "Das Mikrofon konnte nicht sicher beendet werden. Der bisherige Text liegt im Verlauf. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen."
                    : hasSafeRecoveredText
                        ? "Das Mikrofon konnte nicht sicher beendet und der Text nicht im Verlauf gespeichert werden. Der ChatGPT-Entwurf bleibt erhalten. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen."
                        : "Das Mikrofon konnte nicht sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

            var recoveredPersistence = hasSafeRecoveredText
                ? await SaveRecoveredTextThenClearAsync(
                    session,
                    recoveredText,
                    readResult?.Input)
                : null;
            if (!hasSafeRecoveredText)
            {
                _ = _history.TryAdd(
                    string.Empty,
                    DictationHistoryOutcomes.StopFailed,
                    out _);
            }

            ShowMessage(recoveredPersistence is { Persisted: false }
                ? "Die Aufnahme wurde beendet, aber der letzte Textstand konnte nicht im Verlauf gespeichert werden. Der ChatGPT-Entwurf bleibt als Sicherung erhalten."
                : recoveredPersistence is { Persisted: true, Cleared: false }
                    ? "Der letzte Textstand liegt im Verlauf, aber der ChatGPT-Entwurf konnte nicht gelöscht werden. Bitte das ChatGPT-Profil öffnen."
                : readResult?.ClipboardRestoreFailed == true
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
            restoreTargetFocus: RestoreFocus,
            cancellationToken: cancellationToken);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length} Stable={readResult.IsStable} ClipboardRestoreFailed={readResult.ClipboardRestoreFailed}");
        if (!stopResult.IsTerminationConfirmed &&
            !await _dictationController.ConfirmRecordingInactiveAsync(
                session.ChatWindow,
                readResult.Input ?? session.ChatInput,
                cancellationToken))
        {
            var hasSafeText = text.Length > 0 && !AutomationHelpers.IsUnsafeCapturedText(text);
            var textPersisted = hasSafeText &&
                                _history.TryAdd(
                                    text,
                                    DictationHistoryOutcomes.FailureRecovered,
                                    out _);
            RestoreTargetFocus(session.Target);
            _hotkeyWindow.SetEscapeEnabled(true);
            SetStatus(AppStatus.Recording);
            ShowMessage(textPersisted
                ? "Der Stop-Befehl wurde gesendet, aber das Aufnahmeende nicht bestätigt. Der bisherige Text liegt im Verlauf. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen."
                : "Der Stop-Befehl wurde gesendet, aber das Aufnahmeende nicht bestätigt. Der ChatGPT-Entwurf bleibt erhalten. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
            return;
        }

        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text) || !readResult.IsStable)
        {
            var recoveredIncompleteText = text.Length > 0 && !AutomationHelpers.IsUnsafeCapturedText(text);
            var recoveredPersistence = recoveredIncompleteText
                ? await SaveRecoveredTextThenClearAsync(
                    session,
                    text,
                    readResult.Input)
                : null;
            if (!recoveredIncompleteText)
            {
                _ = _history.TryAdd(
                    string.Empty,
                    DictationHistoryOutcomes.TranscriptionFailed,
                    out _);
            }

            RestoreTargetFocus(session.Target);
            ShowMessage(recoveredPersistence is { Persisted: false }
                ? "Die Transkription wurde nicht sicher fertig und der letzte Textstand konnte nicht im Verlauf gespeichert werden. Der ChatGPT-Entwurf bleibt als Sicherung erhalten."
                : recoveredPersistence is { Persisted: true, Cleared: false }
                    ? "Der letzte Textstand liegt im Verlauf, aber der ChatGPT-Entwurf konnte nicht gelöscht werden. Bitte das ChatGPT-Profil öffnen."
                : readResult.ClipboardRestoreFailed
                ? "Die Transkription wurde abgebrochen, weil die vorherige Zwischenablage nicht wiederhergestellt werden konnte. Ein letzter Textstand liegt gegebenenfalls im Verlauf oder Clipboard."
                : recoveredIncompleteText
                    ? "Die Transkription wurde nicht sicher fertig. Der letzte Textstand liegt im Verlauf."
                    : "Nach dem Stoppen wurde kein Text transkribiert.");
            ResetToIdle();
            return;
        }

        await PasteCompletedDictationAsync(
            session,
            text,
            readResult.ClipboardRestoreFailed,
            readResult.Input);
    }

    private async Task PasteCompletedDictationAsync(
        RecordingSession session,
        string text,
        bool readClipboardRestoreFailed,
        AutomationElement? capturedInput)
    {
        SetStatus(AppStatus.Pasting);
        var historyPersisted = _history.TryAdd(
            text,
            DictationHistoryOutcomes.Transcribed,
            out var historyEntryId);
        var composerCleared = historyPersisted &&
                              await _dictationController.ClearPersistedDictationAsync(
                                  session.ChatWindow,
                                  capturedInput,
                                  () => RestoreTargetFocus(session.Target));
        var pasteResult = _pasteService.PasteIntoTarget(text, session.Target, _settings);
        if (historyPersisted)
        {
            _history.UpdateOutcome(
                historyEntryId,
                pasteResult.Succeeded ? DictationHistoryOutcomes.Pasted : DictationHistoryOutcomes.PasteFailed);
        }

        _logger.Info($"Dictation paste completed. Success={pasteResult.Succeeded} TextLength={text.Length} ClipboardRestoreOutcome={pasteResult.ClipboardRestoreOutcome} TextIsOnClipboard={pasteResult.TextIsOnClipboard}");
        if (!historyPersisted)
        {
            ShowMessage(pasteResult.Succeeded
                ? "Text wurde eingefügt, aber nicht im Verlauf gespeichert. Der ChatGPT-Entwurf bleibt als Sicherung erhalten."
                : "Text konnte weder im Verlauf gespeichert noch sicher eingefügt werden. Der ChatGPT-Entwurf bleibt als Sicherung erhalten.");
        }
        else if (!composerCleared)
        {
            ShowMessage(pasteResult.Succeeded
                ? "Text wurde eingefügt und im Verlauf gesichert, aber der ChatGPT-Entwurf konnte nicht gelöscht werden. Bitte das ChatGPT-Profil öffnen und den Entwurf löschen."
                : "Text liegt sicher im Verlauf, aber Einfügen und Löschen des ChatGPT-Entwurfs sind fehlgeschlagen. Bitte das ChatGPT-Profil öffnen.");
        }
        else if (!pasteResult.Succeeded)
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

    private async Task<RecoveryPersistenceResult> SaveRecoveredTextThenClearAsync(
        RecordingSession session,
        string text,
        AutomationElement? capturedInput)
    {
        var persisted = _history.TryAdd(
            text,
            DictationHistoryOutcomes.FailureRecovered,
            out _);
        if (!persisted)
        {
            return new RecoveryPersistenceResult(false, false);
        }

        var cleared = await _dictationController.ClearPersistedDictationAsync(
            session.ChatWindow,
            capturedInput,
            () => RestoreTargetFocus(session.Target));
        return new RecoveryPersistenceResult(true, cleared);
    }

    private async Task AbortRecordingAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            if (_status is AppStatus.Stopping or AppStatus.ReadingText &&
                _dictationReadCancellation is not null)
            {
                _dictationReadCancellation.Cancel();
                _logger.Info($"Dictation processing cancellation requested by Escape. Status={_status}");
            }

            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _dictationReadCancellation = cancellation;
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
                RestoreFocus,
                cancellation.Token);
            StopAudioDucking();
            var textWasRecovered = false;
            var recoveryClipboardRestoreFailed = false;
            var recoveryComposerCleanupFailed = false;
            var terminationConfirmed = stopResult.IsTerminationConfirmed;
            try
            {
                if (stopResult.Ok)
                {
                    SetStatus(AppStatus.ReadingText);
                    var readResult = await _dictationController.ReadDictatedTextAsync(
                        session.ChatWindow,
                        session.ChatInput,
                        restoreTargetFocus: RestoreFocus,
                        cancellationToken: cancellation.Token);
                    recoveryClipboardRestoreFailed = readResult.ClipboardRestoreFailed;
                    var text = readResult.Text.Trim();
                    if (!terminationConfirmed)
                    {
                        terminationConfirmed = await _dictationController.ConfirmRecordingInactiveAsync(
                            session.ChatWindow,
                            readResult.Input ?? session.ChatInput,
                            cancellation.Token);
                    }

                    if (text.Length > 0 && !AutomationHelpers.IsUnsafeCapturedText(text))
                    {
                        if (terminationConfirmed)
                        {
                            var persistence = await AbortRecoveryPersistence.SaveThenClearAsync(
                                text,
                                recoveredText => _history.TryAdd(
                                    recoveredText,
                                    DictationHistoryOutcomes.CancelledRecovered,
                                    out _),
                                () => _dictationController.ClearPersistedDictationAsync(
                                    session.ChatWindow,
                                    readResult.Input ?? session.ChatInput,
                                    RestoreFocus));
                            textWasRecovered = persistence.Persisted;
                            recoveryComposerCleanupFailed =
                                persistence.Persisted && !persistence.Cleared;
                        }
                        else
                        {
                            textWasRecovered = _history.TryAdd(
                                text,
                                DictationHistoryOutcomes.FailureRecovered,
                                out _);
                        }
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
                        session.ChatInput,
                        DictationHistoryOutcomes.CancelledRecovered,
                        stopResult.RequiresDeferredCleanup ? 2500 : null,
                        RestoreFocus,
                        clearComposerAfterPersistence: !stopResult.RequiresDeferredCleanup,
                        cancellationToken: cancellation.Token);
                    textWasRecovered = recovery.TextRecovered;
                    recoveryClipboardRestoreFailed = recovery.ClipboardRestoreFailed;
                    recoveryComposerCleanupFailed = recovery.ComposerCleanupFailed;
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

            if (stopResult.RequiresDeferredCleanup &&
                terminationConfirmed &&
                textWasRecovered)
            {
                recoveryComposerCleanupFailed =
                    ChatGptWindowFinder.IsOwnedBackgroundWindow(session.ChatWindow, _settings) &&
                    !await _dictationController.ClearPersistedDictationAsync(
                        session.ChatWindow,
                        session.ChatInput,
                        RestoreFocus);
            }

            RestoreTargetFocus(session.Target);
            if (!terminationConfirmed)
            {
                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
                ShowMessage("Das Mikrofon konnte nicht sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

            ShowMessage(recoveryComposerCleanupFailed
                ? "Aufnahme abgebrochen und Text im Verlauf gesichert, aber der ChatGPT-Entwurf konnte nicht gelöscht werden. Bitte das ChatGPT-Profil öffnen und den Entwurf löschen."
                : recoveryClipboardRestoreFailed
                ? "Aufnahme abgebrochen, aber die vorherige Zwischenablage konnte bei der Textrettung nicht wiederhergestellt werden."
                : textWasRecovered
                    ? "Aufnahme abgebrochen. Der gesprochene Text wurde im Verlauf gesichert."
                    : "Aufnahme abgebrochen. Es konnte kein Text gesichert werden.");
            ResetToIdle();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StopAudioDucking();
            var session = _session;
            if (session is not null)
            {
                RestoreTargetFocus(session.Target);
                var recordingInactive = await _dictationController.ConfirmRecordingInactiveAsync(
                    session.ChatWindow,
                    session.ChatInput);
                if (recordingInactive)
                {
                    ResetToIdle();
                }
                else
                {
                    _hotkeyWindow.SetEscapeEnabled(true);
                    SetStatus(AppStatus.Recording);
                }
            }

            _logger.Info("Explicit abort processing was cancelled; the ChatGPT composer was preserved.");
            if (!_exitInProgress && !_lifetimeCancellation.IsCancellationRequested)
            {
                ShowMessage("Abbruchverarbeitung beendet. Der ChatGPT-Entwurf bleibt erhalten; bitte F8 erneut drücken oder das ChatGPT-Profil öffnen.");
            }
        }
        finally
        {
            if (ReferenceEquals(_dictationReadCancellation, cancellation))
            {
                _dictationReadCancellation = null;
            }

            _operationLock.Release();
        }
    }

    private void QueueChatGptStartupPreparation()
    {
        if (!_settings.PrepareChatGptOnStartup || !_settings.LaunchChatGptIfMissing)
        {
            return;
        }

        _startupPreparationTask = Task.Run(
            () => PrepareChatGptOnStartupAsync(_startupCancellation.Token));
    }

    private async Task PrepareChatGptOnStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(800, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var foregroundBeforeLaunch = NativeMethods.GetForegroundWindow();
            var prepareResult = await _dictationController.PrepareBackgroundWindowAsync(
                IntPtr.Zero,
                cancellationToken);

            if (!prepareResult.Ok)
            {
                _logger.Info($"ChatGPT startup preparation failed. Failure={prepareResult.Failure}");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (foregroundBeforeLaunch != IntPtr.Zero && NativeMethods.IsWindow(foregroundBeforeLaunch))
            {
                NativeMethods.SetForegroundWindow(foregroundBeforeLaunch);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("ChatGPT startup preparation cancelled during application shutdown.");
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT startup preparation failed.", ex);
        }
    }

    private async Task OpenChatGptProfileAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            ShowMessage("OpenAI Flow verarbeitet gerade eine andere Aktion.");
            return;
        }

        try
        {
            var result = await _dictationController.OpenConfiguredProfileAsync();
            if (!result.Ok)
            {
                ShowMessage(result.Message);
                return;
            }

            ShowMessage($"ChatGPT wurde im Chrome-Profil {_settings.ChromeProfileDirectory} geöffnet.");
        }
        finally
        {
            _operationLock.Release();
        }
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

        await _startupPreparationTask;
        if (_exitInProgress)
        {
            return SettingsApplyResult.Fail("OpenAI Flow wird gerade beendet.");
        }

        await _operationLock.WaitAsync();
        try
        {
            if (_exitInProgress || _status != AppStatus.Idle)
            {
                return SettingsApplyResult.Fail("OpenAI Flow wird gerade beendet oder verarbeitet noch eine Aufnahme.");
            }

            return await ApplySettingsCoreAsync(microphoneName, hotkey, chromeProfile);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<SettingsApplyResult> ApplySettingsCoreAsync(
        string microphoneName,
        string hotkey,
        ChromeProfileInfo chromeProfile)
    {

        var previousExecutablePath = _settings.ChromeExecutablePath;
        var previousUserDataDirectory = _settings.ChromeUserDataDir;
        var previousProfileDirectory = _settings.ChromeProfileDirectory;
        var previousProfileIdentity = ChromeProfileIdentity.Create(
            previousUserDataDirectory,
            previousProfileDirectory);
        var previousMicrophoneName = _settings.PreferredMicrophoneName;
        var previousHotkey = _settings.ToggleHotkey;
        var previousSetupCompleted = _settings.SetupCompleted;
        var profileChanged =
            !previousUserDataDirectory.Equals(chromeProfile.UserDataDirectory, StringComparison.OrdinalIgnoreCase) ||
            !previousProfileDirectory.Equals(chromeProfile.DirectoryName, StringComparison.OrdinalIgnoreCase);
        if (profileChanged &&
            !TryPreservePendingComposerBeforeClose(out var pendingComposerFailure))
        {
            var handedToUser = ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                previousProfileIdentity,
                _logger);
            return SettingsApplyResult.Fail(handedToUser
                ? $"{pendingComposerFailure} Das bisherige Hintergrundfenster wurde sichtbar zur manuellen Textrettung freigegeben. Bitte Speichern danach erneut wählen."
                : pendingComposerFailure);
        }

        void RestorePreviousChromeProfile()
        {
            _settings.ChromeExecutablePath = previousExecutablePath;
            _settings.ChromeUserDataDir = previousUserDataDirectory;
            _settings.ChromeProfileDirectory = previousProfileDirectory;
        }

        async Task<bool> TryRollbackSwitchedProfileAsync()
        {
            if (profileChanged)
            {
                if (!TryPreservePendingComposerBeforeClose(
                        out _,
                        out var preservedComposerText))
                {
                    if (!ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                            ChromeProfileIdentity.From(_settings),
                            _logger))
                    {
                        return false;
                    }

                    RestorePreviousChromeProfile();
                    return true;
                }

                if (!await _dictationController.ReleaseKnownBackgroundWindowAsync(
                        preservedComposerText) &&
                    !ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                        ChromeProfileIdentity.From(_settings),
                        _logger))
                {
                    return false;
                }
            }

            RestorePreviousChromeProfile();
            return true;
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

        var pageReset = await _dictationController.ResetChatGptPageAsync(
            _settingsForm.Handle,
            forceNewProfileWindow: profileChanged,
            previousProfileIdentity: previousProfileIdentity);
        if (!pageReset.Ok)
        {
            if (!await TryRollbackSwitchedProfileAsync())
            {
                return SettingsApplyResult.Fail(
                    "ChatGPT konnte nicht vorbereitet und das neue Chrome-Profil nicht sicher zurückgerollt werden. Bitte OpenAI Flow neu starten.");
            }

            return SettingsApplyResult.Fail("Mikrofon gespeichert, aber ChatGPT konnte nicht vorbereitet werden. Bitte ChatGPT Profil öffnen und Anmeldung prüfen.");
        }

        if (!_hotkeyWindow.TryUpdateToggleHotkey(hotkey, out var hotkeyFailure))
        {
            if (!await TryRollbackSwitchedProfileAsync())
            {
                return SettingsApplyResult.Fail(
                    $"{hotkeyFailure} Das neue Chrome-Profil konnte nicht sicher zurückgerollt werden; bitte OpenAI Flow neu starten.");
            }

            return SettingsApplyResult.Fail(hotkeyFailure);
        }

        _settings.PreferredMicrophoneName = microphoneName;
        _settings.ToggleHotkey = hotkey;
        _settings.SetupCompleted = true;
        if (!_settings.Save(_logger))
        {
            _settings.PreferredMicrophoneName = previousMicrophoneName;
            _settings.ToggleHotkey = previousHotkey;
            _settings.SetupCompleted = previousSetupCompleted;
            if (!_hotkeyWindow.TryUpdateToggleHotkey(previousHotkey, out var rollbackFailure))
            {
                _logger.Info($"Hotkey rollback failed after settings persistence error. Reason={rollbackFailure}");
            }

            if (!await TryRollbackSwitchedProfileAsync())
            {
                return SettingsApplyResult.Fail(
                    "Die Einstellungen konnten nicht gespeichert und das neue Chrome-Profil nicht sicher zurückgerollt werden. Bitte OpenAI Flow neu starten.");
            }

            return SettingsApplyResult.Fail("Die Einstellungen konnten nicht sicher gespeichert werden. Bitte Schreibrechte und freien Speicherplatz prüfen.");
        }
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
        AutomationElement? preferredInput,
        string outcome,
        int? timeoutOverrideMs = null,
        Action? restoreTargetFocus = null,
        bool clearComposerAfterPersistence = true,
        CancellationToken cancellationToken = default)
    {
        var readResult = await _dictationController.ReadDictatedTextAsync(
            chatWindow,
            preferredInput,
            timeoutOverrideMs: timeoutOverrideMs,
            restoreTargetFocus: restoreTargetFocus,
            cancellationToken: cancellationToken);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation recovery read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length}");
        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text))
        {
            return new DictationRecoveryResult(
                false,
                readResult.ClipboardRestoreFailed,
                false);
        }

        var persisted = _history.TryAdd(text, outcome, out _);
        var cleared = persisted &&
                      (!clearComposerAfterPersistence ||
                       await _dictationController.ClearPersistedDictationAsync(
                           chatWindow,
                           readResult.Input ?? preferredInput,
                           restoreTargetFocus));
        return new DictationRecoveryResult(
            persisted,
            readResult.ClipboardRestoreFailed,
            persisted && clearComposerAfterPersistence && !cleared);
    }

    private async Task WriteChatGptDiagnosticsAsync()
    {
        if (!_settings.EnableChatGptInputDiagnostics)
        {
            ShowMessage("ChatGPT-Diagnose ist in den Einstellungen deaktiviert.");
            return;
        }

        if (!await _operationLock.WaitAsync(0))
        {
            ShowMessage("OpenAI Flow verarbeitet gerade eine andere Aktion.");
            return;
        }

        try
        {
            var window = _session?.ChatWindow ?? _dictationController.KnownChatWindow;
            await _dictationController.DiagnoseChatGptUiAsync(window);
            ShowMessage("ChatGPT Diagnose wurde ins Log geschrieben.");
        }
        finally
        {
            _operationLock.Release();
        }
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

    private bool TryPreservePendingComposerBeforeClose(out string failureMessage) =>
        TryPreservePendingComposerBeforeClose(
            out failureMessage,
            out _);

    private bool TryPreservePendingComposerBeforeClose(
        out string failureMessage,
        out string? preservedComposerText)
    {
        var inspection = _dictationController.InspectPendingComposer();
        var preservation = PendingComposerPreservation.Preserve(
            inspection,
            _history.ContainsText,
            text => _history.TryAdd(
                text,
                DictationHistoryOutcomes.PreservedBeforeClose,
                out _));
        if (PendingComposerPreservation.IsSafeToClose(preservation))
        {
            preservedComposerText = inspection.State == PendingComposerState.Text
                ? inspection.Text
                : null;
            if (inspection.State == PendingComposerState.Text)
            {
                _logger.Info($"Pending composer text preserved before owned-window close. TextLength={inspection.Text.Length} Outcome={preservation}");
            }

            failureMessage = string.Empty;
            return true;
        }

        if (preservation == PendingComposerPreservationOutcome.PersistenceFailed)
        {
            preservedComposerText = null;
            failureMessage = "Im ChatGPT-Entwurf liegt noch Text, der nicht im Diktierverlauf gespeichert werden konnte. Das Profilfenster bleibt zum Schutz des Textes geöffnet.";
            return false;
        }

        preservedComposerText = null;
        failureMessage = "Der ChatGPT-Entwurf konnte nicht sicher geprüft werden. Das profilgebundene Hintergrundfenster bleibt zum Schutz möglicher Texte geöffnet; bitte den Vorgang erneut versuchen.";
        return false;
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
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
        _dictationReadCancellation?.Cancel();
        _startupCancellation.Cancel();
        await _startupPreparationTask;
        var exitAllowed = true;
        var exitFailureMessage = string.Empty;
        await _operationLock.WaitAsync();
        try
        {
            var session = _session;
            if (session is not null && _status != AppStatus.Idle)
            {
                exitAllowed = false;
                exitFailureMessage = "OpenAI Flow bleibt geöffnet, solange eine Aufnahme noch aktiv oder nicht sicher abgeschlossen ist. Bitte zuerst F8 oder Escape drücken.";
            }
            else if (!TryPreservePendingComposerBeforeClose(out exitFailureMessage))
            {
                exitAllowed = false;
                if (ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                        ChromeProfileIdentity.From(_settings),
                        _logger))
                {
                    exitFailureMessage += " Das Hintergrundfenster wurde sichtbar zur manuellen Textrettung freigegeben; wählen Sie danach erneut Beenden.";
                }
            }
            else
            {
                var profileIdentity = ChromeProfileIdentity.From(_settings);
                var backgroundWindowWasPresent =
                    ChatGptWindowFinder.FindOwnedBackgroundWindow(_settings) != IntPtr.Zero;
                var backgroundWindowReleased = ChatGptWindowFinder.CloseOwnedBackgroundWindowAndWait(
                    profileIdentity,
                    _logger);
                if (!backgroundWindowReleased)
                {
                    backgroundWindowReleased = ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                        profileIdentity,
                        _logger);
                }

                _logger.Info(!backgroundWindowWasPresent
                    ? "Application exiting; no profile-scoped background window needed closing."
                    : backgroundWindowReleased
                        ? "Application exiting; profile-scoped background ownership was safely released."
                        : "Application exit paused; profile-scoped background window closure was not confirmed.");
                if (!backgroundWindowReleased)
                {
                    exitAllowed = false;
                    exitFailureMessage = "OpenAI Flow bleibt geöffnet, weil das eigene ChatGPT-Hintergrundfenster noch nicht sicher geschlossen wurde. Bitte Beenden erneut versuchen.";
                }
            }
        }
        catch (Exception ex)
        {
            exitAllowed = false;
            exitFailureMessage = "OpenAI Flow konnte nicht sicher beendet werden. Bitte den Vorgang erneut versuchen.";
            _logger.Error("Application exit safety checks failed.", ex);
        }
        finally
        {
            _operationLock.Release();
        }

        if (!exitAllowed)
        {
            _exitInProgress = false;
            _hotkeyWindow.SetToggleEnabled(true);
            _hotkeyWindow.SetEscapeEnabled(_status == AppStatus.Recording);
            ShowMessage(exitFailureMessage);
            return;
        }

        _lifetimeCancellation.Cancel();
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
        bool ClipboardRestoreFailed,
        bool ComposerCleanupFailed);
}

internal static class AbortRecoveryPersistence
{
    public static async Task<RecoveryPersistenceResult> SaveThenClearAsync(
        string text,
        Func<string, bool> saveSynchronously,
        Func<Task<bool>> clearPersistedAsync)
    {
        if (!saveSynchronously(text))
        {
            return new RecoveryPersistenceResult(false, false);
        }

        return new RecoveryPersistenceResult(true, await clearPersistedAsync());
    }
}

internal sealed record RecoveryPersistenceResult(bool Persisted, bool Cleared);
