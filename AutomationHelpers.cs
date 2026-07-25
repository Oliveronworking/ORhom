using System.Windows.Automation;

namespace ORhom;

internal static class AutomationHelpers
{
    private static readonly ControlType[] InputControlTypes =
    [
        ControlType.Edit,
        ControlType.Custom,
        ControlType.Document,
        // Chromium occasionally exposes a contenteditable ProseMirror node as a
        // Group while its accessibility tree is being rebuilt.  It is accepted
        // below only when it also carries strong composer-specific metadata.
        ControlType.Group
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

    public static bool AreSameElement(AutomationElement? left, AutomationElement? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (ReferenceEquals(left, right))
        {
            return true;
        }

        try
        {
            return left.GetRuntimeId().SequenceEqual(right.GetRuntimeId());
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSameElementOrWithinCapturedWebViewRoot(
        AutomationElement? focused,
        AutomationElement? captured)
    {
        if (focused is null || captured is null)
        {
            return false;
        }

        if (AreSameElement(focused, captured))
        {
            return true;
        }

        try
        {
            var capturedCurrent = captured.Current;
            var isWebViewRoot = capturedCurrent.AutomationId.Equals(
                                    "RootWebArea",
                                    StringComparison.OrdinalIgnoreCase) ||
                                capturedCurrent.ClassName.Contains(
                                    "Chrome_RenderWidgetHost",
                                    StringComparison.OrdinalIgnoreCase);
            if (!isWebViewRoot)
            {
                return false;
            }

            var current = focused;
            for (var depth = 0; depth < 80 && current is not null; depth++)
            {
                if (AreSameElement(current, captured))
                {
                    return true;
                }

                current = TreeWalker.RawViewWalker.GetParent(current);
            }
        }
        catch
        {
            return false;
        }

        return false;
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
            Thread.Sleep(40);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Could not focus target automation element.", ex);
            return false;
        }
    }

    public static bool ShouldPreserveWebViewFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var current = element.Current;
            return current.AutomationId.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase) ||
                   current.ClassName.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase) ||
                   current.ClassName.Contains("Chrome_RenderWidgetHost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static SafeFocusMetadata GetSafeFocusMetadata(AutomationElement? element)
    {
        if (element is null)
        {
            return new SafeFocusMetadata("<null>", string.Empty, string.Empty);
        }

        try
        {
            var current = element.Current;
            return new SafeFocusMetadata(
                current.ControlType.ProgrammaticName ?? string.Empty,
                current.ClassName ?? string.Empty,
                current.AutomationId ?? string.Empty);
        }
        catch
        {
            return new SafeFocusMetadata("<stale>", string.Empty, string.Empty);
        }
    }

    public static string GetSafeWebViewRootIdentity(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            var current = element;
            for (var depth = 0; depth < 80 && current is not null; depth++)
            {
                var properties = current.Current;
                var isWebViewRoot = properties.AutomationId.Equals(
                                        "RootWebArea",
                                        StringComparison.OrdinalIgnoreCase) ||
                                    properties.ClassName.Contains(
                                        "Chrome_RenderWidgetHost",
                                        StringComparison.OrdinalIgnoreCase);
                if (isWebViewRoot)
                {
                    var runtimeId = current.GetRuntimeId();
                    return runtimeId.Length == 0
                        ? string.Empty
                        : string.Join(":", runtimeId.Select(value => value.ToString("X8")));
                }

                current = TreeWalker.RawViewWalker.GetParent(current);
            }
        }
        catch
        {
            // A stale or rebuilding Chromium accessibility tree is not a safe
            // source of cross-runtime focus identity.
        }

        return string.Empty;
    }

    public static SafeFocusLayoutFingerprint GetSafeFocusLayoutFingerprint(
        AutomationElement? element,
        IntPtr windowHandle)
    {
        if (element is null ||
            windowHandle == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(windowHandle, out var windowRect) ||
            windowRect.Width <= 0 ||
            windowRect.Height <= 0)
        {
            return SafeFocusLayoutFingerprint.Empty;
        }

        try
        {
            var bounds = element.Current.BoundingRectangle;
            var fingerprint = new SafeFocusLayoutFingerprint(
                (bounds.Left - windowRect.Left) / windowRect.Width,
                (bounds.Top - windowRect.Top) / windowRect.Height,
                bounds.Width / windowRect.Width,
                bounds.Height / windowRect.Height);
            return fingerprint.IsValid
                ? fingerprint
                : SafeFocusLayoutFingerprint.Empty;
        }
        catch
        {
            return SafeFocusLayoutFingerprint.Empty;
        }
    }

