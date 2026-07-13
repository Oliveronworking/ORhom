namespace ChatGptDictationBridge.Tests;

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
    [InlineData((int)AppStatus.Starting, true, true)]
    [InlineData((int)AppStatus.Recording, true, true)]
    [InlineData((int)AppStatus.Stopping, true, true)]
    [InlineData((int)AppStatus.ReadingText, true, true)]
    [InlineData((int)AppStatus.Pasting, true, false)]
    [InlineData((int)AppStatus.Idle, true, false)]
    [InlineData((int)AppStatus.Recording, false, false)]
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
