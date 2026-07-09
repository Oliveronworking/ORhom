namespace ChatGptDictationBridge;

internal static class ChatGptWindowFinder
{
    public static async Task<IntPtr> LaunchConfiguredProfileAsync(
        AppSettings settings,
        AppLogger logger,
        ChromeProfileLauncher profileLauncher,
        IntPtr excludedWindow)
    {
        if (!profileLauncher.TryOpenChatGptProfile(out var failureReason))
        {
            logger.Info($"Configured Chrome profile was not launched. Reason={failureReason}");
            return IntPtr.Zero;
        }

        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 8000)
        {
            await Task.Delay(300);
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            if (IsChatGptWindow(foregroundWindow, settings, excludedWindow))
            {
                logger.Info($"Configured Chrome profile ChatGPT window selected from foreground. Handle=0x{foregroundWindow.ToInt64():X}");
                return foregroundWindow;
            }

        }

        logger.Info("Configured Chrome profile launch did not expose a foreground ChatGPT window; no other ChatGPT window was selected as a fallback.");
        return IntPtr.Zero;
    }

    private static bool IsChatGptWindow(IntPtr hWnd, AppSettings settings, IntPtr excludedWindow)
    {
        if (hWnd == IntPtr.Zero || hWnd == excludedWindow || !NativeMethods.IsWindowVisible(hWnd))
        {
            return false;
        }

        var title = NativeMethods.GetWindowTitle(hWnd);
        return settings.ChatGptWindowTitleContains.Any(part => title.Contains(part, StringComparison.OrdinalIgnoreCase));
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
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Could not prepare ChatGPT window.", ex);
            return false;
        }
    }
}
