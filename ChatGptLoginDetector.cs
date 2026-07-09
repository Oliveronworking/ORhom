using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal enum ChatGptLoginStatus
{
    Unknown,
    LoggedIn,
    LoggedOut
}

internal static class ChatGptLoginDetector
{
    private static readonly string[] LoggedOutMarkers =
    [
        "Anmelden",
        "Log in",
        "Kostenlos registrieren",
        "Sign up",
        "Danke, dass du ChatGPT ausprobiert hast",
        "Thanks for trying ChatGPT"
    ];

    public static ChatGptLoginStatus Detect(IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow))
        {
            logger.Info("ChatGPT login detection skipped because the window is unavailable.");
            return ChatGptLoginStatus.Unknown;
        }

        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            if (root is null)
            {
                logger.Info("ChatGPT login detection could not access the UI Automation root.");
                return ChatGptLoginStatus.Unknown;
            }

            var contentElements = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));

            foreach (AutomationElement element in contentElements.Cast<AutomationElement>().Take(300))
            {
                var name = element.Current.Name ?? string.Empty;
                if (LoggedOutMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.Info("ChatGPT login detection result: LoggedOut (a sign-in marker is visible).");
                    return ChatGptLoginStatus.LoggedOut;
                }
            }

            if (AutomationHelpers.FocusChatGptInput(chatWindow, settings, logger) is not null ||
                HasVoiceControl(contentElements))
            {
                logger.Info("ChatGPT login detection result: LoggedIn (composer or voice control is visible).");
                return ChatGptLoginStatus.LoggedIn;
            }
        }
        catch (Exception ex)
        {
            logger.Error("ChatGPT login detection failed.", ex);
        }

        logger.Info("ChatGPT login detection result: Unknown.");
        return ChatGptLoginStatus.Unknown;
    }

    private static bool HasVoiceControl(AutomationElementCollection contentElements)
    {
        return contentElements.Cast<AutomationElement>().Take(300).Any(element =>
        {
            var name = element.Current.Name ?? string.Empty;
            return name.Contains("microphone", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("mikrofon", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("dictation", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("diktat", StringComparison.OrdinalIgnoreCase);
        });
    }
}
