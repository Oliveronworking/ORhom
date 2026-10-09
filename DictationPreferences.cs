using System.Text.RegularExpressions;

namespace ORhom;

internal sealed class DictationPreferences
{
    public string Language { get; set; } = "de";
    public bool SmartFormatting { get; set; } = true;
    public bool RemoveFillers { get; set; } = true;
    public bool SpokenCommands { get; set; } = true;
    public DictationReplacement[] Dictionary { get; set; } = [];
    public DictationReplacement[] Snippets { get; set; } = [];

    internal DictationPreferences NormalizedCopy() => new()
    {
        Language = DictationLanguages.Normalize(Language),
        SmartFormatting = SmartFormatting,
        RemoveFillers = RemoveFillers,
        SpokenCommands = SpokenCommands,
        Dictionary = NormalizeEntries(Dictionary),
        Snippets = NormalizeEntries(Snippets)
    };

    private static DictationReplacement[] NormalizeEntries(DictationReplacement[]? entries) =>
        (entries ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Cue) &&
                            !string.IsNullOrEmpty(entry.Replacement))
            .Select(entry => new DictationReplacement
            {
                Cue = Regex.Replace(entry.Cue.Trim(), @"\s+", " "),
                Replacement = entry.Replacement
            })
            .DistinctBy(entry => entry.Cue, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed class DictationReplacement
{
    public string Cue { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
}

internal static class DictationLanguages
{
    internal static readonly (string Code, string Label)[] Options =
    [
        ("de", "Deutsch"), ("en", "English"), ("auto", "Automatisch erkennen"),
        ("fr", "Français"), ("es", "Español"), ("it", "Italiano"),
        ("pt", "Português"), ("nl", "Nederlands"), ("pl", "Polski"),
        ("cs", "Čeština"), ("sk", "Slovenčina"), ("hu", "Magyar"),
        ("uk", "Українська"), ("ru", "Русский"), ("tr", "Türkçe"),
        ("ja", "日本語"), ("zh", "中文"), ("ko", "한국어"), ("ar", "العربية"),
        ("hi", "हिन्दी"), ("sv", "Svenska"), ("da", "Dansk"), ("fi", "Suomi"),
        ("no", "Norsk"), ("el", "Ελληνικά"), ("ro", "Română")
    ];

    internal static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return Options.Any(option => option.Code == normalized) ? normalized! : "de";
    }
}

internal static class DictationVocabulary
{
    internal const string InitialPrompt =
        "ORhom, ChatGPT, Codex, GitHub, Repository, PowerShell, .NET, JSON, " +
        "Vulkan, Flash Attention, Whisper Large V3 Turbo, Zwischenablage.";
}
