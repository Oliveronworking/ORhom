using System.Windows.Automation;

namespace ORhom;

internal sealed class PasteService
{
    private const int FocusConfirmationAttemptCount = 8;
    private const int FocusConfirmationRequiredMatches = 2;
    private const int FocusConfirmationDelayMs = 25;
    private const int DispatchConfirmationAttemptCount = 6;
    private const int DispatchConfirmationDelayMs = 15;
    private readonly AppLogger _logger;

    public PasteService(AppLogger logger)
    {
        _logger = logger;
    }

    public PasteResult PasteIntoTarget(string text, FocusTarget target, AppSettings settings)
    {
        if (target.IsPasswordField)
        {
            _logger.Info("Paste skipped because captured target is a password field.");
            return PasteResult.FailedWithClipboardFallback;
        }

        if (!RestoreTargetFocus(target))
        {
            _logger.Info("Paste skipped because the original target focus could not be confirmed.");
            return PasteResult.FailedWithClipboardFallback;
        }

        var focusedBeforePaste = AutomationHelpers.GetFocusedElement(_logger);
        if (settings.BlockPasswordFields && AutomationHelpers.IsPasswordElement(focusedBeforePaste))
        {
            _logger.Info("Paste skipped because current target is a password field.");
            return PasteResult.FailedWithClipboardFallback;
        }

        IDataObject? clipboardSnapshot = null;
        var capturedClipboardSequence = 0U;
        if (settings.RestoreClipboard &&
            !ClipboardHelper.TryCaptureStable(
                _logger,
                out clipboardSnapshot,
                out capturedClipboardSequence))
        {
            _logger.Info("Paste skipped because the current clipboard could not be snapshotted safely.");
            return PasteResult.FailedWithoutClipboardFallback;
        }

        if (settings.RestoreClipboard &&
            NativeMethods.GetClipboardSequenceNumber() != capturedClipboardSequence)
        {
            _logger.Info("Paste skipped because the clipboard changed after it was snapshotted.");
            return PasteResult.FailedWithoutClipboardFallback;
        }

        if (!ClipboardHelper.TrySetText(text, _logger))
        {
            var failedSetRestoreOutcome = ClipboardHelper.Restore(
                clipboardSnapshot,
                settings,
                _logger,
                settings.RestoreClipboard ? capturedClipboardSequence : null);
            return new PasteResult(
                false,
                failedSetRestoreOutcome is ClipboardRestoreOutcome.Restored or ClipboardRestoreOutcome.NotRequested,
                failedSetRestoreOutcome);
        }

        var ownedClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
        var dispatched = false;
        var method = "None";
        try
        {
            Thread.Sleep(Math.Max(settings.PasteDelayMs, 0));
            if (!IsPasteDispatchSafe(target, settings, text, ownedClipboardSequence))
            {
                _logger.Info("Paste dispatch skipped because target focus or clipboard ownership changed.");
            }
            else
            {
                dispatched = NativeMethods.SendPasteShortcut();
                method = "SendInput";
                if (!dispatched)
                {
                    if (IsPasteDispatchSafe(target, settings, text, ownedClipboardSequence))
                    {
                        SendKeys.SendWait("^v");
                        dispatched = true;
                        method = "SendKeysFallback";
                    }
                    else
                    {
                        _logger.Info("Paste SendKeys fallback skipped because target focus or clipboard ownership changed.");
                    }
                }
            }

            var foregroundVerified = NativeMethods.GetForegroundWindow() == target.WindowHandle &&
                                     HasExpectedWindowIdentity(target);
            _logger.Info($"Paste shortcut dispatched. Success={dispatched} Method={method} ForegroundVerified={foregroundVerified} PreserveWebViewFocus={target.AvoidAutomationElementFocus} TextLength={text.Length}");
            if (dispatched)
            {
                Thread.Sleep(Math.Max(settings.RestoreClipboardDelayMs, 0));
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Paste failed.", ex);
        }

        var textWasOnClipboardBeforeRestore = IsExpectedTextOnClipboard(
            text,
            ownedClipboardSequence);
        var restoreOutcome = ClipboardHelper.Restore(
            clipboardSnapshot,
            settings,
            _logger,
            ownedClipboardSequence);
        var textIsOnClipboard = restoreOutcome switch
        {
            ClipboardRestoreOutcome.NotRequested => textWasOnClipboardBeforeRestore,
            ClipboardRestoreOutcome.Failed => IsExpectedTextOnClipboard(text),
            _ => false
        };
        if (dispatched)
        {
            return new PasteResult(
                true,
                false,
                restoreOutcome,
                textIsOnClipboard);
        }

        var allowClipboardFallback = restoreOutcome == ClipboardRestoreOutcome.Restored;
        return new PasteResult(
            false,
            allowClipboardFallback,
            restoreOutcome,
            textIsOnClipboard);
    }

    private bool IsPasteDispatchSafe(
        FocusTarget target,
        AppSettings settings,
        string expectedText,
        uint ownedClipboardSequence)
    {
        return TargetFocusConfirmationPolicy.TryConfirm(
            () => ProbePasteDispatchSafety(
                target,
                settings,
                expectedText,
                ownedClipboardSequence),
            DispatchConfirmationAttemptCount,
            FocusConfirmationRequiredMatches,
            DispatchConfirmationDelayMs);
    }

    private TargetFocusProbeResult ProbePasteDispatchSafety(
        FocusTarget target,
        AppSettings settings,
        string expectedText,
        uint ownedClipboardSequence)
    {
        if (NativeMethods.GetForegroundWindow() != target.WindowHandle ||
            !HasExpectedWindowIdentity(target) ||
            NativeMethods.GetClipboardSequenceNumber() != ownedClipboardSequence)
        {
            return TargetFocusProbeResult.Unsafe;
        }

        var focused = AutomationHelpers.GetFocusedElement(_logger);
        if (settings.BlockPasswordFields && AutomationHelpers.IsPasswordElement(focused))
        {
            return TargetFocusProbeResult.Unsafe;
        }

        if (!IsExpectedTargetFocused(target, focused))
        {
            return TargetFocusProbeResult.TransientMismatch;
        }

        try
        {
            if (!Clipboard.ContainsText() ||
                !Clipboard.GetText().Equals(expectedText, StringComparison.Ordinal))
            {
                return NativeMethods.GetClipboardSequenceNumber() == ownedClipboardSequence
                    ? TargetFocusProbeResult.TransientMismatch
                    : TargetFocusProbeResult.Unsafe;
            }

            return NativeMethods.GetClipboardSequenceNumber() == ownedClipboardSequence
                ? TargetFocusProbeResult.Match
                : TargetFocusProbeResult.Unsafe;
        }
        catch (Exception ex)
        {
            _logger.Error("Paste clipboard ownership verification failed.", ex);
            return NativeMethods.GetClipboardSequenceNumber() == ownedClipboardSequence
                ? TargetFocusProbeResult.TransientMismatch
                : TargetFocusProbeResult.Unsafe;
        }
    }

    private bool IsExpectedTextOnClipboard(
        string expectedText,
        uint? expectedSequenceNumber = null)
    {
        try
        {
            var sequenceBeforeRead = NativeMethods.GetClipboardSequenceNumber();
            if (expectedSequenceNumber is not null &&
                sequenceBeforeRead != expectedSequenceNumber.Value)
            {
                return false;
            }

            return Clipboard.ContainsText() &&
                   Clipboard.GetText().Equals(expectedText, StringComparison.Ordinal) &&
                   NativeMethods.GetClipboardSequenceNumber() == sequenceBeforeRead;
        }
        catch (Exception ex)
        {
            _logger.Error("Paste clipboard fallback verification failed.", ex);
            return false;
        }
    }

    public bool RestoreTargetFocus(FocusTarget target)
    {
        if (!RestoreTargetWindow(target))
        {
            return false;
        }

        var elementFocusAttempted = false;
        var elementFocusSucceeded = false;
        var focusConfirmed = WaitForExpectedTargetFocus(
            target,
            out var focused,
            out var focusProbeAttempts);
        if (!focusConfirmed &&
            NativeMethods.GetForegroundWindow() == target.WindowHandle &&
            HasExpectedWindowIdentity(target) &&
            !target.AvoidAutomationElementFocus &&
            target.FocusedElement is not null)
        {
            elementFocusAttempted = true;
            elementFocusSucceeded = AutomationHelpers.TryFocusElement(target.FocusedElement, _logger);
            focusConfirmed = WaitForExpectedTargetFocus(
                target,
                out focused,
                out var postFocusProbeAttempts);
            focusProbeAttempts += postFocusProbeAttempts;
        }
        else if (!focusConfirmed && target.AvoidAutomationElementFocus)
        {
            _logger.Info("UI Automation focus restore skipped for WebView RootWebArea so the internal active element and caret are preserved.");
        }

        var focusedMetadata = AutomationHelpers.GetSafeFocusMetadata(focused);
        var focusedInsideTarget = AutomationHelpers.IsElementInWindow(focused, target.WindowHandle);
        var foregroundConfirmed = NativeMethods.GetForegroundWindow() == target.WindowHandle;
        focusConfirmed = focusConfirmed && foregroundConfirmed && HasExpectedWindowIdentity(target);
        _logger.Info($"Target focus restore completed. Success={focusConfirmed} ForegroundConfirmed={foregroundConfirmed} FocusProbeAttempts={focusProbeAttempts} ElementFocusAttempted={elementFocusAttempted} ElementFocusSucceeded={elementFocusSucceeded} FocusedInsideTarget={focusedInsideTarget} FocusedControlType='{focusedMetadata.ControlType}' FocusedClass='{focusedMetadata.ClassName}' FocusedAutomationId='{focusedMetadata.AutomationId}'");
        return focusConfirmed;
    }

    private bool WaitForExpectedTargetFocus(
        FocusTarget target,
        out AutomationElement? lastFocused,
        out int probeAttempts)
    {
        AutomationElement? focused = null;
        var attempts = 0;
        var confirmed = TargetFocusConfirmationPolicy.TryConfirm(
            () =>
            {
                attempts++;
                if (NativeMethods.GetForegroundWindow() != target.WindowHandle ||
                    !HasExpectedWindowIdentity(target))
                {
                    return TargetFocusProbeResult.Unsafe;
                }

                focused = AutomationHelpers.GetFocusedElement(_logger);
                return IsExpectedTargetFocused(target, focused)
                    ? TargetFocusProbeResult.Match
                    : TargetFocusProbeResult.TransientMismatch;
            },
            FocusConfirmationAttemptCount,
            FocusConfirmationRequiredMatches,
            FocusConfirmationDelayMs);
        lastFocused = focused;
        probeAttempts = attempts;
        return confirmed;
    }

    public bool RestoreTargetWindow(FocusTarget target)
    {
        if (target.WindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(target.WindowHandle))
        {
            _logger.Info("Target window restore failed because the original window no longer exists.");
            return false;
        }

        if (!HasExpectedWindowIdentity(target))
        {
            _logger.Info($"Target window restore failed because the window handle no longer belongs to the captured process. TargetWindow=0x{target.WindowHandle.ToInt64():X} ExpectedProcessId={target.OwningProcessId} ActualProcessId={NativeMethods.GetOwningProcessId(target.WindowHandle)}");
            return false;
        }

        if (!NativeMethods.ForceForegroundWindow(target.WindowHandle))
        {
            _logger.Info($"Target window restore failed because foreground activation was not confirmed. TargetWindow=0x{target.WindowHandle.ToInt64():X} ActualForeground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            return false;
        }

        var foregroundConfirmed = NativeMethods.GetForegroundWindow() == target.WindowHandle;
        var processConfirmed = HasExpectedWindowIdentity(target);
        var success = foregroundConfirmed && processConfirmed;
        _logger.Info($"Target window restore completed without UI Automation. Success={success} ForegroundConfirmed={foregroundConfirmed} ProcessConfirmed={processConfirmed} TargetWindow=0x{target.WindowHandle.ToInt64():X} ProcessId={target.OwningProcessId}");
        return success;
    }

    private bool IsExpectedTargetFocused(FocusTarget target, AutomationElement? focused)
    {
        var candidateWindowClass = NativeMethods.GetWindowClass(target.WindowHandle);
        if (!HasExpectedWindowIdentity(target) ||
            !AutomationHelpers.IsElementInWindow(focused, target.WindowHandle))
        {
            return false;
        }

        if (target.FocusedElement is not null &&
            AutomationHelpers.IsElementInWindow(target.FocusedElement, target.WindowHandle))
        {
            var exactOrCapturedRootMatch = target.AvoidAutomationElementFocus
                ? AutomationHelpers.IsSameElementOrWithinCapturedWebViewRoot(
                    focused,
                    target.FocusedElement)
                : AutomationHelpers.AreSameElement(focused, target.FocusedElement);
            if (exactOrCapturedRootMatch)
            {
                return true;
            }
        }

        var focusedMetadata = AutomationHelpers.GetSafeFocusMetadata(focused);
        var focusedWebViewRootIdentity = AutomationHelpers.GetSafeWebViewRootIdentity(focused);
        var focusedLayoutFingerprint = AutomationHelpers.GetSafeFocusLayoutFingerprint(
            focused,
            target.WindowHandle);
        var semanticMatch = FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            target.WindowHandle,
            target.OwningProcessId,
            target.WindowClass,
            target.FocusMetadata,
            target.WebViewRootIdentity,
            target.LayoutFingerprint,
            target.WindowHandle,
            NativeMethods.GetOwningProcessId(target.WindowHandle),
            candidateWindowClass,
            focusedMetadata,
            focusedWebViewRootIdentity,
            focusedLayoutFingerprint,
            AutomationHelpers.IsPasswordElement(focused));
        if (semanticMatch)
        {
            _logger.Info($"Target focus accepted through a strong semantic editor fingerprint after the UI Automation runtime identity changed. AutomationId='{focusedMetadata.AutomationId}' Class='{focusedMetadata.ClassName}'");
        }

        return semanticMatch;
    }

    private static bool HasExpectedWindowIdentity(FocusTarget target) =>
        FocusTargetSafetyPolicy.HasSameWindowIdentity(
            target.WindowHandle,
            target.OwningProcessId,
            target.WindowHandle,
            NativeMethods.GetOwningProcessId(target.WindowHandle));
}

internal sealed record PasteResult(
    bool Succeeded,
    bool AllowClipboardFallback,
    ClipboardRestoreOutcome ClipboardRestoreOutcome,
    bool TextIsOnClipboard = false)
{
    public bool ShouldAttemptClipboardFallback =>
        !Succeeded && AllowClipboardFallback && !TextIsOnClipboard;

    public static PasteResult FailedWithClipboardFallback { get; } =
        new(false, true, ClipboardRestoreOutcome.NotRequested);

    public static PasteResult FailedWithoutClipboardFallback { get; } =
        new(false, false, ClipboardRestoreOutcome.NotRequested);
}
