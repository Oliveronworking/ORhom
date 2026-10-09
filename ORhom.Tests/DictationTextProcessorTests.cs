namespace ORhom.Tests;

public sealed class DictationTextProcessorTests
{
    [Theory]
    [InlineData("ähm Hallo ORhom Komma Welt ORhom Punkt", "Hallo, Welt.")]
    [InlineData("Hello ORhom comma world ORhom period", "Hello, world.")]
    [InlineData("Hallo Sprachbefehl, Komma Welt Diktat Punkt", "Hallo, Welt.")]
    [InlineData("Hallo Sprachbefehl neue Absatz, Welt", "Hallo\n\nWelt")]
    [InlineData("Hallo Team, Sprachbefehl Komma, morgen testen wir die Aufnahme, Sprachbefehl neuer Absatz, viele Grüße, Sprachbefehl Punkt", "Hallo Team, morgen testen wir die Aufnahme\n\nviele Grüße.")]
    [InlineData("Hallo. Sprachbefehl Komma, Welt, Sprachbefehl Punkt", "Hallo, Welt.")]
    [InlineData("Hallo, Sprachbefehl neue Zeile, Welt", "Hallo\nWelt")]
    [InlineData("Hallo! Sprachbefehl neuer Absatz Welt", "Hallo!\n\nWelt")]
    [InlineData("Titel: neuer Absatz Text", "Titel:\n\nText")]
    [InlineData("Hello Diktat, comma world Sprachbefehl period", "Hello, world.")]
    [InlineData("Erster Satz ORhom Punkt ORhom neuer Absatz Zweiter Satz", "Erster Satz.\n\nZweiter Satz")]
    [InlineData("Erste Zeile neue Zeile zweite Zeile", "Erste Zeile\nzweite Zeile")]
    [InlineData("Hello ORhom new line world", "Hello\nworld")]
    [InlineData("ORhom Listenpunkt Milch ORhom Listenpunkt Brot", "- Milch\n- Brot")]
    [InlineData("ORhom Nummer eins Apfel ORhom Nummer zwei Birne", "1. Apfel\n2. Birne")]
    [InlineData("ORhom number one apple ORhom number ten pear", "1. apple\n10. pear")]
    [InlineData("ORhom Nummer 1 Apfel ORhom Nummer 10 Birne", "1. Apfel\n10. Birne")]
    [InlineData("Treffen um 3 Uhr, also wie geplant.", "Treffen um 3 Uhr, also wie geplant.")]
    [InlineData("Am Punkt treffen wir uns.", "Am Punkt treffen wir uns.")]
    [InlineData("The period is over, like you know.", "The period is over, like you know.")]
    [InlineData("Wortähm ähmWort ähm_kennung ähm", "Wortähm ähmWort ähm_kennung")]
    [InlineData("Dezimal 3,14 URL https://example.com?a=1 JSON {\"k\":true}.", "Dezimal 3,14 URL https://example.com?a=1 JSON {\"k\":true}.")]
    public void ExplicitFormattingIsDeterministicAndPreservesMeaningfulText(string input, string expected) =>
        Assert.Equal(expected, DictationTextProcessor.Process(input, new()));

    [Theory]
    [InlineData("Dienstag ORhom ersetze Dienstag durch Mittwoch ORhom Korrektur Ende", "Mittwoch")]
    [InlineData("Tuesday ORhom replace Tuesday with Wednesday ORhom end correction", "Wednesday")]
    [InlineData("Dienstag Sprachbefehl, ersetze Dienstag durch Mittwoch Sprachbefehl, Korrektur Ende", "Mittwoch")]
    [InlineData("Tuesday Diktat replace Tuesday with Wednesday Diktat, end correction", "Wednesday")]
    [InlineData("Dienstag und Dienstag ORhom ersetze Dienstag durch Mittwoch ORhom Korrektur Ende", "Dienstag und Mittwoch")]
    [InlineData("Guter falscher ORhom letztes Wort löschen Text.", "Guter Text.")]
    [InlineData("Good wrong ORhom delete last word text.", "Good text.")]
    [InlineData("Bleibt. Wird gelöscht. ORhom letzten Satz löschen Neu.", "Bleibt. Neu.")]
    [InlineData("Keep ORhom period Remove ORhom period ORhom delete last sentence New.", "Keep. New.")]
    [InlineData("Bleibt falsch ORhom Punkt ORhom letztes Wort löschen richtig.", "Bleibt richtig.")]
    [InlineData("Alt ORhom letztes Wort löschen Neu ORhom ersetze Alt durch Anders ORhom Korrektur Ende", "Neu ORhom ersetze Alt durch Anders ORhom Korrektur Ende")]
    [InlineData("Text ORhom ersetze fehlt durch neu ORhom Korrektur Ende", "Text ORhom ersetze fehlt durch neu ORhom Korrektur Ende")]
    [InlineData("Dienstag ORhom ersetze Dienstag durch Mittwoch", "Dienstag ORhom ersetze Dienstag durch Mittwoch")]
    [InlineData("Die Anweisung lautet: ersetze Dienstag durch Mittwoch.", "Die Anweisung lautet: ersetze Dienstag durch Mittwoch.")]
    public void CommandsOnlyModifyEarlierTextAndRunInOrder(string input, string expected) =>
        Assert.Equal(expected, DictationTextProcessor.Process(input, new()));

