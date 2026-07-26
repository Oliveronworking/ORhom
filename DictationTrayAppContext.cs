using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace ORhom;

internal sealed class DictationTrayAppContext : ApplicationContext
{
    private readonly AppLogger _logger;
    private readonly Icon _applicationIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _primaryActionItem;
    private readonly ToolStripMenuItem _abortItem;
    private readonly ToolStripMenuItem _lastDictationItem;
    private readonly ToolStripMenuItem _microphoneMenu;
    private readonly ToolStripMenuItem _overlayVisibilityItem;
    private readonly ToolStripMenuItem _advancedMenu;
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
    private readonly int _uiThreadId;
    private AppStatus _status = AppStatus.Idle;
    private string _idleReadinessTitle = string.Empty;
    private string _idleReadinessHint = string.Empty;
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
    private FocusTarget? _trayFocusTarget;
    private int _disposeState;

    public DictationTrayAppContext(
        AppSettings settings,
        AppLogger logger,
        ChromeProfileDiscovery chromeProfileDiscovery,
        AppPaths paths)
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
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
            paths.DataDirectory,
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
        _recordingOverlay.ApplySizePreset(_settings.RecordingOverlaySize);
        _focusTracker = new FocusTracker(_logger);
        _pasteService = new PasteService(_logger);
        _history = new DictationHistoryStore(
            Path.Combine(paths.DataDirectory, "dictation-history.json"),
            _logger);
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
        _recordingOverlay.AbortRequested += (_, _) =>
            RunUserOperation(AbortRecordingAsync, "overlay-abort");
        _recordingOverlay.PlacementCommitted += OnOverlayPlacementCommitted;
        _settingsForm = new SettingsForm(_settings, _audioInputDevices, _chromeProfileDiscovery, ApplySettingsAsync);
        _settingsForm.Icon = _applicationIcon;
        _settingsForm.VisibleChanged += (_, _) =>
        {
            var interactionEnabled = !_settingsForm.Visible && !_exitInProgress;
            _hotkeyWindow.SetToggleEnabled(interactionEnabled);
            _recordingOverlay.SetInteractionEnabled(interactionEnabled);
            if (Volatile.Read(ref _disposeState) == 0)
            {
                ApplyTrayStatePresentation();
            }
        };

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _primaryActionItem = new ToolStripMenuItem(
            "Diktierung starten",
            null,
            (sender, _) =>
            {
                var target = (sender as ToolStripItem)?.Tag as FocusTarget;
                if (sender is ToolStripItem item)
                {
                    item.Tag = null;
                }

                _trayFocusTarget = null;
                RunUserOperation(
                    () => ToggleFromTrayAsync(target),
                    "tray-primary-action");
            })
        {
            ToolTipText = "Startet oder beendet die Diktierung.",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _abortItem = new ToolStripMenuItem(
            "Aufnahme verwerfen",
            null,
            (_, _) => RunUserOperation(AbortRecordingAsync, "tray-abort"))
        {
            ToolTipText = "Verwirft die laufende Aufnahme ohne Einfügen."
        };
        _lastDictationItem = new ToolStripMenuItem(
            "Letztes Diktat einfügen",
            null,
            (sender, _) =>
            {
                var target = (sender as ToolStripItem)?.Tag as FocusTarget;
                if (sender is ToolStripItem item)
                {
                    item.Tag = null;
                }

                _trayFocusTarget = null;
                RunUserOperation(
                    () => PasteLastDictationAsync(target),
                    "tray-paste-last-dictation");
            })
        {
            ToolTipText =
                "Fügt den letzten Text ins zuvor aktive Feld ein; bei Bedarf bleibt er in der Zwischenablage."
        };
        _microphoneMenu = new ToolStripMenuItem("Mikrofon");
        _overlayVisibilityItem = new ToolStripMenuItem(
            "Diktierleiste anzeigen",
            null,
            (_, _) => RunUserOperation(
                ToggleRecordingOverlayVisibilityAsync,
                "tray-toggle-recording-overlay"))
        {
            CheckOnClick = false
        };
        _advancedMenu = CreateAdvancedMenu();

        _trayMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true
        };
        _trayMenu.Opening += (_, _) =>
        {
            _trayFocusTarget = _status == AppStatus.Idle && !_settingsForm.Visible
                ? _focusTracker.Capture(_settings)
                : null;
            RefreshTrayMenu();
        };
        _trayMenu.ItemClicked += (_, e) =>
        {
            if (e.ClickedItem == _primaryActionItem ||
                e.ClickedItem == _lastDictationItem)
            {
                e.ClickedItem.Tag = _trayFocusTarget;
            }
        };
        _trayMenu.Closed += (_, _) => _trayFocusTarget = null;
        _trayMenu.Items.Add(_statusItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_primaryActionItem);
        _trayMenu.Items.Add(_abortItem);
        _trayMenu.Items.Add(_lastDictationItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_microphoneMenu);
        _trayMenu.Items.Add(_overlayVisibilityItem);
        _trayMenu.Items.Add(new ToolStripMenuItem(
            "Diktierverlauf",
            null,
            (_, _) => OpenHistory()));
        _trayMenu.Items.Add(new ToolStripMenuItem(
            "ORhom öffnen",
            null,
            (_, _) => OpenSettings()));
        _trayMenu.Items.Add(_advancedMenu);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(new ToolStripMenuItem(
            "Beenden",
            null,
            (_, _) => RunUserOperation(ExitAsync, "application-exit")));
        _recordingOverlay.ContextMenuStrip = _trayMenu;

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Visible = true,
            Text = "ORhom · Bereit",
            ContextMenuStrip = _trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
        RefreshTrayMenu();

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

