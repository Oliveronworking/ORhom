using System.Collections.Specialized;
using System.Text;

namespace ORhom.Tests;

public sealed class AppLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ORhom.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoggingIoFailuresNeverEscapeIntoApplicationCode()
    {
        Directory.CreateDirectory(_directory);
        var pathThatIsAFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(pathThatIsAFile, "occupied");

        var exception = Record.Exception(() =>
        {
            using var logger = new AppLogger(pathThatIsAFile);
            logger.Info("This write cannot succeed.");
            logger.Error("Neither can this one.", new IOException("expected"));
        });

        Assert.Null(exception);
    }

    [Fact]
    public void ConstructorFailureDisablesFutureLoggingAttempts()
    {
        Directory.CreateDirectory(_directory);
        var pathThatIsAFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(pathThatIsAFile, "occupied");
        using var logger = new AppLogger(pathThatIsAFile);

        File.Delete(pathThatIsAFile);
        Directory.CreateDirectory(pathThatIsAFile);

        logger.Info("The path is available now, but this logger stays disabled.");
        logger.Error("A disabled logger does not retry I/O.", new IOException("expected"));

        Assert.False(File.Exists(logger.LogPath));
    }

    [Fact]
    public void FullLogIsRotatedBeforeTheNextEntryIsWritten()
    {
        var logDirectory = Path.Combine(_directory, "logs");
        using var logger = new AppLogger(logDirectory);
        File.WriteAllBytes(logger.LogPath, new byte[AppLogger.MaximumLogFileBytes]);

        logger.Info("newest-entry");

        var rotatedPath = Path.Combine(logDirectory, AppLogger.RotatedLogFileName);
        Assert.True(File.Exists(rotatedPath));
        Assert.Equal(AppLogger.MaximumLogFileBytes, new FileInfo(rotatedPath).Length);
        Assert.Contains("newest-entry", ReadAllTextShared(logger.LogPath));
        Assert.True(new FileInfo(logger.LogPath).Length < AppLogger.MaximumLogFileBytes);
    }

    [Fact]
    public void EntryThatExactlyReachesMaximumSizeIsNotRotatedEarly()
    {
        const string exactFitMessage = "exact-fit-entry";
        var logDirectory = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "app.log");
        var entryBytes = Encoding.UTF8.GetByteCount(
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [INFO] {exactFitMessage}{Environment.NewLine}");
        var initialBytes = checked((int)(AppLogger.MaximumLogFileBytes - entryBytes));
        File.WriteAllBytes(logPath, new byte[initialBytes]);
        using var logger = new AppLogger(logDirectory);

        logger.Info(exactFitMessage);

        var rotatedPath = Path.Combine(logDirectory, AppLogger.RotatedLogFileName);
        Assert.False(File.Exists(rotatedPath));
        Assert.Equal(AppLogger.MaximumLogFileBytes, new FileInfo(logPath).Length);

        logger.Info("entry-after-exact-fit");

        Assert.Equal(AppLogger.MaximumLogFileBytes, new FileInfo(rotatedPath).Length);
        Assert.Contains("entry-after-exact-fit", ReadAllTextShared(logPath));
    }

    [Fact]
    public async Task ConcurrentWritesAreSerializedWithoutLosingEntries()
    {
        const int writerCount = 8;
        const int entriesPerWriter = 100;
        using var logger = new AppLogger(Path.Combine(_directory, "logs"));
        var expectedMessages = Enumerable
            .Range(0, writerCount)
            .SelectMany(
                writer => Enumerable.Range(0, entriesPerWriter),
                (writer, entry) => $"writer-{writer:D2}-entry-{entry:D3}")
            .ToHashSet(StringComparer.Ordinal);

        var writers = Enumerable.Range(0, writerCount)
            .Select(writer => Task.Run(() =>
            {
                for (var entry = 0; entry < entriesPerWriter; entry++)
                {
                    logger.Info($"writer-{writer:D2}-entry-{entry:D3}");
                }
            }));

        await Task.WhenAll(writers);

        var lines = ReadAllLinesShared(logger.LogPath);
        Assert.Equal(writerCount * entriesPerWriter, lines.Count);
        var actualMessages = lines
            .Select(line =>
            {
                var separator = line.IndexOf("] ", StringComparison.Ordinal);
                return separator >= 0 ? line[(separator + 2)..] : line;
            })
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedMessages.SetEquals(actualMessages));
    }

    [Fact]
    public void ExceptionsAreLoggedWithTheirStackTrace()
    {
        using var logger = new AppLogger(Path.Combine(_directory, "logs"));

        try
        {
            ThrowLoggedFailure();
        }
        catch (InvalidOperationException ex)
        {
            logger.Error("Expected test failure.", ex);
        }

        var log = ReadAllTextShared(logger.LogPath);
        Assert.Contains("InvalidOperationException", log);
        Assert.Contains(nameof(ThrowLoggedFailure), log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void ThrowLoggedFailure() =>
        throw new InvalidOperationException("stack-trace-test");

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}

public sealed class AppSettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ORhom.Tests",
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
    public void RecordingBarPlacementSurvivesSettingsRoundTrip()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var logger = CreateLogger();
        var settings = AppSettings.Load(settingsPath, logger);
        settings.RecordingOverlayMonitorDeviceName = @"\\.\DISPLAY2";
        settings.RecordingOverlayRelativeX = 0.375;
        settings.RecordingOverlayRelativeY = 0.8125;

        Assert.True(settings.Save(logger));

        var reloaded = AppSettings.Load(settingsPath, logger);
        Assert.Equal(@"\\.\DISPLAY2", reloaded.RecordingOverlayMonitorDeviceName);
        Assert.Equal(0.375, reloaded.RecordingOverlayRelativeX);
        Assert.Equal(0.8125, reloaded.RecordingOverlayRelativeY);
    }

    [Fact]
    public void ExistingSettingsWithoutRecordingBarSizeDefaultToSmall()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "toggleHotkey": "Ctrl+F10"
            }
            """);

        var settings = AppSettings.Load(settingsPath, CreateLogger());

        Assert.Equal(RecordingOverlaySize.Small, settings.RecordingOverlaySize);
        Assert.Equal("Ctrl+F10", settings.ToggleHotkey);
        Assert.True(settings.IsPersistenceAvailable);
    }

    [Theory]
    [InlineData(0)] // Small
    [InlineData(1)] // Medium
    [InlineData(2)] // Large
    public void RecordingBarSizeSurvivesSettingsRoundTrip(int sizeValue)
    {
        var size = (RecordingOverlaySize)sizeValue;
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var logger = CreateLogger();
        var settings = AppSettings.Load(settingsPath, logger);
        settings.RecordingOverlaySize = size;

        Assert.True(settings.Save(logger));

        var json = File.ReadAllText(settingsPath);
        var reloaded = AppSettings.Load(settingsPath, logger);
        Assert.Contains($"\"recordingOverlaySize\": \"{size}\"", json);
        Assert.Equal(size, reloaded.RecordingOverlaySize);
    }

    [Theory]
    [InlineData("\"Gigantic\"")]
    [InlineData("999")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidRecordingBarSizeFallsBackWithoutDiscardingOtherSettings(string jsonValue)
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            settingsPath,
            $$"""
            {
              "toggleHotkey": "Ctrl+F11",
              "recordingOverlaySize": {{jsonValue}}
            }
            """);

        var settings = AppSettings.Load(settingsPath, CreateLogger());

        Assert.Equal(RecordingOverlaySize.Small, settings.RecordingOverlaySize);
        Assert.Equal("Ctrl+F11", settings.ToggleHotkey);
        Assert.True(settings.IsPersistenceAvailable);
        Assert.Empty(Directory.GetFiles(
            _directory,
            "settings.unreadable-*.json",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void IncompleteRecordingBarPlacementIsDiscardedOnLoad()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "recordingOverlayMonitorDeviceName": "\\\\.\\DISPLAY2",
              "recordingOverlayRelativeX": 0.5,
              "recordingOverlayRelativeY": null
            }
            """);

        var settings = AppSettings.Load(settingsPath, CreateLogger());

        Assert.Equal(string.Empty, settings.RecordingOverlayMonitorDeviceName);
        Assert.Null(settings.RecordingOverlayRelativeX);
        Assert.Null(settings.RecordingOverlayRelativeY);
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
        "ORhom.Tests",
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
    public void QuarantinedHistoryRemainsClearableWhenNoEntriesCouldBeLoaded()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        File.WriteAllText(path, "{sensitiver-klartext");
        var store = new DictationHistoryStore(path, CreateLogger());

        Assert.Empty(store.GetEntries());
        Assert.True(store.CanClear());

        Assert.True(store.Clear());
        Assert.False(store.CanClear());
        Assert.Empty(Directory.GetFiles(
            _directory,
            "history.unreadable-*.json",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SanitizedSensitiveHistoryRemainsClearableWhenNoEntriesSurvive()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        var rejectedEntry = new DictationHistoryEntry(
            Guid.Empty,
            default,
            "Sensitiver, aber ungültiger Klartext",
            DictationHistoryOutcomes.Pasted);
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(new[] { rejectedEntry }));
        var store = new DictationHistoryStore(path, CreateLogger());

        Assert.Empty(store.GetEntries());
        Assert.True(store.CanClear());

        Assert.True(store.Clear());
        Assert.False(store.CanClear());
        Assert.DoesNotContain("Sensitiver", File.ReadAllText(path));
    }

    [Fact]
    public void ClearRemovesActiveTextAndAllTimestampedQuarantines()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "private-history.backup.json");
        var store = new DictationHistoryStore(path, CreateLogger());
        Assert.True(store.TryAdd(
            "Aktiver Klartext",
            DictationHistoryOutcomes.Pasted,
            out _));
        var firstQuarantine = Path.Combine(
            _directory,
            "private-history.backup.unreadable-20260101-010203-004.json");
        var secondQuarantine = Path.Combine(
            _directory,
            "private-history.backup.unreadable-20260202-020304-005.json");
        var unrelatedFile = Path.Combine(
            _directory,
            "private-history.backup.unreadable-not-a-timestamp.json");
        File.WriteAllText(firstQuarantine, "Quarantäne-Klartext 1");
        File.WriteAllText(secondQuarantine, "Quarantäne-Klartext 2");
        File.WriteAllText(unrelatedFile, "Nicht vom History-Store erzeugt");

        Assert.True(store.CanClear());
        var cleared = store.Clear();

        Assert.True(cleared);
        Assert.False(store.CanClear());
        Assert.Empty(store.GetEntries());
        Assert.Empty(new DictationHistoryStore(path, CreateLogger()).GetEntries());
        Assert.DoesNotContain("Aktiver Klartext", File.ReadAllText(path));
        Assert.False(File.Exists(firstQuarantine));
        Assert.False(File.Exists(secondQuarantine));
        Assert.True(File.Exists(unrelatedFile));
    }

    [Fact]
    public void ClearReportsLockedQuarantineAndCanBeRetried()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        var store = new DictationHistoryStore(path, CreateLogger());
        Assert.True(store.TryAdd(
            "Aktiver Klartext",
            DictationHistoryOutcomes.Pasted,
            out _));
        var quarantinePath = Path.Combine(
            _directory,
            "history.unreadable-20260303-030405-006.json");
        File.WriteAllText(quarantinePath, "Gesperrter Quarantäne-Klartext");

        using (new FileStream(
                   quarantinePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.False(store.Clear());
            Assert.Empty(store.GetEntries());
            Assert.True(store.CanClear());
            Assert.True(File.Exists(quarantinePath));
        }

        Assert.True(store.Clear());
        Assert.False(store.CanClear());
        Assert.False(File.Exists(quarantinePath));
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

    [Fact]
    public void LatestRecoveredAbortAuthorizesCleanupOnlyOnceAndPersistsConsumption()
    {
        var store = CreateStore();
        store.Add("Geretteter Text", DictationHistoryOutcomes.CancelledRecovered);

        Assert.True(store.CanConsumeRecentRecoverableText("Geretteter Text"));
        Assert.True(store.TryConsumeRecentRecoverableText("Geretteter Text"));
        Assert.False(store.CanConsumeRecentRecoverableText("Geretteter Text"));
        Assert.False(store.TryConsumeRecentRecoverableText("Geretteter Text"));

        var reloaded = CreateStore();
        Assert.Equal(
            DictationHistoryOutcomes.PendingCleanupConsumed,
            Assert.Single(reloaded.GetEntries()).Outcome);
        Assert.False(reloaded.CanConsumeRecentRecoverableText("Geretteter Text"));
    }

    [Fact]
    public void ExplicitPendingCleanupOutcomeCanAuthorizeOneCleanupAttempt()
    {
        var store = CreateStore();
        store.Add("Offener Entwurf", DictationHistoryOutcomes.PendingComposerCleanup);

        Assert.True(store.TryConsumeRecentRecoverableText("Offener Entwurf"));
        Assert.False(store.TryConsumeRecentRecoverableText("Offener Entwurf"));
    }

    [Theory]
    [InlineData(DictationHistoryOutcomes.Pasted)]
    [InlineData(DictationHistoryOutcomes.Transcribed)]
    [InlineData(DictationHistoryOutcomes.PreservedBeforeClose)]
    [InlineData(DictationHistoryOutcomes.FailureRecovered)]
    [InlineData(DictationHistoryOutcomes.CancelledRecoveredCleared)]
    [InlineData(DictationHistoryOutcomes.FailureRecoveredCleared)]
    [InlineData(DictationHistoryOutcomes.PendingCleanupConsumed)]
    public void CompletedOrUnrelatedOutcomeCannotAuthorizeCleanup(string outcome)
    {
        var store = CreateStore();
        store.Add("Gleicher Text", outcome);

        Assert.False(store.CanConsumeRecentRecoverableText("Gleicher Text"));
    }

    [Fact]
    public void OlderMatchingRecoveryCannotAuthorizeCleanupAfterNewerHistoryEntry()
    {
        var store = CreateStore();
        store.Add("Alter Text", DictationHistoryOutcomes.CancelledRecovered);
        store.Add("Neuer Text", DictationHistoryOutcomes.Pasted);

        Assert.False(store.CanConsumeRecentRecoverableText("Alter Text"));
    }

    [Fact]
    public void ExpiredRecoveryCannotAuthorizeCleanup()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        var expiredAt = DateTimeOffset.Now -
                        DictationHistoryStore.PendingCleanupRecoveryWindow -
                        TimeSpan.FromMinutes(1);
        File.WriteAllText(
            path,
            $$"""
            [
              {
                "id": "{{Guid.NewGuid()}}",
                "createdAt": "{{expiredAt:O}}",
                "text": "Alter geretteter Text",
                "outcome": "{{DictationHistoryOutcomes.CancelledRecovered}}"
              }
            ]
            """);

        var store = new DictationHistoryStore(path, CreateLogger());

        Assert.False(store.CanConsumeRecentRecoverableText("Alter geretteter Text"));
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
            "ORhom.Tests",
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
    [InlineData(0)] // NoWindow
    [InlineData(1)] // Empty
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
        Assert.True(withFallback.ShouldAttemptClipboardFallback);
        Assert.Equal(ClipboardRestoreOutcome.NotRequested, withFallback.ClipboardRestoreOutcome);

        Assert.False(withoutFallback.Succeeded);
        Assert.False(withoutFallback.AllowClipboardFallback);
        Assert.False(withoutFallback.ShouldAttemptClipboardFallback);
        Assert.Equal(ClipboardRestoreOutcome.NotRequested, withoutFallback.ClipboardRestoreOutcome);
    }

    [Theory]
    [InlineData(0)] // NotRequested
    [InlineData(1)] // Restored
    [InlineData(2)] // SkippedExternalChange
    [InlineData(3)] // Failed
    public void PasteSuccessAndClipboardRestoreOutcomeRemainIndependent(int restoreOutcomeValue)
    {
        var restoreOutcome = (ClipboardRestoreOutcome)restoreOutcomeValue;
        var result = new PasteResult(
            Succeeded: true,
            AllowClipboardFallback: false,
            ClipboardRestoreOutcome: restoreOutcome);

        Assert.True(result.Succeeded);
        Assert.False(result.AllowClipboardFallback);
        Assert.False(result.ShouldAttemptClipboardFallback);
        Assert.Equal(restoreOutcome, result.ClipboardRestoreOutcome);
    }

    [Fact]
    public void ExistingClipboardCopySuppressesAnotherFallbackWrite()
    {
        var result = new PasteResult(
            Succeeded: false,
            AllowClipboardFallback: true,
            ClipboardRestoreOutcome.NotRequested,
            TextIsOnClipboard: true);

        Assert.False(result.ShouldAttemptClipboardFallback);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnconfirmedPossiblePasteRequestsLastResortCopyOnlyWhenNeeded(
        bool textIsOnClipboard,
        bool expected)
    {
        var result = new PasteResult(
            Succeeded: false,
            AllowClipboardFallback: false,
            ClipboardRestoreOutcome.Restored,
            TextIsOnClipboard: textIsOnClipboard,
            PasteMayHaveReachedTarget: true);

        Assert.Equal(expected, result.ShouldCopyUnconfirmedTextAsLastResort);
    }

    [Fact]
    public void UnconfirmedPossiblePasteSuppressesClipboardFallback()
    {
        var result = new PasteResult(
            Succeeded: false,
            AllowClipboardFallback: true,
            ClipboardRestoreOutcome.Restored,
            PasteMayHaveReachedTarget: true);

        Assert.False(result.ShouldAttemptClipboardFallback);
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
        Assert.Equal("a.txt", pathsClone[0]);
        Assert.Equal("b.txt", pathsClone[1]);
        Assert.Equal("eins", collectionClone[0]);
    }
}
