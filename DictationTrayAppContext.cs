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
            "ORhom",
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
            "ORhom");
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
            ToolTipText = "Verwirft die laufende Aufnahme ohne EinfÃ¼gen."
        };
        _lastDictationItem = new ToolStripMenuItem(
            "Letztes Diktat einfÃ¼gen",
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
                "FÃ¼gt den letzten Text ins zuvor aktive Feld ein; bei Bedarf bleibt er in der Zwischenablage."
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
            "ORhom Ã¶ffnen",
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
            Text = "ORhom Â· Bereit",
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
            "ChatGPT-Profil Ã¶ffnen",
            null,
            (_, _) => RunUserOperation(
                OpenChatGptProfileAsync,
                "open-chatgpt-profile"))
        {
            Name = "open-chatgpt-profile"
        });
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Chrome-Profil prÃ¼fen",
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
            "Chrome-Profilordner Ã¶ffnen",
            null,
            (_, _) => OpenChromeProfileDirectory())
        {
            Name = "open-chrome-profile-directory"
        });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Konfigurationsdatei Ã¶ffnen",
            null,
            (_, _) => OpenPath(_settings.SettingsPath)));
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Logs Ã¶ffnen",
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
            ? "Noch kein Diktat zum EinfÃ¼gen"
            : "Letztes Diktat einfÃ¼gen";

        _microphoneMenu.Text = string.IsNullOrWhiteSpace(
            _settings.PreferredMicrophoneName)
            ? "Mikrofon auswÃ¤hlen"
            : $"Mikrofon Â· {Ellipsize(_settings.PreferredMicrophoneName, 34)}";
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
            ÛÍ;îÚ$z{-®éÜj×÷F¶W“Òw¶†÷F¶W—Òrâ"“°¢6†÷tÖW76vR‚B$Æö¶ÆRFWWG66†RF–·F–W'VærÌ:GVgB¦WG§BÖ—B¶†÷F¶W—Ò–Ò†–çFW&w'VæBâ"“°¢&WGW&â6WGF–æw4Ç•&W7VÇBå7V66W72†Ö–7&÷†öæTæÖRÂ†÷F¶W’“°¢Ð ¢&—fFRfö–B÷Vå6WGF–æw2‚¢°Ð¢–b…÷7FGW2Ò7FGW2ä–FÆRÐ¢°Ð¢6†÷tÖW76vR‚$&—GFR§VW'7BF–RÆVfVæFRVfæ†ÖR&VVæFVââ"“°Ð¢&WGW&ã°Ð¢ÐÐ Ð¢÷6WGF–æw4f÷&Òå6†÷tæD7F—fFR‚“°Ð¢ÐÐ Ð¢&—fFRfö–B÷Vä†—7F÷'’‚Ð¢°Ð¢ö†—7F÷'”f÷&Òå6†÷tæD7F—fFR‚“°Ð¢ÐÐ Ð¢&—fFR7–æ2F6³ÄF–7FF–öå&V6÷fW'•&W7VÇCâG'•&V6÷fW%FW‡EFô†—7F÷'”7–æ2€Ð¢–çEG"6†Ev–æF÷rÀÐ¢WFöÖF–öäVÆVÖVçCò&VfW'&VD–çWBÀÐ¢7G&–ær÷WF6öÖRÀÐ¢–çCòF–ÖV÷WD÷fW'&–FT×2ÒçVÆÂÀÐ¢7F–öãò&W7F÷&UF&vWDfö7W2ÒçVÆÂÀÐ¢&ööÂ6ÆV$6ö×÷6W$gFW%W'6—7FVæ6RÒG'VRÀÐ¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇBÐ¢°Ð¢f"&VE&W7VÇBÒv—BöF–7FF–öä6öçG&öÆÆW"å&VDF–7FFVEFW‡D7–æ2€Ð¢6†Ev–æF÷rÀÐ¢&VfW'&VD–çWBÀÐ¢F–ÖV÷WD÷fW'&–FT×3¢F–ÖV÷WD÷fW'&–FT×2ÀÐ¢&W7F÷&UF&vWDfö7W3¢&W7F÷&UF&vWDfö7W2ÀÐ¢6æ6VÆÆF–öåFö¶Vã¢6æ6VÆÆF–öåFö¶Vâ“°Ð¢f"FW‡BÒ&VE&W7VÇBåFW‡BåG&–Ò‚“°Ð¢öÆövvW"ä–æfò‚B$F–7FF–öâ&V6÷fW'’&VB6ö×ÆWFVBâGFV×G3×·&VE&W7VÇBäGFV×G7ÒÖWF†öC×·&VE&W7VÇBäÖWF†öGÒFW‡DÆVæwFƒ×·FW‡BäÆVæwF‡Ò"“°Ð¢–b‡FW‡BäÆVæwF‚ÓÒÇÂWFöÖF–öä†VÇW'2ä—5Vç6fT6GW&VEFW‡B‡FW‡B’Ð¢°Ð¢&WGW&âæWrF–7FF–öå&V6÷fW'•&W7VÇB€¢fÇ6RÀ¢&VE&W7VÇBä6Æ—&ö&E&W7F÷&Tf–ÆVBÀ¢fÇ6RÀ¢wV–BäV×G’À¢7G&–æräV×G’À¢÷WF6öÖR“°¢ÐÐ Ð¢f"W'6—7FVBÒö†—7F÷'’åG'”FB‡FW‡BÂ÷WF6öÖRÂ÷WBf"†—7F÷'”VçG'”–B“°¢f"6ÆV&VBÒW'6—7FVBb`¢‚6ÆV$6ö×÷6W$gFW%W'6—7FVæ6RÇÀ¢v—BöF–7FF–öä6öçG&öÆÆW"ä6ÆV%W'6—7FVDF–7FF–öä7–æ2€¢6†Ev–æF÷rÀ¢&VE&W7VÇBä–çWBóò&VfW'&VD–çWBÀ¢&W7F÷&UF&vWDfö7W2À¢FW‡B’“°¢–b‡W'6—7FVBbb6ÆV$6ö×÷6W$gFW%W'6—7FVæ6R¢°¢WFFU&V6÷fW&VD6ÆVçW÷WF6öÖR††—7F÷'”VçG'”–BÂ÷WF6öÖRÂ6ÆV&VB“°¢Ð ¢&WGW&âæWrF–7FF–öå&V6÷fW'•&W7VÇB€¢W'6—7FVBÀ¢&VE&W7VÇBä6Æ—&ö&E&W7F÷&Tf–ÆVBÀ¢W'6—7FVBbb6ÆV$6ö×÷6W$gFW%W'6—7FVæ6Rbb6ÆV&VBÀ¢†—7F÷'”VçG'”–BÀ¢FW‡BÀ¢÷WF6öÖR“°¢ÐÐ Ð¢&—fFR7–æ2F6²w&—FT6†DwDF–væ÷7F–747–æ2‚Ð¢°Ð¢–b‚÷6WGF–æw2äVæ&ÆT6†DwD–çWDF–væ÷7F–72Ð¢°Ð¢6†÷tÖW76vR‚$6†DuBÔF–væ÷6R—7B–âFVâV–ç7FVÆÇVævVâFV·F—f–W'Bâ"“°Ð¢&WGW&ã°Ð¢ÐÐ Ð¢–b‚v—Bö÷W&F–öäÆö6²åv—D7–æ2ƒ’Ð¢°Ð¢6†÷tÖW76vR‚$õ&†öÒfW&&&V—FWBvW&FRV–æRæFW&R·F–öââ"“°Ð¢&WGW&ã°Ð¢ÐÐ Ð¢G'Ð¢°Ð¢f"v–æF÷rÒ÷6W76–öãòä6†Ev–æF÷róòöF–7FF–öä6öçG&öÆÆW"ä¶æ÷vä6†Ev–æF÷s°Ð¢v—BöF–7FF–öä6öçG&öÆÆW"äF–væ÷6T6†DwEV”7–æ2‡v–æF÷r“°Ð¢6†÷tÖW76vR‚$6†DuBF–væ÷6RwW&FR–ç2ÆörvW66‡&–V&Vââ"“°Ð¢ÐÐ¢f–æÆÇÐ¢°Ð¢ö÷W&F–öäÆö6²å&VÆV6R‚“°Ð¢ÐÐ¢ÐÐ Ð¢&—fFRfö–B÷Vä6‡&öÖU&öf–ÆTF—&V7F÷'’‚Ð¢°Ð¢f"fÆ–FF–öâÒöF–7FF–öä6öçG&öÆÆW"åfÆ–FFT6öæf–wW&VE&öf–ÆR‚“°Ð¢–b‚fÆ–FF–öâä—5fÆ–BÐ¢°Ð¢6†÷t6öæf–wW&VE&öf–ÆUVæf–Æ&ÆR‚“°Ð¢&WGW&ã°Ð¢ÐÐ Ð¢÷VåF‚‡fÆ–FF–öâå&öf–ÆTF—&V7F÷'•F‚“°Ð¢ÐÐ Ð¢&—fFR&ööÂG'•&W6W'fUVæF–æt6ö×÷6W$&Vf÷&T6Æ÷6R†÷WB7G&–ærf–ÇW&TÖW76vR’ÓàÐ¢G'•&W6W'fUVæF–æt6ö×÷6W$&Vf÷&T6Æ÷6R€Ð¢÷WBf–ÇW&TÖW76vRÀÐ¢÷WBò“°Ð Ð¢&—fFR&ööÂG'•&W6W'fUVæF–æt6ö×÷6W$&Vf÷&T6Æ÷6R€Ð¢÷WB7G&–ærf–ÇW&TÖW76vRÀÐ¢÷WB7G&–æsò&W6W'fVD6ö×÷6W%FW‡BÐ¢°Ð¢f"–ç7V7F–öâÒöF–7FF–öä6öçG&öÆÆW"ä–ç7V7EVæF–æt6ö×÷6W"‚“°Ð¢f"&W6W'fF–öâÒVæF–æt6ö×÷6W%&W6W'fF–öâå&W6W'fR€Ð¢–ç7V7F–öâÀÐ¢ö†—7F÷'’ä6öçF–ç5FW‡BÀÐ¢FW‡BÓâö†—7F÷'’åG'”FB€Ð¢FW‡BÀÐ¢F–7FF–öä†—7F÷'”÷WF6öÖW2å&W6W'fVD&Vf÷&T6Æ÷6RÀÐ¢÷WBò’“°Ð¢–b…VæF–æt6ö×÷6W%&W6W'fF–öâä—56fUFô6Æ÷6R‡&W6W'fF–öâ’Ð¢°Ð¢&W6W'fVD6ö×÷6W%FW‡BÒ–ç7V7F–öâå7FFRÓÒVæF–æt6ö×÷6W%7FFRåFW‡@Ð¢ò–ç7V7F–öâåFW‡@Ð¢¢çVÆÃ°Ð¢–b†–ç7V7F–öâå7FFRÓÒVæF–æt6ö×÷6W%7FFRåFW‡BÐ¢°Ð¢öÆövvW"ä–æfò‚B%VæF–ær6ö×÷6W"FW‡B&W6W'fVB&Vf÷&R÷væVB×v–æF÷r6Æ÷6RâFW‡DÆVæwFƒ×¶–ç7V7F–öâåFW‡BäÆVæwF‡Ò÷WF6öÖS×·&W6W'fF–öçÒ"“°Ð¢ÐÐ Ð¢f–ÇW&TÖW76vRÒ7G&–æräV×G“°Ð¢&WGW&âG'VS°Ð¢ÐÐ Ð¢–b‡&W6W'fF–öâÓÒVæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRåW'6—7FVæ6Tf–ÆVBÐ¢°Ð¢&W6W'fVD6ö×÷6W%FW‡BÒçVÆÃ°Ð¢f–ÇW&TÖW76vRÒ$–Ò6†DuBÔVçGwW&bÆ–VwBæö6‚FW‡BÂFW"æ–6‡B–ÒF–·F–W'fW&ÆVbvW7V–6†W'BvW&FVâ¶öæçFRâF2&öf–ÆfVç7FW"&ÆV–'B§VÒ66‡WG¢FW2FW‡FW2v\;fffæWBâ#°Ð¢&WGW&âfÇ6S°Ð¢ÐÐ Ð¢&W6W'fVD6ö×÷6W%FW‡BÒçVÆÃ°Ð¢f–ÇW&TÖW76vRÒ$FW"6†DuBÔVçGwW&b¶öæçFRæ–6‡B6–6†W"vW,;ÆgBvW&FVââF2&öf–ÆvV'VæFVæR†–çFW&w'VæFfVç7FW"&ÆV–'B§VÒ66‡WG¢Ü;fvÆ–6†W"FW‡FRv\;fffæWC²&—GFRFVâf÷&værW&æWWBfW'7V6†Vââ#°Ð¢&WGW&âfÇ6S°Ð¢ÐÐ Ð¢&—fFRfö–B&W7F÷&UF&vWDfö7W2„fö7W5F&vWBF&vWB¢°Ð¢÷7FU6W'f–6Rå&W7F÷&UF&vWDfö7W2‡F&vWB“°¢Ð ¢&—fFRfö–BWFFU&V6÷fW&VD6ÆVçW÷WF6öÖR€¢wV–B†—7F÷'”VçG'”–BÀ¢7G&–ær&V6÷fW'”÷WF6öÖRÀ¢&ööÂ6ÆVçW6ö×ÆWFVB¢°¢–b††—7F÷'”VçG'”–BÓÒwV–BäV×G’¢°¢&WGW&ã°¢Ð ¢f"÷WF6öÖRÒ6ÆVçW6ö×ÆWFV@¢ò&V6÷fW'”÷WF6öÖRÓÒF–7FF–öä†—7F÷'”÷WF6öÖW2ä6æ6VÆÆVE&V6÷fW&V@¢òF–7FF–öä†—7F÷'”÷WF6öÖW2ä6æ6VÆÆVE&V6÷fW&VD6ÆV&V@¢¢F–7FF–öä†—7F÷'”÷WF6öÖW2äf–ÇW&U&V6÷fW&VD6ÆV&V@¢¢F–7FF–öä†—7F÷'”÷WF6öÖW2åVæF–æt6ö×÷6W$6ÆVçW°¢ö†—7F÷'’åWFFT÷WF6öÖR††—7F÷'”VçG'”–BÂ÷WF6öÖR“°¢Ð ¢&—fFRfö–Böä÷fW&Æ•Æ6VÖVçD6öÖÖ—GFVB€¢ö&¦V7Cò6VæFW"À¢&V6÷&F–æt÷fW&Æ•Æ6VÖVçDWfVçD&w2R¢°¢f"&Wf–÷W4Ööæ—F÷"Ò÷6WGF–æw2å&V6÷&F–æt÷fW&Æ”Ööæ—F÷$FWf–6TæÖS°¢f"&Wf–÷W5&VÆF—fU‚Ò÷6WGF–æw2å&V6÷&F–æt÷fW&Æ•&VÆF—fUƒ°¢f"&Wf–÷W5&VÆF—fU’Ò÷6WGF–æw2å&V6÷&F–æt÷fW&Æ•&VÆF—fU“°¢÷6WGF–æw2å&V6÷&F–æt÷fW&Æ”Ööæ—F÷$FWf–6TæÖRÒRåÆ6VÖVçBäÖöæ—F÷$FWf–6TæÖS°¢÷6WGF–æw2å&V6÷&F–æt÷fW&Æ•&VÆF—fU‚ÒRåÆ6VÖVçBå&VÆF—fUƒ°¢÷6WGF–æw2å&V6÷&F–æt÷fW&Æ•&VÆF—fU’ÒRåÆ6VÖVçBå&VÆF—fU“°¢–b…÷6WGF–æw2å6fR…öÆövvW"’¢°¢öÆövvW"ä–æfò‚B%&V6÷&F–ær&"÷6—F–öâ6fVBâÖöæ—F÷#Òw¶RåÆ6VÖVçBäÖöæ—F÷$FWf–6TæÖWÒr&VÆF—fUƒ×¶RåÆ6VÖVçBå&VÆF—fUƒ¤cGÒ&VÆF—fU“×¶RåÆ6VÖVçBå&VÆF—fU“¤cGÒ"“°¢&WGW&ã°¢Ð ¢÷6WGF–æw2å&V6÷&F–æt÷fW&Æ”Ööæ—F÷$FWf–6TæÖRÒ&Wf–÷W4Ööæ—F÷#°¢÷6WGF–æw2å&V6÷&F–æt÷fW&Æ•&VÆF—fU‚Ò&Wf–÷W5&VÆF—fUƒ°¢÷6WGF–æw2å&V6÷&F–æt÷fW&Æ•&VÆF—fU’Ò&Wf–÷W5&VÆF—fU“°¢6†÷tW'&÷$ÖW76vR‚$F–R÷6—F–öâFW"F–·F–W&ÆV—7FR¶öæçFRæ–6‡BvW7V–6†W'BvW&FVââ"“°¢Ð Ð¢&—fFRfö–B&W6WEFô–FÆR‚¢°¢7F÷VF–ôGV6¶–ær‚“°¢–b…öÆö6Å6W76–öâ—2²ÒÆö6Å6W76–öâ¢°¢Æö6Å6W76–öâä6GW&Rå&V6÷&F–ætÆ–Ö—E&V6†VBÓÒöäÆö6Å&V6÷&F–ætÆ–Ö—E&V6†VC°¢Æö6Å6W76–öâä6GW&RåVæW‡V7FVFÇ•7F÷VBÓÒöäÆö6Ä6GW&UVæW‡V7FVFÇ•7F÷VC°¢Æö6Å6W76–öâä6GW&RäF—7÷6R‚“°¢öÆö6Å6W76–öâÒçVÆÃ°¢Ð¢÷6W76–öâÒçVÆÃ°¢÷VWVVE7F÷&WVW7FVBÒfÇ6S°Ð¢ö†÷F¶W•v–æF÷rå6WDW66TVæ&ÆVB†fÇ6R“°¢6WE7FGW2„7FGW2ä–FÆR“°¢ÐÐ Ð¢&—fFRfö–B7F÷VF–ôGV6¶–ær‚Ð¢°Ð¢öVF–ôGV6¶–æuF–ÖW"å7F÷‚“°Ð¢öVF–ôGV6¶–ærå&W7F÷&R‚“°Ð¢ÐÐ Ð¢&—fFRfö–B6WE7FGW2„7FGW27FGW2¢°¢÷7FGW2Ò7FGW3°¢Ç•G&•7FFU&W6VçFF–öâ‚“°¢–b…÷6WGF–æw2å6†÷u&V6÷&F–æt÷fW&Æ’¢°¢÷&V6÷&F–æt÷fW&Æ’å6†÷u7FGW2‡7FGW2“°¢Ð¢VÇ6P¢°¢÷&V6÷&F–æt÷fW&Æ’ä†–FT÷fW&Æ’‚“°¢Ð¢öÆövvW"ä–æfò‚B%7FGW26†ævVC¢·7FGW7Ò"“°Ð¢ÐÐ Ð¢&—fFRfö–B6†÷tÖW76vR‡7G&–ærÖW76vR¢°Ð¢öæ÷F–g”–6öâä&ÆÆööåF—F—FÆRÒ$õ&†öÒ#°Ð¢öæ÷F–g”–6öâä&ÆÆööåF—FW‡BÒÖW76vS°Ð¢öæ÷F–g”–6öâå6†÷t&ÆÆööåF—ƒ3“°¢Ð ¢&—fFRfö–B6†÷tW'&÷$ÖW76vR‡7G&–ærÖW76vR¢°¢6†÷tÖW76vR†ÖW76vR“°¢–b…÷6WGF–æw2å6†÷u&V6÷&F–æt÷fW&Æ’¢°¢÷&V6÷&F–æt÷fW&Æ’å6†÷uG&ç6–VçDW'&÷"†ÖW76vR“°¢Ð¢Ð Ð¢&—fFRfö–B6†÷t6öæf–wW&VE&öf–ÆUVæf–Æ&ÆR‚¢°¢6†÷tW'&÷$ÖW76vR‚B$6‡&öÖRÕ&öf–Âµ÷6WGF–æw2ä6‡&öÖU&öf–ÆTF—&V7F÷'—Òæ–6‡BvVgVæFVââ&—GFRõ&†öÒ;fffæVâVæBV–â&öf–ÂW7|:F†ÆVââ"“°¢ÐÐ Ð¢&—fFRfö–B÷VåF‚‡7G&–ærF‚Ð¢°Ð¢G'Ð¢°Ð¢W6–ærf"&ö6W72Ò&ö6W72å7F'B†æWr&ö6W757F'D–æfò‡F‚’²W6U6†VÆÄW†V7WFRÒG'VRÒ“°Ð¢ÐÐ¢6F6‚„W†6WF–öâW‚Ð¢°Ð¢öÆövvW"äW'&÷"‚B$6÷VÆBæ÷B÷VâFƒ¢·F‡Ò"ÂW‚“°Ð¢6†÷tW'&÷$ÖW76vR‚$FFV’¶öæçFRæ–6‡Bv\;fffæWBvW&FVââ"“°¢ÐÐ¢ÐÐ Ð¢&—fFR7–æ2F6²W†—D7–æ2‚¢°Ð¢–b…öW†—D–å&öw&W72Ð¢°Ð¢&WGW&ã°Ð¢ÐÐ Ð¢öW†—D–å&öw&W72ÒG'VS°¢ö†÷F¶W•v–æF÷rå6WEFövvÆTVæ&ÆVB†fÇ6R“°¢÷&V6÷&F–æt÷fW&Æ’å6WD–çFW&7F–öäVæ&ÆVB†fÇ6R“°¢Ç•G&•7FFU&W6VçFF–öâ‚“°¢öF–7FF–öå&VD6æ6VÆÆF–öãòä6æ6VÂ‚“°¢÷7F'GW6æ6VÆÆF–öâä6æ6VÂ‚“°¢òÒv—B7F÷Æö6Åv†—7W%&W&F–öä7–æ2‡VæÆöDÖöFVÃ¢fÇ6R“°¢v—B÷7F'GW&W&F–öåF6³°¢f"W†—DÆÆ÷vVBÒG'VS°Ð¢f"W†—Df–ÇW&TÖW76vRÒ7G&–æräV×G“°Ð¢v—Bö÷W&F–öäÆö6²åv—D7–æ2‚“°Ð¢G'Ð¢°Ð¢f"6W76–öä7F—fRÒ÷6W76–öâ—2æ÷BçVÆÂÇÂöÆö6Å6W76–öâ—2æ÷BçVÆÃ°¢–b‡6W76–öä7F—fRbb÷7FGW2Ò7FGW2ä–FÆR¢°Ð¢W†—DÆÆ÷vVBÒfÇ6S°Ð¢W†—Df–ÇW&TÖW76vRÒ$õ&†öÒ&ÆV–'Bv\;fffæWBÂ6öÆævRV–æRVfæ†ÖRæö6‚·F—böFW"æ–6‡B6–6†W"&vW66†Æ÷76Vâ—7Bâ&—GFR§VW'7Bc‚öFW"W66RG,;Æ6¶Vââ#°Ð¢ÐÐ¢VÇ6R–b„F–7FF–öå&÷f–FW'2ä—4Æö6Â…÷6WGF–æw2äF–7FF–öå&÷f–FW"’¢°¢öÆövvW"ä–æfò‚$Æ–6F–öâW†—F–ærg&öÒÆö6ÂF–7FF–öâÖöFS²æò'&÷w6W"v–æF÷r6ÆVçW—2&WV—&VBâ"“°¢Ð¢VÇ6R–b‚G'•&W6W'fUVæF–æt6ö×÷6W$&Vf÷&T6Æ÷6R†÷WBW†—Df–ÇW&TÖW76vR’¢°Ð¢W†—DÆÆ÷vVBÒfÇ6S°Ð¢–b„6†DwEv–æF÷tf–æFW"å&VÆV6T÷væVD&6¶w&÷VæEv–æF÷uFõW6W"€Ð¢6‡&öÖU&öf–ÆT–FVçF—G’äg&öÒ…÷6WGF–æw2’ÀÐ¢öÆövvW"’Ð¢°Ð¢W†—Df–ÇW&TÖW76vR³Ò"F2†–çFW&w'VæFfVç7FW"wW&FR6–6‡F&"§W"ÖçVVÆÆVâFW‡G&WGGVærg&V–vVvV&Vã²|:F†ÆVâ6–RFæ6‚W&æWWB&VVæFVââ#°Ð¢ÐÐ¢ÐÐ¢VÇ6PÐ¢°Ð¢f"&öf–ÆT–FVçF—G’Ò6‡&öÖU&öf–ÆT–FVçF—G’äg&öÒ…÷6WGF–æw2“°Ð¢f"&6¶w&÷VæEv–æF÷uv5&W6VçBÐÐ¢6†DwEv–æF÷tf–æFW"äf–æD÷væVD&6¶w&÷VæEv–æF÷r…÷6WGF–æw2’Ò–çEG"å¦W&ó°Ð¢f"&6¶w&÷VæEv–æF÷u&VÆV6VBÒ6†DwEv–æF÷tf–æFW"ä6Æ÷6T÷væVD&6¶w&÷VæEv–æF÷tæEv—B€Ð¢&öf–ÆT–FVçF—G’ÀÐ¢öÆövvW"“°Ð¢–b‚&6¶w&÷VæEv–æF÷u&VÆV6VBÐ¢°Ð¢&6¶w&÷VæEv–æF÷u&VÆV6VBÒ6†DwEv–æF÷tf–æFW"å&VÆV6T÷væVD&6¶w&÷VæEv–æF÷uFõW6W"€Ð¢&öf–ÆT–FVçF—G’ÀÐ¢öÆövvW"“°Ð¢ÐÐ Ð¢öÆövvW"ä–æfò‚&6¶w&÷VæEv–æF÷uv5&W6Vç@Ð¢ò$Æ–6F–öâW†—F–æs²æò&öf–ÆR×66÷VB&6¶w&÷VæBv–æF÷ræVVFVB6Æ÷6–ærâ Ð¢¢&6¶w&÷VæEv–æF÷u&VÆV6V@Ð¢ò$Æ–6F–öâW†—F–æs²&öf–ÆR×66÷VB&6¶w&÷VæB÷væW'6†—v26fVÇ’&VÆV6VBâ Ð¢¢$Æ–6F–öâW†—BW6VC²&öf–ÆR×66÷VB&6¶w&÷VæBv–æF÷r6Æ÷7W&Rv2æ÷B6öæf—&ÖVBâ"“°Ð¢–b‚&6¶w&÷VæEv–æF÷u&VÆV6VBÐ¢°Ð¢W†—DÆÆ÷vVBÒfÇ6S°Ð¢W†—Df–ÇW&TÖW76vRÒ$õ&†öÒ&ÆV–'Bv\;fffæWBÂvV–ÂF2V–vVæR6†DuBÔ†–çFW&w'VæFfVç7FW"æö6‚æ–6‡B6–6†W"vW66†Æ÷76VâwW&FRâ&—GFR&VVæFVâW&æWWBfW'7V6†Vââ#°Ð¢ÐÐ¢ÐÐ¢ÐÐ¢6F6‚„W†6WF–öâW‚Ð¢°Ð¢W†—DÆÆ÷vVBÒfÇ6S°Ð¢W†—Df–ÇW&TÖW76vRÒ$õ&†öÒ¶öæçFRæ–6‡B6–6†W"&VVæFWBvW&FVââ&—GFRFVâf÷&værW&æWWBfW'7V6†Vââ#°Ð¢öÆövvW"äW'&÷"‚$Æ–6F–öâW†—B6fWG’6†V6·2f–ÆVBâ"ÂW‚“°Ð¢ÐÐ¢f–æÆÇÐ¢°Ð¢ö÷W&F–öäÆö6²å&VÆV6R‚“°Ð¢ÐÐ Ð¢–b‚W†—DÆÆ÷vVBÐ¢°Ð¢öW†—D–å&öw&W72ÒfÇ6S°¢f"–çFW&7F–öäVæ&ÆVBÒ÷6WGF–æw4f÷&Òåf—6–&ÆS°¢ö†÷F¶W•v–æF÷rå6WEFövvÆTVæ&ÆVB†–çFW&7F–öäVæ&ÆVB“°¢÷&V6÷&F–æt÷fW&Æ’å6WD–çFW&7F–öäVæ&ÆVB†–çFW&7F–öäVæ&ÆVB“°¢ö†÷F¶W•v–æF÷rå6WDW66TVæ&ÆVB…÷7FGW2ÓÒ7FGW2å&V6÷&F–ær“°¢Ç•G&•7FFU&W6VçFF–öâ‚“°¢6†÷tÖW76vR†W†—Df–ÇW&TÖW76vR“°¢&WGW&ã°Ð¢ÐÐ ¢÷7F'GW6æ6VÆÆF–öâä6æ6VÂ‚“°¢öÆ–fWF–ÖT6æ6VÆÆF–öâä6æ6VÂ‚“°¢W†—EF‡&VB‚“°¢ÐÐ Ð¢&—fFR7FF–2–6öâÆöDÆ–6F–öä–6öâ‚Ð¢°Ð¢G'Ð¢°Ð¢f"W†V7WF&ÆUF‚ÒVçf—&öæÖVçBå&ö6W75F‚óòÆ–6F–öâäW†V7WF&ÆUFƒ°Ð¢&WGW&â–6öâäW‡G&7D76ö6–FVD–6öâ†W†V7WF&ÆUF‚’óò„–6öâ•7—7FVÔ–6öç2äÆ–6F–öâä6ÆöæR‚“°Ð¢ÐÐ¢6F6€Ð¢°Ð¢&WGW&â„–6öâ•7—7FVÔ–6öç2äÆ–6F–öâä6ÆöæR‚“°Ð¢ÐÐ¢ÐÐ Ð¢&—fFR6VÆVB&V6÷&B&V6÷&F–æu6W76–öâ€¢fö7W5F&vWBF&vWBÀ¢–çEG"6†Ev–æF÷rÀ¢WFöÖF–öäVÆVÖVçCò6†D–çWB“° ¢&—fFR6VÆVB&V6÷&BÆö6Å&V6÷&F–æu6W76–öâ€¢fö7W5F&vWBF&vWBÀ¢Æö6ÄVF–ô6GW&U6W76–öâ6GW&R“° Ð¢&—fFR6VÆVB&V6÷&BF–7FF–öå&V6÷fW'•&W7VÇB€¢&ööÂFW‡E&V6÷fW&VBÀ¢&ööÂ6Æ—&ö&E&W7F÷&Tf–ÆVBÀ¢&ööÂ6ö×÷6W$6ÆVçWf–ÆVBÀ¢wV–B†—7F÷'”VçG'”–BÀ¢7G&–ærFW‡BÀ¢7G&–ær÷WF6öÖR“°§ÐÐ Ð¦–çFW&æÂ7FF–26Æ72&÷'E&V6÷fW'•W'6—7FVæ6PÐ§°Ð¢V&Æ–27FF–27–æ2F6³Å&V6÷fW'•W'6—7FVæ6U&W7VÇCâ6fUF†Vä6ÆV$7–æ2€Ð¢7G&–ærFW‡BÀÐ¢gVæ3Ç7G&–ærÂ&ööÃâ6fU7–æ6‡&öæ÷W6Ç’ÀÐ¢gVæ3ÅF6³Æ&ööÃãâ6ÆV%W'6—7FVD7–æ2Ð¢°Ð¢–b‚6fU7–æ6‡&öæ÷W6Ç’‡FW‡B’Ð¢°Ð¢&WGW&âæWr&V6÷fW'•W'6—7FVæ6U&W7VÇB†fÇ6RÂfÇ6R“°Ð¢ÐÐ Ð¢&WGW&âæWr&V6÷fW'•W'6—7FVæ6U&W7VÇB‡G'VRÂv—B6ÆV%W'6—7FVD7–æ2‚’“°Ð¢ÐÐ§ÐÐ Ð¦–çFW&æÂ6VÆVB&V6÷&B&V6÷fW'•W'6—7FVæ6U&W7VÇB†&ööÂW'6—7FVBÂ&ööÂ6ÆV&VB“°Ð