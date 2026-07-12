using System.IO;
using System.Text.Json;

namespace ChatGptDictationBridge;

internal sealed class DictationHistoryStore
{
    public const int MaximumEntries = 10;

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

    public void Clear()
    {
        var cleared = false;
        lock (_gate)
        {
            if (!EnsurePersistenceAvailableLocked())
            {
                return;
            }

            var previousEntries = _entries.ToList();
            _entries.Clear();
            cleared = SaveLocked();
            if (!cleared)
            {
                _entries = previousEntries;
            }
        }

        if (cleared)
        {
            _logger.Info("Dictation history cleared.");
            Changed?.Invoke(this, EventArgs.Empty);
        }
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
        Transcribed => "Transkribiert",
        _ => "Gespeichert"
    };
}
