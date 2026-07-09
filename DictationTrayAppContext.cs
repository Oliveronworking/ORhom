using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal sealed class DictationTrayAppContext : ApplicationContext
{
    private readonly AppLogger _logger;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly FocusTracker _focusTracker;
    private readonly PasteService _pasteService;
    private readonly AppSettings _settings;
    private readonly ChromeProfileLauncher _chromeProfileLauncher;
    private readonly ChatGptDictationController _dictationController;
    private readonly RecordingOverlayForm _recordingOverlay;
    private AppStatus _status = AppStatus.Idle;
    private RecordingSession? _session;
    private IntPtr _overlayTargetWindow;

    public DictationTrayAppContext()
    {
        var baseDirectory = AppContext.BaseDirectory;
        _logger = new AppLogger(Path.Combine(baseDirectory, "logs"));
        _settings = AppSettings.Load(Path.Combine(baseDirectory, "settings.json"), _logger);
        _chromeProfileLauncher = new ChromeProfileLauncher(_settings, _logger);
        _dictationController = new ChatGptDictationController(_settings, _logger, _chromeProfileLauncher);
        _recordingOverlay = new RecordingOverlayForm(_settings.RecordingOverlayBottomOffsetPx);
        _focusTracker = new FocusTracker(_logger);
        _pasteService = new PasteService(_logger);
        _logger.Info("Application started.");
        var initialProfileValidation = _chromeProfileLauncher.ValidateConfiguredProfile();

        _statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Aufnahme abbrechen", null, (_, _) => _ = AbortRecordingAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("ChatGPT Profil öffnen", null, (_, _) => _ = OpenChatGptProfileAsync())
        {
            Enabled = _settings.OpenChatGptProfileVisibleForSetup
        });
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profil prüfen", null, (_, _) => CheckChromeProfile()));
        menu.Items.Add(new ToolStripMenuItem("ChatGPT Diagnose speichern", null, (_, _) => _ = WriteChatGptDiagnosticsAsync())
        {
            Enabled = _settings.EnableChatGptInputDiagnostics
        });
        menu.Items.Add(new ToolStripMenuItem("Chrome-Profilordner öffnen", null, (_, _) => OpenChromeProfileDirectory()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Einstellungen öffnen", null, (_, _) => OpenPath(_settings.SettingsPath)));
        menu.Items.Add(new ToolStripMenuItem("Logs öffnen", null, (_, _) => OpenPath(_logger.LogPath)));
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

        if (!initialProfileValidation.IsValid)
        {
            if (_settings.WarnIfConfiguredChromeProfileUnavailable)
            {
                ShowConfiguredProfileUnavailable();
            }
        }
        else
        {
            QueueChatGptStartupPreparation();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _hotkeyWindow.Dispose();
            _recordingOverlay.Dispose();
            _dictationController.Dispose();
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
            RestoreTargetAndClipboard(session.Target);
            ShowMessage(stopResult.Message);
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.ReadingText);
        var readResult = await _dictationController.ReadDictatedTextAsync(session.ChatWindow);
        var text = readResult.Text.Trim();
        _logger.Info($"Dictation read completed. Attempts={readResult.Attempts} Method={readResult.Method} TextLength={text.Length}");
        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text))
        {
            RestoreTargetAndClipboard(session.Target);
            ShowMessage("Nach dem Stoppen wurde kein Text transkribiert.");
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Pasting);
        var pasteSucceeded = _pasteService.PasteIntoTarget(text, session.Target, _settings);
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
            await _dictationController.AbortDictationAsync(session.ChatWindow);
            RestoreTargetAndClipboard(session.Target);
            ShowMessage("Aufnahme abgebrochen.");
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

    private sealed record RecordingSession(
        FocusTarget Target,
        IntPtr ChatWindow,
        AutomationElement? ChatInput);
}
