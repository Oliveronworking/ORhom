using System.Windows.Automation;

namespace ChatGptDictationBridge;

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
        var lastSafeInput = IsSafeChatGptInput(preferredInput, chatWindow, settings)
            ? preferredInput
            : null;
        // Long dictations can arrive in chunks with pauses of more than one
        // second. Require a longer quiet period before treating a candidate as
        // complete, otherwise an early partial transcript could be persisted.
        var stableMs = Math.Clamp(settings.DictationTextStableMs, 3000, 10000);
        var tracker = new DictationCandidateTracker();
        var clipboardFallbackEnabled = true;
        var clipboardRestoreFailed = false;
        var passiveInputDiscoveryDeadline = start + Math.Min(1500, timeoutMs / 3);
        var nextActivatingInputDiscoveryAt = passiveInputDiscoveryDeadline;
        var nextClipboardFallbackAt = passiveInputDiscoveryDeadline;

        while (Environment.TickCount64 - start < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            var foregroundMayHaveChanged = false;
            if (!ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings))
            {
                logger.Info($"ChatGPT dictated text read stopped because the owned profile window no longer exists. Attempt={attempts}");
                await Task.Delay(Math.Min(pollIntervalMs, 100), cancellationToken);
                break;
            }

            var input = lastSafeInput;
            if (!IsSafeChatGptInput(input, chatWindow, settings))
            {
                input = FindChatGptInput(chatWindow, settings, logger);
            }

            if (!IsSafeChatGptInput(input, chatWindow, settings))
            {
                if (Environment.TickCount64 < passiveInputDiscoveryDeadline)
                {
                    lastSafeInput = null;
                    tracker.MarkUncertain();
                    logger.Info($"ChatGPT dictated text read is waiting for a fresh composer without taking foreground focus. Attempt={attempts}");
                    await Task.Delay(
                        GetBoundedPollDelay(start, timeoutMs, pollIntervalMs),
                        cancellationToken);
                    continue;
                }

                if (Environment.TickCount64 < nextActivatingInputDiscoveryAt)
                {
                    lastSafeInput = null;
                    tracker.MarkUncertain();
                    await Task.Delay(
                        GetBoundedPollDelay(start, timeoutMs, pollIntervalMs),
                        cancellationToken);
                    continue;
                }

                nextActivatingInputDiscoveryAt = Environment.TickCount64 + 2500;
                foregroundMayHaveChanged = true;
                if (!ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings) ||
                    (!(windowAlreadyPrepared && attempts == 1) &&
                     !ChatGptWindowFinder.PrepareForAutomation(chatWindow, settings, logger)))
                {
                    logger.Info($"ChatGPT dictated text read attempt could not prepare window. Attempt={attempts}");
                    TryRestoreTargetFocus(restoreTargetFocus, logger, "read-prepare-failed");
                    await Task.Delay(
                        GetBoundedPollDelay(start, timeoutMs, pollIntervalMs),
                        cancellationToken);
                    continue;
                }

                input = FocusChatGptInput(chatWindow, settings, logger);
                if (!IsSafeChatGptInput(input, chatWindow, settings))
                {
                    lastSafeInput = null;
                    tracker.MarkUncertain();
                    logger.Info($"ChatGPT dictated text read attempt did not find a safe input. Attempt={attempts}");
                    TryRestoreTargetFocus(restoreTargetFocus, logger, "read-input-not-found");
                    await Task.Delay(
                        GetBoundedPollDelay(start, timeoutMs, pollIntervalMs),
                        cancellationToken);
                    continue;
                }
            }

            lastSafeInput = input;

            var candidate = ReadValuePatternText(input, logger);
            var method = "ValuePattern";
            if (!IsAcceptableCapturedText(candidate))
            {
                LogRejectedRead(method, candidate, attempts, logger);
                candidate = ReadTextPatternText(input, logger);
                method = "TextPattern";
            }

            if (!IsAcceptableCapturedText(candidate))
            {
                LogRejectedRead(method, candidate, attempts, logger);
                candidate = ReadDocumentRangeText(input, logger);
                method = "DocumentRange";
            }

            if (!IsAcceptableCapturedText(candidate) &&
                clipboardFallbackEnabled &&
                Environment.TickCount64 >= nextClipboardFallbackAt)
            {
                LogRejectedRead(method, candidate, attempts, logger);
                nextClipboardFallbackAt = Environment.TickCount64 + 2500;
                var clipboardTimeoutMs = Math.Min(Math.Max(pollIntervalMs * 2, 300), 600);
                foregroundMayHaveChanged = true;
                var clipboardCopy = SafeClipboardCopyResult.Empty;
                if (ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings) &&
                    ChatGptWindowFinder.PrepareForAutomation(chatWindow, settings, logger))
                {
                    var focusedInput = FocusKnownChatGptInput(
                                           input,
                                           chatWindow,
                                           settings,
                                           logger) ??
                                       FocusChatGptInput(chatWindow, settings, logger);
                    if (IsSafeChatGptInput(focusedInput, chatWindow, settings))
                    {
                        input = focusedInput;
                        lastSafeInput = focusedInput;
                        clipboardCopy = await CopyTextSafelyAsync(
                            input,
                            chatWindow,
                            settings,
                            logger,
                            clipboardTimeoutMs);
                    }
                    else
                    {
                        logger.Info("Clipboard fallback skipped because the composer could not be focused safely.");
                    }
                }
                else
                {
                    logger.Info("Clipboard fallback skipped because temporary foreground activation failed.");
                }

                candidate = clipboardCopy.Text;
                method = "Clipboard";
                if (clipboardCopy.RestoreOutcome == ClipboardRestoreOutcome.Failed)
                {
                    clipboardFallbackEnabled = false;
                    clipboardRestoreFailed = true;
                    logger.Info("Clipboard fallback disabled for the remainder of this read because restoration failed.");
                }
            }
            else if (!IsAcceptableCapturedText(candidate))
            {
                method = clipboardFallbackEnabled
                    ? "ClipboardDeferred"
                    : "ClipboardDisabled";
            }

            if (IsAcceptableCapturedText(candidate))
            {
                candidate = candidate.Trim();
                var now = Environment.TickCount64;
                var observation = tracker.Observe(candidate, method, now, stableMs);
                if (observation == CandidateObservation.Changed)
                {
                    logger.Info($"ChatGPT dictated text candidate changed. Attempt={attempts} Method={method} TextLength={candidate.Length}");
                }
                else if (observation == CandidateObservation.Stable)
                {
                    logger.Info($"ChatGPT dictated text stabilized. Attempt={attempts} Method={tracker.Method} TextLength={tracker.Text.Length} StableMs={now - tracker.ChangedAt}");
                    if (foregroundMayHaveChanged)
                    {
                        TryRestoreTargetFocus(restoreTargetFocus, logger, "read-stable");
                    }

                    return new ChatGptDictationReadResult(
                        tracker.Text,
                        tracker.Method,
                        attempts,
                        input,
                        IsStable: true,
                        ClipboardRestoreFailed: clipboardRestoreFailed);
                }
            }
            else
            {
                tracker.MarkUncertain();
                LogRejectedRead(method, candidate, attempts, logger);
            }

            if (foregroundMayHaveChanged)
            {
                TryRestoreTargetFocus(restoreTargetFocus, logger, "read-attempt");
            }

            var delayMs = GetBoundedPollDelay(start, timeoutMs, pollIntervalMs);
            if (tracker.HasCandidate)
            {
                var remainingStabilityMs = stableMs - (Environment.TickCount64 - tracker.ChangedAt);
                if (remainingStabilityMs > 0)
                {
                    delayMs = (int)Math.Min(delayMs, remainingStabilityMs);
                }
            }

            await Task.Delay(Math.Max(delayMs, 1), cancellationToken);
        }

        if (tracker.HasCandidate)
        {
            logger.Info($"ChatGPT dictated text returned at timeout without full stability. Attempts={attempts} Method={tracker.Method} TextLength={tracker.Text.Length}");
            return new ChatGptDictationReadResult(
                tracker.Text,
                tracker.Method,
                attempts,
                lastSafeInput,
                IsStable: false,
                ClipboardRestoreFailed: clipboardRestoreFailed);
        }

        logger.Info($"ChatGPT dictated text read timed out. Attempts={attempts} TimeoutMs={timeoutMs}");
        return new ChatGptDictationReadResult(
            string.Empty,
            "None",
            attempts,
            lastSafeInput,
            IsStable: false,
            ClipboardRestoreFailed: clipboardRestoreFailed);
    }

    private static int GetBoundedPollDelay(long startedAt, int timeoutMs, int pollIntervalMs)
    {
        var remainingMs = timeoutMs - (Environment.TickCount64 - startedAt);
        return (int)Math.Clamp(Math.Min(remainingMs, pollIntervalMs), 1, pollIntervalMs);
    }

    private static void TryRestoreTargetFocus(
        Action? restoreTargetFocus,
        AppLogger logger,
        string context)
    {
        if (restoreTargetFocus is null)
        {
            return;
        }

        try
        {
            restoreTargetFocus();
        }
        catch (Exception ex)
        {
            logger.Error($"Target focus callback failed during dictated text read. Context={context}", ex);
        }
    }

    public static async Task<SafeClipboardCopyResult> CopyTextSafelyAsync(
        AutomationElement? element,
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        int? clipboardTimeoutMs = null)
    {
        if (element is null ||
            !ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings) ||
            !IsSafeChatGptInput(element, chatWindow, settings))
        {
            return SafeClipboardCopyResult.Empty;
        }

        IDataObject? clipboardSnapshot = null;
        var capturedClipboardSequence = 0U;
        uint? ownedClipboardSequence = null;
        var clipboardChangedByUs = false;
        var candidate = string.Empty;
        var restoreOutcome = ClipboardRestoreOutcome.NotRequested;
        try
        {
            do
            {
                element.SetFocus();
                Thread.Sleep(80);
                if (NativeMethods.GetForegroundWindow() != chatWindow ||
                    !IsSafeChatGptInput(GetFocusedElement(logger), chatWindow, settings))
                {
                    logger.Info("ChatGPT copy skipped because focused element is not safe.");
                    break;
                }

                if (settings.RestoreClipboard &&
                    !ClipboardHelper.TryCaptureStable(
                        logger,
                        out clipboardSnapshot,
                        out capturedClipboardSequence))
                {
                    logger.Info("ChatGPT copy skipped because the clipboard could not be snapshotted safely.");
                    break;
                }

                if (settings.RestoreClipboard &&
                    NativeMethods.GetClipboardSequenceNumber() != capturedClipboardSequence)
                {
                    logger.Info("ChatGPT copy skipped because the clipboard changed after it was snapshotted.");
                    break;
                }

                Clipboard.Clear();
                clipboardChangedByUs = true;
                ownedClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                Thread.Sleep(50);
                if (!IsSafeKeyboardTarget(element, chatWindow, settings, logger))
                {
                    logger.Info("ChatGPT copy skipped because focus changed before select-all dispatch.");
                    break;
                }

                SendKeys.SendWait("^a");
                Thread.Sleep(80);
                if (!IsSafeKeyboardTarget(element, chatWindow, settings, logger))
                {
                    logger.Info("ChatGPT copy skipped because focus changed before copy dispatch.");
                    break;
                }

                SendKeys.SendWait("^c");

                var timeoutMs = clipboardTimeoutMs ?? settings.DictationResultTimeoutMs;
                var clipboardRead = await WaitForClipboardTextAsync(
                    timeoutMs,
                    ownedClipboardSequence.Value,
                    logger);
                if (clipboardRead.SequenceNumber == 0 ||
                    NativeMethods.GetForegroundWindow() != chatWindow ||
                    !IsSafeKeyboardTarget(element, chatWindow, settings, logger) ||
                    NativeMethods.GetClipboardSequenceNumber() != clipboardRead.SequenceNumber)
                {
                    logger.Info("Guarded ChatGPT copy rejected because foreground, focus, or clipboard ownership changed while waiting.");
                    break;
                }

                ownedClipboardSequence = clipboardRead.SequenceNumber;
                if (!IsAcceptableCapturedText(clipboardRead.Text))
                {
                    logger.Info($"Guarded ChatGPT copy did not capture acceptable text. Length={clipboardRead.Text.Trim().Length}");
                    break;
                }

                candidate = clipboardRead.Text.Trim();
                logger.Info($"Guarded ChatGPT copy captured text. Length={candidate.Length}");
            }
            while (false);
        }
        catch (Exception ex)
        {
            logger.Error("Guarded ChatGPT copy failed.", ex);
        }
        finally
        {
            if (clipboardChangedByUs)
            {
                restoreOutcome = ClipboardHelper.Restore(
                    clipboardSnapshot,
                    settings,
                    logger,
                    ownedClipboardSequence);
            }
        }

        if (restoreOutcome == ClipboardRestoreOutcome.Failed)
        {
            logger.Info("Guarded ChatGPT copy rejected because the previous clipboard could not be restored.");
            return new SafeClipboardCopyResult(string.Empty, restoreOutcome);
        }

        if (restoreOutcome == ClipboardRestoreOutcome.SkippedExternalChange)
        {
            logger.Info("Guarded ChatGPT copy preserved a newer external clipboard change.");
        }

        return new SafeClipboardCopyResult(candidate, restoreOutcome);
    }

    public static async Task<bool> ClearTextAndConfirmAsync(
        AutomationElement? element,
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        int verificationTimeoutMs = 650,
        bool preferNonActivatingValuePattern = false,
        string? expectedText = null)
    {
        if (element is null ||
            !ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings) ||
            !IsSafeChatGptInput(element, chatWindow, settings))
        {
            return false;
        }

        expectedText = expectedText?.Trim();
        if (expectedText is not null &&
            !HasExpectedText(element, expectedText, logger))
        {
            logger.Info("ChatGPT input clear skipped because the composer changed after it was inspected.");
            return false;
        }

        var keyboardFirst = LooksLikeProseMirror(element) && !preferNonActivatingValuePattern;
        if (keyboardFirst)
        {
            if (TryClearWithKeyboard(element, chatWindow, settings, logger, expectedText) &&
                await WaitForConfirmedEmptyInputAsync(
                    chatWindow,
                    settings,
                    logger,
                    verificationTimeoutMs,
                    requiredEmptyReads: 1))
            {
                logger.Info("ChatGPT input cleared and verified via guarded keyboard input.");
                return true;
            }

            element = FindChatGptInput(chatWindow, settings, logger) ?? element;
            if (TryClearWithValuePattern(element, chatWindow, settings, logger, expectedText) &&
                await WaitForConfirmedEmptyInputAsync(
                    chatWindow,
                    settings,
                    logger,
                    verificationTimeoutMs,
                    requiredEmptyReads: 2))
            {
                logger.Info("ChatGPT input cleared and verified via ValuePattern fallback.");
                return true;
            }
        }
        else
        {
            if (TryClearWithValuePattern(element, chatWindow, settings, logger, expectedText) &&
                await WaitForConfirmedEmptyInputAsync(
                    chatWindow,
                    settings,
                    logger,
                    verificationTimeoutMs,
                    requiredEmptyReads: 2))
            {
                logger.Info("ChatGPT input cleared and verified via ValuePattern.");
                return true;
            }

            element = FindChatGptInput(chatWindow, settings, logger) ?? element;
            if (TryClearWithKeyboard(element, chatWindow, settings, logger, expectedText) &&
                await WaitForConfirmedEmptyInputAsync(
                    chatWindow,
                    settings,
                    logger,
                    verificationTimeoutMs,
                    requiredEmptyReads: 1))
            {
                logger.Info("ChatGPT input cleared and verified via guarded keyboard fallback.");
                return true;
            }
        }

        logger.Info("ChatGPT input clear could not be verified.");
        return false;
    }

    private static bool TryClearWithValuePattern(
        AutomationElement element,
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        string? expectedText)
    {
        try
        {
            if (ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings) &&
                IsElementInWindow(element, chatWindow) &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                valuePatternObj is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly &&
                (expectedText is null ||
                 string.Equals(
                     valuePattern.Current.Value?.Trim(),
                     expectedText,
                     StringComparison.Ordinal)))
            {
                valuePattern.SetValue(string.Empty);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.Error("ValuePattern clear failed.", ex);
        }

        return false;
    }

    private static bool TryClearWithKeyboard(
        AutomationElement element,
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        string? expectedText)
    {
        try
        {
            if (!TryFocusElement(element, logger))
            {
                return false;
            }

            if (!IsSafeKeyboardTarget(element, chatWindow, settings, logger))
            {
                logger.Info("Keyboard clear skipped because focused element is not a safe ChatGPT input.");
                return false;
            }

            if (expectedText is not null &&
                !HasExpectedText(element, expectedText, logger))
            {
                logger.Info("Keyboard clear skipped because the composer changed before select-all dispatch.");
                return false;
            }

            SendKeys.SendWait("^a");
            Thread.Sleep(40);
            if (!IsSafeKeyboardTarget(element, chatWindow, settings, logger))
            {
                logger.Info("Keyboard clear stopped because focus changed before delete dispatch.");
                return false;
            }

            SendKeys.SendWait("{DEL}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Guarded keyboard clear failed.", ex);
            return false;
        }
    }

    private static bool HasExpectedText(
        AutomationElement element,
        string expectedText,
        AppLogger logger) =>
        TryReadText(element, logger, out var currentText) &&
        string.Equals(currentText.Trim(), expectedText, StringComparison.Ordinal);

    private static bool IsSafeKeyboardTarget(
        AutomationElement expected,
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger)
    {
        if (NativeMethods.GetForegroundWindow() != chatWindow ||
            !ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings))
        {
            return false;
        }

        var focused = GetFocusedElement(logger);
        return IsElementInWindow(focused, chatWindow) &&
               IsSafeChatGptInput(focused, chatWindow, settings) &&
               IsSameElementOrWithinCapturedWebViewRoot(focused, expected);
    }

    private static async Task<bool> WaitForConfirmedEmptyInputAsync(
        IntPtr chatWindow,
        AppSettings settings,
        AppLogger logger,
        int timeoutMs,
        int requiredEmptyReads)
    {
        var deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 200, 1500);
        var consecutiveEmptyReads = 0;
        while (Environment.TickCount64 <= deadline)
        {
            if (!ChatGptWindowFinder.IsOwnedBackgroundWindow(chatWindow, settings))
            {
                return false;
            }

            var freshInput = FindChatGptInput(chatWindow, settings, logger);
            if (IsSafeChatGptInput(freshInput, chatWindow, settings) &&
                TryDetermineInputEmpty(freshInput!, out var inputIsEmpty) &&
                inputIsEmpty)
            {
                consecutiveEmptyReads++;
                if (consecutiveEmptyReads >= Math.Max(requiredEmptyReads, 1))
                {
                    return true;
                }
            }
            else
            {
                consecutiveEmptyReads = 0;
            }

            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(remainingMs, 70));
        }

        return false;
    }

    private static bool TryDetermineInputEmpty(AutomationElement element, out bool isEmpty)
    {
        isEmpty = false;
        try
        {
            var foundReadableSource = false;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                foundReadableSource = true;
                var valueText = valuePattern.Current.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(valueText) && !IsPlaceholder(valueText))
                {
                    return true;
                }
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                foundReadableSource = true;
                var textPatternText = textPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrWhiteSpace(textPatternText) && !IsPlaceholder(textPatternText))
                {
                    return true;
                }
            }

            var descendants = element.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            foreach (AutomationElement descendant in descendants.Cast<AutomationElement>().Take(20))
            {
                if (!descendant.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                    patternObject is not TextPattern descendantTextPattern)
                {
                    continue;
                }

                foundReadableSource = true;
                var descendantText = descendantTextPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrWhiteSpace(descendantText) && !IsPlaceholder(descendantText))
                {
                    return true;
                }
            }

            isEmpty = foundReadableSource;
            return foundReadableSource;
        }
        catch
        {
            isEmpty = false;
            return false;
        }
    }

    private static bool LooksLikeProseMirror(AutomationElement element)
    {
        try
        {
            return element.Current.ClassName.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
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
                    logger.Info($"ChatGPT UI candidate #{index + 1}: Status={status} ControlType='{current.ControlType.ProgrammaticName}' Name='<redacted>' NameLength={(current.Name ?? string.Empty).Length} Class='{current.ClassName}' AutomationId='{current.AutomationId}' Rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}");
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

    private static string ReadDocumentRangeText(AutomationElement? element, AppLogger logger)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            var descendants = element.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            foreach (AutomationElement descendant in descendants.Cast<AutomationElement>().Take(20))
            {
                if (descendant.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                    patternObject is TextPattern textPattern)
                {
                    var text = textPattern.DocumentRange.GetText(-1).Trim('\r', '\n', ' ');
                    if (!IsPlaceholder(text))
                    {
                        return text;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("DocumentRange read failed.", ex);
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

    public static string? GetChatGptInputSafetyRejectionReason(AutomationElement? element, IntPtr chatWindow, AppSettings settings)
    {
        if (element is null)
        {
            return "null-element";
        }

        if (chatWindow == IntPtr.Zero || !NativeMethods.GetWindowRect(chatWindow, out var windowRect))
        {
            return "missing-window-rect";
        }

        if (!IsElementInWindow(element, chatWindow))
        {
            return "different-window";
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

            if (LooksLikeKnownNonComposerMetadata(metadata))
            {
                return "known-non-composer";
            }

            var explicitComposer = LooksLikeChatInputMetadata(metadata);
            if (!explicitComposer)
            {
                return "not-chatgpt-composer";
            }

            if (controlType == ControlType.Group && !LooksLikeStrongChatInputMetadata(metadata))
            {
                return "group-without-strong-composer-marker";
            }

            var maxHeight = settings.MaxChatGptInputHeightPx;
            if (rect.Height > maxHeight)
            {
                return "too-tall";
            }

            var maxWidthRatio = settings.MaxChatGptInputWindowWidthRatio;
            if (rect.Width > windowRect.Width * maxWidthRatio)
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

            return null;
        }
        catch
        {
            return "stale-or-uninspectable";
        }
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
        text = text.Trim();
        return text.Equals("Adress- und Suchleiste", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Address and search bar", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Search Google or type a URL", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Google durchsuchen oder eine URL eingeben", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Omnibox", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Adresse und", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Address and", StringComparison.OrdinalIgnoreCase);
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
                   LooksLikeKnownNonComposerMetadata(metadata) ||
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
               metadata.Contains("prompt-textarea", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("send a message", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("enter prompt", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("sprich mit chatgpt", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("ChatGPT message composer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStrongChatInputMetadata(string metadata)
    {
        return metadata.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("prompt-textarea", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("contenteditable", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("composer", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("lexical", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeKnownNonComposerMetadata(string metadata)
    {
        return metadata.Contains("smart-search-input", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("search chats", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("search chat history", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("chats durchsuchen", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("chatverlauf durchsuchen", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("command palette", StringComparison.OrdinalIgnoreCase);
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

    private static async Task<ClipboardTextReadResult> WaitForClipboardTextAsync(
        int timeoutMs,
        uint previousSequenceNumber,
        AppLogger logger)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            try
            {
                var sequenceBeforeRead = NativeMethods.GetClipboardSequenceNumber();
                if (sequenceBeforeRead != previousSequenceNumber && Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText().Trim();
                    var sequenceAfterRead = NativeMethods.GetClipboardSequenceNumber();
                    if (sequenceBeforeRead == sequenceAfterRead)
                    {
                        return new ClipboardTextReadResult(text, sequenceAfterRead);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("Clipboard polling failed.", ex);
            }

            await Task.Delay(100);
        }

        return ClipboardTextReadResult.Empty;
    }
}

internal sealed record ClipboardTextReadResult(string Text, uint SequenceNumber)
{
    public static ClipboardTextReadResult Empty { get; } = new(string.Empty, 0);
}

internal sealed record SafeClipboardCopyResult(
    string Text,
    ClipboardRestoreOutcome RestoreOutcome)
{
    public static SafeClipboardCopyResult Empty { get; } =
        new(string.Empty, ClipboardRestoreOutcome.NotRequested);
}

internal sealed record ChatGptDictationReadResult(
    string Text,
    string Method,
    int Attempts,
    AutomationElement? Input,
    bool IsStable,
    bool ClipboardRestoreFailed = false);

internal sealed record SafeFocusMetadata(string ControlType, string ClassName, string AutomationId);
