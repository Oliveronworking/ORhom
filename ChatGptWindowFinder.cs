using System.Diagnostics;

namespace ChatGptDictationBridge;

internal static class ChatGptWindowFinder
{
    public static IntPtr Find(AppSettings settings, AppLogger logger, IntPtr excludedWindow = default)
    {
        var matches = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (excludedWindow != IntPtr.Zero && hWnd == excludedWindow)
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            var title = NativeMethods.GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (settings.ChatGptWindowTitleContains.Any(part => title.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        var selected = matches.FirstOrDefault();
        logger.Info(selected == IntPtr.Zero
            ? "ChatGPT window not found."
            : $"ChatGPT window found. Handle=0x{selected.ToInt64():X} Title='{NativeMethods.GetWindowTitle(selected)}' Class='{NativeMethods.GetWindowClass(selected)}'");
        return selected;
    }

    public static async Task<IntPtr> FindOrLaunchAsync(AppSettings settings, AppLogger logger, IntPtr excludedWindow)
    {
        var chatWindow = Find(settings, logger, excludedWindow);
        if (chatWindow != IntPtr.Zero || !settings.LaunchChatGptIfMissing)
        {
            return chatWindow;
        }

        try
        {
            Process.Start(new ProcessStartInfo(settings.ChatGptUrl) { UseShellExecute = true });
            logger.Info("ChatGPT URL launched because no ChatGPT window was found.");
        }
        catch (Exception ex)
        {
            logger.Error("Could not launch ChatGPT URL.", ex);
            return IntPtr.Zero;
        }

        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 8000)
        {
            await Task.Delay(300);
            chatWindow = Find(settings, logger, excludedWindow);
            if (chatWindow != IntPtr.Zero)
            {
                return chatWindow;
            }
        }

        return IntPtr.Zero;
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
