using System.Text;
using System.Text.RegularExpressions;

namespace ORhom;

// Deterministic local processing: no paraphrasing, network requests, or guessed corrections.
internal static class DictationTextProcessor
{
    private const string Start = @"(?<![\p{L}\p{M}\p{N}_])";
    private const string End = @"(?![\p{L}\p{M}\p{N}_])";
    private const string PrefixWord = @"(?:ORhom|OR hom|Sprachbefehl|Diktat)";
    private const string Prefix = Start + PrefixWord + @",?\s+";
    private const RegexOptions Rules = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Correction = @"(?:ersetze\s+(?<oldDe>.+?)\s+durch\s+(?<newDe>.+?)\s+" + PrefixWord + @",?\s+korrektur\s+ende|replace\s+(?<oldEn>.+?)\s+with\s+(?<newEn>.+?)\s+" + PrefixWord + @",?\s+end\s+correction)";
    private static readonly (string Cue, string Replacement)[] Punctuation =
    [
        ("neuer Absatz", "\n\n"), ("new paragraph", "\n\n"),
        ("neue Absatz", "\n\n"),
        ("neue Zeile", "\n"), ("new line", "\n"),
        ("Listenpunkt", "\n- "), ("bullet point", "\n- "),
        ("Fragezeichen", "?"), ("question mark", "?"),
        ("Ausrufezeichen", "!"), ("exclamation mark", "!"),
        ("Semikolon", ";"), ("semicolon", ";"),
        ("Doppelpunkt", ":"), ("colon", ":"),
        ("Komma", ","), ("comma", ","), ("Punkt", "."), ("full stop", "."), ("period", ".")
    ];
    private static readonly string[] GermanNumbers = ["eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn"];
    private static readonly string[] EnglishNumbers = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
    private static readonly Regex Commands = new(
        Prefix + @"(?:(?<correction>" + Correction + @")|(?<deleteWord>letztes\s+wort\s+löschen|delete\s+last\s+word)|(?<deleteSentence>letzten\s+satz\s+löschen|delete\s+last\s+sentence)|(?<punctuation>" +
        string.Join("|", Punctuation.Select(item => EscapeCue(item.Cue))) +
        @")|nummer\s+(?<numberDe>" + string.Join("|", GermanNumbers) +
        @")|number\s+(?<numberEn>" + string.Join("|", EnglishNumbers) +
        @")|(?:nummer|number)\s+(?<numberDigit>10|[1-9]))" + End + @"[.,!?]?|" +
        Start + @"(?:(?<paragraph>neuer\s+absatz|new\s+paragraph)|(?<line>neue\s+zeile|new\s+line))" + End + @"[.,!?]?", Rules);

