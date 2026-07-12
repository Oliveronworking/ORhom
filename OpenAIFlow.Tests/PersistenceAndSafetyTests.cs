using System.Collections.Specialized;
using System.Text;

namespace ChatGptDictationBridge.Tests;

public sealed class AppLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenAIFlow.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoggingIoFailuresNeverEscapeIntoApplicationCode()
    {
        Directory.CreateDirectory(_directory);
        var pathThatIsAFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(pathThatIsAFile, "occupied");

        var exception = Record.Exception(() =>
        {
            var logger = new AppLogger(pathThatIsAFile);
            logger.Info("This write cannot succeed.");
            logger.Error("Neither can this one.", new IOException("expected"));
        });

        Assert.Null(exception);
    }

    [Fact]
    public void FullLogIsRotatedBeforeTheNextEntryIsWritten()
    {
        var logDirectory = Path.Combine(_directory, "logs");
        var logger = new AppLogger(logDirectory);
        File.WriteAllBytes(logger.LogPath, new byte[AppLogger.MaximumLogFileBytes]);

        logger.Info("newest-entry");

        var rotatedPath = Path.Combine(logDirectory, AppLogger.RotatedLogFileName);
        Assert.True(File.Exists(rotatedPath));
        Assert.Equal(AppLogger.MaximumLogFileBytes, new FileInfo(rotatedPath).Length);
        Assert.Contains("newest-entry", File.ReadAllText(logger.LogPath, Encoding.UTF8));
        Assert.True(new FileInfo(logger.LogPath).Length < AppLogger.MaximumLogFileBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class AppSettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenAIFlow.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAtomicallyReplacesExistingSettingsWithoutLeavingTempFiles()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var logger = CreateLogger();
        var settings = AppSettings.Load(settingsPath, logger);
        settings.ToggleHotkey = "Ctrl+F9";
        settings.ChromeProfileDirectory = "Profile 7";

        Assert.True(settings.Save(logger));

        var reloaded = AppSettings.Load(settingsPath, logger);
        Assert.Equal("Ctrl+F9", reloaded.ToggleHotkey);
        Assert.Equal("Profile 7", reloaded.ChromeProfileDirectory);
        Assert.Empty(FindTemporarySettingsFiles());
    }

    [Fact]
    public void FailedAtomicReplacePreservesOriginalAndRemovesTempFile()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var logger = CreateLogger();
        var settings = AppSettings.Load(settingsPath, logger);
        var originalJson = File.ReadAllText(settingsPath);
        settings.ToggleHotkey = "Ctrl+F10";

        using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(settings.Save(logger));
        }

        Assert.Equal(originalJson, File.ReadAllText(settingsPath));
        Assert.Empty(FindTemporarySettingsFiles());
    }

    [Fact]
    public void CorruptSettingsAreQuarantinedBeforeDefaultsAreCreated()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, "{not-json");
        var logger = CreateLogger();

        var settings = AppSettings.Load(settingsPath, logger);

        Assert.True(settings.IsPersistenceAvailable);
        Assert.True(File.Exists(settingsPath));
        var quarantinedPath = Assert.Single(Directory.GetFiles(
            _directory,
            "settings.unreadable-*.json",
            SearchOption.TopDirectoryOnly));
        Assert.Equal("{not-json", File.ReadAllText(quarantinedPath));
        Assert.Equal("F8", settings.ToggleHotkey);
    }

    [Fact]
    public void TransientSettingsReadFailureCannotOverwriteExistingConfiguration()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var logger = CreateLogger();
        var initial = AppSettings.Load(settingsPath, logger);
        initial.ToggleHotkey = "Ctrl+F11";
        Assert.True(initial.Save(logger));
        var originalJson = File.ReadAllText(settingsPath);

        AppSettings blocked;
        using (new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            blocked = AppSettings.Load(settingsPath, logger);
            Assert.False(blocked.IsPersistenceAvailable);
        }

        blocked.ToggleHotkey = "Ctrl+F12";
        Assert.False(blocked.Save(logger));
        Assert.Equal(originalJson, File.ReadAllText(settingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AppLogger CreateLogger() => new(Path.Combine(_directory, "logs"));

    private string[] FindTemporarySettingsFiles() =>
        Directory.GetFiles(_directory, ".settings.json.*.tmp", SearchOption.TopDirectoryOnly);
}

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
    public void CorruptHistoryIsQuarantinedBeforeNewEntriesAreAllowed()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        File.WriteAllText(path, "{not-json");

        var store = new DictationHistoryStore(path, CreateLogger());

        Assert.Empty(store.GetEntries());
        var quarantinedPath = Assert.Single(Directory.GetFiles(
            _directory,
            "history.unreadable-*.json",
            SearchOption.TopDirectoryOnly));
        Assert.Equal("{not-json", File.ReadAllText(quarantinedPath));
        Assert.True(store.TryAdd(
            "Neuer Text",
            DictationHistoryOutcomes.PreservedBeforeClose,
            out _));
        Assert.True(File.Exists(path));
        Assert.Equal("{not-json", File.ReadAllText(quarantinedPath));
    }

    [Fact]
    public void TransientReadFailureCannotOverwriteExistingHistoryAndRecoversLater()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        var initialStore = new DictationHistoryStore(path, CreateLogger());
        Assert.True(initialStore.TryAdd(
            "Vorhandener Text",
            DictationHistoryOutcomes.Pasted,
            out _));
        var originalJson = File.ReadAllText(path);

        DictationHistoryStore blockedStore;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            blockedStore = new DictationHistoryStore(path, CreateLogger());
            Assert.Empty(blockedStore.GetEntries());
            Assert.False(blockedStore.TryAdd(
                "Darf nicht überschreiben",
                DictationHistoryOutcomes.PreservedBeforeClose,
                out _));
        }

        Assert.Equal(originalJson, File.ReadAllText(path));
        Assert.True(blockedStore.TryAdd(
            "Nach Freigabe",
            DictationHistoryOutcomes.PreservedBeforeClose,
            out _));
        var reloaded = new DictationHistoryStore(path, CreateLogger()).GetEntries();
        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded, entry => entry.Text == "Vorhandener Text");
        Assert.Contains(reloaded, entry => entry.Text == "Nach Freigabe");
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

    [Fact]
    public void FailedHistoryWriteIsReportedAndDoesNotPretendTheEntryExists()
    {
        Directory.CreateDirectory(_directory);
        var pathThatIsAFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(pathThatIsAFile, "occupied");
        var store = new DictationHistoryStore(
            Path.Combine(pathThatIsAFile, "history.json"),
            CreateLogger());

        var saved = store.TryAdd(
            "Nicht verlierbarer Text",
            DictationHistoryOutcomes.CancelledRecovered,
            out var id);

        Assert.False(saved);
        Assert.Equal(Guid.Empty, id);
        Assert.Empty(store.GetEntries());
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
    [Fact]
    public async Task LongRunningComposerReadHonorsCancellation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "OpenAIFlow.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var settings = new AppSettings { DictationResultTimeoutMs = 300_000 };
            var logger = new AppLogger(Path.Combine(directory, "logs"));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AutomationHelpers.ReadChatGptTextRobustlyAsync(
                    IntPtr.Zero,
                    settings,
                    logger,
                    cancellationToken: cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

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
        var dispatchedUnconfirmed = ChatGptStopResult.DispatchedUnconfirmed();
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

        Assert.True(dispatchedUnconfirmed.Ok);
        Assert.True(dispatchedUnconfirmed.CanRecoverText);
        Assert.False(dispatchedUnconfirmed.RequiresDeferredCleanup);
        Assert.False(dispatchedUnconfirmed.IsTerminationConfirmed);

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

public sealed class AbortRecoveryPersistenceTests
{
    [Fact]
    public async Task SavedRecoveryIsPersistedBeforeComposerIsCleared()
    {
        var operations = new List<string>();

        var result = await AbortRecoveryPersistence.SaveThenClearAsync(
            "Geretteter Text",
            text =>
            {
                operations.Add($"saved:{text}");
                return true;
            },
            () =>
            {
                operations.Add("cleared");
                return Task.FromResult(true);
            });

        Assert.Equal(
            ["saved:Geretteter Text", "cleared"],
            operations);
        Assert.True(result.Persisted);
        Assert.True(result.Cleared);
    }

    [Fact]
    public async Task RejectedPersistenceDoesNotClearComposer()
    {
        var clearCalled = false;

        var result = await AbortRecoveryPersistence.SaveThenClearAsync(
            "Geretteter Text",
            _ => false,
            () =>
            {
                clearCalled = true;
                return Task.FromResult(true);
            });

        Assert.False(result.Persisted);
        Assert.False(result.Cleared);
        Assert.False(clearCalled);
    }

    [Fact]
    public async Task FailedSynchronousPersistenceDoesNotClearComposer()
    {
        var clearCalled = false;

        await Assert.ThrowsAsync<IOException>(() =>
            AbortRecoveryPersistence.SaveThenClearAsync(
                "Geretteter Text",
                _ => throw new IOException("Speichern fehlgeschlagen"),
                () =>
                {
                    clearCalled = true;
                    return Task.FromResult(true);
                }));

        Assert.False(clearCalled);
    }
}

public sealed class PendingComposerPreservationTests
{
    [Theory]
    [InlineData((int)PendingComposerState.NoWindow)]
    [InlineData((int)PendingComposerState.Empty)]
    public void EmptyOrMissingComposerIsSafeWithoutPersistence(int stateValue)
    {
        var callbackCalled = false;
        var inspection = (PendingComposerState)stateValue == PendingComposerState.NoWindow
            ? PendingComposerInspection.NoWindow
            : PendingComposerInspection.Empty;

        var outcome = PendingComposerPreservation.Preserve(
            inspection,
            _ => callbackCalled = true,
            _ => callbackCalled = true);

        Assert.Equal(PendingComposerPreservationOutcome.SafeWithoutText, outcome);
        Assert.True(PendingComposerPreservation.IsSafeToClose(outcome));
        Assert.False(callbackCalled);
    }

    [Fact]
    public void UnavailableInspectionBlocksCloseWithoutPersistenceAttempt()
    {
        var callbackCalled = false;

        var outcome = PendingComposerPreservation.Preserve(
            PendingComposerInspection.Unavailable,
            _ => callbackCalled = true,
            _ => callbackCalled = true);

        Assert.Equal(PendingComposerPreservationOutcome.InspectionUnavailable, outcome);
        Assert.False(PendingComposerPreservation.IsSafeToClose(outcome));
        Assert.False(callbackCalled);
    }

    [Fact]
    public void ExistingHistoryCopyAllowsCloseWithoutDuplicateWrite()
    {
        var persistCalled = false;

        var outcome = PendingComposerPreservation.Preserve(
            PendingComposerInspection.WithText("Entwurf"),
            _ => true,
            _ => persistCalled = true);

        Assert.Equal(PendingComposerPreservationOutcome.AlreadyPersisted, outcome);
        Assert.True(PendingComposerPreservation.IsSafeToClose(outcome));
        Assert.False(persistCalled);
    }

    [Fact]
    public void NewlyPersistedComposerAllowsClose()
    {
        var persistedText = string.Empty;

        var outcome = PendingComposerPreservation.Preserve(
            PendingComposerInspection.WithText("Entwurf"),
            _ => false,
            text =>
            {
                persistedText = text;
                return true;
            });

        Assert.Equal(PendingComposerPreservationOutcome.PersistedNow, outcome);
        Assert.True(PendingComposerPreservation.IsSafeToClose(outcome));
        Assert.Equal("Entwurf", persistedText);
    }

    [Fact]
    public void FailedPersistenceBlocksClose()
    {
        var outcome = PendingComposerPreservation.Preserve(
            PendingComposerInspection.WithText("Entwurf"),
            _ => false,
            _ => false);

        Assert.Equal(PendingComposerPreservationOutcome.PersistenceFailed, outcome);
        Assert.False(PendingComposerPreservation.IsSafeToClose(outcome));
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
