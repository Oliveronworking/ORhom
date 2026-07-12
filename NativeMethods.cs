using System.Runtime.InteropServices;
using System.Text;

namespace ChatGptDictationBridge;

internal static class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const int WmClose = 0x0010;
    public const int SwShowNoActivate = 4;
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
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint GetAncestorRoot = 2;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

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

    public static bool ClickAt(int x, int y, IntPtr expectedWindow)
    {
        if (expectedWindow == IntPtr.Zero ||
            GetForegroundWindow() != expectedWindow ||
            !IsPointOwnedByWindow(x, y, expectedWindow))
        {
            return false;
        }

        var restoreCursor = GetCursorPos(out var originalPosition);
        if (!SetCursorPos(x, y))
        {
            return false;
        }

        if (GetForegroundWindow() != expectedWindow ||
            !IsPointOwnedByWindow(x, y, expectedWindow))
        {
            if (restoreCursor)
            {
                _ = SetCursorPos(originalPosition.X, originalPosition.Y);
            }

            return false;
        }

        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        if (restoreCursor)
        {
            _ = SetCursorPos(originalPosition.X, originalPosition.Y);
        }

        return true;
    }

    private static bool IsPointOwnedByWindow(int x, int y, IntPtr expectedWindow)
    {
        var pointWindow = WindowFromPoint(new NativePoint { X = x, Y = y });
        return pointWindow != IntPtr.Zero && GetAncestor(pointWindow, GetAncestorRoot) == expectedWindow;
    }

    public static bool ForceForegroundWindow(IntPtr targetWindow, int timeoutMs = 250)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            return false;
        }

        if (IsIconic(targetWindow))
        {
            _ = ShowWindow(targetWindow, SwRestore);
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == targetWindow)
        {
            return true;
        }

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(targetWindow, out _);
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var attachedTarget = targetThread != 0 && targetThread != currentThread &&
                             AttachThreadInput(currentThread, targetThread, true);
        var attachedForeground = foregroundThread != 0 &&
                                 foregroundThread != currentThread &&
                                 foregroundThread != targetThread &&
                                 AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            _ = BringWindowToTop(targetWindow);
            _ = SetForegroundWindow(targetWindow);
            _ = SetActiveWindow(targetWindow);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }
        }

        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < Math.Max(timeoutMs, 0))
        {
            if (GetForegroundWindow() == targetWindow)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return GetForegroundWindow() == targetWindow;
    }

    public static bool SendPasteShortcut()
    {
        var inputs = new[]
        {
            CreateKeyboardInput((ushort)Keys.ControlKey, keyUp: false),
            CreateKeyboardInput((ushort)Keys.V, keyUp: false),
            CreateKeyboardInput((ushort)Keys.V, keyUp: true),
            CreateKeyboardInput((ushort)Keys.ControlKey, keyUp: true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == (uint)inputs.Length;
    }

    public static bool IsKeyDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    public static bool MarkWindow(IntPtr hWnd, string propertyName)
    {
        return hWnd != IntPtr.Zero && SetProp(hWnd, propertyName, new IntPtr(1));
    }

    public static bool HasWindowMark(IntPtr hWnd, string propertyName)
    {
        return hWnd != IntPtr.Zero && GetProp(hWnd, propertyName) != IntPtr.Zero;
    }

    public static bool UnmarkWindow(IntPtr hWnd, string propertyName)
    {
        return hWnd != IntPtr.Zero && RemoveProp(hWnd, propertyName) != IntPtr.Zero;
    }

    public static uint GetOwningProcessId(IntPtr hWnd)
    {
        _ = GetWindowThreadProcessId(hWnd, out var processId);
        return processId;
    }

    public static bool RequestWindowClose(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && IsWindow(hWnd) && PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);

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

    public static bool HideWindowFromTaskbar(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }

        var extendedStyle = GetWindowLong(hWnd, GwlExStyle);
        var backgroundStyle = (extendedStyle | WsExToolWindow) & ~WsExAppWindow;
        if (backgroundStyle != extendedStyle)
        {
            Marshal.SetLastPInvokeError(0);
            var previousStyle = SetWindowLong(hWnd, GwlExStyle, backgroundStyle);
            if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }
        }

        return SetWindowPos(
            hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public static bool ShowWindowInTaskbar(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }

        var extendedStyle = GetWindowLong(hWnd, GwlExStyle);
        var foregroundStyle = (extendedStyle & ~WsExToolWindow) | WsExAppWindow;
        if (foregroundStyle != extendedStyle)
        {
            Marshal.SetLastPInvokeError(0);
            var previousStyle = SetWindowLong(hWnd, GwlExStyle, foregroundStyle);
            if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }
        }

        return SetWindowPos(
            hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static Input CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
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

[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    public uint Type;
    public InputUnion Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public KeyboardInput Keyboard;

    [FieldOffset(0)]
    public MouseInput Mouse;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}
