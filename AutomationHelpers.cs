using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal static class AutomationHelpers
{
    public static AutomationElement? GetFocusedElement(AppLogger logger)
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            logger.Error("Could not read focused automation element.", ex);
            return null;
        }
    }

    public static bool IsPasswordElement(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var value = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
            return value is bool isPassword && isPassword;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFocusElement(AutomationElement? element, AppLogger logger)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            element.SetFocus();
            Thread.Sleep(80);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Could not focus target automation element.", ex);
            return false;
        }
    }
}
