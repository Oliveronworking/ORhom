using System.Windows.Automation;

namespace ORhom;

internal static class ChatGptWindowFinder
{
    internal const string BackgroundWindowProperty = "ORhom.ChatGptBackgroundWindow";
    private const byte HiddenAutomationOpacity = 1;

    public static async Task<IntPtr> LaunchConfiguredProfileAsync(
        AppSettings settings,
        AppLogger logger,
        ChromeProfileLauncher profileLauncher,
        IntPtr excludedWindow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileIdentity = ChromeProfileIdentity.From(settings);
        var targetUrl = settings.ChatGptUrl;
        var existingOwnedWindow = FindOwnedBackgroundWindow(profileIdentity, excludedWindow);
        if (existingOwnedWindow != IntPtr.Zero)
        {
            logger.Info($"Persistent ChatGPT background window reused. Handle=0x{existingOwnedWindow.ToInt64():X}");
            return existingOwnedWindow;
        }

        var markerUrl = CreateBackgroundLaunchMarkerUrl();
        var correlationWindowProperty = $"ORhom.BackgroundLaunch.{Guid.NewGuid():N}";
        var windowsBeforeLaunch = FindChromeWindows(excludedWindow).ToHashSet();
        if (!profileLauncher.TryOpenChatGptProfile(markerUrl, out var failureReason))
        {
            logger.Info($"Configured Chrome profile was not launched. Reason={failureReason}");
            return IntPtr.Zero;
        }

        var start = Environment.TickCount64;
        try
        {
            while (Environment.TickCount64 - start < 12000)
            {
                await Task.Delay(250, cancellationToken);
                var newWindow = FindCorrelatedWindow(
                    windowsBeforeLaunch,
                    excludedWindow,
                    markerUrl);

                if (newWindow == IntPtr.Zero)
                {
                    continue;
                }

                if (!NativeMethods.MarkWindow(newWindow, correlationWindowProperty))
                {
                    logger.Info("Exactly correlated Chrome launch window could not be marked; it was left visible and untouched for safety.");
                    return IntPtr.Zero;
                }

                if (!TryNavigateOwnedWindow(
                        newWindow,
                        targetUrl,
                        correlationWindowProperty,
                        logger))
                {
                    logger.Info("Correlated ChatGPT background window could not be navigated and will not be reused.");
                    await CloseCorrelatedLaunchWindowAsync(
                        newWindow,
                        correlationWindowProperty,
                        logger);
                    return IntPtr.Zero;
                }

                // Write the generic marker first. If the profile-specific marker
                // cannot be written afterwards, the window can never be adopted
                // by a later profile lookup.
                if (!NativeMethods.HasWindowMark(newWindow, correlationWindowProperty) ||
                    !NativeMethods.MarkWindow(newWindow, BackgroundWindowProperty))
                {
                    logger.Info("New ChatGPT window was not claimed because its generic ownership marker could not be written.");
                    await CloseCorrelatedLaunchWindowAsync(
                        newWindow,
                        correlationWindowProperty,
                        logger);
                    return IntPtr.Zero;
                }

                if (!NativeMethods.HasWindowMark(newWindow, correlationWindowProperty) ||
                    !NativeMethods.MarkWindow(newWindow, profileIdentity.OwnershipPropertyName))
                {
                    logger.Info("New ChatGPT window was not claimed because its profile ownership marker could not be written.");
                    await CloseCorrelatedLaunchWindowAsync(
                        newWindow,
                        correlationWindowProperty,
                        logger);
                    return IntPtr.Zero;
                }

                _ = NativeMethods.UnmarkWindow(newWindow, correlationWindowProperty);
                MinimizeBackgroundWindow(newWindow, settings, logger);
                logger.Info($"Persistent ChatGPT background window created from an exact launch correlation. Handle=0x{newWindow.ToInt64():X}");
                return newWindow;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var correlatedWindow = await WaitForCorrelatedWindowDuringCleanupAsync(
                windowsBeforeLaunch,
                excludedWindow,
                markerUrl,
                timeoutMs: 2000);
            if (correlatedWindow != IntPtr.Zero)
            {
                if (NativeMethods.MarkWindow(correlatedWindow, correlationWindowProperty))
                {
                    await CloseCorrelatedLaunchWindowAsync(
                        correlatedWindow,
                        correlationWindowProperty,
                        logger);
                }
            }

            logger.Info("Configured Chrome profile launch cancelled; only the exactly correlated launch window was closed.");
            throw;
        }

        var lateWindow = await WaitForCorrelatedWindowDuringCleanupAsync(
            windowsBeforeLaunch,
            excludedWindow,
            markerUrl,
            timeoutMs: 1500);
        if (lateWindow != IntPtr.Zero &&
            NativeMethods.MarkWindow(lateWindow, correlationWindowProperty))
        {
            await CloseCorrelatedLaunchWindowAsync(
                lateWindow,
                correlationWindowProperty,
                logger);
        }

        logger.Info("Configured Chrome profile launch did not expose the exact correlation marker; all unrelated Chrome windows were left untouched.");
        return IntPtr.Zero;
    }

    private static async Task<IntPtr> WaitForCorrelatedWindowDuringCleanupAsync(
        IReadOnlySet<IntPtr> windowsBeforeLaunch,
        IntPtr excludedWindow,
        string markerUrl,
        int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);
        while (Environment.TickCount64 < deadline)
        {
            var window = FindCorrelatedWindow(
                windowsBeforeLaunch,
                excludedWindow,
                markerUrl);
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(100);
        }

        return FindCorrelatedWindow(windowsBeforeLaunch, excludedWindow, markerUrl);
    }

