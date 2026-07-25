namespace ORhom;

internal enum RecordingUiState
{
    Unknown,
    Inactive,
    Active
}

internal enum CandidateObservation
{
    Changed,
    Waiting,
    Stable
}

internal sealed class DictationCandidateTracker
{
    private string _text = string.Empty;
    private string _method = "None";
    private long _changedAt;
    private bool _uncertainSinceLastCandidate;

    public string Text => _text;
    public string Method => _method;
    public long ChangedAt => _changedAt;
    public bool HasCandidate => _text.Length > 0;

    public CandidateObservation Observe(string candidate, string method, long now, int stableMs)
    {
        candidate = candidate.Trim();
        if (!candidate.Equals(_text, StringComparison.Ordinal))
        {
            _text = candidate;
            _method = method;
            _changedAt = now;
            _uncertainSinceLastCandidate = false;
            return CandidateObservation.Changed;
        }

        if (_uncertainSinceLastCandidate)
        {
            _uncertainSinceLastCandidate = false;
            _changedAt = now;
            return CandidateObservation.Waiting;
        }

        return HasCandidate && now - _changedAt >= Math.Max(stableMs, 0)
            ? CandidateObservation.Stable
            : CandidateObservation.Waiting;
    }

    public void MarkUncertain()
    {
        if (HasCandidate)
        {
            _uncertainSinceLastCandidate = true;
        }
    }
}

internal sealed class RecordingStateConfirmationTracker
{
    private readonly RecordingUiState _expected;
    private int _consecutiveMatches;

    public RecordingStateConfirmationTracker(RecordingUiState expected)
    {
        _expected = expected;
    }

    public bool Observe(RecordingUiState state, bool finalProbe = false)
    {
        _consecutiveMatches = state == _expected ? _consecutiveMatches + 1 : 0;
        return _consecutiveMatches >= 2 || (finalProbe && state == _expected);
    }
}

internal static class HybridPushToTalkPolicy
{
    public static bool ShouldStopOnRelease(bool enabled, long heldForMs, int thresholdMs)
    {
        return enabled && heldForMs >= Math.Clamp(thresholdMs, 200, 1500);
    }
}

internal static class StopTransitionTimeoutPolicy
{
    public const int DefaultRecordingStateTimeoutMs = 5_000;
    public const int DefaultTranscriptionTimeoutMs = 300_000;
    public const int MinimumSafeTranscriptionTimeoutMs = 300_000;
    public const int MaximumTimeoutMs = 900_000;

    public static int ResolveTimeoutMs(
        int recordingStateTimeoutMs = DefaultRecordingStateTimeoutMs,
        int transcriptionTimeoutMs = DefaultTranscriptionTimeoutMs)
    {
        var effectiveRecordingStateTimeoutMs = recordingStateTimeoutMs > 0
            ? Math.Min(recordingStateTimeoutMs, MaximumTimeoutMs)
            : DefaultRecordingStateTimeoutMs;
        var effectiveTranscriptionTimeoutMs = ResolveTranscriptionTimeoutMs(
            transcriptionTimeoutMs);

        return Math.Max(effectiveRecordingStateTimeoutMs, effectiveTranscriptionTimeoutMs);
    }

    public static int ResolveTranscriptionTimeoutMs(
        int transcriptionTimeoutMs = DefaultTranscriptionTimeoutMs)
    {
        if (transcriptionTimeoutMs <= 0)
        {
            return DefaultTranscriptionTimeoutMs;
        }

        return Math.Clamp(
            transcriptionTimeoutMs,
            MinimumSafeTranscriptionTimeoutMs,
            MaximumTimeoutMs);
    }
}

internal static class RecoveryTextPolicy
{
    public static bool CanUseNormalPastePath(
        string? text,
        bool isStable,
        bool completedWithoutDestructiveCleanup)
    {
        return completedWithoutDestructiveCleanup &&
               isStable &&
               !string.IsNullOrWhiteSpace(text) &&
               !AutomationHelpers.IsUnsafeCapturedText(text);
    }
}

