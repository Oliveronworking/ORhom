namespace ORhom.Tests;

public sealed class DictationCandidateTrackerTests
{
    [Fact]
    public void CandidateBecomesStableOnlyAfterConfiguredDuration()
    {
        var tracker = new DictationCandidateTracker();

        Assert.Equal(CandidateObservation.Changed, tracker.Observe("Hallo", "ValuePattern", 100, 1000));
        Assert.Equal(CandidateObservation.Waiting, tracker.Observe("Hallo", "ValuePattern", 1099, 1000));
        Assert.Equal(CandidateObservation.Stable, tracker.Observe("Hallo", "ValuePattern", 1100, 1000));
        Assert.Equal("Hallo", tracker.Text);
        Assert.Equal("ValuePattern", tracker.Method);
    }

    [Fact]
    public void ChangedCandidateRestartsStabilityWindow()
    {
        var tracker = new DictationCandidateTracker();

        tracker.Observe("Hallo", "ValuePattern", 0, 1000);
        Assert.Equal(CandidateObservation.Changed, tracker.Observe("Hallo Welt", "TextPattern", 800, 1000));
        Assert.Equal(CandidateObservation.Waiting, tracker.Observe("Hallo Welt", "TextPattern", 1799, 1000));
        Assert.Equal(CandidateObservation.Stable, tracker.Observe("Hallo Welt", "TextPattern", 1800, 1000));
    }

    [Fact]
    public void UncertainReadRequiresFreshStabilityEvidence()
    {
        var tracker = new DictationCandidateTracker();

        tracker.Observe("vollständiger Text", "ValuePattern", 0, 1000);
        tracker.MarkUncertain();

        Assert.Equal(CandidateObservation.Waiting, tracker.Observe("vollständiger Text", "ValuePattern", 1200, 1000));
        Assert.Equal(CandidateObservation.Stable, tracker.Observe("vollständiger Text", "ValuePattern", 2200, 1000));
    }

    [Fact]
    public void CandidateIsTrimmedBeforeComparison()
    {
        var tracker = new DictationCandidateTracker();

        tracker.Observe("  Text  ", "ValuePattern", 0, 300);

        Assert.Equal("Text", tracker.Text);
        Assert.Equal(CandidateObservation.Stable, tracker.Observe("Text", "ValuePattern", 300, 300));
    }
}

public sealed class RecordingStateConfirmationTrackerTests
{
    [Fact]
    public void NormalPollingRequiresTwoConsecutiveMatches()
    {
        var tracker = new RecordingStateConfirmationTracker(RecordingUiState.Inactive);

        Assert.False(tracker.Observe(RecordingUiState.Inactive));
        Assert.False(tracker.Observe(RecordingUiState.Active));
        Assert.False(tracker.Observe(RecordingUiState.Inactive));
        Assert.True(tracker.Observe(RecordingUiState.Inactive));
    }

    [Fact]
    public void FinalDeadlineProbeAcceptsStrongExpectedState()
    {
        var tracker = new RecordingStateConfirmationTracker(RecordingUiState.Inactive);

        Assert.False(tracker.Observe(RecordingUiState.Active));
        Assert.True(tracker.Observe(RecordingUiState.Inactive, finalProbe: true));
    }

    [Fact]
    public void FinalDeadlineProbeRejectsWrongState()
    {
        var tracker = new RecordingStateConfirmationTracker(RecordingUiState.Inactive);

        Assert.False(tracker.Observe(RecordingUiState.Active, finalProbe: true));
    }
}

public sealed class HybridPushToTalkPolicyTests
{
    [Theory]
    [InlineData(true, 349, 350, false)]
    [InlineData(true, 350, 350, true)]
    [InlineData(false, 1000, 350, false)]
    [InlineData(true, 199, 0, false)]
    [InlineData(true, 200, 0, true)]
    public void ReleaseDecisionHonorsModeAndThreshold(
        bool enabled,
        long heldForMs,
        int thresholdMs,
        bool expected)
    {
        Assert.Equal(expected, HybridPushToTalkPolicy.ShouldStopOnRelease(enabled, heldForMs, thresholdMs));
    }
}

public sealed class StopTransitionTimeoutPolicyTests
{
    [Fact]
    public void DefaultStopTransitionTimeoutUsesFiveMinuteTranscriptionTimeout()
    {
        Assert.Equal(300_000, StopTransitionTimeoutPolicy.ResolveTimeoutMs());
    }