    private ToolStripMenuItem CreateAdvancedMenu()
    {
        var menu = new ToolStripMenuItem("Erweitert");
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "ChatGPT-Profil öffnen",
            null,
            (_, _) => RunUserOperation(
                OpenChatGptProfileAsync,
                "open-chatgpt-profile"))
        {
            Name = "open-chatgpt-profile"
        });
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Chrome-Profil prüfen",
            null,
            (_, _) => CheckChromeProfile())
        {
            Name = "check-chrome-profile"
        });
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "ChatGPT-Diagnose speichern",
            null,
            (_, _) => RunUserOperation(
                WriteChatGptDiagnosticsAsync,
                "write-diagnostics"))
        {
            Name = "write-chatgpt-diagnostics"
        });
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Chrome-Profilordner öffnen",
            null,
            (_, _) => OpenChromeProfileDirectory())
        {
            Name = "open-chrome-profile-directory"
        });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Konfigurationsdatei öffnen",
            null,
            (_, _) => OpenPath(_settings.SettingsPath)));
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Logs öffnen",
            null,
            (_, _) => OpenPath(_logger.LogPath)));
        return menu;
    }

    private void RefreshTrayMenu()
    {
        ApplyTrayStatePresentation();

        var latest = _history.GetEntries()
            .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Text));
        _lastDictationItem.Enabled =
            latest is not null &&
            _status == AppStatus.Idle &&
            !_exitInProgress &&
            !_settingsForm.Visible;
        _lastDictationItem.Text = latest is null
            ? "Noch kein Diktat zum Einfügen"
            : "Letztes Diktat einfügen";

        _microphoneMenu.Text = string.IsNullOrWhiteSpace(
            _settings.PreferredMicrophoneName)
            ? "Mikrofon auswählen"
            : $"Mikrofon · {Ellipsize(_settings.PreferredMicrophoneName, 34)}";
        _microphoneMenu.Enabled =
            _status == AppStatus.Idle &&
            !_exitInProgress &&
            !_settingsForm.Visible;
        RefreshMicrophoneMenu();

        _overlayVisibilityItem.Checked = _settings.ShowRecordingOverlay;
        _overlayVisibilityItem.Enabled =
            _status == AppStatus.Idle &&
            !_exitInProgress &&
            !_settingsForm.Visible;
        _overlayVisibilityItem.Text = _settings.ShowRecordingOverlay
            ? "Diktierleiste anzeigen"
            : "Diktierleiste einblenden";

        var browserMode = !DictationProviders.IsLocal(
            _settings.DictationProvider);
        SetAdvancedItemState(
            "open-chatgpt-profile",
            browserMode && _settings.OpenChatGptProfileVisibleForSetup);
        SetAdvancedItemState("check-chrome-profile", browserMode);
        SetAdvancedItemState(
            "write-chatgpt-diagnostics",
            browserMode && _settings.EnableChatGptInputDiagnostics);
        SetAdvancedItemState("open-chrome-profile-directory", browserMode);
    }

    private void ApplyTrayStatePresentation()
    {
        var presentation = UiStatePresentation.For(
            _status,
            _settings.ToggleHotkey);
        var hasReadinessOverride =
            _status == AppStatus.Idle &&
            !string.IsNullOrWhiteSpace(_idleReadinessTitle);
        var readinessHint = _idleReadinessTitle.Equals(
            "Bereit · Lokal · Vulkan",
            StringComparison.Ordinal)
                ? $"{_settings.ToggleHotkey} drücken, um zu diktieren"
                : _idleReadinessHint;
        _statusItem.Text = hasReadinessOverride
            ? string.IsNullOrWhiteSpace(readinessHint)
                ? _idleReadinessTitle
                : $"{_idleReadinessTitle} · {readinessHint}"
            : $"{presentation.VisibleStatus} · {presentation.Hint}";
        _primaryActionItem.Text = presentation.PrimaryAction;
        _primaryActionItem.ShortcutKeyDisplayString = _settings.ToggleHotkey;
        _primaryActionItem.Enabled =
            presentation.CanToggle &&
            !_exitInProgress &&
            !_settingsForm.Visible;
        _abortItem.ShortcutKeyDisplayString = "Esc";
        _abortItem.Visible = presentation.CanAbort;
        _abortItem.Enabled = presentation.CanAbort && !_exitInProgress;
        var idleInteractionAvailable =
            _status == AppStatus.Idle &&
            !_exitInProgress &&
            !_settingsForm.Visible;
        _microphoneMenu.Enabled = idleInteractionAvailable;
        _overlayVisibilityItem.Enabled = idleInteractionAvailable;
        _lastDictationItem.Enabled =
            idleInteractionAvailable &&
            _history.GetEntries().Any(entry =>
                !string.IsNullOrWhiteSpace(entry.Text));
        var tooltip = hasReadinessOverride
            ? $"ORhom – {_idleReadinessTitle}"
            : presentation.TrayTooltip;
        _notifyIcon.Text = TruncateNotifyText(tooltip);
    }

    private void SetAdvancedItemState(string name, bool enabled)
    {
        if (_advancedMenu.DropDownItems[name] is ToolStripItem item)
        {
            item.Enabled = enabled;
            item.Visible = enabled;
        }
    }

    private void RefreshMicrophoneMenu()
    {
        while (_microphoneMenu.DropDownItems.Count > 0)
        {
            var oldItem = _microphoneMenu.DropDownItems[0];
            _microphoneMenu.DropDownItems.RemoveAt(0);
            oldItem.Dispose();
        }

        var devices = _audioInputDevices.GetActiveMicrophoneDevices();
        if (devices.Count == 0)
        {
            _microphoneMenu.DropDownItems.Add(new ToolStripMenuItem(
                "Kein aktives Mikrofon gefunden")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var device in devices)
            {
                var item = new ToolStripMenuItem(device.DisplayName)
                {
                    Checked =
                        device.Id.Equals(
                            _settings.PreferredMicrophoneId,
                            StringComparison.OrdinalIgnoreCase) ||
                        (string.IsNullOrWhiteSpace(
                             _settings.PreferredMicrophoneId) &&
                         device.DisplayName.Equals(
                             _settings.PreferredMicrophoneName,
                             StringComparison.OrdinalIgnoreCase)),
                    ToolTipText = device.DisplayName
                };
                item.Click += (_, _) => RunUserOperation(
                    () => SelectMicrophoneAsync(device),
                    "tray-select-microphone");
                _microphoneMenu.DropDownItems.Add(item);
            }
        }

        _microphoneMenu.DropDownItems.Add(new ToolStripSeparator());
        _microphoneMenu.DropDownItems.Add(new ToolStripMenuItem(
            "Mikrofon & Einstellungen öffnen …",
            null,
            (_, _) => OpenSettings()));
    }

    private async Task ToggleFromTrayAsync(FocusTarget? target)
    {
        if (_settingsForm.Visible)
        {
            ShowMessage(
                "Bitte das ORhom-Fenster zuerst im Hintergrund schließen.");
            return;
        }

        if (_status == AppStatus.Idle)
        {
            var capturedTarget = target;
            if (capturedTarget is null ||
                capturedTarget.WindowHandle == IntPtr.Zero ||
                !_pasteService.RestoreTargetFocus(capturedTarget))
            {
                ShowErrorMessage(
                    $"Kein sicheres Textfeld gefunden. Bitte in das gewünschte Feld klicken und {_settings.ToggleHotkey} drücken.");
                return;
            }

            await Task.Delay(75);
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            if (!FocusTargetSafetyPolicy.HasSameWindowIdentity(
                    capturedTarget.WindowHandle,
                    capturedTarget.OwningProcessId,
                    foregroundWindow,
                    NativeMethods.GetOwningProcessId(foregroundWindow)))
            {
                ShowErrorMessage(
                    "Das zuvor aktive Textfeld ist nicht mehr im Vordergrund. Die Aufnahme wurde nicht gestartet.");
                return;
            }
        }

        await ToggleAsync();
    }

    private async Task SelectMicrophoneAsync(AudioInputDeviceInfo device)
    {
        if (_status != AppStatus.Idle || _settingsForm.Visible)
        {
            ShowMessage(
                "Das Mikrofon kann geändert werden, sobald ORhom bereit und das Einstellungsfenster geschlossen ist.");
            return;
        }

        if (device.Id.Equals(
                _settings.PreferredMicrophoneId,
                StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage($"„{device.DisplayName}“ ist bereits ausgewählt.");
            return;
        }

        ChromeProfileInfo? chromeProfile = null;
        if (!DictationProviders.IsLocal(_settings.DictationProvider))
        {
            var profiles = _chromeProfileDiscovery.Discover(
                _settings.ChromeExecutablePath,
                _settings.ChromeUserDataDir);
            chromeProfile = profiles.Profiles.FirstOrDefault(profile =>
                profile.DirectoryName.Equals(
                    _settings.ChromeProfileDirectory,
                    StringComparison.OrdinalIgnoreCase));
            if (chromeProfile is null)
            {
                ShowErrorMessage(
                    "Das konfigurierte Chrome-Profil ist nicht verfügbar. Bitte ORhom öffnen.");
                return;
            }
        }

        var result = await ApplySettingsAsync(new SettingsFormValues(
            _settings.DictationProvider,
            device.Id,
            device.DisplayName,
            _settings.ToggleHotkey,
            chromeProfile,
            _settings.RecordingOverlaySize));
        if (!result.Ok)
        {
            ShowErrorMessage(result.Message);
            return;
        }

        RefreshTrayMenu();
        ShowMessage($"Mikrofon gewechselt: {device.DisplayName}");
    }

    private async Task PasteLastDictationAsync(FocusTarget? target)
    {
        var latest = _history.GetEntries()
            .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Text));
        if (latest is null)
        {
            ShowMessage("Im Diktierverlauf ist noch kein erkannter Text.");
            return;
        }

        if (_settingsForm.Visible ||
            _status != AppStatus.Idle ||
            !await _operationLock.WaitAsync(0))
        {
            ShowMessage("ORhom verarbeitet gerade noch ein anderes Diktat.");
            return;
        }

        try
        {
            SetStatus(AppStatus.Pasting);
            var result = target is null ||
                         target.WindowHandle == IntPtr.Zero
                ? PasteResult.FailedWithClipboardFallback
                : _pasteService.PasteIntoTarget(
                    latest.Text,
                    target,
                    _settings);
            if (result.Succeeded)
            {
                if (result.ClipboardRestoreOutcome ==
                    ClipboardRestoreOutcome.Failed)
                {
                    ShowErrorMessage(
                        "Letztes Diktat eingefügt, aber die vorherige Zwischenablage konnte nicht wiederhergestellt werden.");
                }
                else
                {
                    ShowMessage("Letztes Diktat erneut eingefügt.");
                }

                return;
            }

            if (result.PasteMayHaveReachedTarget)
            {
                ShowMessage(
                    "Das automatische Einfügen konnte nicht bestätigt werden. Bitte zuerst das Zielfeld prüfen; das Diktat bleibt im Verlauf.");
                return;
            }

            var availableOnClipboard =
                result.TextIsOnClipboard ||
                (result.AllowClipboardFallback &&
                 ClipboardHelper.TrySetText(latest.Text, _logger));
            if (availableOnClipboard)
            {
                ShowMessage(
                    "Automatisches Einfügen war hier nicht sicher. Der Text ist kopiert – mit Strg+V einfügen.");
            }
            else
            {
                ShowErrorMessage(
                    "Das letzte Diktat konnte nicht eingefügt oder kopiert werden. Bitte den Verlauf öffnen.");
            }
        }
        finally
        {
            SetStatus(AppStatus.Idle);
            _operationLock.Release();
        }
    }

    private async Task ToggleRecordingOverlayVisibilityAsync()
    {
        if (_exitInProgress ||
            _lifetimeCancellation.IsCancellationRequested ||
            _status != AppStatus.Idle ||
            _settingsForm.Visible)
        {
            ShowMessage(
                "Die Diktierleiste kann geändert werden, sobald ORhom bereit ist.");
            return;
        }

        if (!await _operationLock.WaitAsync(0))
        {
            ShowMessage("ORhom übernimmt gerade noch eine andere Änderung.");
            return;
        }

        try
        {
            if (_exitInProgress ||
                _lifetimeCancellation.IsCancellationRequested ||
                _status != AppStatus.Idle ||
                _settingsForm.Visible)
            {
                return;
            }

            var previous = _settings.ShowRecordingOverlay;
            _settings.ShowRecordingOverlay = !previous;
            if (!_settings.Save(_logger))
            {
                _settings.ShowRecordingOverlay = previous;
                ShowErrorMessage(
                    "Die Anzeige der Diktierleiste konnte nicht gespeichert werden.");
                return;
            }

            if (_settings.ShowRecordingOverlay)
            {
                _recordingOverlay.ShowStatus(_status);
                ShowMessage("Die Diktierleiste ist wieder sichtbar.");
            }
            else
            {
                _recordingOverlay.HideOverlay();
                ShowMessage(
                    $"Diktierleiste ausgeblendet. Diktieren funktioniert weiter mit {_settings.ToggleHotkey}.");
            }

            RefreshTrayMenu();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static string Ellipsize(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : $"{value[..Math.Max(maximumLength - 1, 1)]}…";

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
        _trayMenu.Dispose();
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
        _applicationIcon.Dispose();
        var preparationTasks = Task.WhenAll(
            _startupPreparationTask,
            _localPreparationTask);
        if (preparationTasks.IsCompleted)
        {
            _dictationController.Dispose();
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
                    _dictationController.Dispose();
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

            ShowErrorMessage("ORhom hat einen internen Fehler abgefangen. Es läuft weiter; bitte erneut versuchen oder das Log prüfen.");
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
        if (target.IsPasswordFieldOrUnverifiable)
        {
            ShowErrorMessage("Das Zielfeld ist ein Passwortfeld oder konnte nicht sicher geprüft werden. Aufnahme wurde nicht gestartet.");
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

            // Capture is already running before this second focus sample. Chromium
            // can briefly expose the page's main group instead of the active
            // contenteditable node; recording first prevents the re-probe from
            // clipping the first spoken syllable.
            var targetReprobe = await _focusTracker.ReprobeAfterLocalCaptureStartedAsync(
                target,
                _settings,
                _lifetimeCancellation.Token);
            if (targetReprobe.PasswordFieldDetected)
            {
                await StopLocalCaptureSafelyAsync(capture);
                ShowErrorMessage("Ziel ist ein Passwortfeld. Die bereits gestartete Aufnahme wurde sofort verworfen.");
                ResetToIdle();
                return;
            }

            target = targetReprobe.Target;
            if (targetReprobe.TargetPromoted)
            {
                _localSession = new LocalRecordingSession(target, capture);
            }

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

        var unconfirmedRecoveryCopied =
            !historyPersisted &&
            (pasteResult.TextIsOnClipboard ||
             (pasteResult.ShouldCopyUnconfirmedTextAsLastResort &&
              ClipboardHelper.TrySetText(text, _logger)));
        var copied = !pasteResult.Succeeded &&
                     !pasteResult.PasteMayHaveReachedTarget &&
                     (pasteResult.TextIsOnClipboard ||
                      (pasteResult.ShouldAttemptClipboardFallback &&
                       ClipboardHelper.TrySetText(text, _logger)));
        _logger.Info($"Local dictation paste completed. Success={pasteResult.Succeeded} MayHaveReachedTarget={pasteResult.PasteMayHaveReachedTarget} TextLength={text.Length} ClipboardRestoreOutcome={pasteResult.ClipboardRestoreOutcome}.");
        if (!historyPersisted)
        {
            ShowMessage(pasteResult.PasteMayHaveReachedTarget
                ? unconfirmedRecoveryCopied
                    ? "Das Einfügen konnte nicht bestätigt und der Text nicht im Diktierverlauf gesichert werden. Bitte zuerst das Zielfeld prüfen; nur falls er dort fehlt, liegt er als Rettung in der Zwischenablage."
                    : "Das Einfügen konnte nicht bestätigt und der Text weder im Diktierverlauf noch in der Zwischenablage gesichert werden. Bitte das Zielfeld prüfen."
                : pasteResult.Succeeded
                ? "Text wurde eingefügt, konnte aber nicht im Diktierverlauf gesichert werden."
                : copied
                    ? "Text konnte weder eingefügt noch im Diktierverlauf gesichert werden. Er liegt als Rettung in der Zwischenablage."
                    : "Text konnte weder eingefügt noch sicher im Diktierverlauf oder in der Zwischenablage gesichert werden.");
        }
        else if (pasteResult.PasteMayHaveReachedTarget)
        {
            ShowMessage(
                "Das Einfügen konnte nicht bestätigt werden. Bitte zuerst das Zielfeld prüfen; der Text bleibt im Diktierverlauf.");
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
        if (target.IsPasswordFieldOrUnverifiable)
        {
            _logger.Info("Recording blocked because the target is a password field or could not be verified safely.");
            ShowErrorMessage("Das Zielfeld ist ein Passwortfeld oder konnte nicht sicher geprüft werden. Aufnahme wurde nicht gestartet.");
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

        _logger.Info($"Dictation paste completed. Success={pasteResult.Succeeded} MayHaveReachedTarget={pasteResult.PasteMayHaveReachedTarget} TextLength={text.Length} ClipboardRestoreOutcome={pasteResult.ClipboardRestoreOutcome} TextIsOnClipboard={pasteResult.TextIsOnClipboard}");
        if (!historyPersisted)
        {
            ShowMessage(pasteResult.PasteMayHaveReachedTarget
                ? "Das Einfügen konnte nicht bestätigt und der Text nicht im Verlauf gespeichert werden. Bitte zuerst das Zielfeld prüfen; der ChatGPT-Entwurf bleibt als Sicherung erhalten."
                : pasteResult.Succeeded
                ? "Text wurde eingefügt, aber nicht im Verlauf gespeichert. Der ChatGPT-Entwurf bleibt als Sicherung erhalten."
                : "Text konnte weder im Verlauf gespeichert noch sicher eingefügt werden. Der ChatGPT-Entwurf bleibt als Sicherung erhalten.");
        }
        else if (!composerCleared)
        {
            ShowMessage(pasteResult.Succeeded
                ? "Text wurde eingefügt und im Verlauf gesichert, aber der ChatGPT-Entwurf konnte nicht gelöscht werden. Bitte das ChatGPT-Profil öffnen und den Entwurf löschen."
                : pasteResult.PasteMayHaveReachedTarget
                    ? "Das Einfügen konnte nicht bestätigt werden. Bitte zuerst das Zielfeld prüfen. Der Text liegt im Verlauf; der ChatGPT-Entwurf konnte nicht gelöscht werden."
                : "Text liegt sicher im Verlauf, aber Einfügen und Löschen des ChatGPT-Entwurfs sind fehlgeschlagen. Bitte das ChatGPT-Profil öffnen.");
        }
        else if (pasteResult.PasteMayHaveReachedTarget)
        {
            ShowMessage(
                "Das Einfügen konnte nicht bestätigt werden. Bitte zuerst das Zielfeld prüfen; der Text bleibt im Diktierverlauf.");
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
            var terminationConfirmed = stopResult.IsTerminationConfirmed;
            if (stopResult.RequiresDeferredCleanup)
            {
                var cleanup = await _dictationController.CompleteDeferredStopCleanupAsync(
                    session.ChatWindow);
                terminationConfirmed = cleanup.IsTerminationConfirmed;
            }

            RestoreTargetFocus(session.Target);
            if (!terminationConfirmed)
            {
                _hotkeyWindow.SetEscapeEnabled(true);
                SetStatus(AppStatus.Recording);
                ShowMessage("Das Mikrofon konnte nicht sicher beendet werden. Bitte F8 erneut drücken oder das ChatGPT-Fenster schließen.");
                return;
            }

            var ownedWindowAvailable =
                ChatGptWindowFinder.IsOwnedBackgroundWindow(session.ChatWindow, _settings);
            var composerCleared = !ownedWindowAvailable ||
                                  await _dictationController.ClearPersistedDictationAsync(
                                      session.ChatWindow,
                                      session.ChatInput,
                                      RestoreFocus);
            _logger.Info(
                $"Recording discarded without reading or persisting dictated text. ComposerCleared={composerCleared}");
            ShowMessage(composerCleared
                ? "Aufnahme verworfen."
                : "Aufnahme beendet, aber der ChatGPT-Entwurf konnte nicht gelöscht werden. Bitte das ChatGPT-Profil öffnen und den Entwurf manuell löschen.");
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
            await session.Capture.StopAsync(cancellationToken);
            StopAudioDucking();
            RestoreTargetFocus(session.Target);
            _logger.Info("Local recording discarded without transcription or persistence.");
            ShowMessage("Aufnahme verworfen.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Local microphone could not be stopped while discarding the recording.", ex);
            RestoreTargetFocus(session.Target);
            ShowMessage($"Aufnahme konnte nicht sauber verworfen werden. {GetLocalWhisperErrorMessage(ex)}");
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
        _idleReadinessTitle = "Sprachmodell wird vorbereitet";
        _idleReadinessHint = "whisper.cpp · Vulkan · Deutsch";
        ApplyTrayStatePresentation();
        var generation = Interlocked.Increment(ref _localPreparationGeneration);
        _localPreparationTask = PrepareLocalWhisperAsync(
            generation,
            _localPreparationCancellation.Token);
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
        if (!DictationProviders.IsLocal(_settings.DictationProvider))
        {
            _idleReadinessTitle = string.Empty;
            _idleReadinessHint = string.Empty;
            ApplyTrayStatePresentation();
        }

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
        long generation,
        CancellationToken cancellationToken)
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
                    _idleReadinessTitle = "Bereit · Lokal · Vulkan";
                    _idleReadinessHint = string.Empty;
                    ApplyTrayStatePresentation();
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
                _idleReadinessTitle = "Sprachmodell nicht bereit";
                _idleReadinessHint = "ORhom öffnen und Einstellungen prüfen";
                ApplyTrayStatePresentation();
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
            _idleReadinessTitle = title;
            _idleReadinessHint = hint;
            ApplyTrayStatePresentation();
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
        if (!CanDispatchUiAction())
        {
            return;
        }

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            if (CanDispatchUiAction())
            {
                action();
            }

            return;
        }

        try
        {
            _hotkeyWindow.BeginInvoke((Action)(() =>
            {
                if (CanDispatchUiAction())
                {
                    action();
                }
            }));
        }
        catch (InvalidOperationException) when (!CanDispatchUiAction())
        {
            // Shutdown can destroy the dispatcher handle between the checks above.
        }
    }

    private bool CanDispatchUiAction() =>
        Volatile.Read(ref _disposeState) == 0 &&
        !_lifetimeCancellation.IsCancellationRequested &&
        !_hotkeyWindow.IsDisposed &&
        _hotkeyWindow.IsHandleCreated;

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
            ShowMessage("ORhom verarbeitet gerade eine andere Aktion.");
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
            return SettingsApplyResult.Fail("ORhom wird gerade beendet.");
        }

        await _operationLock.WaitAsync();
        try
        {
            if (_exitInProgress || _status != AppStatus.Idle)
            {
                return SettingsApplyResult.Fail("ORhom wird gerade beendet oder verarbeitet noch eine Aufnahme.");
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
        var recordingOverlaySize = Enum.IsDefined(values.RecordingOverlaySize)
            ? values.RecordingOverlaySize
            : RecordingOverlaySize.Small;
        if (DictationProviders.IsLocal(provider))
        {
            return await ApplyLocalSettingsCoreAsync(
                microphoneId,
                microphoneName,
                hotkey,
                recordingOverlaySize);
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
        var previousRecordingOverlaySize = _settings.RecordingOverlaySize;
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
                    "ChatGPT konnte nicht vorbereitet und das neue Chrome-Profil nicht sicher zurückgerollt werden. Bitte ORhom neu starten.");
            }

            return SettingsApplyResult.Fail("Mikrofon gespeichert, aber ChatGPT konnte nicht vorbereitet werden. Bitte ChatGPT Profil öffnen und Anmeldung prüfen.");
        }

        if (!_hotkeyWindow.TryUpdateToggleHotkey(hotkey, out var hotkeyFailure))
        {
            if (!await TryRollbackSwitchedProfileAsync())
            {
                return SettingsApplyResult.Fail(
                    $"{hotkeyFailure} Das neue Chrome-Profil konnte nicht sicher zurückgerollt werden; bitte ORhom neu starten.");
            }

            return SettingsApplyResult.Fail(hotkeyFailure);
        }

        _settings.DictationProvider = DictationProviders.ChatGptBrowser;
        _settings.PreferredMicrophoneId = microphoneId;
        _settings.PreferredMicrophoneName = microphoneName;
        _settings.ToggleHotkey = hotkey;
        _settings.RecordingOverlaySize = recordingOverlaySize;
        _settings.SetupCompleted = true;
        if (!_settings.Save(_logger))
        {
            _settings.DictationProvider = previousProvider;
            _settings.PreferredMicrophoneId = previousMicrophoneId;
            _settings.PreferredMicrophoneName = previousMicrophoneName;
            _settings.ToggleHotkey = previousHotkey;
            _settings.RecordingOverlaySize = previousRecordingOverlaySize;
            _settings.SetupCompleted = previousSetupCompleted;
            if (!_hotkeyWindow.TryUpdateToggleHotkey(previousHotkey, out var rollbackFailure))
            {
                _logger.Info($"Hotkey rollback failed after settings persistence error. Reason={rollbackFailure}");
            }

            if (!await TryRollbackSwitchedProfileAsync())
            {
                return SettingsApplyResult.Fail(
                    "Die Einstellungen konnten nicht gespeichert und das neue Chrome-Profil nicht sicher zurückgerollt werden. Bitte ORhom neu starten.");
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
        _recordingOverlay.ApplySizePreset(recordingOverlaySize);
        _logger.Info($"Settings applied from UI. ChromeProfileDirectory='{chromeProfile.DirectoryName}' MicrophoneName='{microphoneName}' ToggleHotkey='{hotkey}' RecordingOverlaySize='{recordingOverlaySize}'.");
        ShowMessage(localResourcesReleased
            ? $"ORhom läuft jetzt mit {hotkey} im Hintergrund."
            : "Die Browser-Diktierung ist aktiv, aber das lokale GPU-Modell konnte nicht freigegeben werden. Ein Neustart von ORhom gibt die Ressourcen frei.");
        return SettingsApplyResult.Success(microphoneName, hotkey);
    }

    private async Task<SettingsApplyResult> ApplyLocalSettingsCoreAsync(
        string microphoneId,
        string microphoneName,
        string hotkey,
        RecordingOverlaySize recordingOverlaySize)
    {
        if (!_audioInputDevices.IsMicrophoneActive(microphoneId, microphoneName))
        {
            return SettingsApplyResult.Fail("Das ausgewählte Mikrofon ist nicht mehr verbunden.");
        }

        var previousProvider = _settings.DictationProvider;
        var previousMicrophoneId = _settings.PreferredMicrophoneId;
        var previousMicrophoneName = _settings.PreferredMicrophoneName;
        var previousHotkey = _settings.ToggleHotkey;
        var previousRecordingOverlaySize = _settings.RecordingOverlaySize;
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
        _settings.RecordingOverlaySize = recordingOverlaySize;
        _settings.SetupCompleted = true;
        if (!_settings.Save(_logger))
        {
            _settings.DictationProvider = previousProvider;
            _settings.PreferredMicrophoneId = previousMicrophoneId;
            _settings.PreferredMicrophoneName = previousMicrophoneName;
            _settings.ToggleHotkey = previousHotkey;
            _settings.RecordingOverlaySize = previousRecordingOverlaySize;
            _settings.SetupCompleted = previousSetupCompleted;
            _ = _hotkeyWindow.TryUpdateToggleHotkey(previousHotkey, out _);
            return SettingsApplyResult.Fail(
                "Die Einstellungen konnten nicht sicher gespeichert werden. Bitte Schreibrechte und freien Speicherplatz prüfen.");
        }

        _recordingOverlay.SetToggleHotkey(hotkey);
        _recordingOverlay.ApplySizePreset(recordingOverlaySize);
        QueueLocalWhisperPreparation();
        _logger.Info($"Local settings applied. MicrophoneId='{microphoneId}' MicrophoneName='{microphoneName}' ToggleHotkey='{hotkey}' RecordingOverlaySize='{recordingOverlaySize}'.");
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
            ShowMessage("ORhom verarbeitet gerade eine andere Aktion.");
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
        ApplyTrayStatePresentation();
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
        _notifyIcon.BalloonTipTitle = "ORhom";
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
        ShowErrorMessage($"Chrome-Profil {_settings.ChromeProfileDirectory} nicht gefunden. Bitte ORhom öffnen und ein Profil auswählen.");
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
        ApplyTrayStatePresentation();
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
                exitFailureMessage = "ORhom bleibt geöffnet, solange eine Aufnahme noch aktiv oder nicht sicher abgeschlossen ist. Bitte zuerst F8 oder Escape drücken.";
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
                var backgroundWindowReleased = await ChatGptWindowFinder.CloseOwnedBackgroundWindowAndWaitAsync(
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
                    exitFailureMessage = "ORhom bleibt geöffnet, weil das eigene ChatGPT-Hintergrundfenster noch nicht sicher geschlossen wurde. Bitte Beenden erneut versuchen.";
                }
            }
        }
        catch (Exception ex)
        {
            exitAllowed = false;
            exitFailureMessage = "ORhom konnte nicht sicher beendet werden. Bitte den Vorgang erneut versuchen.";
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
            ApplyTrayStatePresentation();
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