internal static class PendingComposerStartRecovery
{
    public static async Task<ChatGptStartResult> RetryKnownPersistedTextOnceAsync(
        ChatGptStartResult initialResult,
        Func<ChatGptStartResult, Task<bool>> tryClearKnownPersistedText,
        Func<Task<ChatGptStartResult>> retryStart)
    {
        var pendingText = initialResult.PendingText.Trim();
        if (initialResult.Failure != ChatGptFailure.PendingText ||
            pendingText.Length == 0 ||
            AutomationHelpers.IsUnsafeCapturedText(pendingText))
        {
            return initialResult;
        }

        if (!await tryClearKnownPersistedText(initialResult))
        {
            return initialResult;
        }

        // Deliberately retry exactly once. A second PendingText result remains a
        // hard safety stop instead of turning into a cleanup loop.
        return await retryStart();
    }
}

internal static class UnexpectedFailureStatePolicy
{
    public static bool RecordingMayStillBeActive(
        AppStatus status,
        bool hasSession) =>
        hasSession && status is
            AppStatus.Starting or
            AppStatus.Recording or
            AppStatus.Stopping or
            AppStatus.ReadingText;
}

internal static class FocusRestorationPolicy
{
    public static bool ShouldAvoidAutomationElementFocus(
        string windowClass,
        bool elementIsWebViewRoot) =>
        elementIsWebViewRoot ||
        windowClass.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase);
}

internal enum TargetFocusProbeResult
{
    Match,
    TransientMismatch,
    Unsafe
}

internal static class TargetFocusConfirmationPolicy
{
    public static bool TryConfirm(
        Func<TargetFocusProbeResult> probe,
        int maximumAttempts,
        int requiredConsecutiveMatches,
        int retryDelayMs,
        Action<int>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        maximumAttempts = Math.Max(maximumAttempts, 1);
        requiredConsecutiveMatches = Math.Clamp(
            requiredConsecutiveMatches,
            1,
            maximumAttempts);
        retryDelayMs = Math.Max(retryDelayMs, 0);
        delay ??= Thread.Sleep;

        var consecutiveMatches = 0;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            switch (probe())
            {
                case TargetFocusProbeResult.Match:
                    consecutiveMatches++;
                    if (consecutiveMatches >= requiredConsecutiveMatches)
                    {
                        return true;
                    }

                    break;

                case TargetFocusProbeResult.TransientMismatch:
                    consecutiveMatches = 0;
                    break;

                case TargetFocusProbeResult.Unsafe:
                    return false;

                default:
                    throw new InvalidOperationException("Unknown target focus probe result.");
            }

            if (attempt < maximumAttempts && retryDelayMs > 0)
            {
                delay(retryDelayMs);
            }
        }

        return false;
    }
}

internal static class FocusTargetSafetyPolicy
{
    private const string ChromiumWindowClass = "Chrome_WidgetWin_1";
    private const string ProseMirrorMarker = "ProseMirror";

    public static bool HasSameWindowIdentity(
        IntPtr expectedWindow,
        uint expectedProcessId,
        IntPtr candidateWindow,
        uint candidateProcessId) =>
        expectedWindow != IntPtr.Zero &&
        expectedWindow == candidateWindow &&
        expectedProcessId != 0 &&
        expectedProcessId == candidateProcessId;

    public static bool HasSameViewIdentity(
        string originalWindowClass,
        string originalWindowTitle,
        string candidateWindowClass,
        string candidateWindowTitle)
    {
        if (!IsChromiumWindow(originalWindowClass))
        {
            return true;
        }

        return IsChromiumWindow(candidateWindowClass) &&
               !string.IsNullOrWhiteSpace(originalWindowTitle) &&
               !string.IsNullOrWhiteSpace(candidateWindowTitle) &&
               originalWindowTitle.Equals(candidateWindowTitle, StringComparison.Ordinal);
    }

