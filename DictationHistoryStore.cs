using System.IO;
using System.Text.Json;

namespace ORhom;

internal sealed class DictationHistoryStore
{
    public const int MaximumEntries = 10;
    internal static readonly TimeSpan PendingCleanupRecoveryWindow = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly AppLogger _logger;
    private List<DictationHistoryEntry> _entries;
    private bool _persistenceAvailable;

    public DictationHistoryStore(string path, AppLogger logger)
    {
        _path = path;
        _logger = logger;
        _entries = Load(out _persistenceAvailable);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<DictationHistoryEntry> GetEntries()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public bool CanClear()
    {
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                return true;
            }

            try
            {
                if (File.Exists(_path) &&
                    new FileInfo(_path).Length > "[]".Length)
                {
                    return true;
                }

                return GetQuarantinedHistoryPaths().Length > 0;
            }
            catch
            {
                // Keep the action available when the disk state cannot be inspected.
                return true;
            }
        }
    }

    public Guid Add(string text, string outcome)
    {
        return TryAdd(text, outcome, out var id) ? id : Guid.Empty;
    }

    public bool ContainsText(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _entries.Any(entry =>
                string.Equals(entry.Text, text, StringComparison.Ordinal));
        }
    }

    public bool CanConsumeRecentRecoverableText(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var latest = _entries.FirstOrDefault();
            return IsConsumableRecovery(latest, text, DateTimeOffset.Now);
        }
    }

    public bool TryConsumeRecentRecoverableText(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var consumed = false;
        lock (_gate)
        {
            if (!EnsurePersistenceAvailableLocked())
            {
                return false;
            }

            var latest = _entries.FirstOrDefault();
            if (!IsConsumableRecovery(latest, text, DateTimeOffset.Now))
            {
                return false;
            }

            var previousEntry = latest!;
            _entries[0] = previousEntry with
            {
                Outcome = DictationHistoryOutcomes.PendingCleanupConsumed
            };
            consumed = SaveLocked();
            if (!consumed)
            {
                _entries[0] = previousEntry;
            }
        }

        if (consumed)
        {
            _logger.Info("One-time pending composer cleanup authorization consumed.");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return consumed;
    }

    public bool TryAdd(string text, string outcome, out Guid id)
    {
        var entry = new DictationHistoryEntry(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            text.Trim(),
            outcome);
        int entryCount;

        lock (_gate)
        {
            if (!EnsurePersistenceAvailableLocked())
            {
                id = Guid.Empty;
                return false;
            }

            var previousEntries = _entries.ToList();
            _entries.Insert(0, entry);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            }

            entryCount = _entries.Count;
            if (!SaveLocked())
            {
                _entries = previousEntries;
                id = Guid.Empty;
                return false;
            }
        }

        _logger.Info($"Dictation history entry saved. Outcome={outcome} TextLength={entry.Text.Length} EntryCount={entryCount}");
        Changed?.Invoke(this, EventArgs.Empty);
        id = entry.Id;
        return true;
    }

    public void UpdateOutcome(Guid id, string outcome)
    {
        var updated = false;
        lock (_gate)
        {
            if (!EnsurePersistenceAvailableLocked())
            {
                return;
            }

            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index >= 0)
            {
                var previousEntry = _entries[index];
                _entries[index] = _entries[index] with { Outcome = outcome };
                updated = SaveLocked();
                if (!updated)
                {
                    _entries[index] = previousEntry;
                }
            }
        }

        if (updated)
        {
            _logger.Info($"Dictation history outcome updated. Outcome={outcome}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Clear()
    {
        var activeHistoryCleared = false;
        var quarantineFilesCleared = false;
        lock (_gate)
        {
            if (EnsurePersistenceAvailableLocked())
            {
                var previousEntries = _entries.ToList();
                _entries.Clear();
                activeHistoryCleared = SaveLocked();
                if (!activeHistoryCleared)
                {
                    _entries = previousEntries;
                }
            }

            quarantineFilesCleared = TryDeleteQuarantinedHistory();
        }

        if (activeHistoryCleared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (activeHistoryCleared && quarantineFilesCleared)
        {
            _logger.Info("Dictation history and quarantined history files cleared.");
            return true;
        }

        _logger.Error("Dictation history could not be cleared completely.");
        return false;
    }

    private List<DictationHistoryEntry> Load(out bool persistenceAvailable)
    {
        try
        {
            if (!File.Exists(_path))
            {
                persistenceAvailable = true;
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<DictationHistoryEntry>>(
                              File.ReadAllText(_path),
                              JsonOptions) ?? [];
            var sanitized = entries
                .OfType<DictationHistoryEntry>()
                .Where(entry => entry.Id != Guid.Empty && entry.CreatedAt != default)
                .Select(entry => entry with
                {
                    Text = entry.Text?.Trim() ?? string.Empty,
                    Outcome = string.IsNullOrWhiteSpace(entry.Outcome)
                        ? DictationHistoryOutcomes.Transcribed
                        : entry.Outcome
                })
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(MaximumEntries)
                .ToList();
            _logger.Info($"Dictation history loaded. EntryCount={sanitized.Count}");
            persistenceAvailable = true;
            return sanitized;
        }
        catch (JsonException ex)
        {
            _logger.Error("Dictation history JSON could not be parsed.", ex);
            if (TryQuarantineUnreadableHistory())
            {
                persistenceAvailable = true;
                return [];
            }

            persistenceAvailable = false;
            return [];
        }
        catch (Exception ex)
        {
            _logger.Error("Dictation history could not be loaded; persistence remains disabled to protect the existing file.", ex);
            persistenceAvailable = false;
            return [];
        }
    }

    private bool EnsurePersistenceAvailableLocked()
    {
        if (_persistenceAvailable)
        {
            return true;
        }

        var reloadedEntries = Load(out var persistenceAvailable);
        if (!persistenceAvailable)
        {
            return false;
        }

        _entries = reloadedEntries;
        _persistenceAvailable = true;
        return true;
    }

    private static bool IsConsumableRecovery(
        DictationHistoryEntry? entry,
        string expectedText,
        DateTimeOffset now)
    {
        if (entry is null ||
            !string.Equals(entry.Text, expectedText, StringComparison.Ordinal) ||
            entry.Outcome is not (
                DictationHistoryOutcomes.CancelledRecovered or
                DictationHistoryOutcomes.PendingComposerCleanup))
        {
            return false;
        }

        var age = now - entry.CreatedAt;
        return age >= TimeSpan.FromMinutes(-5) &&
               age <= PendingCleanupRecoveryWindow;
    }

    private bool TryQuarantineUnreadableHistory()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory;
            var fileName = Path.GetFileNameWithoutExtension(_path);
            var extension = Path.GetExtension(_path);
            var quarantinePath = Path.Combine(
                directory,
                $"{fileName}.unreadable-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}{extension}");
            File.Move(_path, quarantinePath);
            _logger.Info($"Unreadable dictation history was preserved as '{quarantinePath}'.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Unreadable dictation history could not be quarantined; persistence remains disabled.", ex);
            return false;
        }
    }

    private bool TryDeleteQuarantinedHistory()
    {
        string[] quarantinePaths;
        try
        {
            quarantinePaths = GetQuarantinedHistoryPaths();
        }
        catch (Exception ex)
        {
            _logger.Error("Quarantined dictation history files could not be enumerated.", ex);
            return false;
        }

        var allDeleted = true;
        foreach (var quarantinePath in quarantinePaths)
        {
            try
            {
                File.Delete(quarantinePath);
            }
            catch (Exception ex)
            {
                allDeleted = false;
                _logger.Error(
                    $"Quarantined dictation history file '{quarantinePath}' could not be removed.",
                    ex);
            }
        }

        return allDeleted;
    }

    private string[] GetQuarantinedHistoryPaths()
    {
        var directory = Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory;
        return Directory
            .GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsQuarantinedHistoryPath)
            .ToArray();
    }

    private bool IsQuarantinedHistoryPath(string candidatePath)
    {
        var candidateName = Path.GetFileName(candidatePath);
        var prefix = $"{Path.GetFileNameWithoutExtension(_path)}.unreadable-";
        var extension = Path.GetExtension(_path);
        if (!candidateName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !candidateName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var timestampLength = candidateName.Length - prefix.Length - extension.Length;
        if (timestampLength != 19)
        {
            return false;
        }

        var timestamp = candidateName.AsSpan(prefix.Length, timestampLength);
        for (var index = 0; index < timestamp.Length; index++)
        {
            if (index is 8 or 15)
            {
                if (timestamp[index] != '-')
                {
                    return false;
                }
            }
            else if (!char.IsAsciiDigit(timestamp[index]))
            {
                return false;
            }
        }

        return true;
    }

    private bool SaveLocked()
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory);
            temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception ex)
        {
            _persistenceAvailable = false;
            _logger.Error("Dictation history could not be saved.", ex);
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    _logger.Error("Temporary dictation history file could not be removed.", ex);
                }
            }
        }
    }
}

