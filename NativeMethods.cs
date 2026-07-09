using System.Runtime.InteropServices;
using System.Text;

namespace ChatGptDictationBridge;

internal static class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const int SwMinimize = 6;
    public const int SwRestore = 9;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, HotkeyModifiers fsModifiers, Keys vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var length = Math.Max(GetWindowTextLength(hWnd), 0);
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static string GetWindowClass(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static bool ClickAt(int x, int y)
    {
        var restoreCursor = GetCursorPos(out var originalPosition);
        if (!SetCursorPos(x, y))
        {
            return false;
        }

        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(35);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        if (restoreCursor)
        {
            _ = SetCursorPos(originalPosition.X, originalPosition.Y);
        }

        return true;
    }

    public static bool MarkWindow(IntPtr hWnd, string propertyName)
    {
        return hWnd != IntPtr.Zero && SetProp(hWnd, propertyName, new IntPtr(1));
    }

    public static bool HasWindowMark(IntPtr hWnd, string propertyName)
    {
        return hWnd != IntPtr.Zero && GetProp(hWnd, propertyName) != IntPtr.Zero;
    }

    public static bool SetWindowOpacity(IntPtr hWnd, byte alpha)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }

        var extendedStyle = GetWindowLong(hWnd, GwlExStyle);
        if ((extendedStyle & WsExLayered) == 0)
        {
            _ = SetWindowLong(hWnd, GwlExStyle, extendedStyle | WsExLayered);
        }

        return SetLayeredWindowAttributes(hWnd, 0, alpha, LwaAlpha);
    }
}

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}
