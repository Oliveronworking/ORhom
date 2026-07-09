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
    private readonly PasteService _pasteService;
    private readonly AppSettings _settings;
    private AppStatus _status = AppStatus.Idle;
    private RecordingSession? _session;

    public DictationTrayAppContext()
    {
        var baseDirectory = AppContext.BaseDirectory;
        _logger = new AppLogger(Path.Combine(baseDirectory, "logs"));
        _settings = AppSettings.Load(Path.Combine(baseDirectory, "settings.json"), _logger);
        _focusTracker = new FocusTracker(_logger);
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

        QueueChatGptStartupPreparation();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
        if (target.IsPasswordField)
        {
            _logger.Info("Recording blocked because target is a password field.");
            ShowMessage("Ziel ist ein Passwortfeld. Aufnahme wurde nicht gestartet.");
            SetTemporaryError();
            return;
        }

        var chatWindow = await ChatGptWindowFinder.FindOrLaunchAsync(_settings, _logger, target.WindowHandle);
        if (chatWindow == IntPtr.Zero)
        {
            ShowMessage("Kein ChatGPT-Fenster gefunden.");
            SetTemporaryError();
            return;
        }

        if (!TrySendChatGptDictationHotkey(chatWindow, knownInput: null, requireSafeInput: true, out var chatInput))
        {
            _pasteService.RestoreClipboard(target, _settings);
            ShowMessage("ChatGPT-Eingabefeld nicht sicher fokussiert. Es wurde kein Shortcut gesendet.");
            SetTemporaryError();
            return;
        }

        _session = new RecordingSession(target, chatWindow, chatInput);
        _hotkeyWindow.SetEscapeEnabled(true);
        SetStatus(AppStatus.Recording);

        if (_settings.RestoreTargetAfterStart)
        {
            _pasteService.RestoreTargetFocus(target);
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
            if (ChatGptWindowFinder.Find(_settings, _logger) != IntPtr.Zero)
            {
                _logger.Info("ChatGPT startup preparation skipped because a ChatGPT window is already open.");
                return;
            }

            var chatWindow = await ChatGptWindowFinder.FindOrLaunchAsync(_settings, _logger, excludedWindow: IntPtr.Zero);
            if (chatWindow == IntPtr.Zero)
            {
                _logger.Info("ChatGPT startup preparation did not find a ChatGPT window after launch.");
                return;
            }

            if (_settings.MinimizeChatGptAfterStartup)
            {
                NativeMethods.ShowWindow(chatWindow, NativeMethods.SwMinimize);
                _logger.Info("ChatGPT startup window minimized after preparation.");
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

    private async Task FinishWebDictationAsync()
    {
        var session = _session;
        if (session is null)
        {
            ResetToIdle();
            return;
        }

        SetStatus(AppStatus.Pasting);
        var chatWindow = NativeMethods.IsWindow(session.ChatWindow)
            ? session.ChatWindow
            : ChatGptWindowFinder.Find(_settings, _logger, session.Target.WindowHandle);
        if (chatWindow == IntPtr.Zero)
        {
            _pasteService.RestoreTargetFocus(session.Target);
            ShowMessage("ChatGPT-Fenster nicht mehr gefunden.");
            SetTemporaryError();
            return;
        }

        if (!TrySendChatGptDictationHotkey(chatWindow, session.ChatInput, requireSafeInput: false, out var chatInput))
        {
            _pasteService.RestoreTargetFocus(session.Target);
            ShowMessage("Stopp-Shortcut konnte nicht gesendet werden. Aufnahme laeuft weiter.");
            SetStatus(AppStatus.Recording);
            return;
        }

        await Task.Delay(Math.Max(_settings.SettleDelayMs, 0));
        chatInput = await WaitForChatGptInputAsync(chatWindow, chatInput ?? session.ChatInput);
        var text = await AutomationHelpers.CopyTextSafelyAsync(chatInput, chatWindow, _settings, _logger);
        if (text.Length == 0)
        {
            text = await AutomationHelpers.WaitForTextAsync(chatInput, _settings.ReadTextTimeoutMs, _logger);
        }

        text = text.Trim();
        _logger.Info($"Text read from ChatGPT web input. Length={text.Length}");

        if (text.Length == 0 || AutomationHelpers.IsUnsafeCapturedText(text))
        {
            _pasteService.RestoreTargetFocus(session.Target);
            _pasteService.RestoreClipboard(session.Target, _settings);
            ShowMessage("Kein sicherer diktierter Text in ChatGPT gefunden. Es wurde nichts eingefuegt.");
            ResetToIdle();
            return;
        }

        _ = AutomationHelpers.ClearTextSafely(chatInput, chatWindow, _settings, _logger);
        if (!_pasteService.PasteIntoTarget(text, session.Target, _settings))
        {
            ShowMessage("Text konnte nicht eingefuegt werden.");
        }

        ResetToIdle();
    }

    private Task AbortRecordingAsync()
    {
        if (_status != AppStatus.Recording)
        {
            return Task.CompletedTask;
        }

        _logger.Info("Abort requested.");
        var session = _session;
        if (session is not null && NativeMethods.IsWindow(session.ChatWindow))
        {
            _ = TrySendChatGptDictationHotkey(session.ChatWindow, session.ChatInput, requireSafeInput: false, out _);
            _pasteService.RestoreTargetFocus(session.Target);
            _pasteService.RestoreClipboard(session.Target, _settings);
        }

        ShowMessage("Aufnahme abgebrochen.");
        ResetToIdle();
        return Task.CompletedTask;
    }

    private bool TrySendChatGptDictationHotkey(
        IntPtr chatWindow,
        System.Windows.Automation.AutomationElement? knownInput,
        bool requireSafeInput,
        out System.Windows.Automation.AutomationElement? chatInput)
    {
        chatInput = null;
        if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _logger))
        {
            return false;
        }

        chatInput = AutomationHelpers.FocusKnownChatGptInput(knownInput, chatWindow, _settings, _logger) ??
                    AutomationHelpers.FocusChatGptInput(chatWindow, _settings, _logger);
        if (!AutomationHelpers.IsSafeChatGptInput(chatInput, chatWindow, _settings))
        {
            if (requireSafeInput)
            {
                _logger.Info("ChatGPT dictation hotkey skipped because focused element is not safe.");
                return false;
            }

            _logger.Info("ChatGPT dictation hotkey sent via recording fallback after a safe start.");
        }
        else
        {
            _logger.Info("ChatGPT input verified before dictation hotkey.");
        }

        KeyboardHelpers.SendHotkey(_settings.ChatGptDictationHotkey);
        _logger.Info($"ChatGPT dictation hotkey sent: {_settings.ChatGptDictationHotkey}");
        return true;
    }

    private async Task<System.Windows.Automation.AutomationElement?> WaitForChatGptInputAsync(
        IntPtr chatWindow,
        System.Windows.Automation.AutomationElement? knownInput)
    {
        var timeoutMs = Math.Max(_settings.ReadTextTimeoutMs, 1000);
        var start = Environment.TickCount64;

        while (Environment.TickCount64 - start < timeoutMs)
        {
            var chatInput = AutomationHelpers.FocusKnownChatGptInput(knownInput, chatWindow, _settings, _logger) ??
                            AutomationHelpers.FocusChatGptInput(chatWindow, _settings, _logger);
            if (AutomationHelpers.IsSafeChatGptInput(chatInput, chatWindow, _settings))
            {
                return chatInput;
            }

            await Task.Delay(250);
        }

        return null;
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

    private sealed record RecordingSession(
        FocusTarget Target,
        IntPtr ChatWindow,
        System.Windows.Automation.AutomationElement? ChatInput);
}
