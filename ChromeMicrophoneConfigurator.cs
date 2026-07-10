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

    public async Task<MicrophoneConfigurationResult> ApplyAsync(string microphoneName)
    {
        microphoneName = microphoneName.Trim();
        if (microphoneName.Length == 0)
        {
            return MicrophoneConfigurationResult.Fail("Bitte ein Mikrofon auswählen.");
        }

        var foregroundBeforeLaunch = NativeMethods.GetForegroundWindow();
        var windowsBeforeLaunch = FindChromeWindows().ToHashSet();
        IntPtr settingsWindow = IntPtr.Zero;
        var navigatedWindows = new HashSet<IntPtr>();
        try
        {
            if (!_launcher.TryOpenMicrophoneSettingsHidden(out var failureReason))
            {
                return MicrophoneConfigurationResult.Fail(failureReason);
            }

            var deadline = Environment.TickCount64 + 12000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(100);
                var newWindows = FindChromeWindows().Where(window => !windowsBeforeLaunch.Contains(window)).ToList();
                foreach (var window in newWindows)
                {
                    settingsWindow = window;
                    _ = NativeMethods.SetWindowOpacity(settingsWindow, 1);
                    if (NativeMethods.IsIconic(settingsWindow))
                    {
                        _ = NativeMethods.ShowWindow(settingsWindow, NativeMethods.SwRestore);
                    }

                    if (navigatedWindows.Add(settingsWindow) && !TryNavigateToMicrophoneSettings(settingsWindow))
                    {
                        continue;
                    }

                    var combo = FindMicrophoneCombo(settingsWindow);
                    if (combo is null)
                    {
                        continue;
                    }

                    _ = NativeMethods.ForceForegroundWindow(settingsWindow, timeoutMs: 300);
                    if (TrySelectMicrophone(settingsWindow, combo, microphoneName, out var selectedName))
                    {
                        _logger.Info($"Chrome microphone configured. RequestedName='{microphoneName}' SelectedName='{selectedName}'.");
                        await Task.Delay(350);
                        return MicrophoneConfigurationResult.Success(selectedName);
                    }

                    return MicrophoneConfigurationResult.Fail($"Das Mikrofon „{microphoneName}“ wurde in Chrome nicht gefunden.");
                }
            }

            return MicrophoneConfigurationResult.Fail("Die Chrome-Mikrofoneinstellungen konnten nicht geladen werden.");
        }
        catch (Exception ex)
        {
            _logger.Error("Chrome microphone configuration failed.", ex);
            return MicrophoneConfigurationResult.Fail("Das Mikrofon konnte in Chrome nicht eingestellt werden.");
        }
        finally
        {
            if (settingsWindow != IntPtr.Zero && NativeMethods.IsWindow(settingsWindow))
            {
                _ = NativeMethods.PostMessage(settingsWindow, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
            }

            if (foregroundBeforeLaunch != IntPtr.Zero && NativeMethods.IsWindow(foregroundBeforeLaunch))
            {
                _ = NativeMethods.ForceForegroundWindow(foregroundBeforeLaunch, timeoutMs: 250);
            }
        }
    }

    private static AutomationElement? FindMicrophoneCombo(IntPtr window)
    {
        try
        {
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

    private static bool TryNavigateToMicrophoneSettings(IntPtr window)
    {
        try
        {
            if (!NativeMethods.ForceForegroundWindow(window, timeoutMs: 300))
            {
                return false;
            }

            var root = AutomationElement.FromHandle(window);
            var omnibox = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "view_2001"));
            omnibox ??= root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>()
                .FirstOrDefault(element => element.Current.ClassName.Contains("Omnibox", StringComparison.OrdinalIgnoreCase));
            if (omnibox is null ||
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

    private static bool TrySelectMicrophone(
        IntPtr window,
        AutomationElement combo,
        string microphoneName,
        out string selectedName)
    {
        selectedName = string.Empty;
        try
        {
            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandObject) &&
                expandObject is ExpandCollapsePattern expand &&
                expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            {
                expand.Expand();
                Thread.Sleep(250);
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

            selection.Select();
            Thread.Sleep(250);
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

internal sealed record MicrophoneConfigurationResult(bool Ok, string Message, string SelectedName)
{
    public static MicrophoneConfigurationResult Success(string selectedName) =>
        new(true, string.Empty, selectedName);

    public static MicrophoneConfigurationResult Fail(string message) =>
        new(false, message, string.Empty);
}
