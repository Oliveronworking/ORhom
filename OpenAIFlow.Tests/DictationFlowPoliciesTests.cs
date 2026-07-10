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
