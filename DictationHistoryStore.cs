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

    public DictationHistoryStore(string path, AppLogger logger)
    {
        _path = path;
        _logger = logger;
        _entries = Load();
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
        var entry = new DictationHistoryEntry(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            text.Trim(),
            outcome);
        int entryCount;

        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            }

            entryCount = _entries.Count;
            SaveLocked();
        }

        _logger.Info($"Dictation history entry saved. Outcome={outcome} TextLength={entry.Text.Length} EntryCount={entryCount}");
        Changed?.Invoke(this, EventArgs.Empty);
        return entry.Id;
    }

    public void UpdateOutcome(Guid id, string outcome)
    {
        var updated = false;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index >= 0)
            {
                _entries[index] = _entries[index] with { Outcome = outcome };
                SaveLocked();
                updated = true;
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
        lock (_gate)
        {
            _entries.Clear();
            SaveLocked();
        }

        _logger.Info("Dictation history cleared.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<DictationHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<DictationHistoryEntry>>(
                              File.ReadAllText(_path),
                              JsonOptions) ?? [];
            var sanitized = entries
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
            return sanitized;
        }
        catch (Exception ex)
        {
            _logger.Error("Dictation history could not be loaded; starting with an empty history.", ex);
            return [];
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Dictation history could not be saved.", ex);
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

    public static string GetDisplayText(string outcome) => outcome switch
    {
        Pasted => "Eingefügt",
        PasteFailed => "Einfügen fehlgeschlagen",
        CancelledRecovered => "Abgebrochen · Text gerettet",
        CancelledWithoutText => "Abgebrochen · Kein Text erkannt",
        FailureRecovered => "Fehler · Text gerettet",
        StopFailed => "Stoppen fehlgeschlagen",
        TranscriptionFailed => "Kein Text erkannt",
        Transcribed => "Transkribiert",
        _ => "Gespeichert"
    };
}