    [Theory]
    [InlineData(5_000, 30_000, 300_000)]
    [InlineData(5_000, 45_000, 300_000)]
    [InlineData(60_000, 30_000, 300_000)]
    [InlineData(5_000, 420_000, 420_000)]
    public void StopTransitionTimeoutIsNeverShorterThanEitherRequiredTimeout(
        int recordingStateTimeoutMs,
        int transcriptionTimeoutMs,
        int expected)
    {
        Assert.Equal(
            expected,
            StopTransitionTimeoutPolicy.ResolveTimeoutMs(
                recordingStateTimeoutMs,
                transcriptionTimeoutMs));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void InvalidTimeoutsFallBackToSafeDefaults(
        int recordingStateTimeoutMs,
        int transcriptionTimeoutMs)
    {
        Assert.Equal(
            StopTransitionTimeoutPolicy.DefaultTranscriptionTimeoutMs,
            StopTransitionTimeoutPolicy.ResolveTimeoutMs(
                recordingStateTimeoutMs,
                transcriptionTimeoutMs));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidReaderTimeoutUsesTheSameSafeDefault(int transcriptionTimeoutMs)
    {
        Assert.Equal(
            StopTransitionTimeoutPolicy.DefaultTranscriptionTimeoutMs,
            StopTransitionTimeoutPolicy.ResolveTranscriptionTimeoutMs(
                transcriptionTimeoutMs));
    }

    [Fact]
    public void ExcessiveTimeoutsAreClampedToFifteenMinutes()
    {
        Assert.Equal(
            StopTransitionTimeoutPolicy.MaximumTimeoutMs,
            StopTransitionTimeoutPolicy.ResolveTimeoutMs(
                int.MaxValue,
                int.MaxValue));
        Assert.Equal(
            StopTransitionTimeoutPolicy.MaximumTimeoutMs,
            StopTransitionTimeoutPolicy.ResolveTranscriptionTimeoutMs(
                int.MaxValue));
    }
}

public sealed class RecoveryTextPolicyTests
{
    [Fact]
    public void StableSafeRecoveryTextCanUseNormalPastePath()
    {
        Assert.True(RecoveryTextPolicy.CanUseNormalPastePath(
            "Das ist ein vollständig transkribierter Text.",
            isStable: true,
            completedWithoutDestructiveCleanup: true));
    }

    [Fact]
    public void LongStableUnicodeRecoveryTextCanUseNormalPastePath()
    {
        var text = string.Concat(Enumerable.Repeat(
            "Grüße aus Wien – déjà-vu, 東京 und ein Emoji 🧠. ",
            32));

        Assert.True(text.Length > 1_000);
        Assert.True(RecoveryTextPolicy.CanUseNormalPastePath(
            text,
            isStable: true,
            completedWithoutDestructiveCleanup: true));
    }

    [Fact]
    public void UnstableRecoveryTextCannotUseNormalPastePath()
    {
        Assert.False(RecoveryTextPolicy.CanUseNormalPastePath(
            "Noch nicht stabiler Zwischenstand",
            isStable: false,
            completedWithoutDestructiveCleanup: true));
    }

    [Fact]
    public void DestructiveOrUnconfirmedCleanupCannotUseNormalPastePath()
    {
        Assert.False(RecoveryTextPolicy.CanUseNormalPastePath(
            "Vollständiger stabiler Text",
            isStable: true,
            completedWithoutDestructiveCleanup: false));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData(" http://example.com/recovered ")]
    public void UnsafeRecoveryTextCannotUseNormalPastePath(string text)
    {
        Assert.False(RecoveryTextPolicy.CanUseNormalPastePath(
            text,
            isStable: true,
            completedWithoutDestructiveCleanup: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n")]
    public void EmptyRecoveryTextCannotUseNormalPastePath(string? text)
    {
        Assert.False(RecoveryTextPolicy.CanUseNormalPastePath(
            text,
            isStable: true,
            completedWithoutDestructiveCleanup: true));
    }
}

public sealed class PendingComposerStartRecoveryTests
{
    [Fact]
    public async Task KnownPersistedTextIsClearedAndStartIsRetriedExactlyOnce()
    {
        var clearCalls = 0;
        var retryCalls = 0;
        var initial = Pending("Geretteter Text");

        var result = await PendingComposerStartRecovery.RetryKnownPersistedTextOnceAsync(
            initial,
            _ =>
            {
                clearCalls++;
                return Task.FromResult(true);
            },
            () =>
            {
                retryCalls++;
                return Task.FromResult(ChatGptStartResult.Success((IntPtr)42, null));
            });

        Assert.True(result.Ok);
        Assert.Equal(1, clearCalls);
        Assert.Equal(1, retryCalls);
    }

    [Fact]
    public async Task UnapprovedTextIsNotRetried()
    {
        var clearCalls = 0;
        var retryCalls = 0;
        var initial = Pending("Nicht im Verlauf");

        var result = await PendingComposerStartRecovery.RetryKnownPersistedTextOnceAsync(
            initial,
            _ =>
            {
                clearCalls++;
                return Task.FromResult(false);
            },
            () =>
            {
                retryCalls++;
                return Task.FromResult(ChatGptStartResult.Success((IntPtr)42, null));
            });

        Assert.Same(initial, result);
        Assert.Equal(1, clearCalls);
        Assert.Equal(0, retryCalls);
    }

    [Fact]
    public async Task FailedClearKeepsSafetyBlockAndDoesNotRetry()
    {
        var retryCalls = 0;
        var initial = Pending("Geretteter Text");

        var result = await PendingComposerStartRecovery.RetryKnownPersistedTextOnceAsync(
            initial,
            _ => Task.FromResult(false),
            () =>
            {
                retryCalls++;
                return Task.FromResult(ChatGptStartResult.Success((IntPtr)42, null));
            });

        Assert.Same(initial, result);
        Assert.Equal(0, retryCalls);
    }

    [Fact]
    public async Task PendingRetryDoesNotCreateARecoveryLoop()
    {
        var retryCalls = 0;
        var initial = Pending("Geretteter Text");

        var result = await PendingComposerStartRecovery.RetryKnownPersistedTextOnceAsync(
            initial,
            _ => Task.FromResult(true),
            () =>
            {
                retryCalls++;
                return Task.FromResult(Pending("Geretteter Text"));
            });

        Assert.Equal(ChatGptFailure.PendingText, result.Failure);
        Assert.Equal(1, retryCalls);
    }

    [Fact]
    public async Task UnsafePendingUrlIsNeverAutomaticallyCleared()
    {
        var clearCalls = 0;
        var initial = Pending("https://example.com");

        var result = await PendingComposerStartRecovery.RetryKnownPersistedTextOnceAsync(
            initial,
            _ =>
            {
                clearCalls++;
                return Task.FromResult(true);
            },
            () => Task.FromResult(ChatGptStartResult.Success((IntPtr)42, null)));

        Assert.Same(initial, result);
        Assert.Equal(0, clearCalls);
    }

    private static ChatGptStartResult Pending(string text) =>
        ChatGptStartResult.Fail(
            ChatGptFailure.PendingText,
            "blocked",
            chatWindow: (IntPtr)42,
            pendingText: text);
}

public sealed class UnexpectedFailureStatePolicyTests
{
    [Theory]
    [InlineData(1, true, true)] // Starting
    [InlineData(2, true, true)] // Recording
    [InlineData(3, true, true)] // Stopping
    [InlineData(4, true, true)] // ReadingText
    [InlineData(5, true, false)] // Pasting
    [InlineData(0, true, false)] // Idle
    [InlineData(2, false, false)] // Recording without an active session
    public void OnlyActiveOrStartingSessionRemainsRecording(
        int status,
        bool hasSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            UnexpectedFailureStatePolicy.RecordingMayStillBeActive(
                (AppStatus)status,
                hasSession));
    }
}

public sealed class FocusRestorationPolicyTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_1", false, true)]
    [InlineData("chrome_widgetwin_1", false, true)]
    [InlineData("Notepad", true, true)]
    [InlineData("Notepad", false, false)]
    public void ChromiumAndWebViewTargetsAvoidElementFocus(
        string windowClass,
        bool elementIsWebViewRoot,
        bool expected)
    {
        Assert.Equal(
            expected,
            FocusRestorationPolicy.ShouldAvoidAutomationElementFocus(
                windowClass,
                elementIsWebViewRoot));
    }
}

public sealed class FocusTargetSafetyPolicyTests
{
    private static readonly IntPtr Window = (IntPtr)0x1234;
    private const uint ProcessId = 42;
    private const string WindowTitle = "ChatGPT";
    private const string WebViewRoot = "2A:00001234:00000001";
    private static readonly SafeFocusLayoutFingerprint CapturedEditorLayout =
        new(0.10, 0.70, 0.80, 0.12);
    private static readonly SafeFocusLayoutFingerprint NearbyEditorLayout =
        new(0.11, 0.705, 0.79, 0.115);

    [Fact]
    public void LocalReprobePromotesTransientChromiumMainToStrongEditor()
    {
        Assert.True(FocusTargetSafetyPolicy.ShouldPromoteTransientChromiumTarget(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata("ControlType.Group", "not-keyboard-focused:outline-none", "main"),
            WebViewRoot,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata("ControlType.Edit", "ProseMirror ProseMirror-focused", "prompt-textarea"),
            WebViewRoot,
            candidateIsPassword: false));
    }

    [Theory]
    [InlineData(true, 42U, "ControlType.Edit", "ProseMirror ProseMirror-focused", "prompt-textarea")]
    [InlineData(false, 99U, "ControlType.Edit", "ProseMirror ProseMirror-focused", "prompt-textarea")]
    [InlineData(false, 42U, "ControlType.Group", "text-message keyboard-focused", "message")]
    [InlineData(false, 42U, "ControlType.Edit", "ProseMirror ProseMirror-focused", "")]
    public void LocalReprobeRejectsPasswordProcessMismatchAndWeakTargets(
        bool candidateIsPassword,
        uint candidateProcessId,
        string controlType,
        string className,
        string automationId)
    {
        Assert.False(FocusTargetSafetyPolicy.ShouldPromoteTransientChromiumTarget(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata("ControlType.Group", "page-main", "main"),
            WebViewRoot,
            Window,
            candidateProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata(controlType, className, automationId),
            WebViewRoot,
            candidateIsPassword));
    }

    [Fact]
    public void RuntimeIdChangeAcceptsOnlySameStrongEditorFingerprint()
    {
        var captured = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");
        var rebuilt = Metadata(
            "ControlType.Group",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.True(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            captured,
            WebViewRoot,
            CapturedEditorLayout,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            rebuilt,
            WebViewRoot,
            NearbyEditorLayout,
            candidateIsPassword: false));
    }

    [Fact]
    public void FinalPasteSemanticReplacementRejectsChangedChromiumViewTitle()
    {
        var metadata = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.False(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            CapturedEditorLayout,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            "Other conversation",
            metadata,
            WebViewRoot,
            NearbyEditorLayout,
            candidateIsPassword: false));
    }

    [Theory]
    [InlineData(42U, "prompt-textarea", "text-message", "ControlType.Group", false)]
    [InlineData(42U, "other-editor", "ProseMirror", "ControlType.Edit", false)]
    [InlineData(42U, "prompt-textarea", "ProseMirror", "ControlType.Edit", true)]
    [InlineData(99U, "prompt-textarea", "ProseMirror", "ControlType.Edit", false)]
    public void RuntimeIdChangeRejectsWeakDifferentPasswordOrReusedProcessTargets(
        uint candidateProcessId,
        string candidateAutomationId,
        string candidateClass,
        string candidateControlType,
        bool candidateIsPassword)
    {
        Assert.False(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata("ControlType.Edit", "ProseMirror ProseMirror-focused", "prompt-textarea"),
            WebViewRoot,
            CapturedEditorLayout,
            Window,
            candidateProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata(candidateControlType, candidateClass, candidateAutomationId),
            WebViewRoot,
            NearbyEditorLayout,
            candidateIsPassword));
    }

    [Fact]
    public void SameEditorFingerprintInDifferentWebViewRootIsRejected()
    {
        var metadata = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.False(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            CapturedEditorLayout,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            "2A:00005678:00000001",
            NearbyEditorLayout,
            candidateIsPassword: false));
    }

