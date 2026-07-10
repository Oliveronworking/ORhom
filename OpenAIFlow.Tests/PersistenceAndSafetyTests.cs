using System.Collections.Specialized;

namespace ChatGptDictationBridge.Tests;

public sealed class DictationHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenAIFlow.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void HistoryKeepsNewestTenEntries()
    {
        var store = CreateStore();
        for (var index = 0; index < 12; index++)
        {
            store.Add($"Text {index}", DictationHistoryOutcomes.Pasted);
        }

        var entries = store.GetEntries();

        Assert.Equal(DictationHistoryStore.MaximumEntries, entries.Count);
        Assert.Equal("Text 11", entries[0].Text);
        Assert.Equal("Text 2", entries[^1].Text);
    }

    [Fact]
    public void CorruptHistoryStartsEmpty()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        File.WriteAllText(path, "{not-json");

        var store = new DictationHistoryStore(path, CreateLogger());

        Assert.Empty(store.GetEntries());
    }

    [Fact]
    public void OutcomeUpdateIsPersisted()
    {
        var store = CreateStore();
        var id = store.Add("Wichtiger Text", DictationHistoryOutcomes.Transcribed);

        store.UpdateOutcome(id, DictationHistoryOutcomes.PasteFailed);
        var reloaded = CreateStore();

        Assert.Equal(DictationHistoryOutcomes.PasteFailed, Assert.Single(reloaded.GetEntries()).Outcome);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DictationHistoryStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new DictationHistoryStore(Path.Combine(_directory, "history.json"), CreateLogger());
    }

    private AppLogger CreateLogger()
    {
        return new AppLogger(Path.Combine(_directory, "logs"));
    }
}

public sealed class SafetyPolicyTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("Das ist https://example.com", false)]
    [InlineData("Normaler diktierter Text.", false)]
    [InlineData("", false)]
    public void UnsafeCapturedTextRejectsOnlyStandaloneWebUrls(string text, bool expected)
    {
        Assert.Equal(expected, AutomationHelpers.IsUnsafeCapturedText(text));
    }

    [Fact]
    public void StopResultFactoriesExposeRecoveryAndCleanupSemantics()
    {
        var success = ChatGptStopResult.Success();
        var defaultFailure = ChatGptStopResult.Fail(ChatGptFailure.StopFailed, "Fehler");
        var recoverableFailure = ChatGptStopResult.Fail(ChatGptFailure.StopFailed, "Fehler", canRecoverText: true);
        var deferredFailure = ChatGptStopResult.Fail(
            ChatGptFailure.StopFailed,
            "Fehler",
            canRecoverText: true,
            requiresDeferredCleanup: true);

        Assert.True(success.Ok);
        Assert.Equal(ChatGptFailure.None, success.Failure);
        Assert.True(success.CanRecoverText);
        Assert.False(success.RequiresDeferredCleanup);
        Assert.True(success.IsTerminationConfirmed);

        Assert.False(defaultFailure.Ok);
        Assert.False(defaultFailure.CanRecoverText);
        Assert.False(defaultFailure.RequiresDeferredCleanup);
        Assert.False(defaultFailure.IsTerminationConfirmed);

        Assert.True(recoverableFailure.CanRecoverText);
        Assert.False(recoverableFailure.RequiresDeferredCleanup);

        Assert.True(deferredFailure.CanRecoverText);
        Assert.True(deferredFailure.RequiresDeferredCleanup);
        Assert.False(deferredFailure.IsTerminationConfirmed);
    }

    [Fact]
    public void CleanupPoliciesKeepDeferredRecoveryDistinctFromImmediateRecovery()
    {
        Assert.True(RecordingFailureCleanupResult.Recoverable.CanRecoverText);
        Assert.False(RecordingFailureCleanupResult.Recoverable.RequiresDeferredCleanup);
        Assert.True(RecordingFailureCleanupResult.Recoverable.IsTerminationConfirmed);

        Assert.True(RecordingFailureCleanupResult.DeferredRecovery.CanRecoverText);
        Assert.True(RecordingFailureCleanupResult.DeferredRecovery.RequiresDeferredCleanup);
        Assert.False(RecordingFailureCleanupResult.DeferredRecovery.IsTerminationConfirmed);

        Assert.False(RecordingFailureCleanupResult.NotRecoverable.CanRecoverText);
        Assert.False(RecordingFailureCleanupResult.NotRecoverable.RequiresDeferredCleanup);
        Assert.True(RecordingFailureCleanupResult.NotRecoverable.IsTerminationConfirmed);

        Assert.False(RecordingFailureCleanupResult.Unconfirmed.CanRecoverText);
        Assert.False(RecordingFailureCleanupResult.Unconfirmed.RequiresDeferredCleanup);
        Assert.False(RecordingFailureCleanupResult.Unconfirmed.IsTerminationConfirmed);
    }
}

public sealed class PasteResultTests
{
    [Fact]
    public void FailurePresetsControlClipboardFallbackExplicitly()
    {
        var withFallback = PasteResult.FailedWithClipboardFallback;
        var withoutFallback = PasteResult.FailedWithoutClipboardFallback;

        Assert.False(withFallback.Succeeded);
        Assert.True(withFallback.AllowClipboardFallback);
        Assert.Equal(ClipboardRestoreOutcome.NotRequested, withFallback.ClipboardRestoreOutcome);

        Assert.False(withoutFallback.Succeeded);
        Assert.False(withoutFallback.AllowClipboardFallback);
        Assert.Equal(ClipboardRestoreOutcome.NotRequested, withoutFallback.ClipboardRestoreOutcome);
    }

    [Theory]
    [InlineData((int)ClipboardRestoreOutcome.NotRequested)]
    [InlineData((int)ClipboardRestoreOutcome.Restored)]
    [InlineData((int)ClipboardRestoreOutcome.SkippedExternalChange)]
    [InlineData((int)ClipboardRestoreOutcome.Failed)]
    public void PasteSuccessAndClipboardRestoreOutcomeRemainIndependent(int restoreOutcomeValue)
    {
        var restoreOutcome = (ClipboardRestoreOutcome)restoreOutcomeValue;
        var result = new PasteResult(
            Succeeded: true,
            AllowClipboardFallback: false,
            ClipboardRestoreOutcome: restoreOutcome);

        Assert.True(result.Succeeded);
        Assert.False(result.AllowClipboardFallback);
        Assert.Equal(restoreOutcome, result.ClipboardRestoreOutcome);
    }
}

public sealed class ClipboardSnapshotTests
{
    [Fact]
    public void MutableClipboardValuesAreCloned()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var paths = new[] { "a.txt", "b.txt" };
        var collection = new StringCollection { "eins", "zwei" };

        var bytesClone = Assert.IsType<byte[]>(ClipboardHelper.CloneClipboardValue(bytes));
        var pathsClone = Assert.IsType<string[]>(ClipboardHelper.CloneClipboardValue(paths));
        var collectionClone = Assert.IsType<StringCollection>(ClipboardHelper.CloneClipboardValue(collection));
        bytes[0] = 9;
        paths[0] = "changed";
        collection[0] = "changed";

        Assert.Equal(new byte[] { 1, 2, 3 }, bytesClone);
        Assert.Equal(new[] { "a.txt", "b.txt" }, pathsClone);
        Assert.Equal("eins", collectionClone[0]);
    }
}
