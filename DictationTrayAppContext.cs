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
    private readonly LocalAudioCaptureService _localAudioCapture;
    private readonly LocalWhisperRecognitionService _localWhisper;
    private readonly AudioDuckingService _audioDucking;
    private readonly System.Windows.Forms.Timer _audioDuckingTimer;
    private readonly RecordingOverlayForm _recordingOverlay;
    private readonly SettingsForm _settingsForm;
    private readonly DictationHistoryStore _history;
    private readonly DictationHistoryForm _historyForm;
    private AppStatus _status = AppStatus.Idle;
    private RecordingSession? _session;
    private LocalRecordingSession? _localSession;
    private bool _queuedStopRequested;
    private bool _exitInProgress;
    private Task _startupPreparationTask = Task.CompletedTask;
    private CancellationTokenSource? _localPreparationCancellation;
    private Task _localPreparationTask = Task.CompletedTask;
    private long _localPreparationGeneration;
    private string? _localPreparationFailureMessage;
    private CancellationTokenSource? _dictationReadCancellation;
    private int _disposeState;

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
        _localAudioCapture = new LocalAudioCaptureService(_logger);
        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAIFlow",
            "models",
            "whisper.cpp");
        _localWhisper = new LocalWhisperRecognitionService(
            new LocalWhisperModelManager(modelDirectory),
            _logger);
        _audioDucking = new AudioDuckingService(_settings, _logger);
        _audioDuckingTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _audioDuckingTimer.Tick += (_, _) => _audioDucking.Refresh();
        var overlayPlacement =
            _settings.RecordingOverlayRelativeX is { } relativeX &&
            _settings.RecordingOverlayRelativeY is { } relativeY
                ? new RecordingOverlayPlacement(
                    _settings.RecordingOverlayMonitorDeviceName,
                    relativeX,
                    relativeY)
                : null;
        _recordingOverlay = new RecordingOverlayForm(
            _settings.RecordingOverlayBottomOffsetPx,
            _settings.ToggleHotkey,
            overlayPlacement);
        _focusTracker = new FocusTracker(_logger);
        _pasteService = new PasteService(_logger);
        var historyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAIFlow");
        _history = new DictationHistoryStore(Path.Combine(historyDirectory, "dictation-history.json"), _logger);
        _historyForm = new DictationHistoryForm(_history) { Icon = _applicationIcon };
        _logger.Info("Application started.");
        var initialProfileValidation = DictationProviders.IsLocal(_settings.DictationProvider)
            ? new ChromeProfileValidationResult(true, string.Empty, string.Empty)
            : _chromeProfileLauncher.ValidateConfiguredProfile();

        _hotkeyWindow = new HotkeyWindow(_settings, _logger);
        _hotkeyWindow.TogglePressed += (_, _) =>
            RunUserOperation(ToggleAsync, "toggle-hotkey");
        _hotkeyWindow.ToggleReleased += OnToggleReleased;
        _hotkeyWindow.EscapePressed += (_, _) =>
            RunUserOperation(AbortRecordingAsync, "escape-abort");
        _hotkeyWindow.CreateControl();
        _recordingOverlay.ToggleRequested += (_, _) =>
            RunUserOperation(ToggleAsync, "overlay-toggle");
        _recordingOverlay.PlacementCommitted += OnOverlayPlacementCommitted;
        _settingsForm = new SettingsForm(_settings, _audioInputDevices, _chromeProfileDiscovery, ApplySettingsAsync);
        _settingsForm.Icon = _applicationIcon;
        _settingsForm.VisibleChanged += (_, _) =>
        {
            var interactionEnabled = !_settingsForm.Visible && !_exitInProgress;
            _hotkeyWindow.SetToggleEnabled(interactionEnabled);
            _recordingOverlay.SetInteractionEnabled(interactionEnabled);
        };

        _statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Aufnahme abbrechen", null, (_, _) =>
            RunUserOperation(AbortRecordingAsync, "tray-abort")));
        menu.Items.Add(new ToolStripMenuItem("Diktierverlauf (letzte 10)", null, (_, _) => OpenHistory()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("ChatGPT Profil öffnen", null, (_, _) =>
            RunUserOperation(OpenChatGptProfileAsync, "open-chatgpt-profile"))
        {
            Enabled = _settings.OpenChatGptProfileVisibleForSetup
        });
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profil, Mikrofon & Hotkey einstellen", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profil prüfen", null, (_, _) => CheckChromeProfile()));
        menu.Items.Add(new ToolStripMenuItem("ChatGPT Diagnose speichern", null, (_, _) =>
            RunUserOperation(WriteChatGptDiagnosticsAsync, "write-diagnostics"))
        {
            Enabled = _settings.EnableChatGptInputDiagnostics
        });
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profilordner öffnen", null, (_, _) => OpenChromeProfileDirectory()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("OpenAI Flow öffnen", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Konfigurationsdatei öffnen", null, (_, _) => OpenPath(_settings.SettingsPath)));
        menu.Items.Add(new ToolStripMenuItem("Logs öffnen", null, (_, _) => OpenPath(_logger.LogPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) =>
            RunUserOperation(ExitAsync, "application-exit")));
        _recordingOverlay.ContextMenuStrip = menu;

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Visible = true,
            Text = "OpenAI Flow Dictation - Idle",
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        if (_settings.ShowRecordingOverlay)
        {
            _recordingOverlay.ShowStatus(AppStatus.Idle);
        }

        if (DictationProviders.IsLocal(_settings.DictationProvider))
        {
            QueueLocalWhisperPreparation();
        }
        else if (!initialProfileValidation.IsValid)
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
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _startupCancellation.Cancel();
        _localPreparationCancellation?.Cancel();
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
        _localSession?.Capture.Dispose();
        _dictationController.Dispose();
        _applicationIcon.Dispose();
        var preparationTasks = Task.WhenAll(
            _startupPreparationTask,
            _localPreparationTask);
        if (preparationTasks.IsCompleted)
        {
            _localWhisper.Dispose();
            _localPreparationCancellation?.Dispose();
            _startupCancellation.Dispose();
            _lifetimeCancellation.Dispose();
        }
        else
        {
            var localPreparationCancellation = _localPreparationCancellation;
            _ = preparationTasks.ContinueWith(
                _ =>
                {
                    _localWhisper.Dispose();
                    localPreparationCancellation?.Dispose();
                    _startupCancellation.Dispose();
                    _lifetimeCancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        base.Dispose(disposing);
    }

    private async void RunUserOperation(
        Func<Task> operation,
        string operationName)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (
            _lifetimeCancellation.IsCancellationRequested ||
            _startupCancellation.IsCancellationRequested)
        {
            _logger.Info($"User operation cancelled during shutdown. Operation={operationName}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Unhandled user operation failure. Operation={operationName}", ex);
            if (_lifetimeCancellation.IsCancellationRequested || _recordingOverlay.IsDisposed)
            {
                return;
            }

            _exitInProgress = false;
            _recordingOverlay.SetInteractionEnabled(!_settingsForm.Visible);
            _hotkeyWindow.SetToggleEnabled(!_settingsForm.Visible);
            StopAudioDucking();
            var recordingMayStillBeActive =
                UnexpectedFailureStatePolicy.RecordingMayStillBeActive(
                    _status,
                    _session is not null || _localSession is not null);
            if (!recordingMayStillBeActive)
            {
                ResetToIdle();
            }
            else
            {
                _queuedStopRequested = false;
                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
            }

            ShowErrorMessage("OpenAI Flow hat einen internen Fehler abgefangen. Es läuft weiter; bitte erneut versuchen oder das Log prüfen.");
        }
    }

    private async Task ToggleAsync()
    {
        if (_exitInProgress || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (!await _operationLock.WaitAsync(0))
        {
            if (_status is AppStatus.Stopping or AppStatus.ReadingText &&
                _dictationReadCancellation is not null)
            {
                _dictationReadCancellation.Cancel();
                _logger.Info($"Dictation processing cancellation requested. Status={_status}");
                ShowMessage(DictationProviders.IsLocal(_settings.DictationProvider)
                    ? "Die lokale Verarbeitung wird abgebrochen; die Aufnahme wird verworfen."
                    : "Die laufende Verarbeitung wird abgebrochen; der ChatGPT-Entwurf bleibt erhalten.");
                return;
            }

            if (_status == AppStatus.Starting)
            {
                _queuedStopRequested = true;
                _logger.Info("Toggle hotkey queued a stop while dictation is starting.");
                RunUserOperation(StopAfterQueuedRequestAsync, "queued-stop");
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
                if (DictationProviders.IsLocal(_settings.DictationProvider))
                {
                    await BeginLocalDictationAsync();
                }
                else
                {
                    await BeginWebDictationAsync();
                }
            }
            else if (_status == AppStatus.Recording)
            {
                _queuedStopRequested = false;
                await FinishActiveDictationAsync();
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
        RunUserOperation(StopAfterQueuedRequestAsync, "push-to-talk-stop");
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
            await FinishActiveDictationAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private Task FinishActiveDictationAsync() =>
        _localSession is not null
            ? FinishLocalDictationAsync()
            : FinishWebDictationAsync();

    private async Task BeginLocalDictationAsync()
    {
        if (!_audioInputDevices.IsMicrophoneActive(
                _settings.PreferredMicrophoneId,
                _settings.PreferredMicrophoneName))
        {
            ShowErrorMessage(
                $"Das eingestellte Mikrofon „{_settings.PreferredMicrophoneName}“ ist nicht verbunden oder nicht aktiv.");
            return;
        }

        var target = _focusTracker.Capture(_settings);
        if (target.IsPasswordField)
        {
            ShowErrorMessage("Ziel ist ein Passwortfeld. Aufnahme wurde nicht gestartet.");
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Starting);
        try
        {
            QueueLocalWhisperPreparation();
            await _localPreparationTask;
            if (_exitInProgress || _lifetimeCancellation.IsCancellationRequested)
            {
                ResetToIdle();
                return;
            }

            if (_queuedStopRequested)
            {
                _logger.Info("Queued local stop consumed before microphone start after model preparation.");
                _pasteService.RestoreTargetWindow(target);
                ResetToIdle();
                ShowMessage("Die lokale Aufnahme wurde vor dem Mikrofonstart abgebrochen; das Sprachmodell bleibt für den nächsten Versuch bereit.");
                return;
            }

            if (!_localWhisper.IsReady)
            {
                throw new LocalWhisperRecognitionException(
                    _localPreparationFailureMessage ??
                    "Das lokale deutsche Sprachmodell konnte nicht vorbereitet werden. Bitte erneut versuchen oder den ChatGPT-Browser-Fallback auswählen.");
            }

            var capture = _localAudioCapture.Start(
                _settings.PreferredMicrophoneId,
                _settings.PreferredMicrophoneName,
                TimeSpan.FromSeconds(_settings.LocalMaxRecordingSeconds),
                OnLocalCaptureUnexpectedlyStopped);
            capture.RecordingLimitReached += OnLocalRecordingLimitReached;
            _localSession = new LocalRecordingSession(target, capture);
            _audioDucking.Begin();
            _audioDuckingTimer.Start();
            _hotkeyWindow.SetEscapeEnabled(true);
            SetStatus(AppStatus.Recording);
            _logger.Info($"Local recording session started. TargetClass='{target.WindowClass}' DeviceId='{capture.DeviceId}'.");
            if (_settings.RestoreTargetAfterStart)
            {
                RestoreTargetFocus(target);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            ResetToIdle();
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Local recording start failed.", ex);
            _pasteService.RestoreTargetWindow(target);
            ShowErrorMessage(GetLocalWhisperErrorMessage(ex));
            ResetToIdle();
        }
    }

    private void OnLocalRecordingLimitReached(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (_localSession?.Capture != sender || _status != AppStatus.Recording)
            {
                return;
            }

            _queuedStopRequested = true;
            ShowMessage(
                $"Die maximale lokale Aufnahmedauer von {_settings.LocalMaxRecordingSeconds / 60} Minuten ist erreicht. Das Diktat wird jetzt transkribiert.");
            RunUserOperation(StopAfterQueuedRequestAsync, "local-duration-limit");
        });
    }

    private void OnLocalCaptureUnexpectedlyStopped(
        object? sender,
        LocalAudioCaptureUnexpectedlyStoppedEventArgs e)
    {
        if (sender is not LocalAudioCaptureSession capture)
        {
            return;
        }

        RunOnUiThread(() => RunUserOperation(
            () => HandleLocalCaptureUnexpectedlyStoppedAsync(capture, e.Error),
            "local-capture-unexpected-stop"));
    }

    private async Task HandleLocalCaptureUnexpectedlyStoppedAsync(
        LocalAudioCaptureSession capture,
        Exception? error)
    {
        await _operationLock.WaitAsync();
        try
        {
            var session = _localSession;
            if (_exitInProgress ||
                session?.Capture != capture ||
                _status != AppStatus.Recording)
            {
                return;
            }

            if (error is null)
            {
                _logger.Error(
                    "Local microphone capture stopped unexpectedly.",
                    new LocalAudioCaptureException(
                        "Windows hat den Audiostream ohne weitere Fehlerangabe beendet."));
            }
            else
            {
                _logger.Error("Local microphone capture stopped unexpectedly.", error);
            }

            StopAudioDucking();
            RestoreTargetFocus(session.Target);
            ResetToIdle();
            ShowErrorMessage(
                "Die lokale Mikrofonaufnahme wurde unerwartet beendet. " +
                "Bitte prüfen, ob das Mikrofon noch verbunden und in Windows aktiv ist, und versuchen Sie es erneut.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task FinishLocalDictationAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _dictationReadCancellation = cancellation;
        try
        {
            await FinishLocalDictationCoreAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StopAudioDucking();
            var session = _localSession;
            if (session is not null)
            {
                await StopLocalCaptureSafelyAsync(session.Capture);
                RestoreTargetFocus(session.Target);
            }

            ResetToIdle();
            if (!_exitInProgress && !_lifetimeCancellation.IsCancellationRequested)
            {
                ShowMessage("Lokale Verarbeitung abgebrochen; die Aufnahme wurde verworfen.");
            }
        }
        catch (Exception ex)
        {
            StopAudioDucking();
            var session = _localSession;
            if (session is not null)
            {
                await StopLocalCaptureSafelyAsync(session.Capture);
                RestoreTargetFocus(session.Target);
            }

            _logger.Error("Local stop/transcription failed.", ex);
            ShowErrorMessage(GetLocalWhisperErrorMessage(ex));
            ResetToIdle();
        }
        finally
        {
            if (ReferenceEquals(_dictationReadCancellation, cancellation))
            {
                _dictationReadCancellation = null;
            }
        }
    }

    private async Task FinishLocalDictationCoreAsync(CancellationToken cancellationToken)
    {
        var session = _localSession;
        if (session is null)
        {
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Stopping);
        if (session.Capture.IsRecording)
        {
            await Task.Delay(
                Math.Clamp(_settings.DictationStopGracePeriodMs, 0, 1000),
                cancellationToken);
        }

        var audio = await session.Capture.StopAndGetSamplesAsync(cancellationToken);
        StopAudioDucking();
        SetStatus(AppStatus.ReadingText);
        var text = await _localWhisper.TranscribeGermanAsync(
            audio.Samples,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(text) ||
            AutomationHelpers.IsUnsafeCapturedText(text))
        {
            _history.TryAdd(
                string.Empty,
                DictationHistoryOutcomes.TranscriptionFailed,
                out _);
            RestoreTargetFocus(session.Target);
            ShowErrorMessage(
                "Es wurde kein sicherer deutscher Text erkannt. Bitte näher am Mikrofon sprechen und erneut versuchen.");
            ResetToIdle();
            return;
        }

        await PasteLocalCompletedDictationAsync(session.Target, text);
    }

    private Task PasteLocalCompletedDictationAsync(FocusTarget target, string text)
    {
        SetStatus(AppStatus.Pasting);
        var historyPersisted = _history.TryAdd(
            text,
            DictationHistoryOutcomes.Transcribed,
            out var historyEntryId);
        var pasteResult = _pasteService.PasteIntoTarget(text, target, _settings);
        if (historyPersisted)
        {
            _history.UpdateOutcome(
                historyEntryId,
                pasteResult.Succeeded
                    ? DictationHistoryOutcomes.Pasted
                    : DictationHistoryOutcomes.PasteFailed);
        }

        var copied = !pasteResult.Succeeded &&
                     (pasteResult.TextIsOnClipboard ||
                      (pasteResult.ShouldAttemptClipboardFallback &&
                       ClipboardHelper.TrySetText(text, _logger)));
        _logger.Info($"Local dictation paste completed. Success={pasteResult.Succeeded} TextLength={text.Length} ClipboardRestoreOutcome={pasteResult.ClipboardRestoreOutcome}.");
        if (!historyPersisted)
        {
            ShowMessage(pasteResult.Succeeded
                ? "Text wurde eingefügt, konnte aber nicht im Diktierverlauf gesichert werden."
                : copied
                    ? "Text konnte weder eingefügt noch im Diktierverlauf gesichert werden. Er liegt als Rettung in der Zwischenablage."
                    : "Text konnte weder eingefügt noch sicher im Diktierverlauf oder in der Zwischenablage gesichert werden.");
        }
        else if (!pasteResult.Succeeded)
        {
            ShowMessage(copied
                ? "Text konnte nicht eingefügt werden. Er liegt im Clipboard und im Diktierverlauf."
                : "Text konnte nicht eingefügt werden, wurde aber im Diktierverlauf gesichert.");
        }
        else if (pasteResult.ClipboardRestoreOutcome == ClipboardRestoreOutcome.Failed)
        {
            ShowMessage(
                "Text wurde eingefügt, aber die vorherige Zwischenablage konnte nicht wiederhergestellt werden.");
        }

        ResetToIdle();
        return Task.CompletedTask;
    }

    private async Task BeginWebDictationAsync()
    {
        if (!_audioInputDevices.IsMicrophoneActive(
                _settings.PreferredMicrophoneId,
                _settings.PreferredMicrophoneName))
        {
            _logger.Info("Recording blocked because the configured microphone is not active.");
            ShowErrorMessage($"Das eingestellte Mikrofon „{_settings.PreferredMicrophoneName}“ ist nicht verbunden oder nicht aktiv.");
            return;
        }

        var target = _focusTracker.Capture(_settings);
        if (target.IsPasswordField)
        {
            _logger.Info("Recording blocked because target is a password field.");
            ShowErrorMessage("Ziel ist ein Passwortfeld. Aufnahme wurde nicht gestartet.");
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Starting);
        var startResult = await _dictationController.StartDictationAsync(target.WindowHandle);
        var initialStartResult = startResult;
        startResult = await PendingComposerStartRecovery.RetryKnownPersistedTextOnceAsync(
            startResult,
            result => TryClearKnownPendingComposerAsync(result, target),
            () => _dictationController.StartDictationAsync(target.WindowHandle));
        if (initialStartResult.Failure == ChatGptFailure.PendingText &&
            !ReferenceEquals(initialStartResult, startResult))
        {
            _logger.Info($"Known persisted composer text was cleared before a one-time start retry. RetrySuccess={startResult.Ok} RetryFailure={startResult.Failure}");
        }

        if (!startResult.Ok)
        {
            // No confirmed recording needs element-level focus restoration in a
            // failed start path. Window-only restoration avoids stale UIA calls.
            _pasteService.RestoreTargetWindow(target);
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
                ShowErrorMessage("Die Aufnahme konnte nicht bestätigt oder sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

            ShowErrorMessage(startResult.Message);
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

    private async Task<bool> TryClearKnownPendingComposerAsync(
        ChatGptStartResult result,
        FocusTarget target)
    {
        var expectedText = result.PendingText.Trim();
        if (!_history.CanConsumeRecentRecoverableText(expectedText))
        {
            return false;
        }

        if (!await _dictationController.ConfirmRecordingInactiveAsync(
                result.ChatWindow,
                result.Input))
        {
            _logger.Info("Known pending composer cleanup skipped because inactive recording state was not confirmed.");
            return false;
        }

        if (!_history.TryConsumeRecentRecoverableText(expectedText))
        {
            return false;
        }

        return await _dictationController.ClearPersistedDictationAsync(
            result.ChatWindow,
            result.Input,
            () => _pasteService.RestoreTargetWindow(target),
            expectedText);
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
                                  () => RestoreTargetFocus(session.Target),
                                  text);
        var pasteResult = _pasteService.PasteIntoTarget(text, session.Target, _settings);
        if (historyPersisted)
        {
            _history.UpdateOutcome(
                historyEntryId,
                !composerCleared
                    ? DictationHistoryOutcomes.PendingComposerCleanup
                    : pasteResult.Succeeded
                        ? DictationHistoryOutcomes.Pasted
                        : DictationHistoryOutcomes.PasteFailed);
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
            out var historyEntryId);
        if (!persisted)
        {
            return new RecoveryPersistenceResult(false, false);
        }

        var cleared = await _dictationController.ClearPersistedDictationAsync(
            session.ChatWindow,
            capturedInput,
            () => RestoreTargetFocus(session.Target),
            text);
        UpdateRecoveredCleanupOutcome(
            historyEntryId,
            DictationHistoryOutcomes.FailureRecovered,
            cleared);

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
            if (_localSession is not null)
            {
                await AbortLocalRecordingAsync(cancellation.Token);
                return;
            }

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
            DictationRecoveryResult? deferredRecovery = null;
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
                            var historyEntryId = Guid.Empty;
                            var persistence = await AbortRecoveryPersistence.SaveThenClearAsync(
                                text,
                                recoveredText => _history.TryAdd(
                                    recoveredText,
                                    DictationHistoryOutcomes.CancelledRecovered,
                                    out historyEntryId),
                                () => _dictationController.ClearPersistedDictationAsync(
                                    session.ChatWindow,
                                    readResult.Input ?? session.ChatInput,
                                    RestoreFocus,
                                    text));
                            textWasRecovered = persistence.Persisted;
                            recoveryComposerCleanupFailed =
                                persistence.Persisted && !persistence.Cleared;
                            UpdateRecoveredCleanupOutcome(
                                historyEntryId,
                                DictationHistoryOutcomes.CancelledRecovered,
                                persistence.Cleared);
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
                    deferredRecovery = stopResult.RequiresDeferredCleanup
                        ? recovery
                        : null;
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
                textWasRecovered &&
                deferredRecovery is not null)
            {
                var ownedWindowAvailable =
                    ChatGptWindowFinder.IsOwnedBackgroundWindow(session.ChatWindow, _settings);
                var cleared = ownedWindowAvailable &&
                              await _dictationController.ClearPersistedDictationAsync(
                        session.ChatWindow,
                        session.ChatInput,
                        RestoreFocus,
                        deferredRecovery.Text);
                var cleanupCompleted = !ownedWindowAvailable || cleared;
                recoveryComposerCleanupFailed = !cleanupCompleted;
                UpdateRecoveredCleanupOutcome(
                    deferredRecovery.HistoryEntryId,
                    deferredRecovery.Outcome,
                    cleanupCompleted);
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
            var localSession = _localSession;
            if (localSession is not null)
            {
                await StopLocalCaptureSafelyAsync(localSession.Capture);
                RestoreTargetFocus(localSession.Target);
                ResetToIdle();
                _logger.Info("Explicit local recording abort was cancelled.");
                return;
            }

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

    private async Task AbortLocalRecordingAsync(CancellationToken cancellationToken)
    {
        var session = _localSession;
        if (_status != AppStatus.Recording || session is null)
        {
            return;
        }

        _logger.Info("Local recording abort requested.");
        SetStatus(AppStatus.Stopping);
        try
        {
            var audio = await session.Capture.StopAndGetSamplesAsync(cancellationToken);
            StopAudioDucking();
            SetStatus(AppStatus.ReadingText);
            var text = await _localWhisper.TranscribeGermanAsync(
                audio.Samples,
                cancellationToken);
            var hasSafeText = !string.IsNullOrWhiteSpace(text) &&
                              !AutomationHelpers.IsUnsafeCapturedText(text);
            var historyRecovered = hasSafeText &&
                                   _history.TryAdd(
                                       text,
                                       DictationHistoryOutcomes.CancelledRecovered,
                                       out _);
            var clipboardRecovered = hasSafeText &&
                                     !historyRecovered &&
                                     ClipboardHelper.TrySetText(text, _logger);
            if (!hasSafeText)
            {
                _history.TryAdd(
                    string.Empty,
                    DictationHistoryOutcomes.CancelledWithoutText,
                    out _);
            }

            RestoreTargetFocus(session.Target);
            ShowMessage(historyRecovered
                ? "Aufnahme abgebrochen. Der lokal erkannte Text wurde im Diktierverlauf gesichert."
                : clipboardRecovered
                    ? "Aufnahme abgebrochen. Der Verlauf war nicht beschreibbar; der erkannte Text liegt zur Rettung in der Zwischenablage."
                    : "Aufnahme abgebrochen. Es konnte kein sicherer Text gesichert werden.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Local text recovery after abort failed.", ex);
            _history.TryAdd(
                string.Empty,
                DictationHistoryOutcomes.CancelledWithoutText,
                out _);
            RestoreTargetFocus(session.Target);
            ShowMessage($"Aufnahme abgebrochen. {GetLocalWhisperErrorMessage(ex)}");
        }
        finally
        {
            ResetToIdle();
        }
    }

    private async Task StopLocalCaptureSafelyAsync(LocalAudioCaptureSession capture)
    {
        try
        {
            await capture.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Local microphone could not be cleanly stopped during recovery.", ex);
        }
    }

    private void QueueLocalWhisperPreparation()
    {
        if (!DictationProviders.IsLocal(_settings.DictationProvider) ||
            _localWhisper.IsReady ||
            !_localPreparationTask.IsCompleted)
        {
            return;
        }

        _localPreparationCancellation?.Dispose();
        _localPreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _localPreparationFailureMessage = null;
        var generation = Interlocked.Increment(ref _localPreparationGeneration);
        _localPreparationTask = PrepareLocalWhisperAsync(
            _localPreparationCancellation.Token,
            generation);
    }

    private async Task<bool> StopLocalWhisperPreparationAsync(bool unloadModel)
    {
        Interlocked.Increment(ref _localPreparationGeneration);
        var cancellation = _localPreparationCancellation;
        var preparationTask = _localPreparationTask;
        cancellation?.Cancel();
        try
        {
            await preparationTask;
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            // Preparation normally consumes cancellation itself. This also
            // covers cancellation before its handler is entered.
        }

        if (ReferenceEquals(cancellation, _localPreparationCancellation))
        {
            _localPreparationCancellation = null;
            _localPreparationTask = Task.CompletedTask;
            cancellation?.Dispose();
        }

        _recordingOverlay.ClearOperationProgress();
        if (!unloadModel)
        {
            return true;
        }

        try
        {
            await _localWhisper.UnloadAsync(_lifetimeCancellation.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error("Local whisper.cpp model could not be unloaded after provider switch.", ex);
            return false;
        }
    }

    private async Task PrepareLocalWhisperAsync(
        CancellationToken cancellationToken,
        long generation)
    {
        try
        {
            var progress = new Progress<LocalWhisperModelProgress>(value =>
                ReportLocalModelProgress(value, generation));
            await _localWhisper.EnsureReadyAsync(progress, cancellationToken);
            _logger.Info("Local whisper.cpp startup preparation completed with Vulkan.");
            RunOnUiThread(() =>
            {
                if (!IsCurrentLocalPreparation(generation))
                {
                    return;
                }

                _recordingOverlay.ClearOperationProgress();
                if (_status == AppStatus.Idle)
                {
                    _statusItem.Text = "Status: Bereit · Lokal · Vulkan";
                    _notifyIcon.Text = "OpenAI Flow - Lokal/Vulkan bereit";
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("Local whisper.cpp startup preparation cancelled.");
        }
        catch (Exception ex)
        {
            _localPreparationFailureMessage = GetLocalWhisperErrorMessage(ex);
            _logger.Error("Local whisper.cpp startup preparation failed.", ex);
            RunOnUiThread(() =>
            {
                if (!IsCurrentLocalPreparation(generation))
                {
                    return;
                }

                _recordingOverlay.ClearOperationProgress();
                ShowErrorMessage(GetLocalWhisperErrorMessage(ex));
            });
        }
    }

    private void ReportLocalModelProgress(
        LocalWhisperModelProgress progress,
        long generation)
    {
        RunOnUiThread(() =>
        {
            if (!IsCurrentLocalPreparation(generation))
            {
                return;
            }

            var title = progress.Stage switch
            {
                LocalWhisperModelProgressStage.CheckingCache => "Sprachmodell wird geprüft",
                LocalWhisperModelProgressStage.VerifyingCache => "Sprachmodell wird verifiziert",
                LocalWhisperModelProgressStage.Downloading => $"Sprachmodell {progress.Percentage:F0} %",
                LocalWhisperModelProgressStage.Ready => "Sprachmodell ist bereit",
                _ => "Sprachmodell wird vorbereitet"
            };
            var hint = progress.Stage == LocalWhisperModelProgressStage.Downloading
                ? $"{FormatBytes(progress.BytesProcessed)} von {FormatBytes(progress.TotalBytes)}"
                : progress.FromCache
                    ? "Lokaler Cache wird wiederverwendet"
                    : "whisper.cpp · Vulkan · Deutsch";
            _statusItem.Text = $"Status: {title}";
            _notifyIcon.Text = TruncateNotifyText($"OpenAI Flow - {title}");
            if (_settings.ShowRecordingOverlay)
            {
                _recordingOverlay.ShowOperationProgress(title, hint);
            }
        });
    }

    private bool IsCurrentLocalPreparation(long generation) =>
        DictationProviders.IsLocal(_settings.DictationProvider) &&
        Volatile.Read(ref _localPreparationGeneration) == generation;

    private void RunOnUiThread(Action action)
    {
        if (_recordingOverlay.IsDisposed || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (_recordingOverlay.InvokeRequired)
        {
            _recordingOverlay.BeginInvoke(action);
            return;
        }

        action();
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):F1} GB"
            : $"{bytes / (1024d * 1024):F0} MB";

    private static string TruncateNotifyText(string text) =>
        text.Length <= 63 ? text : text[..63];

    private static string GetLocalWhisperErrorMessage(Exception exception) =>
        exception switch
        {
            LocalWhisperModelException modelException => modelException.Message,
            LocalWhisperRecognitionException recognitionException => recognitionException.Message,
            LocalAudioCaptureException captureException => captureException.Message,
            _ => "Die lokale deutsche Spracherkennung konnte nicht vorbereitet werden. Bitte Log und AMD-Treiber prüfen oder den ChatGPT-Browser-Fallback auswählen."
        };

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

    private async Task<SettingsApplyResult> ApplySettingsAsync(SettingsFormValues values)
    {
        if (_status != AppStatus.Idle)
        {
            return SettingsApplyResult.Fail("Bitte zuerst die laufende Aufnahme beenden.");
        }

        if (!DictationProviders.IsLocal(_settings.DictationProvider))
        {
            await _startupPreparationTask;
        }
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

            return await ApplySettingsCoreAsync(values);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<SettingsApplyResult> ApplySettingsCoreAsync(SettingsFormValues values)
    {
        var microphoneName = values.MicrophoneName;
        var microphoneId = values.MicrophoneId;
        var hotkey = values.Hotkey;
        var provider = DictationProviders.Normalize(values.DictationProvider);
        if (DictationProviders.IsLocal(provider))
        {
            return await ApplyLocalSettingsCoreAsync(
                microphoneId,
                microphoneName,
                hotkey);
        }

        if (values.ChromeProfile is not { } chromeProfile)
        {
            return SettingsApplyResult.Fail(
                "Bitte für die Browser-Diktierung ein Chrome-Profil auswählen.");
        }

        var previousExecutablePath = _settings.ChromeExecutablePath;
        var previousUserDataDirectory = _settings.ChromeUserDataDir;
        var previousProfileDirectory = _settings.ChromeProfileDirectory;
        var previousProfileIdentity = ChromeProfileIdentity.Create(
            previousUserDataDirectory,
            previousProfileDirectory);
        var previousProvider = _settings.DictationProvider;
        var previousMicrophoneId = _settings.PreferredMicrophoneId;
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

        if (!_audioInputDevices.IsMicrophoneActive(microphoneId, microphoneName))
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

        _settings.DictationProvider = DictationProviders.ChatGptBrowser;
        _settings.PreferredMicrophoneId = microphoneId;
        _settings.PreferredMicrophoneName = microphoneName;
        _settings.ToggleHotkey = hotkey;
        _settings.SetupCompleted = true;
        if (!_settings.Save(_logger))
        {
            _settings.DictationProvider = previousProvider;
            _settings.PreferredMicrophoneId = previousMicrophoneId;
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

        var localResourcesReleased = true;
        if (DictationProviders.IsLocal(previousProvider))
        {
            localResourcesReleased = await StopLocalWhisperPreparationAsync(unloadModel: true);
            SetStatus(AppStatus.Idle);
        }

        _recordingOverlay.SetToggleHotkey(hotkey);
        _logger.Info($"Settings applied from UI. ChromeProfileDirectory='{chromeProfile.DirectoryName}' MicrophoneName='{microphoneName}' ToggleHotkey='{hotkey}'.");
        ShowMessage(localResourcesReleased
            ? $"OpenAI Flow läuft jetzt mit {hotkey} im Hintergrund."
            : "Die Browser-Diktierung ist aktiv, aber das lokale GPU-Modell konnte nicht freigegeben werden. Ein Neustart von OpenAI Flow gibt die Ressourcen frei.");
        return SettingsApplyResult.Success(microphoneName, hotkey);
    }

    private async Task<SettingsApplyResult> ApplyLocalSettingsCoreAsync(
        string microphoneId,
        string microphoneName,
        string hotkey)
    {
        if (!_audioInputDevices.IsMicrophoneActive(microphoneId, microphoneName))
        {
            return SettingsApplyResult.Fail("Das ausgewählte Mikrofon ist nicht mehr verbunden.");
        }

        var previousProvider = _settings.DictationProvider;
        var previousMicrophoneId = _settings.PreferredMicrophoneId;
        var previousMicrophoneName = _settings.PreferredMicrophoneName;
        var previousHotkey = _settings.ToggleHotkey;
        var previousSetupCompleted = _settings.SetupCompleted;

        if (!_hotkeyWindow.TryUpdateToggleHotkey(hotkey, out var hotkeyFailure))
        {
            return SettingsApplyResult.Fail(hotkeyFailure);
        }

        if (!DictationProviders.IsLocal(previousProvider) &&
            ChromeProfileIdentity.From(_settings).IsConfigured)
        {
            if (!TryPreservePendingComposerBeforeClose(
                    out var preservationFailure,
                    out var preservedComposerText))
            {
                _ = _hotkeyWindow.TryUpdateToggleHotkey(previousHotkey, out _);
                return SettingsApplyResult.Fail(preservationFailure);
            }

            if (!await _dictationController.ReleaseKnownBackgroundWindowAsync(
                    preservedComposerText) &&
                !ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                    ChromeProfileIdentity.From(_settings),
                    _logger))
            {
                _ = _hotkeyWindow.TryUpdateToggleHotkey(previousHotkey, out _);
                return SettingsApplyResult.Fail(
                    "Das ChatGPT-Hintergrundfenster konnte vor dem Wechsel zur lokalen Erkennung nicht sicher freigegeben werden.");
            }
        }

        _settings.DictationProvider = DictationProviders.LocalWhisper;
        _settings.PreferredMicrophoneId = microphoneId;
        _settings.PreferredMicrophoneName = microphoneName;
        _settings.ToggleHotkey = hotkey;
        _settings.SetupCompleted = true;
        if (!_settings.Save(_logger))
        {
            _settings.DictationProvider = previousProvider;
            _settings.PreferredMicrophoneId = previousMicrophoneId;
            _settings.PreferredMicrophoneName = previousMicrophoneName;
            _settings.ToggleHotkey = previousHotkey;
            _settings.SetupCompleted = previousSetupCompleted;
            _ = _hotkeyWindow.TryUpdateToggleHotkey(previousHotkey, out _);
            return SettingsApplyResult.Fail(
                "Die Einstellungen konnten nicht sicher gespeichert werden. Bitte Schreibrechte und freien Speicherplatz prüfen.");
        }

        _recordingOverlay.SetToggleHotkey(hotkey);
        QueueLocalWhisperPreparation();
        _logger.Info($"Local settings applied. MicrophoneId='{microphoneId}' MicrophoneName='{microphoneName}' ToggleHotkey='{hotkey}'.");
        ShowMessage($"Lokale deutsche Diktierung läuft jetzt mit {hotkey} im Hintergrund.");
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
                false,
                Guid.Empty,
                string.Empty,
                outcome);
        }

        var persisted = _history.TryAdd(text, outcome, out var historyEntryId);
        var cleared = persisted &&
                      (!clearComposerAfterPersistence ||
                       await _dictationController.ClearPersistedDictationAsync(
                           chatWindow,
                           readResult.Input ?? preferredInput,
                           restoreTargetFocus,
                           text));
        if (persisted && clearComposerAfterPersistence)
        {
            UpdateRecoveredCleanupOutcome(historyEntryId, outcome, cleared);
        }

        return new DictationRecoveryResult(
            persisted,
            readResult.ClipboardRestoreFailed,
            persisted && clearComposerAfterPersistence && !cleared,
            historyEntryId,
            text,
            outcome);
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

    private void UpdateRecoveredCleanupOutcome(
        Guid historyEntryId,
        string recoveryOutcome,
        bool cleanupCompleted)
    {
        if (historyEntryId == Guid.Empty)
        {
            return;
        }

        var outcome = cleanupCompleted
            ? recoveryOutcome == DictationHistoryOutcomes.CancelledRecovered
                ? DictationHistoryOutcomes.CancelledRecoveredCleared
                : DictationHistoryOutcomes.FailureRecoveredCleared
            : DictationHistoryOutcomes.PendingComposerCleanup;
        _history.UpdateOutcome(historyEntryId, outcome);
    }

    private void OnOverlayPlacementCommitted(
        object? sender,
        RecordingOverlayPlacementEventArgs e)
    {
        var previousMonitor = _settings.RecordingOverlayMonitorDeviceName;
        var previousRelativeX = _settings.RecordingOverlayRelativeX;
        var previousRelativeY = _settings.RecordingOverlayRelativeY;
        _settings.RecordingOverlayMonitorDeviceName = e.Placement.MonitorDeviceName;
        _settings.RecordingOverlayRelativeX = e.Placement.RelativeX;
        _settings.RecordingOverlayRelativeY = e.Placement.RelativeY;
        if (_settings.Save(_logger))
        {
            _logger.Info($"Recording bar position saved. Monitor='{e.Placement.MonitorDeviceName}' RelativeX={e.Placement.RelativeX:F4} RelativeY={e.Placement.RelativeY:F4}");
            return;
        }

        _settings.RecordingOverlayMonitorDeviceName = previousMonitor;
        _settings.RecordingOverlayRelativeX = previousRelativeX;
        _settings.RecordingOverlayRelativeY = previousRelativeY;
        ShowErrorMessage("Die Position der Diktierleiste konnte nicht gespeichert werden.");
    }

    private void ResetToIdle()
    {
        StopAudioDucking();
        if (_localSession is { } localSession)
        {
            localSession.Capture.RecordingLimitReached -= OnLocalRecordingLimitReached;
            localSession.Capture.UnexpectedlyStopped -= OnLocalCaptureUnexpectedlyStopped;
            localSession.Capture.Dispose();
            _localSession = null;
        }
        _session = null;
        _queuedStopRequested = false;
        _hotkeyWindow.SetEscapeEnabled(false);
        SetStatus(AppStatus.Idle);
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
            _recordingOverlay.ShowStatus(status);
        }
        else
        {
            _recordingOverlay.HideOverlay();
        }
        _logger.Info($"Status changed: {status}");
    }

    private void ShowMessage(string message)
    {
        _notifyIcon.BalloonTipTitle = "OpenAI Flow Dictation";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void ShowErrorMessage(string message)
    {
        ShowMessage(message);
        if (_settings.ShowRecordingOverlay)
        {
            _recordingOverlay.ShowTransientError(message);
        }
    }

    private void ShowConfiguredProfileUnavailable()
    {
        ShowErrorMessage($"Chrome-Profil {_settings.ChromeProfileDirectory} nicht gefunden. Bitte OpenAI Flow öffnen und ein Profil auswählen.");
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
            ShowErrorMessage("Datei konnte nicht geöffnet werden.");
        }
    }

    private async Task ExitAsync()
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        _hotkeyWindow.SetToggleEnabled(false);
        _recordingOverlay.SetInteractionEnabled(false);
        _dictationReadCancellation?.Cancel();
        _startupCancellation.Cancel();
        _ = await StopLocalWhisperPreparationAsync(unloadModel: false);
        await _startupPreparationTask;
        var exitAllowed = true;
        var exitFailureMessage = string.Empty;
        await _operationLock.WaitAsync();
        try
        {
            var sessionActive = _session is not null || _localSession is not null;
            if (sessionActive && _status != AppStatus.Idle)
            {
                exitAllowed = false;
                exitFailureMessage = "OpenAI Flow bleibt geöffnet, solange eine Aufnahme noch aktiv oder nicht sicher abgeschlossen ist. Bitte zuerst F8 oder Escape drücken.";
            }
            else if (DictationProviders.IsLocal(_settings.DictationProvider))
            {
                _logger.Info("Application exiting from local dictation mode; no browser window cleanup is required.");
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
            var interactionEnabled = !_settingsForm.Visible;
            _hotkeyWindow.SetToggleEnabled(interactionEnabled);
            _recordingOverlay.SetInteractionEnabled(interactionEnabled);
            _hotkeyWindow.SetEscapeEnabled(_status == AppStatus.Recording);
            ShowMessage(exitFailureMessage);
            return;
        }

        _startupCancellation.Cancel();
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

    private sealed record LocalRecordingSession(
        FocusTarget Target,
        LocalAudioCaptureSession Capture);

    private sealed record DictationRecoveryResult(
        bool TextRecovered,
        bool ClipboardRestoreFailed,
        bool ComposerCleanupFailed,
        Guid HistoryEntryId,
        string Text,
        string Outcome);
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
