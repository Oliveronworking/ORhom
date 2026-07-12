using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal sealed class ChromeMicrophoneConfigurator
{
    private readonly ChromeProfileLauncher _launcher;
    private readonly AppLogger _logger;

    public ChromeMicrophoneConfigurator(ChromeProfileLauncher launcher, AppLogger logger)
    {
        _launcher = launcher;
        _logger = logger;
    }

    public async Task<MicrophoneConfigurationResult> ApplyAsync(
        string microphoneName,
        CancellationToken cancellationToken = default)
    {
        microphoneName = microphoneName.Trim();
        if (microphoneName.Length == 0)
        {
            return MicrophoneConfigurationResult.Fail("Bitte ein Mikrofon auswählen.");
        }

        var foregroundBeforeLaunch = NativeMethods.GetForegroundWindow();
        var windowsBeforeLaunch = FindChromeWindows().ToHashSet();
        var markerUrl = string.Empty;
        var ownershipProperty = $"OpenAIFlow.MicrophoneSettingsWindow.{Guid.NewGuid():N}";
        IntPtr settingsWindow = IntPtr.Zero;
        var navigationSucceeded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_launcher.TryOpenMicrophoneSettingsHidden(out markerUrl, out var failureReason))
            {
                return MicrophoneConfigurationResult.Fail(failureReason);
            }

            var deadline = Environment.TickCount64 + 12000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(100, cancellationToken);
                if (settingsWindow == IntPtr.Zero)
                {
                    var observations = FindChromeWindows()
                        .Where(window => !windowsBeforeLaunch.Contains(window))
                        .Select(window => new ChromeWindowUrlObservation(
                            window,
                            ChromeWindowUrlCorrelation.TryReadOmniboxUrl(window)));
                    var correlatedWindow = ChromeWindowUrlCorrelation.FindExactMatch(observations, markerUrl);
                    if (correlatedWindow == IntPtr.Zero)
                    {
                        continue;
                    }

                    settingsWindow = correlatedWindow;
                    if (!NativeMethods.MarkWindow(settingsWindow, ownershipProperty))
                    {
                        return MicrophoneConfigurationResult.Fail(
                            "Das eigene Chrome-Einstellungsfenster konnte nicht sicher markiert werden.");
                    }

                    _logger.Info($"Chrome microphone launch correlated to an exact marker URL. Handle=0x{settingsWindow.ToInt64():X}");
                    if (!NativeMethods.HasWindowMark(settingsWindow, ownershipProperty))
                    {
                        return MicrophoneConfigurationResult.Fail(
                            "Das eigene Chrome-Einstellungsfenster ist nicht mehr verfügbar.");
                    }

                    _ = NativeMethods.SetWindowOpacity(settingsWindow, 1);
                    if (NativeMethods.IsIconic(settingsWindow))
                    {
                        _ = NativeMethods.ShowWindow(settingsWindow, NativeMethods.SwRestore);
                    }
                }

                if (!navigationSucceeded)
                {
                    navigationSucceeded = NativeMethods.HasWindowMark(settingsWindow, ownershipProperty) &&
                                          TryNavigateToMicrophoneSettings(
                                              settingsWindow,
                                              ownershipProperty);
                }

                if (!navigationSucceeded)
                {
                    continue;
                }

                if (!NativeMethods.HasWindowMark(settingsWindow, ownershipProperty))
                {
                    return MicrophoneConfigurationResult.Fail(
                        "Das eigene Chrome-Einstellungsfenster ist nicht mehr verfügbar.");
                }

                var combo = FindMicrophoneCombo(settingsWindow, ownershipProperty);
                if (combo is null)
                {
                    continue;
                }

                if (!NativeMethods.HasWindowMark(settingsWindow, ownershipProperty))
                {
                    return MicrophoneConfigurationResult.Fail(
                        "Das eigene Chrome-Einstellungsfenster ist nicht mehr verfügbar.");
                }

                _ = NativeMethods.ForceForegroundWindow(settingsWindow, timeoutMs: 300);
                if (TrySelectMicrophone(
                        settingsWindow,
                        combo,
                        microphoneName,
                        ownershipProperty,
                        out var selectedName))
                {
                    _logger.Info($"Chrome microphone configured. RequestedName='{microphoneName}' SelectedName='{selectedName}'.");
                    await Task.Delay(350, cancellationToken);
                    return MicrophoneConfigurationResult.Success(selectedName);
                }

                return MicrophoneConfigurationResult.Fail($"Das Mikrofon „{microphoneName}“ wurde in Chrome nicht gefunden.");
            }

            return MicrophoneConfigurationResult.Fail("Die Chrome-Mikrofoneinstellungen konnten nicht geladen werden.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("Chrome microphone configuration cancelled during application shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Chrome microphone configuration failed.", ex);
            return MicrophoneConfigurationResult.Fail("Das Mikrofon konnte in Chrome nicht eingestellt werden.");
        }
        finally
        {
            if (settingsWindow == IntPtr.Zero && markerUrl.Length > 0)
            {
                settingsWindow = await WaitForExactMarkerWindowAsync(
                    windowsBeforeLaunch,
                    markerUrl,
                    timeoutMs: 2000);
            }

            if (settingsWindow != IntPtr.Zero &&
                !NativeMethods.HasWindowMark(settingsWindow, ownershipProperty) &&
                ChromeWindowUrlCorrelation.IsExactMatch(
                    ChromeWindowUrlCorrelation.TryReadOmniboxUrl(settingsWindow),
                    markerUrl) &&
                !NativeMethods.MarkWindow(settingsWindow, ownershipProperty))
            {
                _logger.Info("Late correlated Chrome microphone window could not be marked and was left visible for safety.");
                settingsWindow = IntPtr.Zero;
            }

            if (IsOwnedSettingsWindow(settingsWindow, ownershipProperty))
            {
                _ = NativeMethods.RequestWindowClose(settingsWindow);
                var closeDeadline = Environment.TickCount64 + 2000;
                while (Environment.TickCount64 < closeDeadline &&
                       IsOwnedSettingsWindow(settingsWindow, ownershipProperty))
                {
                    await Task.Delay(75);
                }

                if (IsOwnedSettingsWindow(settingsWindow, ownershipProperty))
                {
                    _ = NativeMethods.SetWindowOpacity(settingsWindow, 255);
                    _ = NativeMethods.ShowWindow(settingsWindow, NativeMethods.SwRestore);
                    _ = NativeMethods.UnmarkWindow(settingsWindow, ownershipProperty);
                    _logger.Info("Chrome microphone settings window did not close; full visibility was restored for manual cleanup.");
                }
            }

            if (foregroundBeforeLaunch != IntPtr.Zero && NativeMethods.IsWindow(foregroundBeforeLaunch))
            {
                _ = NativeMethods.ForceForegroundWindow(foregroundBeforeLaunch, timeoutMs: 250);
            }
        }
    }

    private static async Task<IntPtr> WaitForExactMarkerWindowAsync(
        IReadOnlySet<IntPtr> windowsBeforeLaunch,
        string markerUrl,
        int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);
        while (Environment.TickCount64 < deadline)
        {
            var observations = FindChromeWindows()
                .Where(window => !windowsBeforeLaunch.Contains(window))
                .Select(window => new ChromeWindowUrlObservation(
                    window,
                    ChromeWindowUrlCorrelation.TryReadOmniboxUrl(window)));
            var window = ChromeWindowUrlCorrelation.FindExactMatch(
                observations,
                markerUrl);
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(100);
        }

        return IntPtr.Zero;
    }

    private static AutomationElement? FindMicrophoneCombo(
        IntPtr window,
        string ownershipProperty)
    {
        try
        {
            if (!NativeMethods.HasWindowMark(window, ownershipProperty))
            {
                return null;
            }

            var root = AutomationElement.FromHandle(window);
            return root?.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "mediaPicker"));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNavigateToMicrophoneSettings(
        IntPtr window,
        string ownershipProperty)
    {
        try
        {
            if (!NativeMethods.HasWindowMark(window, ownershipProperty) ||
                !NativeMethods.ForceForegroundWindow(window, timeoutMs: 300))
            {
                return false;
            }

            var root = AutomationElement.FromHandle(window);
            var omnibox = ChromeWindowUrlCorrelation.FindOmnibox(root);
            if (omnibox is null ||
                !NativeMethods.HasWindowMark(window, ownershipProperty) ||
                !omnibox.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
                valueObject is not ValuePattern valuePattern)
            {
                return false;
            }

            valuePattern.SetValue("chrome://settings/content/microphone");
            omnibox.SetFocus();
            Thread.Sleep(40);

            var focusedElement = AutomationElement.FocusedElement;
            if (NativeMethods.GetForegroundWindow() != window ||
                !NativeMethods.HasWindowMark(window, ownershipProperty) ||
                !AutomationHelpers.AreSameElement(omnibox, focusedElement))
            {
                return false;
            }

            SendKeys.SendWait("{ENTER}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedSettingsWindow(
        IntPtr window,
        string ownershipProperty)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        return NativeMethods.HasWindowMark(window, ownershipProperty);
    }

    private static bool TrySelectMicrophone(
        IntPtr window,
        AutomationElement combo,
        string microphoneName,
        string ownershipProperty,
        out string selectedName)
    {
        selectedName = string.Empty;
        try
        {
            if (!NativeMethods.HasWindowMark(window, ownershipProperty))
            {
                return false;
            }

            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandObject) &&
                expandObject is ExpandCollapsePattern expand &&
                expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            {
                expand.Expand();
                Thread.Sleep(250);
            }

            if (!NativeMethods.HasWindowMark(window, ownershipProperty))
            {
                return false;
            }

            var root = AutomationElement.FromHandle(window);
            var items = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var item = items.Cast<AutomationElement>().FirstOrDefault(candidate =>
                candidate.Current.Name.Contains(microphoneName, StringComparison.OrdinalIgnoreCase));
            if (item is null ||
                !item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject) ||
                selectionObject is not SelectionItemPattern selection)
            {
                return false;
            }

            if (!NativeMethods.HasWindowMark(window, ownershipProperty))
            {
                return false;
            }

            selection.Select();
            Thread.Sleep(250);
            if (!NativeMethods.HasWindowMark(window, ownershipProperty))
            {
                return false;
            }
            selectedName = item.Current.Name;
            return selection.Current.IsSelected;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<IntPtr> FindChromeWindows()
    {
        var windows = new List<IntPtr>();
        _ = NativeMethods.EnumWindows((window, _) =>
        {
            if (NativeMethods.IsWindowVisible(window) &&
                NativeMethods.GetWindowClass(window).Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }
}

internal readonly record struct ChromeWindowUrlObservation(IntPtr Window, string? Url);

internal static class ChromeWindowUrlCorrelation
{
    public static bool IsExactMatch(string? actualUrl, string expectedUrl)
    {
        if (string.IsNullOrEmpty(expectedUrl))
        {
            return false;
        }

        if (string.Equals(actualUrl, expectedUrl, StringComparison.Ordinal))
        {
            return true;
        }

        const string elidedScheme = "https://";
        return expectedUrl.StartsWith(elidedScheme, StringComparison.Ordinal) &&
               string.Equals(actualUrl, expectedUrl[elidedScheme.Length..], StringComparison.Ordinal);
    }

    public static IntPtr FindExactMatch(
        IEnumerable<ChromeWindowUrlObservation> observations,
        string expectedUrl)
    {
        foreach (var observation in observations)
        {
            if (IsExactMatch(observation.Url, expectedUrl))
            {
                return observation.Window;
            }
        }

        return IntPtr.Zero;
    }

    public static string? TryReadOmniboxUrl(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var omnibox = FindOmnibox(root);
            if (omnibox is null)
            {
                return null;
            }

            if (omnibox.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
                valueObject is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static AutomationElement? FindOmnibox(AutomationElement root)
    {
        var omnibox = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "view_2001"));
        return omnibox ?? root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
            .Cast<AutomationElement>()
            .FirstOrDefault(element => element.Current.ClassName.Contains("Omnibox", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record MicrophoneConfigurationResult(bool Ok, string Message, string SelectedName)
{
    public static MicrophoneConfigurationResult Success(string selectedName) =>
        new(true, string.Empty, selectedName);

    public static MicrophoneConfigurationResult Fail(string message) =>
        new(false, message, string.Empty);
}
