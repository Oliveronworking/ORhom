namespace ChatGptDictationBridge;

internal static class ChatGptWindowFinder
{
    private const string BackgroundWindowProperty = "OpenAIFlow.ChatGptBackgroundWindow";
    private const byte HiddenAutomationOpacity = 1;

    public static async Task<IntPtr> LaunchConfiguredProfileAsync(
        AppSettings settings,
        AppLogger logger,
        ChromeProfileLauncher profileLauncher,
        IntPtr excludedWindow)
    {
        var existingOwnedWindow = FindOwnedBackgroundWindow(settings, excludedWindow);
        if (existingOwnedWindow != IntPtr.Zero)
        {
            logger.Info($"Persistent ChatGPT background window reused. Handle=0x{existingOwnedWindow.ToInt64():X}");
            return existingOwnedWindow;
        }

        var windowsBeforeLaunch = FindChatGptWindows(settings, excludedWindow).ToHashSet();
        if (!profileLauncher.TryOpenChatGptProfile(out var failureReason))
        {
            logger.Info($"Configured Chrome profile was not launched. Reason={failureReason}");
            return IntPtr.Zero;
        }

        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 12000)
        {
            await Task.Delay(250);
            var candidates = FindChatGptWindows(settings, excludedWindow);
            var newWindow = candidates.FirstOrDefault(window => !windowsBeforeLaunch.Contains(window));
            if (newWindow == IntPtr.Zero)
            {
                var foregroundWindow = NativeMethods.GetForegroundWindow();
                if (IsChatGptWindow(foregroundWindow, settings, excludedWindow) &&
                    (!windowsBeforeLaunch.Contains(foregroundWindow) || windowsBeforeLaunch.Count == 0))
                {
                    newWindow = foregroundWindow;
                }
            }

            if (newWindow != IntPtr.Zero)
            {
                _ = NativeMethods.MarkWindow(newWindow, BackgroundWindowProperty);
                MinimizeBackgroundWindow(newWindow, settings, logger);
                logger.Info($"Persistent ChatGPT background window created. Handle=0x{newWindow.ToInt64():X}");
                return newWindow;
            }
        }

        logger.Info("Configured Chrome profile launch did not expose a new ChatGPT background window; an unrelated existing window was not selected.");
        return IntPtr.Zero;
    }

    public static IntPtr FindOwnedBackgroundWindow(AppSettings settings, IntPtr excludedWindow = default)
    {
        return FindChatGptWindows(settings, excludedWindow)
            .FirstOrDefault(window => NativeMethods.HasWindowMark(window, BackgroundWindowProperty));
    }

    public static bool IsChatGptWindow(IntPtr hWnd, AppSettings settings, IntPtr excludedWindow = default)
    {
        if (hWnd == IntPtr.Zero || hWnd == excludedWindow || !NativeMethods.IsWindowVisible(hWnd))
        {
            return false;
        }

        var className = NativeMethods.GetWindowClass(hWnd);
        if (!className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var title = NativeMethods.GetWindowTitle(hWnd);
        return settings.ChatGptWindowTitleContains.Any(part =>
            !string.IsNullOrWhiteSpace(part) && title.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    public static bool PrepareForAutomation(IntPtr hWnd, AppSettings settings, AppLogger logger)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return false;
        }

        try
        {
            if (settings.KeepChatGptWindowHidden &&
                !NativeMethods.SetWindowOpacity(hWnd, HiddenAutomationOpacity))
            {
                logger.Info("ChatGPT background window opacity could not be reduced before automation.");
            }

            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SwRestore);
                Thread.Sleep(70);
            }

            if (NativeMethods.GetForegroundWindow() != hWnd)
            {
                if (!NativeMethods.ForceForegroundWindow(hWnd, timeoutMs: 160))
                {
                    return false;
                }

                Thread.Sleep(35);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Could not prepare hidden ChatGPT window.", ex);
            return false;
        }
    }

    public static void MinimizeBackgroundWindow(IntPtr hWnd, AppSettings settings, AppLogger logger)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return;
        }

        if (settings.KeepChatGptWindowHidden)
        {
            _ = NativeMethods.SetWindowOpacity(hWnd, HiddenAutomationOpacity);
        }

        NativeMethods.ShowWindow(hWnd, NativeMethods.SwMinimize);
        logger.Info($"Persistent ChatGPT background window minimized. Handle=0x{hWnd.ToInt64():X}");
    }

    public static bool ShowForSetup(IntPtr hWnd, AppLogger logger)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return false;
        }

        _ = NativeMethods.SetWindowOpacity(hWnd, byte.MaxValue);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SwRestore);
        Thread.Sleep(120);
        NativeMethods.SetForegroundWindow(hWnd);
        Thread.Sleep(120);
        logger.Info($"Persistent ChatGPT background window shown for explicit setup. Handle=0x{hWnd.ToInt64():X}");
        return true;
    }

    private static IReadOnlyList<IntPtr> FindChatGptWindows(AppSettings settings, IntPtr excludedWindow)
    {
        var windows = new List<IntPtr>();
        _ = NativeMethods.EnumWindows((window, _) =>
        {
            if (IsChatGptWindow(window, settings, excludedWindow))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
