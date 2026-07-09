using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal static class AutomationHelpers
{
    private static readonly ControlType[] InputControlTypes =
    [
        ControlType.Edit,
        ControlType.Custom,
        ControlType.Document
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

    public static AutomationElement? FocusKnownChatGptInput(AutomationElement? element, IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        if (!IsSafeChatGptInput(element, chatWindow, settings))
        {
            return null;
        }

        if (!TryFocusElement(element, logger))
        {
            return null;
        }

        var focused = GetFocusedElement(logger);
        LogElement("Focused known ChatGPT input", focused, logger);
        return IsSafeChatGptInput(focused, chatWindow, settings) ? focused : element;
    }

    public static bool IsSafeChatGptInput(AutomationElement? element, IntPtr chatWindow, AppSettings settings)
    {
        return GetChatGptInputSafetyRejectionReason(element, chatWindow, settings) is null;
    }

    public static string ReadText(AutomationElement? element, AppLogger logger)
    {
        var valueText = ReadValuePatternText(element, logger);
        if (valueText.Length > 0)
        {
            return valueText;
        }

        return ReadTextPatternText(element, logger);
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

    public static async Task<ChatGptDictationReadResult> ReadChatGptDictatedTextRobustlyAsync(
        IntPtr chatWindow,
        AutomationElement? previousInput,
        AppSettings settings,
        AppLogger logger)
    {
        var timeoutMs = Math.Max(settings.DictationResultTimeoutMs, settings.ReadTextTimeoutMs);
        timeoutMs = Math.Max(timeoutMs, 1000);
        var pollIntervalMs = Math.Clamp(settings.DictationResultPollIntervalMs, 100, 1000);
        var start = Environment.TickCount64;
        var attempts = 0;
        AutomationElement? lastSafeInput = null;

        while (Environment.TickCount64 - start < timeoutMs)
        {
            attempts++;
            if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow))
            {
                logger.Info($"ChatGPT dictated text read attempt skipped because window is gone. Attempt={attempts}");
                await Task.Delay(pollIntervalMs);
                continue;
            }

            if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, logger))
            {
                logger.Info($"ChatGPT dictated text read attempt could not prepare window. Attempt={attempts}");
                await Task.Delay(pollIntervalMs);
                continue;
            }

            var input = FocusChatGptInput(chatWindow, settings, logger);
            if (!IsSafeChatGptInput(input, chatWindow, settings))
            {
                input = FocusKnownChatGptInput(previousInput, chatWindow, settings, logger);
            }

            if (!IsSafeChatGptInput(input, chatWindow, settings))
            {
                logger.Info($"ChatGPT dictated text read attempt did not find a safe input. Attempt={attempts}");
                await Task.Delay(pollIntervalMs);
                continue;
            }

            lastSafeInput = input;

            var valueText = ReadValuePatternText(input, logger);
            if (IsAcceptableCapturedText(valueText))
            {
                logger.Info($"ChatGPT dictated text captured via ValuePattern. Attempt={attempts} TextLength={valueText.Trim().Length}");
                return new ChatGptDictationReadResult(valueText.Trim(), "ValuePattern", attempts, input);
            }

            LogRejectedRead("ValuePattern", valueText, attempts, logger);

            var textPatternText = ReadTextPatternText(input, logger);
            if (IsAcceptableCapturedText(textPatternText))
            {
                logger.Info($"ChatGPT dictated text captured via TextPattern. Attempt={attempts} TextLength={textPatternText.Trim().Length}");
                return new ChatGptDictationReadResult(textPatternText.Trim(), "TextPattern", attempts, input);
            }

            LogRejectedRead("TextPattern", textPatternText, attempts, logger);

            var clipboardTimeoutMs = Math.Min(Math.Max(pollIntervalMs * 2, 500), 1000);
            var clipboardText = await CopyTextSafelyAsync(input, chatWindow, settings, logger, clipboardTimeoutMs);
            if (IsAcceptableCapturedText(clipboardText))
            {
                logger.Info($"ChatGPT dictated text captured via Clipboard. Attempt={attempts} TextLength={clipboardText.Trim().Length}");
                return new ChatGptDictationReadResult(clipboardText.Trim(), "Clipboard", attempts, input);
            }

            LogRejectedRead("Clipboard", clipboardText, attempts, logger);
            await Task.Delay(pollIntervalMs);
        }

        logger.Info($"ChatGPT dictated text read timed out. Attempts={attempts} TimeoutMs={timeoutMs}");
        return new ChatGptDictationReadResult(string.Empty, "None", attempts, lastSafeInput);
    }

    public static async Task<string> CopyTextSafelyAsync(
        AutomationElement? element,
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        int? clipboardTimeoutMs = null)
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

            var timeoutMs = clipboardTimeoutMs ?? settings.ReadTextTimeoutMs;
            var text = await WaitForClipboardTextAsync(timeoutMs, logger);
            if (!IsAcceptableCapturedText(text))
            {
                logger.Info($"Guarded ChatGPT copy did not capture acceptable text. Length={text.Trim().Length}");
                return string.Empty;
            }

            logger.Info($"Guarded ChatGPT copy captured text. Length={text.Trim().Length}");
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

    public static void LogElement(string label, AutomationElement? element, AppLogger logger)
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
            logger.Info($"{label}: ControlType='{current.ControlType.ProgrammaticName}' NameLength={(current.Name ?? string.Empty).Length} Class='{current.ClassName}' Rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}");
        }
        catch (Exception ex)
        {
            logger.Error($"{label}: could not inspect element.", ex);
        }
    }

    public static void WriteChatGptInputDiagnostics(IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        logger.Info("ChatGPT UI diagnostics started.");
        if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow))
        {
            logger.Info("ChatGPT UI diagnostics: ChatGPT window not found.");
            return;
        }

        logger.Info($"ChatGPT UI diagnostics: WindowHandle=0x{chatWindow.ToInt64():X} Class='{NativeMethods.GetWindowClass(chatWindow)}'");

        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            if (root is null)
            {
                logger.Info("ChatGPT UI diagnostics: root automation element not available.");
                return;
            }

            var condition = new OrCondition(InputControlTypes
                .Select(type => new PropertyCondition(AutomationElement.ControlTypeProperty, type))
                .Cast<Condition>()
                .ToArray());

            var elements = root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Take(80).ToList();
            logger.Info($"ChatGPT UI diagnostics: CandidateCount={elements.Count}");

            for (var index = 0; index < elements.Count; index++)
            {
                var element = elements[index];
                try
                {
                    var current = element.Current;
                    var rect = current.BoundingRectangle;
                    var reason = GetChatGptInputSafetyRejectionReason(element, chatWindow, settings);
                    var status = reason is null ? "safe" : $"rejected:{reason}";
                    logger.Info($"ChatGPT UI candidate #{index + 1}: Status={status} ControlType='{current.ControlType.ProgrammaticName}' NameLength={(current.Name ?? string.Empty).Length} Class='{current.ClassName}' AutomationId='{current.AutomationId}' Rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}");
                }
                catch (Exception ex)
                {
                    logger.Error($"ChatGPT UI candidate #{index + 1}: could not inspect element.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("ChatGPT UI diagnostics failed.", ex);
        }
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

            var metadata = GetInputMetadata(current);
            if (LooksLikeChatInputMetadata(metadata))
            {
                score += 5000;
            }

            if (metadata.Contains("prosemirror", StringComparison.OrdinalIgnoreCase))
            {
                score += 7000;
            }

            if (metadata.Contains("composer", StringComparison.OrdinalIgnoreCase))
            {
                score += 6000;
            }

            return score;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadValuePatternText(AutomationElement? element, AppLogger logger)
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

        return string.Empty;
    }

    private static string ReadTextPatternText(AutomationElement? element, AppLogger logger)
    {
        if (element is null)
        {
            return string.Empty;
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

    private static bool IsAcceptableCapturedText(string text)
    {
        text = text.Trim();
        return text.Length > 0 && !IsPlaceholder(text) && !IsUnsafeCapturedText(text);
    }

    private static void LogRejectedRead(string method, string text, int attempt, AppLogger logger)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        logger.Info($"ChatGPT dictated text rejected from {method}. Attempt={attempt} TextLength={trimmed.Length} Unsafe={IsUnsafeCapturedText(trimmed)} Placeholder={IsPlaceholder(trimmed)}");
    }

    private static string? GetChatGptInputSafetyRejectionReason(AutomationElement? element, IntPtr chatWindow, AppSettings settings)
    {
        if (element is null)
        {
            return "null-element";
        }

        if (chatWindow == IntPtr.Zero || !NativeMethods.GetWindowRect(chatWindow, out var windowRect))
        {
            return "missing-window-rect";
        }

        try
        {
            var current = element.Current;
            var controlType = current.ControlType;
            if (!InputControlTypes.Contains(controlType))
            {
                return "unsupported-control-type";
            }

            if (IsPasswordElement(element))
            {
                return "password-element";
            }

            var rect = current.BoundingRectangle;
            if (rect.Width < 80 || rect.Height < 18)
            {
                return "too-small";
            }

            if (rect.Top < windowRect.Top + settings.BrowserChromeExclusionTopPx)
            {
                return "browser-chrome-top";
            }

            var name = (current.Name ?? string.Empty).Trim();
            var className = current.ClassName ?? string.Empty;
            var metadata = GetInputMetadata(current);

            if (LooksLikeUnsafeBrowserDialog(name, className))
            {
                return "browser-dialog";
            }

            if (LooksLikeBrowserChrome(name) || metadata.Contains("omnibox", StringComparison.OrdinalIgnoreCase))
            {
                return "browser-chrome-name";
            }

            if (rect.Height > settings.MaxChatGptInputHeightPx)
            {
                return "too-tall";
            }

            if (rect.Width > windowRect.Width * settings.MaxChatGptInputWindowWidthRatio)
            {
                return "too-wide";
            }

            var hasReadableOrFocusableShape = current.IsKeyboardFocusable ||
                                              element.TryGetCurrentPattern(ValuePattern.Pattern, out _) ||
                                              element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            if (!hasReadableOrFocusableShape)
            {
                return "not-readable-or-focusable";
            }

            if (!LooksLikeChatInputMetadata(metadata) && !LooksLikeLikelyPageTextInput(current, rect, windowRect))
            {
                return "not-chatgpt-composer";
            }

            return null;
        }
        catch
        {
            return "stale-or-uninspectable";
        }
    }

    private static bool LooksLikeLikelyPageTextInput(AutomationElement.AutomationElementInformation current, System.Windows.Rect rect, Rect windowRect)
    {
        if (current.ControlType != ControlType.Edit)
        {
            return false;
        }

        var windowHeight = Math.Max(windowRect.Height, 1);
        var verticalPosition = (rect.Top - windowRect.Top) / windowHeight;
        return verticalPosition > 0.35 && rect.Height <= 160 && current.IsKeyboardFocusable;
    }

    private static string GetInputMetadata(AutomationElement.AutomationElementInformation current)
    {
        return string.Join(" ", new[]
        {
            current.Name ?? string.Empty,
            current.ClassName ?? string.Empty,
            current.AutomationId ?? string.Empty,
            current.HelpText ?? string.Empty,
            current.FrameworkId ?? string.Empty,
            current.ControlType.ProgrammaticName ?? string.Empty
        });
    }

    private static bool LooksLikeBrowserChrome(string text)
    {
        return text.Contains("adresse", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("such", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("url", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("tab", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeUnsafeHotkeyTarget(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var current = element.Current;
            var name = (current.Name ?? string.Empty).Trim();
            var className = current.ClassName ?? string.Empty;
            var metadata = GetInputMetadata(current);
            return LooksLikeBrowserChrome(name) ||
                   metadata.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
                   LooksLikeUnsafeBrowserDialog(name, className) ||
                   IsPasswordElement(element);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeUnsafeBrowserDialog(string name, string className)
    {
        var looksLikeDialogName = name.Contains("lesezeichen", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("bookmark", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("speichern", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("save", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("ordner", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("folder", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeDialogName)
        {
            return false;
        }

        return className.Contains("Textfield", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
               className.Length == 0;
    }

    private static bool LooksLikeChatInputMetadata(string metadata)
    {
        return metadata.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("message", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("frage", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("nachricht", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("ask", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("chatten", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("textarea", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("textbox", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("contenteditable", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("composer", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("lexical", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("ChatGPT message composer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholder(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Equals("Stelle irgendeine Frage", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Message ChatGPT", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Ask anything", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Nachricht an ChatGPT", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("ChatGPT fragen", StringComparison.OrdinalIgnoreCase);
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
}

internal sealed record ChatGptDictationReadResult(
    string Text,
    string Method,
    int Attempts,
    AutomationElement? Input);
