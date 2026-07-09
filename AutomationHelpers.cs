using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal static class AutomationHelpers
{
    private static readonly ControlType[] InputControlTypes =
    [
        ControlType.Edit,
        ControlType.Document,
        ControlType.Custom,
        ControlType.Pane
    ];

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

    public static AutomationElement? FocusChatGptInput(IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            if (root is null)
            {
                return null;
            }

            var candidate = FindBestInputCandidate(root, chatWindow, settings);
            if (candidate is not null && TryFocusElement(candidate, logger))
            {
                LogElement("ChatGPT input candidate", candidate, logger);
                var focused = GetFocusedElement(logger);
                LogElement("Focused element after ChatGPT input focus", focused, logger);
                if (IsSafeChatGptInput(focused, chatWindow, settings))
                {
                    logger.Info("ChatGPT input focused safely.");
                    return focused;
                }

                if (IsSafeChatGptInput(candidate, chatWindow, settings))
                {
                    logger.Info("ChatGPT input candidate focused safely.");
                    return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("Could not focus ChatGPT input.", ex);
        }

        return null;
    }

    public static bool IsSafeChatGptInput(AutomationElement? element, IntPtr chatWindow, AppSettings settings)
    {
        if (element is null || chatWindow == IntPtr.Zero || !NativeMethods.GetWindowRect(chatWindow, out var windowRect))
        {
            return false;
        }

        try
        {
            var current = element.Current;
            var rect = current.BoundingRectangle;
            if (rect.Width < 80 || rect.Height < 18)
            {
                return false;
            }

            if (rect.Top < windowRect.Top + settings.BrowserChromeExclusionTopPx)
            {
                return false;
            }

            var name = (current.Name ?? string.Empty).Trim();
            if (LooksLikeBrowserChrome(name))
            {
                return false;
            }

            return current.IsKeyboardFocusable ||
                   element.TryGetCurrentPattern(ValuePattern.Pattern, out _) ||
                   element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    public static string ReadText(AutomationElement? element, AppLogger logger)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                valuePatternObj is ValuePattern valuePattern)
            {
                var value = (valuePattern.Current.Value ?? string.Empty).Trim();
                if (!IsPlaceholder(value))
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("ValuePattern read failed.", ex);
        }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) &&
                textPatternObj is TextPattern textPattern)
            {
                var text = textPattern.DocumentRange.GetText(-1).Trim('\r', '\n', ' ');
                if (!IsPlaceholder(text))
                {
                    return text;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("TextPattern read failed.", ex);
        }

        return string.Empty;
    }

    public static async Task<string> WaitForTextAsync(AutomationElement? element, int timeoutMs, AppLogger logger)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            var text = ReadText(element, logger).Trim();
            if (text.Length > 0)
            {
                return text;
            }

            await Task.Delay(250);
        }

        return string.Empty;
    }

    public static async Task<string> CopyTextSafelyAsync(AutomationElement? element, IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        if (element is null || !IsSafeChatGptInput(element, chatWindow, settings))
        {
            return string.Empty;
        }

        try
        {
            element.SetFocus();
            Thread.Sleep(80);
            if (!IsSafeChatGptInput(GetFocusedElement(logger), chatWindow, settings))
            {
                logger.Info("ChatGPT copy skipped because focused element is not safe.");
                return string.Empty;
            }

            Clipboard.Clear();
            Thread.Sleep(50);
            SendKeys.SendWait("^a");
            Thread.Sleep(80);
            SendKeys.SendWait("^c");

            var text = await WaitForClipboardTextAsync(settings.ReadTextTimeoutMs, logger);
            if (IsUnsafeCapturedText(text))
            {
                logger.Info($"Guarded ChatGPT copy rejected unsafe text. Length={text.Length}");
                return string.Empty;
            }

            logger.Info($"Guarded ChatGPT copy captured text. Length={text.Length}");
            return text.Trim();
        }
        catch (Exception ex)
        {
            logger.Error("Guarded ChatGPT copy failed.", ex);
            return string.Empty;
        }
    }

    public static bool ClearTextSafely(AutomationElement? element, IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        if (element is null || !IsSafeChatGptInput(element, chatWindow, settings))
        {
            return false;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                valuePatternObj is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(string.Empty);
                logger.Info("ChatGPT input cleared via ValuePattern.");
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.Error("ValuePattern clear failed.", ex);
        }

        try
        {
            element.SetFocus();
            Thread.Sleep(80);
            if (!IsSafeChatGptInput(GetFocusedElement(logger), chatWindow, settings))
            {
                logger.Info("Keyboard clear skipped because focused element is not a safe ChatGPT input.");
                return false;
            }

            SendKeys.SendWait("^a");
            Thread.Sleep(50);
            SendKeys.SendWait("{DEL}");
            logger.Info("ChatGPT input cleared via guarded keyboard fallback.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Guarded keyboard clear failed.", ex);
            return false;
        }
    }

    public static bool IsUnsafeCapturedText(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
    }

    private static AutomationElement? FindBestInputCandidate(AutomationElement root, IntPtr chatWindow, AppSettings settings)
    {
        var condition = new OrCondition(InputControlTypes
            .Select(type => new PropertyCondition(AutomationElement.ControlTypeProperty, type))
            .Cast<Condition>()
            .ToArray());

        var elements = root.FindAll(TreeScope.Descendants, condition);
        return elements
            .Cast<AutomationElement>()
            .Where(element => IsSafeChatGptInput(element, chatWindow, settings))
            .OrderByDescending(ScoreInputCandidate)
            .FirstOrDefault();
    }

    private static double ScoreInputCandidate(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            var rect = current.BoundingRectangle;
            var score = rect.Bottom + rect.Width / 10;
            if (current.ControlType == ControlType.Edit)
            {
                score += 10000;
            }

            var name = current.Name ?? string.Empty;
            if (name.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("frage", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("nachricht", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ask", StringComparison.OrdinalIgnoreCase))
            {
                score += 5000;
            }

            return score;
        }
        catch
        {
            return 0;
        }
    }

    private static bool LooksLikeBrowserChrome(string name)
    {
        return name.Contains("adresse", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("such", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("tab", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholder(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Equals("Stelle irgendeine Frage", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Message ChatGPT", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Ask anything", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WaitForClipboardTextAsync(int timeoutMs, AppLogger logger)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    return Clipboard.GetText().Trim();
                }
            }
            catch (Exception ex)
            {
                logger.Error("Clipboard polling failed.", ex);
            }

            await Task.Delay(100);
        }

        return string.Empty;
    }

    private static void LogElement(string label, AutomationElement? element, AppLogger logger)
    {
        if (element is null)
        {
            logger.Info($"{label}: <null>");
            return;
        }

        try
        {
            var current = element.Current;
            var rect = current.BoundingRectangle;
            logger.Info($"{label}: ControlType='{current.ControlType.ProgrammaticName}' Name='{current.Name}' Class='{current.ClassName}' Rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}");
        }
        catch (Exception ex)
        {
            logger.Error($"{label}: could not inspect element.", ex);
        }
    }
}