    internal static string Process(string? transcript, DictationPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return string.Empty;
        var options = preferences.NormalizedCopy();
        var literals = new List<string>();
        var marker = "\uE000" + Guid.NewGuid().ToString("N") + "_";
        var text = ProtectReplacements(transcript.Trim(), options.Snippets, literals, marker);
        if (options.SpokenCommands) text = ApplyCommands(text);
        text = ProtectReplacements(text, options.Dictionary, literals, marker);
        var code = LooksLikeCode(text);
        if (!code && (options.RemoveFillers || options.SmartFormatting))
            text = ProtectStructuredTokens(text, literals, marker);
        if (options.RemoveFillers && !code)
        {
            // In German "um" is a preposition. In auto mode its meaning is ambiguous.
            // Preserve uppercase abbreviations in every language.
            var fillers = options.Language == "en" ? "uhm|umm|um|uh|erm" : "ähm|äh|öh";
            if (options.Language == "auto") fillers += "|uhm|umm|uh|erm";
            text = Regex.Replace(text, Start + "(?:" + fillers + ")" + End + @"[,;]?",
                match => match.Value.TrimEnd(',', ';') == match.Value.TrimEnd(',', ';').ToUpperInvariant() ? match.Value : string.Empty, Rules);
        }
        if (!code)
        {
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @" *\r?\n *", "\n");
        }
        text = text.Trim();
        if (options.SmartFormatting && !code)
        {
            // Only remove stray spaces before punctuation. Do not inject spaces
            // inside URLs, numbers, JSON, source code or email addresses.
            text = Regex.Replace(text, @" +([,.;:!?])", "$1");
        }
        for (var index = 0; index < literals.Count; index++)
            text = text.Replace(marker + index + "\uE001", literals[index], StringComparison.Ordinal);
        return text;
    }

    internal static string BuildWhisperPrompt(DictationPreferences preferences)
    {
        var options = preferences.NormalizedCopy();
        // Whisper's prompt is vocabulary context, not an instruction prompt.
        var terms = options.Dictionary.SelectMany(entry => new[] { entry.Cue, entry.Replacement })
            .Concat(options.Snippets.Select(entry => entry.Cue))
            .Select(term => Regex.Replace(term, @"\s+", " ").Trim())
            .Where(term => term.Length is > 0 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var prompt = new StringBuilder(DictationVocabulary.InitialPrompt);
        if (options.SpokenCommands)
            prompt.Append(" Sprachbefehl Komma, Sprachbefehl neuer Absatz, Sprachbefehl Punkt.");
        foreach (var term in terms)
        {
            if (prompt.Length + term.Length + 2 > 800) break;
            prompt.Append(' ').Append(term).Append(';');
        }
        return prompt.ToString();
    }

    private static string ProtectReplacements(string text, DictationReplacement[] entries, List<string> literals, string marker)
    {
        if (entries.Length == 0) return text;
        var ordered = entries.OrderByDescending(entry => entry.Cue.Length).ToArray();
        var lookup = entries.ToDictionary(entry => entry.Cue, StringComparer.OrdinalIgnoreCase);
        return Regex.Replace(text, Start + "(?:" + string.Join("|", ordered.Select(entry => EscapeCue(entry.Cue))) + ")" + End, match =>
        {
            var cue = Regex.Replace(match.Value, @"\s+", " ");
            var token = marker + literals.Count + "\uE001";
            literals.Add(lookup[cue].Replacement);
            return token;
        }, Rules);
    }

    private static string EscapeCue(string cue) => string.Join(@"\s+", cue.Split(' ').Select(Regex.Escape));

    private static bool LooksLikeCode(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') || text.Contains('`') ||
            Regex.IsMatch(text, @"(?:^|\n)[ \t]*(?:let|var|const|func|class|import|def|return)[ \t]+|(?:=>|==|:=)|[\p{L}_][\p{L}\p{N}_]*[ \t]*=[ \t]*\S", Rules);
    }

    private static string ProtectStructuredTokens(string text, List<string> literals, string marker) =>
        Regex.Replace(text,
            @"(?:https?://|www\.)[^\s]+|[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}|(?:[A-Z]:\\|~/|/)[\p{L}\p{N}_.\\/\-]+|[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*|\[[0-9]+\])+",
            match =>
            {
                var token = marker + literals.Count + "\uE001";
                literals.Add(match.Value);
                return token;
            }, Rules);

    private static string ApplyCommands(string text)
    {
        var offset = 0;
        while (Commands.Match(text, offset) is { Success: true } command)
        {
            var before = text[..command.Index];
            var replacement = string.Empty;
            if (command.Groups["correction"].Success)
            {
                var oldText = (command.Groups["oldDe"].Success ? command.Groups["oldDe"] : command.Groups["oldEn"]).Value.Trim();
                var newText = (command.Groups["newDe"].Success ? command.Groups["newDe"] : command.Groups["newEn"]).Value.Trim();
                var targets = Regex.Matches(before, Start + EscapeCue(oldText) + End, Rules);
                if (targets.Count == 0)
                {
                    // Preserve unmatched corrections for review.
                    offset = command.Index + command.Length;
                    continue;
                }
                var target = targets[^1];
                before = before[..target.Index] + newText + before[(target.Index + target.Length)..];
            }
            else if (command.Groups["deleteWord"].Success)
            {
                before = Regex.Replace(before, @"[\p{L}\p{M}\p{N}]+(?:[’'\-][\p{L}\p{M}\p{N}]+)*[ \t]*[.,!?;:]*[ \t]*$", string.Empty);
            }
            else if (command.Groups["deleteSentence"].Success)
            {
                var trimmed = Regex.Replace(before.TrimEnd(), @"[.!?]+$", string.Empty);
                var boundaries = Regex.Matches(trimmed, @"[.!?]+(?=\s|$)|\n+");
                var end = boundaries.Count == 0 ? 0 : boundaries[^1].Index + boundaries[^1].Length;
                before = trimmed[..end] + (end > 0 ? " " : string.Empty);
            }
            else if (command.Groups["punctuation"].Success)
            {
                var cue = Regex.Replace(command.Groups["punctuation"].Value, @"\s+", " ");
                replacement = Punctuation.First(item => item.Cue.Equals(cue, StringComparison.OrdinalIgnoreCase)).Replacement;
                if (replacement.Length == 1 && replacement != "\n")
                    before = Regex.Replace(before.TrimEnd(), @"[.,;:!?]+$", string.Empty).TrimEnd();
            }
            else if (command.Groups["paragraph"].Success) replacement = "\n\n";
            else if (command.Groups["line"].Success) replacement = "\n";
            else
            {
                var number = command.Groups["numberDigit"].Success ? int.Parse(command.Groups["numberDigit"].Value) :
                    command.Groups["numberDe"].Success ? Array.FindIndex(GermanNumbers, value => value.Equals(command.Groups["numberDe"].Value, StringComparison.OrdinalIgnoreCase)) + 1 :
                    Array.FindIndex(EnglishNumbers, value => value.Equals(command.Groups["numberEn"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
                replacement = $"\n{number}. ";
            }
            if (replacement is "\n" or "\n\n")
                before = Regex.Replace(before.TrimEnd(), @",+$", string.Empty).TrimEnd();
            text = before + replacement + text[(command.Index + command.Length)..];
            offset = before.Length + replacement.Length;
        }
        return text;
    }
}