    [Fact]
    public void SnippetContentIsExactAndDoesNotTriggerOtherFeatures()
    {
        const string snippet = "  ähm ORhom Komma\nhttps://example.com?a=1\nMit  zwei Leerzeichen.  ";
        var preferences = new DictationPreferences
        {
            Snippets = [Entry("meine Signatur", snippet)],
            Dictionary = [Entry("Signatur", "Changed"), Entry("example", "Changed")]
        };
        Assert.Equal(snippet, DictationTextProcessor.Process("meine Signatur", preferences));
    }

    [Fact]
    public void SnippetCuesCanOverrideACommandAndAreNotReplacedAgain()
    {
        var preferences = new DictationPreferences
        {
            Snippets = [Entry("ORhom Komma", "Literal: ORhom Punkt ähm")],
            Dictionary = [Entry("ORhom", "ORHOM")]
        };
        Assert.Equal("Literal: ORhom Punkt ähm", DictationTextProcessor.Process("ORhom Komma", preferences));
    }

    [Fact]
    public void DictionaryKeepsItsSpellingAndLongestWordBoundaryMatch()
    {
        var preferences = new DictationPreferences
        {
            Dictionary = [Entry("ore home", "ORhom"), Entry("home", "HOME"), Entry("ähm", "Ähm™")]
        };
        Assert.Equal("ORhom HOME homework Ähm™", DictationTextProcessor.Process("ORE HOME home homework ähm", preferences));
        Assert.Equal("home\u0301", DictationTextProcessor.Process("home\u0301", preferences));
    }

    [Fact]
    public void DictionaryDoesNotBreakCommandPrefixOrTriggerAnotherReplacement()
    {
        var preferences = new DictationPreferences
        {
            Dictionary = [Entry("ORhom", "ORHOM"), Entry("ore home", "ORhom")]
        };
        Assert.Equal("ORHOM, ORhom", DictationTextProcessor.Process("ORhom ORhom Komma ore home", preferences));
    }

    [Theory]
    [InlineData("de", "ähm Treffen um 3 Uhr", "Treffen um 3 Uhr")]
    [InlineData("en", "um hello uh there erm UM", "hello there UM")]
    [InlineData("auto", "um ähm uh drei", "um drei")]
    [InlineData("de", "ÄHM ist ein Akronym", "ÄHM ist ein Akronym")]
    public void HesitationRemovalRespectsLanguageAndAbbreviations(string language, string input, string expected) =>
        Assert.Equal(expected, DictationTextProcessor.Process(input, new() { Language = language }));

    [Theory]
    [InlineData("{\"um\":  true, \"uh\":  1}")]
    [InlineData("const um =  42;\n  return um;")]
    [InlineData("`um  uh  erm` is code.")]
    [InlineData("https://example.com/um/uh me@um.example C:\\um\\uh\\file /um/uh/file")]
    public void StructuredTextIsPreservedWhenEnglishFillersAreEnabled(string input) =>
        Assert.Equal(input, DictationTextProcessor.Process(input, new() { Language = "en" }));

    [Fact]
    public void FeatureSwitchesAreIndependent()
    {
        var preferences = new DictationPreferences { SmartFormatting = false, RemoveFillers = false };
        Assert.Equal("ähm Hallo, Welt", DictationTextProcessor.Process("ähm Hallo ORhom Komma Welt", preferences));
        preferences.SpokenCommands = false;
        Assert.Equal("ähm Hallo ORhom Komma Welt", DictationTextProcessor.Process("ähm Hallo ORhom Komma Welt", preferences));
    }

    [Fact]
    public void PromptAddsPersonalVocabularyButDoesNotInsertExpandedSnippetContent()
    {
        var preferences = new DictationPreferences
        {
            Dictionary = [Entry("qbits", "Qubits")],
            Snippets = [Entry("meine private Signatur", "Dieser private Text gehört nicht in den Prompt.")]
        };
        var prompt = DictationTextProcessor.BuildWhisperPrompt(preferences);
        Assert.Contains("ORhom", prompt);
        Assert.Contains("Sprachbefehl Komma, Sprachbefehl neuer Absatz, Sprachbefehl Punkt.", prompt);
        Assert.Contains("Qubits", prompt);
        Assert.Contains("meine private Signatur", prompt);
        Assert.DoesNotContain("Dieser private Text", prompt);
        Assert.True(prompt.Length <= 800);
    }

    [Theory]
    [InlineData(null, "de")]
    [InlineData(" DE ", "de")]
    [InlineData("EN", "en")]
    [InlineData("auto", "auto")]
    [InlineData("fr", "fr")]
    [InlineData("not-a-language", "de")]
    public void InvalidLanguagesFallBackToGerman(string? language, string expected) =>
        Assert.Equal(expected, DictationLanguages.Normalize(language));

    [Fact]
    public void NormalizationCopiesEntriesWithoutTrimmingLiteralReplacement()
    {
        var preferences = new DictationPreferences
        {
            Dictionary = [Entry("  ore  home ", "  ORhom\n "), Entry("ORE HOME", "Duplicate")],
            Snippets = null!
        };
        var normalized = preferences.NormalizedCopy();
        Assert.Single(normalized.Dictionary);
        Assert.Equal("ore home", normalized.Dictionary[0].Cue);
        Assert.Equal("  ORhom\n ", normalized.Dictionary[0].Replacement);
        Assert.Empty(normalized.Snippets);
        Assert.Equal("  ore  home ", preferences.Dictionary[0].Cue);
    }

    private static DictationReplacement Entry(string cue, string replacement) => new() { Cue = cue, Replacement = replacement };
}