    private static async Task CloseCorrelatedLaunchWindowAsync(
        IntPtr window,
        string correlationWindowProperty,
        AppLogger logger)
    {
        if (window == IntPtr.Zero ||
            !NativeMethods.IsWindow(window) ||
            !NativeMethods.HasWindowMark(window, correlationWindowProperty))
        {
            return;
        }

        _ = NativeMethods.RequestWindowClose(window);
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline &&
               NativeMethods.IsWindow(window) &&
               NativeMethods.HasWindowMark(window, correlationWindowProperty))
        {
            await Task.Delay(75);
        }

        if (NativeMethods.IsWindow(window) &&
            NativeMethods.HasWindowMark(window, correlationWindowProperty))
        {
            _ = NativeMethods.SetWindowOpacity(window, 255);
            NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
            _ = NativeMethods.UnmarkWindow(window, correlationWindowProperty);
            _ = NativeMethods.UnmarkWindow(window, BackgroundWindowProperty);
            logger.Info("Correlated Chrome launch window did not close; full visibility was restored for manual cleanup.");
        }
    }

    internal static string CreateBackgroundLaunchMarkerUrl() =>
        CreateBackgroundLaunchMarkerUrl(Guid.NewGuid());

    internal static string CreateBackgroundLaunchMarkerUrl(Guid correlationId) =>
        $"https://orhom.invalid/background/{correlationId:D}";

    public static IntPtr FindOwnedBackgroundWindow(AppSettings settings, IntPtr excludedWindow = default)
    {
        var identity = ChromeProfileIdentity.From(settings);
        return FindOwnedBackgroundWindow(identity, excludedWindow);
    }

    public static IntPtr CloseOwnedBackgroundWindow(AppSettings settings, AppLogger logger)
    {
        return CloseOwnedBackgroundWindow(ChromeProfileIdentity.From(settings), logger);
    }

    public static IntPtr CloseOwnedBackgroundWindow(ChromeProfileIdentity identity, AppLogger logger)
    {
        var window = FindOwnedBackgroundWindow(identity);
        if (window == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (NativeMethods.RequestWindowClose(window))
        {
            logger.Info($"Owned ChatGPT background window close requested. Handle=0x{window.ToInt64():X}");
            return window;
        }

        logger.Info($"Owned ChatGPT background window close request failed. Handle=0x{window.ToInt64():X}");
        return IntPtr.Zero;
    }

    public static bool CloseOwnedBackgroundWindowAndWait(
        ChromeProfileIdentity identity,
        AppLogger logger,
        int timeoutMs = 2500)
    {
        var window = FindOwnedBackgroundWindow(identity);
        if (window == IntPtr.Zero)
        {
            return true;
        }

        if (CloseOwnedBackgroundWindow(window, identity, logger) == IntPtr.Zero)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);
        while (Environment.TickCount64 < deadline &&
               IsOwnedBackgroundWindow(window, identity))
        {
            Thread.Sleep(75);
        }

        var released = !IsOwnedBackgroundWindow(window, identity);
        logger.Info(released
            ? $"Owned ChatGPT background window closure confirmed. Handle=0x{window.ToInt64():X}"
            : $"Owned ChatGPT background window closure was not confirmed. Handle=0x{window.ToInt64():X}");
        return released;
    }

    internal static async Task<bool> CloseOwnedBackgroundWindowAndWaitAsync(
        IntPtr knownOwnedWindow,
        ChromeProfileIdentity identity,
        AppLogger logger,
        int timeoutMs = 2500,
        CancellationToken cancellationToken = default)
    {
        if (!IsOwnedBackgroundWindow(knownOwnedWindow, identity))
        {
            return true;
        }

        if (CloseOwnedBackgroundWindow(knownOwnedWindow, identity, logger) == IntPtr.Zero)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);
        while (Environment.TickCount64 < deadline &&
               IsOwnedBackgroundWindow(knownOwnedWindow, identity))
        {
            await Task.Delay(75, cancellationToken);
        }

        var released = !IsOwnedBackgroundWindow(knownOwnedWindow, identity);
        logger.Info(released
            ? $"Owned ChatGPT background window closure confirmed. Handle=0x{knownOwnedWindow.ToInt64():X}"
            : $"Owned ChatGPT background window closure was not confirmed. Handle=0x{knownOwnedWindow.ToInt64():X}");
        return released;
    }

    internal static async Task<bool> CloseOwnedBackgroundWindowAndWaitAsync(
        ChromeProfileIdentity identity,
        AppLogger logger,
        int timeoutMs = 2500,
        CancellationToken cancellationToken = default)
    {
        var window = FindOwnedBackgroundWindow(identity);
        return window == IntPtr.Zero ||
               await CloseOwnedBackgroundWindowAndWaitAsync(
                   window,
                   identity,
                   logger,
                   timeoutMs,
                   cancellationToken);
    }

    public static IntPtr CloseOwnedBackgroundWindow(
        IntPtr knownOwnedWindow,
        ChromeProfileIdentity identity,
        AppLogger logger)
    {
        if (!IsOwnedBackgroundWindow(knownOwnedWindow, identity))
        {
            return IntPtr.Zero;
        }

        if (NativeMethods.RequestWindowClose(knownOwnedWindow))
        {
            logger.Info($"Known owned ChatGPT background window close requested. Handle=0x{knownOwnedWindow.ToInt64():X}");
            return knownOwnedWindow;
        }

        logger.Info($"Known owned ChatGPT background window close request failed. Handle=0x{knownOwnedWindow.ToInt64():X}");
        return IntPtr.Zero;
    }

    public static bool ReleaseOwnedBackgroundWindowToUser(
        ChromeProfileIdentity identity,
        AppLogger logger)
    {
        var window = FindOwnedBackgroundWindow(identity);
        return window == IntPtr.Zero ||
               ReleaseOwnedBackgroundWindowToUser(window, identity, logger);
    }

    internal static bool ReleaseOwnedBackgroundWindowToUser(
        IntPtr window,
        ChromeProfileIdentity identity,
        AppLogger logger)
    {
        if (!IsOwnedBackgroundWindow(window, identity))
        {
            return true;
        }

        var opacityRestored = NativeMethods.SetWindowOpacity(window, 255);
        if (!opacityRestored || !IsOwnedBackgroundWindow(window, identity))
        {
            return false;
        }

        var taskbarRestored = NativeMethods.ShowWindowInTaskbar(window);
        if (!taskbarRestored || !IsOwnedBackgroundWindow(window, identity))
        {
            return false;
        }

        _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        Thread.Sleep(70);
        if (!IsOwnedBackgroundWindow(window, identity) ||
            !NativeMethods.IsWindowVisible(window) ||
            NativeMethods.IsIconic(window))
        {
            logger.Info($"Owned background window could not be made safely visible for user handoff. Handle=0x{window.ToInt64():X}");
            return false;
        }

        if (!NativeMethods.UnmarkWindow(window, identity.OwnershipPropertyName))
        {
            logger.Info($"Owned background window profile marker could not be released after visibility restoration. Handle=0x{window.ToInt64():X}");
            return false;
        }

        _ = NativeMethods.UnmarkWindow(window, BackgroundWindowProperty);
        logger.Info($"Owned background window released as a visible user-controlled window. Handle=0x{window.ToInt64():X}");
        return true;
    }

    public static int ReleaseLegacyGenericWindowsToUser(
        ChromeProfileIdentity configuredIdentity,
        AppLogger logger)
    {
        var releasedCount = 0;
        _ = NativeMethods.EnumWindows((window, state) =>
        {
            if (ReleaseLegacyGenericWindowToUser(
                    window,
                    configuredIdentity,
                    logger))
            {
                releasedCount++;
            }

            return true;
        }, IntPtr.Zero);
        return releasedCount;
    }

    internal static bool ReleaseLegacyGenericWindowToUser(
        IntPtr window,
        ChromeProfileIdentity configuredIdentity,
        AppLogger logger)
    {
        if (!NativeMethods.HasWindowMark(window, BackgroundWindowProperty) ||
            IsOwnedBackgroundWindow(window, configuredIdentity))
        {
            return false;
        }

        if (!NativeMethods.SetWindowOpacity(window, 255) ||
            !NativeMethods.HasWindowMark(window, BackgroundWindowProperty) ||
            !NativeMethods.ShowWindowInTaskbar(window))
        {
            return false;
        }

        _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        Thread.Sleep(70);
        if (!NativeMethods.IsWindowVisible(window) ||
            NativeMethods.IsIconic(window) ||
            !NativeMethods.HasWindowMark(window, BackgroundWindowProperty) ||
            !NativeMethods.UnmarkWindow(window, BackgroundWindowProperty))
        {
            return false;
        }

        logger.Info($"Legacy generic-only background window released as a visible user window. Handle=0x{window.ToInt64():X}");
        return true;
    }

    internal static bool IsOwnedBackgroundWindow(IntPtr window, AppSettings settings) =>
        IsOwnedBackgroundWindow(window, ChromeProfileIdentity.From(settings));

    internal static bool IsOwnedBackgroundWindow(
        IntPtr window,
        ChromeProfileIdentity identity)
    {
        return identity.IsConfigured &&
               window != IntPtr.Zero &&
               NativeMethods.IsWindow(window) &&
               NativeMethods.HasWindowMark(window, BackgroundWindowProperty) &&
               NativeMethods.HasWindowMark(window, identity.OwnershipPropertyName);
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
        if (!IsOwnedBackgroundWindow(hWnd, settings))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.HideWindowFromTaskbar(hWnd))
            {
                logger.Info("ChatGPT background window could not be removed from the taskbar before automation.");
            }
            if (!IsOwnedBackgroundWindow(hWnd, settings))
            {
                return false;
            }

            if (settings.KeepChatGptWindowHidden &&
                !NativeMethods.SetWindowOpacity(hWnd, HiddenAutomationOpacity))
            {
                logger.Info("ChatGPT background window opacity could not be reduced before automation.");
            }
            if (!IsOwnedBackgroundWindow(hWnd, settings))
            {
                return false;
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

            return IsOwnedBackgroundWindow(hWnd, settings);
        }
        catch (Exception ex)
        {
            logger.Error("Could not prepare hidden ChatGPT window.", ex);
            return false;
        }
    }

    public static bool PrepareForBackgroundAutomation(IntPtr hWnd, AppSettings settings, AppLogger logger)
    {
        if (!IsOwnedBackgroundWindow(hWnd, settings))
        {
            return false;
        }

        try
        {
            if (settings.KeepChatGptWindowHidden &&
                !NativeMethods.SetWindowOpacity(hWnd, HiddenAutomationOpacity))
            {
                logger.Info("ChatGPT background window opacity could not be reduced before non-activating automation.");
            }
            if (!IsOwnedBackgroundWindow(hWnd, settings))
            {
                return false;
            }

            if (NativeMethods.IsIconic(hWnd) || !NativeMethods.IsWindowVisible(hWnd))
            {
                _ = NativeMethods.ShowWindow(hWnd, NativeMethods.SwShowNoActivate);
                Thread.Sleep(70);
            }

            return IsOwnedBackgroundWindow(hWnd, settings) &&
                   NativeMethods.IsWindowVisible(hWnd) &&
                   !NativeMethods.IsIconic(hWnd);
        }
        catch (Exception ex)
        {
            logger.Error("Could not prepare hidden ChatGPT window without activation.", ex);
            return false;
        }
    }

    public static void MinimizeBackgroundWindow(IntPtr hWnd, AppSettings settings, AppLogger logger)
    {
        if (!IsOwnedBackgroundWindow(hWnd, settings))
        {
            return;
        }

        if (settings.KeepChatGptWindowHidden)
        {
            _ = NativeMethods.SetWindowOpacity(hWnd, HiddenAutomationOpacity);
        }
        if (!IsOwnedBackgroundWindow(hWnd, settings))
        {
            return;
        }

        if (!NativeMethods.HideWindowFromTaskbar(hWnd))
        {
            logger.Info("ChatGPT background window could not be removed from the taskbar while minimizing.");
        }
        if (!IsOwnedBackgroundWindow(hWnd, settings))
        {
            return;
        }

        NativeMethods.ShowWindow(hWnd, NativeMethods.SwMinimize);
        logger.Info($"Persistent ChatGPT background window minimized. Handle=0x{hWnd.ToInt64():X}");
    }

    private static List<IntPtr> FindChatGptWindows(AppSettings settings, IntPtr excludedWindow)
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

    private static List<IntPtr> FindChromeWindows(IntPtr excludedWindow)
    {
        var windows = new List<IntPtr>();
        _ = NativeMethods.EnumWindows((window, _) =>
        {
            if (window != excludedWindow &&
                NativeMethods.IsWindowVisible(window) &&
                NativeMethods.GetWindowClass(window).Contains(
                    "Chrome_WidgetWin",
                    StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindCorrelatedWindow(
        IReadOnlySet<IntPtr> windowsBeforeLaunch,
        IntPtr excludedWindow,
        string markerUrl)
    {
        var observations = FindChromeWindows(excludedWindow)
            .Where(window => !windowsBeforeLaunch.Contains(window))
            .Select(window => new ChromeWindowUrlObservation(
                window,
                ChromeWindowUrlCorrelation.TryReadOmniboxUrl(window)));
        return ChromeWindowUrlCorrelation.FindExactMatch(observations, markerUrl);
    }

    private static bool TryNavigateOwnedWindow(
        IntPtr window,
        string targetUrl,
        string correlationWindowProperty,
        AppLogger logger)
    {
        try
        {
            if (!PrepareCorrelatedLaunchWindowForNavigation(
                    window,
                    correlationWindowProperty))
            {
                return false;
            }

            var root = AutomationElement.FromHandle(window);
            var omnibox = ChromeWindowUrlCorrelation.FindOmnibox(root);
            if (omnibox is null ||
                !NativeMethods.HasWindowMark(window, correlationWindowProperty) ||
                !omnibox.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
                valueObject is not ValuePattern valuePattern)
            {
                return false;
            }

            valuePattern.SetValue(targetUrl);
            omnibox.SetFocus();
            Thread.Sleep(40);
            if (NativeMethods.GetForegroundWindow() != window ||
                !NativeMethods.HasWindowMark(window, correlationWindowProperty) ||
                !AutomationHelpers.AreSameElement(omnibox, AutomationElement.FocusedElement))
            {
                return false;
            }

            SendKeys.SendWait("{ENTER}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Correlated ChatGPT background window navigation failed.", ex);
            return false;
        }
    }

    private static bool PrepareCorrelatedLaunchWindowForNavigation(
        IntPtr window,
        string correlationWindowProperty)
    {
        if (window == IntPtr.Zero ||
            !NativeMethods.IsWindow(window) ||
            !NativeMethods.HasWindowMark(window, correlationWindowProperty))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
            Thread.Sleep(70);
        }

        if (NativeMethods.GetForegroundWindow() != window &&
            !NativeMethods.ForceForegroundWindow(window, timeoutMs: 160))
        {
            return false;
        }

        Thread.Sleep(35);
        return NativeMethods.HasWindowMark(window, correlationWindowProperty);
    }

    internal static IntPtr FindOwnedBackgroundWindow(
        ChromeProfileIdentity identity,
        IntPtr excludedWindow = default)
    {
        if (!identity.IsConfigured)
        {
            return IntPtr.Zero;
        }

        var ownedWindow = IntPtr.Zero;
        _ = NativeMethods.EnumWindows((window, _) =>
        {
            if (window != excludedWindow && IsOwnedBackgroundWindow(window, identity))
            {
                ownedWindow = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return ownedWindow;
    }
}