internal sealed record DictationHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Text,
    string Outcome);

internal static class DictationHistoryOutcomes
{
    public const string Transcribed = "transcribed";
    public const string Pasted = "pasted";
    public const string PasteFailed = "paste-failed";
    public const string CancelledRecovered = "cancelled-recovered";
    public const string CancelledWithoutText = "cancelled-without-text";
    public const string FailureRecovered = "failure-recovered";
    public const string StopFailed = "stop-failed";
    public const string TranscriptionFailed = "transcription-failed";
    public const string PreservedBeforeClose = "preserved-before-close";
    public const string CancelledRecoveredCleared = "cancelled-recovered-cleared";
    public const string FailureRecoveredCleared = "failure-recovered-cleared";
    public const string PendingComposerCleanup = "pending-composer-cleanup";
    public const string PendingCleanupConsumed = "pending-cleanup-consumed";

    public static string GetDisplayText(string outcome) => outcome switch
    {
        Pasted => "Eingefügt",
        PasteFailed => "Einfügen fehlgeschlagen",
        CancelledRecovered => "Abgebrochen · Text gerettet",
        CancelledWithoutText => "Abgebrochen · Kein Text erkannt",
        FailureRecovered => "Fehler · Text gerettet",
        StopFailed => "Stoppen fehlgeschlagen",
        TranscriptionFailed => "Kein Text erkannt",
        PreservedBeforeClose => "Vor dem Schließen gesichert",
        CancelledRecoveredCleared => "Abgebrochen · Text gerettet",
        FailureRecoveredCleared => "Fehler · Text gerettet",
        PendingComposerCleanup => "Text gerettet · Entwurf offen",
        PendingCleanupConsumed => "Text gerettet · Bereinigung versucht",
        Transcribed => "Transkribiert",
        _ => "Gespeichert"
    };
}
