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
        if (target.IsPasswordFieldOrUnverifiable)
        {
            _logger.Info("Paste skipped because the captured target is a password field or could not be verified safely.");
            return PasteResult.FailedWithClipboardFallback;
        }

        if (!RestoreTargetFocus(target))
        {
            _logger.Info("Paste skipped because the original target focus could not be confirmed.");
            return PasteResult.FailedWithClipboardFallback;
        }

        var focusedBeforePaste = AutomationHelpers.GetFocusedElement(_logger);
        if (AutomationHelpers.ShouldBlockPasswordField(
                focusedBeforePaste,
                settings.BlockPasswordFields))
        {
            _logger.Info("Paste skipped because the current target is a password field or could not be verified safely.");
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

        using var ownedClipboardSnapshot = clipboardSnapshot as IDisposable;
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
        var pasteMayHaveReachedTarget = false;
        var method = "None";
        PasteShortcutDispatchResult? dispatchResult = null;
        try
        {
            Thread.Sleep(Math.Max(settings.PasteDelayMs, 0));
            if (!IsPasteDispatchSafe(target, settings, text, ownedClipboardSequence))
            {
                _logger.Info("Paste dispatch skipped because target focus or clipboard ownership changed.");
            }
            else
            {
                dispatchResult = NativeMethods.SendPasteShortcut();
                var decision = PasteDispatchSafetyPolicy.Decide(dispatchResult.Value);
                dispatched = decision == PasteDispatchDecision.ConfirmedSuccess;
                pasteMayHaveReachedTarget =
                    decision == PasteDispatchDecision.UnconfirmedMayHaveReachedTarget;
                method = dispatched
                    ? "SendInput"
                    : "SendInputUnconfirmed";
                if (decision != PasteDispatchDecision.ConfirmedSuccess)
                {
                    // A partial SendInput may already have reached the target.
                    // SendKeys has no delivery acknowledgement, so retrying here
                    // could duplicate the paste while still reporting false success.
                    _logger.Info(
                        $"Paste dispatch was not fully confirmed. MayHaveReachedTarget={pasteMayHaveReachedTarget} AcceptedInputs={dispatchResult.Value.AcceptedInputCount} RequestedInputs={dispatchResult.Value.RequestedInputCount} CleanupAttempted={dispatchResult.Value.CleanupAttempted} CleanupSucceeded={dispatchResult.Value.CleanupSucceeded}");
                }
            }

            var foregroundVerified = NativeMethods.GetForegroundWindow() == target.WindowHandle &&
                                     HasExpectedWindowIdentity(target);
            _logger.Info($"Paste shortcut dispatched. Success={dispatched} Method={method} ForegroundVerified={foregroundVerified} PreserveWebViewFocus={target.AvoidAutomationElementFocus} TextLength={text.Length}");
            if (dispatched || pasteMayHaveReachedTarget)
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

        var allowClipboardFallback =
            !pasteMayHaveReachedTarget &&
            restoreOutcome == ClipboardRestoreOutcome.Restored;
        return new PasteResult(
            false,
            allowClipboardFallback,
            restoreOutcome,
            textIsOnClipboard,
            pasteMayHaveReachedTarget);
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
        if (AutomationHelpers.ShouldBlockPasswordField(
                focused,
                settings.BlockPasswordFields))
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
        var candidateWindowTitle = NativeMethods.GetWindowTitle(target.WindowHandle);
        if (!HasExpectedWindowIdentity(target) ||
            !FocusTargetSafetyPolicy.HasSameViewIdentity(
                target.WindowClass,
                target.WindowTitle,
                candidateWindowClass,
                candidateWindowTitle) ||
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
            target.WindowTitle,
            target.FocusMetadata,
            target.WebViewRootIdentity,
            target.LayoutFingerprint,
            target.WindowHandle,
            NativeMethods.GetOwningProcessId(target.WindowHandle),
            candidateWindowClass,
            candidateWindowTitle,
            focusedMetadata,
            focusedWebViewRootIdentity,
            focusedLayoutFingerprint,
            AutomationHelpers.ShouldBlockPasswordField(
                focused,
                blockPasswordFields: true));
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

internal static class PasteDispatchSafetyPolicy
{
    public static PasteDispatchDecision Decide(PasteShortcutDispatchResult result)
    {
        if (result.RequestedInputCount > 0 &&
            result.AcceptedInputCount == result.RequestedInputCount &&
            !result.CleanupAttempted &&
            result.CleanupSucceeded)
        {
            return PasteDispatchDecision.ConfirmedSuccess;
        }

        return MayHaveReachedTarget(result)
            ? PasteDispatchDecision.UnconfirmedMayHaveReachedTarget
            : PasteDispatchDecision.FailWithClipboardFallback;
    }

    public static bool MayHaveReachedTarget(PasteShortcutDispatchResult result) =>
        result.AcceptedInputCount >= 2;
}

internal enum PasteDispatchDecision
{
    ConfirmedSuccess,
    FailWithClipboardFallback,
    UnconfirmedMayHaveReachedTarget
}

internal sealed record PasteResult(
    bool Succeeded,
    bool AllowClipboardFallback,
    ClipboardRestoreOutcome ClipboardRestoreOutcome,
    bool TextIsOnClipboard = false,
    bool PasteMayHaveReachedTarget = false)
{
    public bool ShouldAttemptClipboardFallback =>
        !Succeeded &&
        !PasteMayHaveReachedTarget &&
        AllowClipboardFallback &&
        !TextIsOnClipboard;

    public bool ShouldCopyUnconfirmedTextAsLastResort =>
        !Succeeded &&
        PasteMayHaveReachedTarget &&
        !TextIsOnClipboard;

    public static PasteResult FailedWithClipboardFallback { get; } =
        new(false, true, ClipboardRestoreOutcome.NotRequested);

    public static PasteResult FailedWithoutClipboardFallback { get; } =
        new(false, false, ClipboardRestoreOutcome.NotRequested);
}
