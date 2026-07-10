namespace ChatGptDictationBridge;

internal sealed class FocusTracker
{
    private readonly AppLogger _logger;

    public FocusTracker(AppLogger logger)
    {
        _logger = logger;
    }

    public FocusTarget Capture(AppSettings settings)
    {
        var window = NativeMethods.GetForegroundWindow();
        var element = AutomationHelpers.GetFocusedElement(_logger);
        var avoidElementFocus = AutomationHelpers.ShouldPreserveWebViewFocus(element);
        var isPassword = settings.BlockPasswordFields && AutomationHelpers.IsPasswordElement(element);

        var title = window == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowTitle(window);
        var className = window == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowClass(window);
        var targetMetadata = AutomationHelpers.GetSafeFocusMetadata(element);
        _logger.Info($"Focus captured. WindowHandle=0x{window.ToInt64():X} Class='{className}' TargetControlType='{targetMetadata.ControlType}' TargetClass='{targetMetadata.ClassName}' TargetAutomationId='{targetMetadata.AutomationId}' PreserveWebViewFocus={avoidElementFocus} PasswordTarget={isPassword}");

        return new FocusTarget(window, title, className, element, avoidElementFocus, isPassword);
    }
}
