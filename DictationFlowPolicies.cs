namespace ChatGptDictationBridge;

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
