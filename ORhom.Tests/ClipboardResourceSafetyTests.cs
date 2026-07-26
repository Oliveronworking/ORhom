namespace ORhom.Tests;

public sealed class ClipboardResourceSafetyTests
{
    [Fact]
    public void RejectedUnstableSnapshotsAreDisposed()
    {
        uint sequence = 0;
        var candidates = new List<TrackingDisposable>();

        var captured = ClipboardHelper.TryCaptureStableCore(
            () => sequence++,
            () =>
            {
                var candidate = new TrackingDisposable();
                candidates.Add(candidate);
                return candidate;
            },
            _ => { },
            maximumAttempts: 3,
            out TrackingDisposable? snapshot,
            out _);

        Assert.False(captured);
        Assert.Null(snapshot);
        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, candidate => Assert.True(candidate.IsDisposed));
    }

    [Fact]
    public void SnapshotDisposesOwnedStreamsAfterClipboardPersistence()
    {
        var stream = new MemoryStream([1, 2, 3], writable: false);
        using (var snapshot = new DisposableDataObject())
        {
            snapshot.SetData("ORhom.Test.Stream", stream);
        }

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