    [Theory]
    [InlineData("", WebViewRoot)]
    [InlineData(WebViewRoot, "")]
    public void EmptyWebViewRootIdentityNeverAuthorizesSemanticReplacement(
        string originalWebViewRoot,
        string candidateWebViewRoot)
    {
        var metadata = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.False(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            originalWebViewRoot,
            CapturedEditorLayout,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            candidateWebViewRoot,
            NearbyEditorLayout,
            candidateIsPassword: false));
    }

    [Fact]
    public void SameEditorFingerprintAtVeryDifferentLayoutIsRejected()
    {
        var metadata = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.False(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            CapturedEditorLayout,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            new SafeFocusLayoutFingerprint(0.10, 0.20, 0.80, 0.12),
            candidateIsPassword: false));
    }

    [Fact]
    public void NearbyButNonOverlappingEditorLayoutsAreRejected()
    {
        var metadata = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.False(FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            new SafeFocusLayoutFingerprint(0.40, 0.70, 0.03, 0.10),
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            new SafeFocusLayoutFingerprint(0.44, 0.70, 0.03, 0.10),
            candidateIsPassword: false));
    }

    [Fact]
    public void EmptyOrInvalidLayoutNeverAuthorizesSemanticReplacement()
    {
        var metadata = Metadata(
            "ControlType.Edit",
            "ProseMirror ProseMirror-focused",
            "prompt-textarea");

        Assert.False(CanAcceptWithLayouts(metadata, SafeFocusLayoutFingerprint.Empty, NearbyEditorLayout));
        Assert.False(CanAcceptWithLayouts(metadata, CapturedEditorLayout, SafeFocusLayoutFingerprint.Empty));
        Assert.False(CanAcceptWithLayouts(
            metadata,
            CapturedEditorLayout,
            new SafeFocusLayoutFingerprint(0.10, 0.70, 0, 0.12)));
    }

    [Fact]
    public void LocalReprobeCannotPromoteEditorFromAnotherWebViewRoot()
    {
        Assert.False(FocusTargetSafetyPolicy.ShouldPromoteTransientChromiumTarget(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata("ControlType.Group", "page-main", "main"),
            WebViewRoot,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            Metadata("ControlType.Edit", "ProseMirror", "prompt-textarea"),
            "2A:00005678:00000001",
            candidateIsPassword: false));
    }

    [Theory]
    [InlineData("ChatGPT", "ChatGPT", true)]
    [InlineData("ChatGPT", "Other conversation", false)]
    [InlineData("ChatGPT", "chatgpt", false)]
    [InlineData("", "ChatGPT", false)]
    [InlineData("ChatGPT", "", false)]
    [InlineData(" ", " ", false)]
    public void ChromiumViewIdentityRequiresSameNonEmptyOrdinalWindowTitle(
        string originalTitle,
        string candidateTitle,
        bool expected)
    {
        Assert.Equal(
            expected,
            FocusTargetSafetyPolicy.HasSameViewIdentity(
                "Chrome_WidgetWin_1",
                originalTitle,
                "Chrome_WidgetWin_1",
                candidateTitle));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Document A", "Document B")]
    public void NonChromiumViewIdentityPreservesPreviousBehavior(
        string originalTitle,
        string candidateTitle)
    {
        Assert.True(FocusTargetSafetyPolicy.HasSameViewIdentity(
            "Notepad",
            originalTitle,
            "Notepad",
            candidateTitle));
    }

    [Theory]
    [InlineData("ChatGPT", "Other conversation")]
    [InlineData("", "ChatGPT")]
    [InlineData("ChatGPT", "")]
    public void LocalReprobeCannotPromoteAcrossChromiumViewIdentity(
        string originalTitle,
        string candidateTitle)
    {
        Assert.False(FocusTargetSafetyPolicy.ShouldPromoteTransientChromiumTarget(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            originalTitle,
            Metadata("ControlType.Group", "page-main", "main"),
            WebViewRoot,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            candidateTitle,
            Metadata("ControlType.Edit", "ProseMirror", "prompt-textarea"),
            WebViewRoot,
            candidateIsPassword: false));
    }

    [Fact]
    public void WindowHandleReuseWithDifferentProcessIsNotTheSameTarget()
    {
        Assert.False(FocusTargetSafetyPolicy.HasSameWindowIdentity(
            Window,
            ProcessId,
            Window,
            ProcessId + 1));
    }

    private static SafeFocusMetadata Metadata(
        string controlType,
        string className,
        string automationId) =>
        new(controlType, className, automationId);

    private static bool CanAcceptWithLayouts(
        SafeFocusMetadata metadata,
        SafeFocusLayoutFingerprint originalLayout,
        SafeFocusLayoutFingerprint candidateLayout) =>
        FocusTargetSafetyPolicy.CanAcceptSemanticEditorReplacement(
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            originalLayout,
            Window,
            ProcessId,
            "Chrome_WidgetWin_1",
            WindowTitle,
            metadata,
            WebViewRoot,
            candidateLayout,
            candidateIsPassword: false);
}
