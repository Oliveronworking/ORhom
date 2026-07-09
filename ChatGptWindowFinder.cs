namespace ChatGptDictationBridge;

internal static class ChatGptWindowFinder
{
    public static async Task<IntPtr> LaunchConfiguredProfileAsync(
        AppSettings settings,
        AppLogger logger,
        ChromeProfileLauncher profileLauncher,
        IntPtr excludedWindow)
    {
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
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            if (IsChatGptWindow(foregroundWindow, settings, excludedWindow) &&
                (!windowsBeforeLaunch.Contains(foregroundWindow) || windowsBeforeLaunch.Count == 0))
            {
                logger.Info($"Configured Chrome profile ChatGPT window selected from foreground. Handle=0x{foregroundWindow.ToInt64():X}");
                return foregroundWindow;
            }

            var newWindow = FindChatGptWindows(settings, excludedWindow)
                .FirstOrDefault(window => !windowsBeforeLaunch.Contains(window));
            if (newWindow != IntPtr.Zero)
            {
                logger.Info($"Configured Chrome profile ChatGPT window selected after launch. Handle=0x{newWindow.ToInt64():X}");
                return newWindow;
            }
        }

        logger.Info("Configured Chrome profile launch did not expose a new ChatGPT window; an unrelated existing window was not selected.");
        return IntPtr.Zero;
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

    public static bool PrepareForAutomation(IntPtr hWnd, AppLogger logger)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return false;
        }

        try
        {
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SwRestore);
                Thread.Sleep(150);
            }

            NativeMethods.SetForegroundWindow(hWnd);
            Thread.Sleep(180);
            return NativeMethods.GetForegroundWindow() == hWnd;
        }
        catch (Exception ex)
        {
            logger.Error("Could not prepare ChatGPT window.", ex);
            return false;
        }
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
