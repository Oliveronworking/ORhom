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

    [Fact]
    public void ForeignSeekableStreamIsClonedWithoutTakingOwnership()
    {
        using var source = new BufferedStream(new MemoryStream([1, 2, 3]));
        source.Position = 1;

        var clone = Assert.IsType<MemoryStream>(
            ClipboardHelper.CloneClipboardValue(source));
        using (var snapshot = new DisposableDataObject())
        {
            snapshot.SetData("ORhom.Test.ForeignStream", clone);
        }

        Assert.Equal(1, source.Position);
        Assert.Equal(2, source.ReadByte());
        Assert.Equal([1, 2, 3], clone.ToArray());
    }

    [Fact]
    public void ForeignDisposableValueIsRejectedWithoutDisposal()
    {
        var value = new TrackingDisposable();

        Assert.Throws<NotSupportedException>(
            () => ClipboardHelper.CloneClipboardValue(value));

        Assert.False(value.IsDisposed);
    }

    [Fact]
    public void AppOwnedTextIncludesWindowsClipboardPrivacyFormats()
    {
        using var dataObject =
            ClipboardHelper.CreatePrivacyProtectedTextDataObject("vertrauliches Diktat");

        Assert.Equal(
            "vertrauliches Diktat",
            dataObject.GetData(DataFormats.UnicodeText, autoConvert: false));
        AssertDisabledDword(
            dataObject,
            ClipboardHelper.ExcludeClipboardContentFromMonitorProcessingFormat);
        AssertDisabledDword(
            dataObject,
            ClipboardHelper.CanIncludeInClipboardHistoryFormat);
        AssertDisabledDword(
            dataObject,
            ClipboardHelper.CanUploadToCloudClipboardFormat);
    }

    [Fact]
    public void ClipboardPrivacyPayloadsAreOwnedAndDisposedWithDataObject()
    {
        var dataObject = ClipboardHelper.CreatePrivacyProtectedTextDataObject("sensitiv");
        var streams = new[]
        {
            GetPrivacyStream(
                dataObject,
                ClipboardHelper.ExcludeClipboardContentFromMonitorProcessingFormat),
            GetPrivacyStream(
                dataObject,
                ClipboardHelper.CanIncludeInClipboardHistoryFormat),
            GetPrivacyStream(
                dataObject,
                ClipboardHelper.CanUploadToCloudClipboardFormat)
        };

        dataObject.Dispose();

        Assert.All(
            streams,
            stream => Assert.Throws<ObjectDisposedException>(() => stream.ReadByte()));
    }

    private static void AssertDisabledDword(DisposableDataObject dataObject, string format)
    {
        Assert.True(dataObject.GetDataPresent(format, autoConvert: false));
        Assert.Equal(new byte[sizeof(uint)], GetPrivacyStream(dataObject, format).ToArray());
    }

    private static MemoryStream GetPrivacyStream(
        DisposableDataObject dataObject,
        string format) =>
        Assert.IsType<MemoryStream>(dataObject.GetData(format, autoConvert: false));

    private sealed class TrackingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
