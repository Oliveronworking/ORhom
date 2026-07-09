using System.ComponentModel;

namespace ChatGptDictationBridge;

internal sealed class HotkeyWindow : Form
{
    private const int ToggleHotkeyId = 1;
    private const int EscapeHotkeyId = 3;
    private readonly AppLogger _logger;
    private bool _escapeRegistered;
    private string _toggleHotkey;
    private bool _toggleRegistered;

    public HotkeyWindow(AppSettings settings, AppLogger logger)
    {
        _logger = logger;
        _toggleHotkey = settings.ToggleHotkey;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Load += (_, _) => Visible = false;

        _toggleRegistered = RegisterHotkey(ToggleHotkeyId, _toggleHotkey);
    }

    public event EventHandler? TogglePressed;
    public event EventHandler? EscapePressed;

    public void SetToggleEnabled(bool enabled)
    {
        if (enabled == _toggleRegistered)
        {
            return;
        }

        if (enabled)
        {
            _toggleRegistered = RegisterHotkey(ToggleHotkeyId, _toggleHotkey);
        }
        else
        {
            _ = NativeMethods.UnregisterHotKey(Handle, ToggleHotkeyId);
            _toggleRegistered = false;
            _logger.Info("Toggle hotkey temporarily suspended while settings are open.");
        }
    }

    public bool TryUpdateToggleHotkey(string hotkey, out string failureReason)
    {
        hotkey = hotkey.Trim();
        if (hotkey.Equals(_toggleHotkey, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = string.Empty;
            return true;
        }

        var shouldRemainRegistered = _toggleRegistered;
        if (_toggleRegistered)
        {
            _ = NativeMethods.UnregisterHotKey(Handle, ToggleHotkeyId);
            _toggleRegistered = false;
        }

        if (RegisterHotkey(ToggleHotkeyId, hotkey))
        {
            _toggleHotkey = hotkey;
            if (shouldRemainRegistered)
            {
                _toggleRegistered = true;
            }
            else
            {
                _ = NativeMethods.UnregisterHotKey(Handle, ToggleHotkeyId);
            }
            failureReason = string.Empty;
            return true;
        }

        if (shouldRemainRegistered)
        {
            _toggleRegistered = RegisterHotkey(ToggleHotkeyId, _toggleHotkey);
        }
        failureReason = $"Die Tastenkombination {hotkey} wird bereits verwendet oder ist ungültig.";
        return false;
    }

    public void SetEscapeEnabled(bool enabled)
    {
        if (enabled == _escapeRegistered)
        {
            return;
        }

        if (enabled)
        {
            _escapeRegistered = RegisterHotkey(EscapeHotkeyId, "Escape");
        }
        else
        {
            NativeMethods.UnregisterHotKey(Handle, EscapeHotkeyId);
            _escapeRegistered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
        {
            var id = m.WParam.ToInt32();
            if (id == ToggleHotkeyId)
            {
                TogglePressed?.Invoke(this, EventArgs.Empty);
            }
            else if (id == EscapeHotkeyId)
            {
                EscapePressed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        NativeMethods.UnregisterHotKey(Handle, ToggleHotkeyId);
        NativeMethods.UnregisterHotKey(Handle, EscapeHotkeyId);
        base.Dispose(disposing);
    }

    private bool RegisterHotkey(int id, string hotkey)
    {
        try
        {
            var (modifiers, key) = ParseHotkey(hotkey);
            modifiers |= HotkeyModifiers.NoRepeat;
            if (!NativeMethods.RegisterHotKey(Handle, id, modifiers, key))
            {
                var error = new Win32Exception();
                _logger.Error($"Hotkey registration failed for {hotkey}.", error);
                return false;
            }

            _logger.Info($"Hotkey registered: {hotkey}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Hotkey registration failed for {hotkey}.", ex);
            return false;
        }
    }

    private static (HotkeyModifiers Modifiers, Keys Key) ParseHotkey(string hotkey)
    {
        var modifiers = HotkeyModifiers.None;
        Keys key = Keys.None;
        foreach (var rawPart in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : rawPart;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Windows;
            }
            else if (!Enum.TryParse(part, ignoreCase: true, out key))
            {
                throw new InvalidOperationException($"Unsupported hotkey key: {part}");
            }
        }

        if (key == Keys.None)
        {
            throw new InvalidOperationException($"Unsupported hotkey: {hotkey}");
        }

        return (modifiers, key);
    }
}