    public static bool IsTransientChromiumMainTarget(
        string windowClass,
        SafeFocusMetadata metadata) =>
        IsChromiumWindow(windowClass) &&
        metadata.ControlType.Equals("ControlType.Group", StringComparison.OrdinalIgnoreCase) &&
        metadata.AutomationId.Equals("main", StringComparison.OrdinalIgnoreCase);

    public static bool IsStrongSemanticWebEditor(
        string windowClass,
        SafeFocusMetadata metadata)
    {
        if (!IsChromiumWindow(windowClass) ||
            !metadata.ClassName.Contains(ProseMirrorMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supportedControlType =
            metadata.ControlType.Equals("ControlType.Edit", StringComparison.OrdinalIgnoreCase) ||
            metadata.ControlType.Equals("ControlType.Document", StringComparison.OrdinalIgnoreCase) ||
            metadata.ControlType.Equals("ControlType.Custom", StringComparison.OrdinalIgnoreCase) ||
            metadata.ControlType.Equals("ControlType.Group", StringComparison.OrdinalIgnoreCase);
        if (!supportedControlType)
        {
            return false;
        }

        // During a Chromium accessibility-tree rebuild the focused ProseMirror
        // node is sometimes exposed as a Group without its usual AutomationId.
        // The focused class marker is the stable semantic signal in that state.
        return !string.IsNullOrWhiteSpace(metadata.AutomationId) ||
               (metadata.ControlType.Equals(
                    "ControlType.Group",
                    StringComparison.OrdinalIgnoreCase) &&
                HasFocusedProseMirrorMarker(metadata));
    }

    public static bool ShouldPromoteTransientChromiumTarget(
        IntPtr originalWindow,
        uint originalProcessId,
        string originalWindowClass,
        string originalWindowTitle,
        SafeFocusMetadata originalMetadata,
        string originalWebViewRootIdentity,
        IntPtr candidateWindow,
        uint candidateProcessId,
        string candidateWindowClass,
        string candidateWindowTitle,
        SafeFocusMetadata candidateMetadata,
        string candidateWebViewRootIdentity,
        bool candidateIsPassword) =>
        !candidateIsPassword &&
        HasSameWindowIdentity(
            originalWindow,
            originalProcessId,
            candidateWindow,
            candidateProcessId) &&
        HasSameViewIdentity(
            originalWindowClass,
            originalWindowTitle,
            candidateWindowClass,
            candidateWindowTitle) &&
        HasCompatiblePromotionRootIdentity(
            originalWebViewRootIdentity,
            candidateWebViewRootIdentity) &&
        IsTransientChromiumCaptureTarget(originalWindowClass, originalMetadata) &&
        IsStrongSemanticWebEditor(candidateWindowClass, candidateMetadata);

    public static bool CanAcceptSemanticEditorReplacement(
        IntPtr originalWindow,
        uint originalProcessId,
        string originalWindowClass,
        SafeFocusMetadata originalMetadata,
        string originalWebViewRootIdentity,
        SafeFocusLayoutFingerprint originalLayout,
        IntPtr candidateWindow,
        uint candidateProcessId,
        string candidateWindowClass,
        SafeFocusMetadata candidateMetadata,
        string candidateWebViewRootIdentity,
        SafeFocusLayoutFingerprint candidateLayout,
        bool candidateIsPassword) =>
        !candidateIsPassword &&
        HasSameWindowIdentity(
            originalWindow,
            originalProcessId,
            candidateWindow,
            candidateProcessId) &&
        HasSameWebViewRootIdentity(
            originalWebViewRootIdentity,
            candidateWebViewRootIdentity) &&
        IsStrongSemanticWebEditor(originalWindowClass, originalMetadata) &&
        IsStrongSemanticWebEditor(candidateWindowClass, candidateMetadata) &&
        HasStrongEditorLayoutMatch(originalLayout, candidateLayout) &&
        HaveCompatibleEditorAutomationIds(originalMetadata, candidateMetadata);

    public static bool HasStrongEditorLayoutMatch(
        SafeFocusLayoutFingerprint original,
        SafeFocusLayoutFingerprint candidate)
    {
        if (!original.IsValid || !candidate.IsValid)
        {
            return false;
        }

        var originalCenterX = original.RelativeLeft + original.RelativeWidth / 2;
        var originalCenterY = original.RelativeTop + original.RelativeHeight / 2;
        var candidateCenterX = candidate.RelativeLeft + candidate.RelativeWidth / 2;
        var candidateCenterY = candidate.RelativeTop + candidate.RelativeHeight / 2;
        var widthTolerance = Math.Max(0.03, original.RelativeWidth * 0.15);
        var heightTolerance = Math.Max(0.03, original.RelativeHeight * 0.25);

        var intersectionWidth = Math.Max(
            0,
            Math.Min(
                original.RelativeLeft + original.RelativeWidth,
                candidate.RelativeLeft + candidate.RelativeWidth) -
            Math.Max(original.RelativeLeft, candidate.RelativeLeft));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(
                original.RelativeTop + original.RelativeHeight,
                candidate.RelativeTop + candidate.RelativeHeight) -
            Math.Max(original.RelativeTop, candidate.RelativeTop));
        var intersectionArea = intersectionWidth * intersectionHeight;
        var smallerArea = Math.Min(
            original.RelativeWidth * original.RelativeHeight,
            candidate.RelativeWidth * candidate.RelativeHeight);
        var overlapRatio = intersectionArea / smallerArea;

        return Math.Abs(originalCenterX - candidateCenterX) <= 0.05 &&
               Math.Abs(originalCenterY - candidateCenterY) <= 0.05 &&
               Math.Abs(original.RelativeWidth - candidate.RelativeWidth) <= widthTolerance &&
               Math.Abs(original.RelativeHeight - candidate.RelativeHeight) <= heightTolerance &&
               overlapRatio >= 0.6;
    }

    private static bool HasSameWebViewRootIdentity(
        string originalIdentity,
        string candidateIdentity) =>
        !string.IsNullOrWhiteSpace(originalIdentity) &&
        !string.IsNullOrWhiteSpace(candidateIdentity) &&
        originalIdentity.Equals(candidateIdentity, StringComparison.Ordinal);

    private static bool HasCompatiblePromotionRootIdentity(
        string originalIdentity,
        string candidateIdentity) =>
        !string.IsNullOrWhiteSpace(candidateIdentity) &&
        (string.IsNullOrWhiteSpace(originalIdentity) ||
         originalIdentity.Equals(candidateIdentity, StringComparison.Ordinal));

    private static bool IsTransientChromiumCaptureTarget(
        string windowClass,
        SafeFocusMetadata metadata)
    {
        if (!IsChromiumWindow(windowClass))
        {
            return false;
        }

        if (IsTransientChromiumMainTarget(windowClass, metadata) ||
            metadata.ControlType.Equals("<null>", StringComparison.OrdinalIgnoreCase) ||
            metadata.ControlType.Equals("<stale>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isWindowShell =
            metadata.ControlType.Equals("ControlType.Pane", StringComparison.OrdinalIgnoreCase) ||
            metadata.ControlType.Equals("ControlType.Window", StringComparison.OrdinalIgnoreCase);
        return isWindowShell &&
               metadata.ClassName.Equals(
                   ChromiumWindowClass,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HaveCompatibleEditorAutomationIds(
        SafeFocusMetadata original,
        SafeFocusMetadata candidate)
    {
        if (!string.IsNullOrWhiteSpace(original.AutomationId) &&
            !string.IsNullOrWhiteSpace(candidate.AutomationId))
        {
            return original.AutomationId.Equals(
                candidate.AutomationId,
                StringComparison.OrdinalIgnoreCase);
        }

        return HasFocusedProseMirrorMarker(original) &&
               HasFocusedProseMirrorMarker(candidate);
    }

    private static bool HasFocusedProseMirrorMarker(SafeFocusMetadata metadata) =>
        metadata.ClassName.Contains(
            "ProseMirror-focused",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsChromiumWindow(string windowClass) =>
        windowClass.Equals(ChromiumWindowClass, StringComparison.OrdinalIgnoreCase);
}