    public static bool IsElementInWindow(AutomationElement? element, IntPtr windowHandle)
    {
        if (element is null || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var current = element;
            for (var depth = 0; depth < 80 && current is not null; depth++)
            {
                var nativeHandle = new IntPtr(current.Current.NativeWindowHandle);
                if (nativeHandle == windowHandle)
                {
                    return true;
                }

                current = TreeWalker.RawViewWalker.GetParent(current);
            }
        }
        catch
        {
            return false;
        }

        return false;
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
                if (IsElementInWindow(focused, chatWindow) &&
                    IsSafeChatGptInput(focused, chatWindow, settings))
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
        if (IsElementInWindow(focused, chatWindow) &&
            IsSafeChatGptInput(focused, chatWindow, settings))
        {
            return focused;
        }

        logger.Info("Known ChatGPT input focus could not be confirmed.");
        return null;
    }

    public static bool IsSafeChatGptInput(AutomationElement? element, IntPtr chatWindow, AppSettings settings)
    {
        return GetChatGptInputSafetyRejectionReason(element, chatWindow, settings) is null;
    }

    public static AutomationElement? FindChatGptInput(IntPtr chatWindow, AppSettings settings, AppLogger logger)
    {
        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            return root is null ? null : FindBestInputCandidate(root, chatWindow, settings);
        }
        catch (Exception ex)
        {
            logger.Error("Could not find ChatGPT input.", ex);
            return null;
        }
    }

    public static IReadOnlyList<AutomationElement> FindChatGptInputCandidates(IntPtr chatWindow, AppLogger logger)
    {
        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            if (root is null)
            {
                return [];
            }

            var condition = new OrCondition(InputControlTypes
                .Select(type => new PropertyCondition(AutomationElement.ControlTypeProperty, type))
                .Cast<Condition>()
                .ToArray());
            return root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Take(120).ToList();
        }
        catch (Exception ex)
        {
            logger.Error("Could not enumerate ChatGPT input candidates.", ex);
            return [];
        }
    }

    public static string ReadText(AutomationElement? element, AppLogger logger)
    {
        return TryReadText(element, logger, out var text) ? text : string.Empty;
    }

    public static bool TryReadText(
        AutomationElement? element,
        AppLogger logger,
        out string text)
    {
        text = string.Empty;
        if (element is null)
        {
            return false;
        }

        var foundReadableSource = false;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value ?? string.Empty;
                foundReadableSource = true;
                if (!IsPlaceholder(value))
                {
                    text = value;
                    if (text.Length > 0)
                    {
                        return true;
                    }
                }
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var value = textPattern.DocumentRange.GetText(-1).Trim('\r', '\n', ' ');
                foundReadableSource = true;
                if (!IsPlaceholder(value))
                {
                    text = value;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("ChatGPT input text inspection failed.", ex);
            return false;
        }

        text = string.Empty;
        return foundReadableSource;
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

    public static async Task<ChatGptDictationReadResult> ReadChatGptTextRobustlyAsync(
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        bool windowAlreadyPrepared = false,
        AutomationElement? preferredInput = null,
        int? timeoutOverrideMs = null,
        Action? restoreTargetFocus = null,
        CancellationToken cancellationToken = default)
    {
        var configuredTimeoutMs = Math.Max(
            StopTransitionTimeoutPolicy.ResolveTranscriptionTimeoutMs(
                settings.DictationResultTimeoutMs),
            1000);
        var timeoutMs = timeoutOverrideMs is null
            ? configuredTimeoutMs
            : Math.Clamp(timeoutOverrideMs.Value, 1000, configuredTimeoutMs);
        var pollIntervalMs = Math.Clamp(settings.DictationResultPollIntervalMs, 100, 1000);
        var start = Environment.TickCount64;
        var attempts = 0;
        var lastSafeInput = IsSafeChatGptInput(pr×N·êÚ$z{-®éÜj×†ÖWFFFä6öçF–ç2‚&6ö×÷6W""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’Ğ¢°Ğ¢66÷&R³Òc°Ğ¢ĞĞ Ğ¢&WGW&â66÷&S°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&â°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7FF–27G&–ær&VEfÇVUGFW&åFW‡B„WFöÖF–öäVÆVÖVçCòVÆVÖVçBÂÆövvW"ÆövvW"Ğ¢°Ğ¢–b†VÆVÖVçB—2çVÆÂĞ¢°Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢–b†VÆVÖVçBåG'”vWD7W'&VçEGFW&â…fÇVUGFW&âåGFW&âÂ÷WBf"fÇVUGFW&äö&¢’b`Ğ¢fÇVUGFW&äö&¢—2fÇVUGFW&âfÇVUGFW&âĞ¢°Ğ¢f"fÇVRÒ‡fÇVUGFW&âä7W'&VçBåfÇVRóò7G&–æräV×G’’åG&–Ò‚“°Ğ¢–b‚—5Æ6V†öÆFW"‡fÇVR’Ğ¢°Ğ¢&WGW&âfÇVS°Ğ¢ĞĞ¢ĞĞ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢ÆövvW"äW'&÷"‚%fÇVUGFW&â&VBf–ÆVBâ"ÂW‚“°Ğ¢ĞĞ Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ Ğ¢&—fFR7FF–27G&–ær&VEFW‡EGFW&åFW‡B„WFöÖF–öäVÆVÖVçCòVÆVÖVçBÂÆövvW"ÆövvW"Ğ¢°Ğ¢–b†VÆVÖVçB—2çVÆÂĞ¢°Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢–b†VÆVÖVçBåG'”vWD7W'&VçEGFW&â…FW‡EGFW&âåGFW&âÂ÷WBf"FW‡EGFW&äö&¢’b`Ğ¢FW‡EGFW&äö&¢—2FW‡EGFW&âFW‡EGFW&âĞ¢°Ğ¢f"FW‡BÒFW‡EGFW&âäFö7VÖVçE&ævRävWEFW‡B‚Ó’åG&–Ò‚uÇ"rÂuÆârÂrr“°Ğ¢–b‚—5Æ6V†öÆFW"‡FW‡B’Ğ¢°Ğ¢&WGW&âFW‡C°Ğ¢ĞĞ¢ĞĞ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢ÆövvW"äW'&÷"‚%FW‡EGFW&â&VBf–ÆVBâ"ÂW‚“°Ğ¢ĞĞ Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ Ğ¢&—fFR7FF–27G&–ær&VDFö7VÖVçE&ævUFW‡B„WFöÖF–öäVÆVÖVçCòVÆVÖVçBÂÆövvW"ÆövvW"Ğ¢°Ğ¢–b†VÆVÖVçB—2çVÆÂĞ¢°Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢f"FW66VæFçG2ÒVÆVÖVçBäf–æDÆÂ€Ğ¢G&VU66÷RäFW66VæFçG2ÀĞ¢æWr&÷W'G”6öæF—F–öâ„WFöÖF–öäVÆVÖVçBä—5FW‡EGFW&äf–Æ&ÆU&÷W'G’ÂG'VR’“°Ğ¢f÷&V6‚„WFöÖF–öäVÆVÖVçBFW66VæFçB–âFW66VæFçG2ä67CÄWFöÖF–öäVÆVÖVçCâ‚’åF¶Rƒ#’Ğ¢°Ğ¢–b†FW66VæFçBåG'”vWD7W'&VçEGFW&â…FW‡EGFW&âåGFW&âÂ÷WBf"GFW&äö&¦V7B’b`Ğ¢GFW&äö&¦V7B—2FW‡EGFW&âFW‡EGFW&âĞ¢°Ğ¢f"FW‡BÒFW‡EGFW&âäFö7VÖVçE&ævRävWEFW‡B‚Ó’åG&–Ò‚uÇ"rÂuÆârÂrr“°Ğ¢–b‚—5Æ6V†öÆFW"‡FW‡B’Ğ¢°Ğ¢&WGW&âFW‡C°Ğ¢ĞĞ¢ĞĞ¢ĞĞ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢ÆövvW"äW'&÷"‚$Fö7VÖVçE&ævR&VBf–ÆVBâ"ÂW‚“°Ğ¢ĞĞ Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂ—466WF&ÆT6GW&VEFW‡B‡7G&–ærFW‡BĞ¢°Ğ¢FW‡BÒFW‡BåG&–Ò‚“°Ğ¢&WGW&âFW‡BäÆVæwF‚âbb—5Æ6V†öÆFW"‡FW‡B’bb—5Vç6fT6GW&VEFW‡B‡FW‡B“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2fö–BÆöu&V¦V7FVE&VB‡7G&–ærÖWF†öBÂ7G&–ærFW‡BÂ–çBGFV×BÂÆövvW"ÆövvW"Ğ¢°Ğ¢f"G&–ÖÖVBÒFW‡BåG&–Ò‚“°Ğ¢–b‡G&–ÖÖVBäÆVæwF‚ÓÒĞ¢°Ğ¢&WGW&ã°Ğ¢ĞĞ Ğ¢ÆövvW"ä–æfò‚B$6†DuBF–7FFVBFW‡B&V¦V7FVBg&öÒ¶ÖWF†öGÒâGFV×C×¶GFV×GÒFW‡DÆVæwFƒ×·G&–ÖÖVBäÆVæwF‡ÒVç6fS×´—5Vç6fT6GW&VEFW‡B‡G&–ÖÖVB—ÒÆ6V†öÆFW#×´—5Æ6V†öÆFW"‡G&–ÖÖVB—Ò"“°Ğ¢ĞĞ Ğ¢V&Æ–27FF–27G&–æsòvWD6†DwD–çWE6fWG•&V¦V7F–öå&V6öâ„WFöÖF–öäVÆVÖVçCòVÆVÖVçBÂ–çEG"6†Ev–æF÷rÂ6WGF–æw26WGF–æw2Ğ¢°Ğ¢–b†VÆVÖVçB—2çVÆÂĞ¢°Ğ¢&WGW&â&çVÆÂÖVÆVÖVçB#°Ğ¢ĞĞ Ğ¢–b†6†Ev–æF÷rÓÒ–çEG"å¦W&òÇÂæF—fTÖWF†öG2ävWEv–æF÷u&V7B†6†Ev–æF÷rÂ÷WBf"v–æF÷u&V7B’Ğ¢°Ğ¢&WGW&â&Ö—76–ær×v–æF÷r×&V7B#°Ğ¢ĞĞ Ğ¢–b‚—4VÆVÖVçD–åv–æF÷r†VÆVÖVçBÂ6†Ev–æF÷r’Ğ¢°Ğ¢&WGW&â&F–ffW&VçB×v–æF÷r#°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢f"7W'&VçBÒVÆVÖVçBä7W'&VçC°Ğ¢f"6öçG&öÅG—RÒ7W'&VçBä6öçG&öÅG—S°Ğ¢–b‚–çWD6öçG&öÅG—W2ä6öçF–ç2†6öçG&öÅG—R’Ğ¢°Ğ¢&WGW&â'Vç7W÷'FVBÖ6öçG&öÂ×G—R#°Ğ¢ĞĞ Ğ¢–b„—577v÷&DVÆVÖVçB†VÆVÖVçB’Ğ¢°Ğ¢&WGW&â'77v÷&BÖVÆVÖVçB#°Ğ¢ĞĞ Ğ¢f"&V7BÒ7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢–b‡&V7Båv–GF‚ÂƒÇÂ&V7Bä†V–v‡BÂ‚Ğ¢°Ğ¢&WGW&â'Föò×6ÖÆÂ#°Ğ¢ĞĞ Ğ¢–b‡&V7BåF÷Âv–æF÷u&V7BåF÷²6WGF–æw2ä'&÷w6W$6‡&öÖTW†6ÇW6–öåF÷‚Ğ¢°Ğ¢&WGW&â&'&÷w6W"Ö6‡&öÖR×F÷#°Ğ¢ĞĞ Ğ¢f"æÖRÒ†7W'&VçBäæÖRóò7G&–æräV×G’’åG&–Ò‚“°Ğ¢f"6Æ74æÖRÒ7W'&VçBä6Æ74æÖRóò7G&–æräV×G“°Ğ¢f"ÖWFFFÒvWD–çWDÖWFFF†7W'&VçB“°Ğ Ğ¢–b„Æöö·4Æ–¶UVç6fT'&÷w6W$F–Æör†æÖRÂ6Æ74æÖR’Ğ¢°Ğ¢&WGW&â&'&÷w6W"ÖF–Æör#°Ğ¢ĞĞ Ğ¢–b„Æöö·4Æ–¶T'&÷w6W$6‡&öÖR†æÖR’ÇÂÖWFFFä6öçF–ç2‚&öÖæ–&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’Ğ¢°Ğ¢&WGW&â&'&÷w6W"Ö6‡&öÖRÖæÖR#°Ğ¢ĞĞ Ğ¢–b„Æöö·4Æ–¶T¶æ÷väæöä6ö×÷6W$ÖWFFF†ÖWFFF’Ğ¢°Ğ¢&WGW&â&¶æ÷vâÖæöâÖ6ö×÷6W"#°Ğ¢ĞĞ Ğ¢f"W‡Æ–6—D6ö×÷6W"ÒÆöö·4Æ–¶T6†D–çWDÖWFFF†ÖWFFF“°Ğ¢–b‚W‡Æ–6—D6ö×÷6W"Ğ¢°Ğ¢&WGW&â&æ÷BÖ6†FwBÖ6ö×÷6W"#°Ğ¢ĞĞ Ğ¢–b†6öçG&öÅG—RÓÒ6öçG&öÅG—Räw&÷WbbÆöö·4Æ–¶U7G&öæt6†D–çWDÖWFFF†ÖWFFF’Ğ¢°Ğ¢&WGW&â&w&÷W×v—F†÷WB×7G&öærÖ6ö×÷6W"ÖÖ&¶W"#°Ğ¢ĞĞ Ğ¢f"Ö„†V–v‡BÒ6WGF–æw2äÖ„6†DwD–çWD†V–v‡Eƒ°Ğ¢–b‡&V7Bä†V–v‡BâÖ„†V–v‡BĞ¢°Ğ¢&WGW&â'Föò×FÆÂ#°Ğ¢ĞĞ Ğ¢f"Ö…v–GF…&F–òÒ6WGF–æw2äÖ„6†DwD–çWEv–æF÷uv–GF…&F–ó°Ğ¢–b‡&V7Båv–GF‚âv–æF÷u&V7Båv–GF‚¢Ö…v–GF…&F–òĞ¢°Ğ¢&WGW&â'Föò×v–FR#°Ğ¢ĞĞ Ğ¢f"†5&VF&ÆT÷$fö7W6&ÆU6†RÒ7W'&VçBä—4¶W–&ö&Dfö7W6&ÆRÇÀĞ¢VÆVÖVçBåG'”vWD7W'&VçEGFW&â…fÇVUGFW&âåGFW&âÂ÷WBò’ÇÀĞ¢VÆVÖVçBåG'”vWD7W'&VçEGFW&â…FW‡EGFW&âåGFW&âÂ÷WBò“°Ğ¢–b‚†5&VF&ÆT÷$fö7W6&ÆU6†RĞ¢°Ğ¢&WGW&â&æ÷B×&VF&ÆRÖ÷"Öfö7W6&ÆR#°Ğ¢ĞĞ Ğ¢&WGW&âçVÆÃ°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&â'7FÆRÖ÷"×Væ–ç7V7F&ÆR#°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7FF–27G&–ærvWD–çWDÖWFFF„WFöÖF–öäVÆVÖVçBäWFöÖF–öäVÆVÖVçD–æf÷&ÖF–öâ7W'&VçBĞ¢°Ğ¢&WGW&â7G&–ærä¦ö–â‚""ÂæWuµĞĞ¢°Ğ¢7W'&VçBäæÖRóò7G&–æräV×G’ÀĞ¢7W'&VçBä6Æ74æÖRóò7G&–æräV×G’ÀĞ¢7W'&VçBäWFöÖF–öä–Bóò7G&–æräV×G’ÀĞ¢7W'&VçBä†VÇFW‡Bóò7G&–æräV×G’ÀĞ¢7W'&VçBäg&ÖWv÷&´–Bóò7G&–æräV×G’ÀĞ¢7W'&VçBä6öçG&öÅG—Rå&öw&ÖÖF–4æÖRóò7G&–æräV×GĞ¢Ò“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂÆöö·4Æ–¶T'&÷w6W$6‡&öÖR‡7G&–ærFW‡BĞ¢°Ğ¢FW‡BÒFW‡BåG&–Ò‚“°Ğ¢&WGW&âFW‡BäWVÇ2‚$G&W72ÒVæB7V6†ÆV—7FR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$FG&W72æB6V&6‚&""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚%6V&6‚vöövÆR÷"G—RU$Â"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$vöövÆRGW&6‡7V6†VâöFW"V–æRU$ÂV–ævV&Vâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$öÖæ–&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡Bå7F'G5v—F‚‚$G&W76RVæB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡Bå7F'G5v—F‚‚$FG&W72æB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°Ğ¢ĞĞ Ğ¢V&Æ–27FF–2&ööÂÆöö·4Æ–¶UVç6fT†÷F¶W•F&vWB„WFöÖF–öäVÆVÖVçCòVÆVÖVçBĞ¢°Ğ¢–b†VÆVÖVçB—2çVÆÂĞ¢°Ğ¢&WGW&âfÇ6S°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢f"7W'&VçBÒVÆVÖVçBä7W'&VçC°Ğ¢f"æÖRÒ†7W'&VçBäæÖRóò7G&–æräV×G’’åG&–Ò‚“°Ğ¢f"6Æ74æÖRÒ7W'&VçBä6Æ74æÖRóò7G&–æräV×G“°Ğ¢f"ÖWFFFÒvWD–çWDÖWFFF†7W'&VçB“°Ğ¢&WGW&âÆöö·4Æ–¶T'&÷w6W$6‡&öÖR†æÖR’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&öÖæ–&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢Æöö·4Æ–¶T¶æ÷väæöä6ö×÷6W$ÖWFFF†ÖWFFF’ÇÀĞ¢Æöö·4Æ–¶UVç6fT'&÷w6W$F–Æör†æÖRÂ6Æ74æÖR’ÇÀĞ¢—577v÷&DVÆVÖVçB†VÆVÖVçB“°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&âfÇ6S°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂÆöö·4Æ–¶UVç6fT'&÷w6W$F–Æör‡7G&–æræÖRÂ7G&–ær6Æ74æÖRĞ¢°Ğ¢f"Æöö·4Æ–¶TF–ÆötæÖRÒæÖRä6öçF–ç2‚&ÆW6W¦V–6†Vâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢æÖRä6öçF–ç2‚&&öö¶Ö&²"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢æÖRä6öçF–ç2‚'7V–6†W&â"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢æÖRä6öçF–ç2‚'6fR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢æÖRä6öçF–ç2‚&÷&FæW""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢æÖRä6öçF–ç2‚&föÆFW""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°Ğ¢–b‚Æöö·4Æ–¶TF–ÆötæÖRĞ¢°Ğ¢&WGW&âfÇ6S°Ğ¢ĞĞ Ğ¢&WGW&â6Æ74æÖRä6öçF–ç2‚%FW‡Ff–VÆB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢6Æ74æÖRä6öçF–ç2‚$VF—B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢6Æ74æÖRäÆVæwF‚ÓÒ°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂÆöö·4Æ–¶T6†D–çWDÖWFFF‡7G&–ærÖWFFFĞ¢°Ğ¢&WGW&âÖWFFFä6öçF–ç2‚%&÷6TÖ—'&÷""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&ÖW76vR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'&ö×B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&g&vR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&æ6‡&–6‡B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6²"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6†GFVâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'FW‡F&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6öçFVçFVF—F&ÆR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6ö×÷6W""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&ÆW†–6Â"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'&ö×B×FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'6VæBÖW76vR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&VçFW"&ö×B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'7&–6‚Ö—B6†FwB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚$6†DuBÖW76vR6ö×÷6W""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂÆöö·4Æ–¶U7G&öæt6†D–çWDÖWFFF‡7G&–ærÖWFFFĞ¢°Ğ¢&WGW&âÖWFFFä6öçF–ç2‚%&÷6TÖ—'&÷""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'&ö×B×FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6öçFVçFVF—F&ÆR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6ö×÷6W""Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&ÆW†–6Â"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂÆöö·4Æ–¶T¶æ÷väæöä6ö×÷6W$ÖWFFF‡7G&–ærÖWFFFĞ¢°Ğ¢&WGW&âÖWFFFä6öçF–ç2‚'6Ö'B×6V&6‚Ö–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'6V&6‚6†G2"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚'6V&6‚6†B†—7F÷'’"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6†G2GW&6‡7V6†Vâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6†GfW&ÆVbGW&6‡7V6†Vâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢ÖWFFFä6öçF–ç2‚&6öÖÖæBÆWGFR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂ—5Æ6V†öÆFW"‡7G&–ærFW‡BĞ¢°Ğ¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡FW‡B’Ğ¢°Ğ¢&WGW&âG'VS°Ğ¢ĞĞ Ğ¢&WGW&âFW‡BäWVÇ2‚%7FVÆÆR—&vVæFV–æRg&vR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$ÖW76vR6†DuB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$6²ç—F†–ær"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$æ6‡&–6‡Bâ6†DuB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW‡BäWVÇ2‚$6†DuBg&vVâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°Ğ¢ĞĞ Ğ¢&—fFR7FF–27–æ2F6³Ä6Æ—&ö&EFW‡E&VE&W7VÇCâv—Df÷$6Æ—&ö&EFW‡D7–æ2€Ğ¢–çBF–ÖV÷WD×2ÀĞ¢V–çB&Wf–÷W56WVVæ6TçVÖ&W"ÀĞ¢ÆövvW"ÆövvW"Ğ¢°Ğ¢f"7F'BÒVçf—&öæÖVçBåF–6´6÷VçCcC°Ğ¢v†–ÆR„Vçf—&öæÖVçBåF–6´6÷VçCcBÒ7F'BÂF–ÖV÷WD×2Ğ¢°Ğ¢G'Ğ¢°Ğ¢f"6WVVæ6T&Vf÷&U&VBÒæF—fTÖWF†öG2ävWD6Æ—&ö&E6WVVæ6TçVÖ&W"‚“°Ğ¢–b‡6WVVæ6T&Vf÷&U&VBÒ&Wf–÷W56WVVæ6TçVÖ&W"bb6Æ—&ö&Bä6öçF–ç5FW‡B‚’Ğ¢°Ğ¢f"FW‡BÒ6Æ—&ö&BävWEFW‡B‚’åG&–Ò‚“°Ğ¢f"6WVVæ6TgFW%&VBÒæF—fTÖWF†öG2ävWD6Æ—&ö&E6WVVæ6TçVÖ&W"‚“°Ğ¢–b‡6WVVæ6T&Vf÷&U&VBÓÒ6WVVæ6TgFW%&VBĞ¢°Ğ¢&WGW&âæWr6Æ—&ö&EFW‡E&VE&W7VÇB‡FW‡BÂ6WVVæ6TgFW%&VB“°Ğ¢ĞĞ¢ĞĞ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢ÆövvW"äW'&÷"‚$6Æ—&ö&BöÆÆ–ærf–ÆVBâ"ÂW‚“°Ğ¢ĞĞ Ğ¢v—BF6²äFVÆ’ƒ“°Ğ¢ĞĞ Ğ¢&WGW&â6Æ—&ö&EFW‡E&VE&W7VÇBäV×G“°Ğ¢ĞĞ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6Æ—&ö&EFW‡E&VE&W7VÇB‡7G&–ærFW‡BÂV–çB6WVVæ6TçVÖ&W"Ğ§°Ğ¢V&Æ–27FF–26Æ—&ö&EFW‡E&VE&W7VÇBV×G’²vWC²ÒÒæWr‡7G&–æräV×G’Â“°Ğ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6fT6Æ—&ö&D6÷•&W7VÇB€Ğ¢7G&–ærFW‡BÀĞ¢6Æ—&ö&E&W7F÷&T÷WF6öÖR&W7F÷&T÷WF6öÖRĞ§°Ğ¢V&Æ–27FF–26fT6Æ—&ö&D6÷•&W7VÇBV×G’²vWC²ÒĞĞ¢æWr‡7G&–æräV×G’Â6Æ—&ö&E&W7F÷&T÷WF6öÖRäæ÷E&WVW7FVB“°Ğ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6†DwDF–7FF–öå&VE&W7VÇB€Ğ¢7G&–ærFW‡BÀĞ¢7G&–ærÖWF†öBÀĞ¢–çBGFV×G2ÀĞ¢WFöÖF–öäVÆVÖVçCò–çWBÀĞ¢&ööÂ—57F&ÆRÀĞ¢&ööÂ6Æ—&ö&E&W7F÷&Tf–ÆVBÒfÇ6R“°Ğ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6fTfö7W4ÖWFFF‡7G&–ær6öçG&öÅG—RÂ7G&–ær6Æ74æÖRÂ7G&–ærWFöÖF–öä–B“°Ğ