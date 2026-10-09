namespace ORhom.Tests;

public sealed class DictationPreferencesPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ORhom.Preferences.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OldSettingsKeepGermanAndEnableLocalTextFeatures()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"toggleHotkey\":\"F8\"}");
        var preferences = AppSettings.Load(path, Logger()).DictationPreferences;
        Assert.Equal("de", preferences.Language);
        Assert.True(preferences.SmartFormatting);
        Assert.True(preferences.SpokenCommands);
        Assert.True(preferences.RemoveFillers);
        Assert.Empty(preferences.Dictionary);
        Assert.Empty(preferences.Snippets);
    }

    [Fact]
    public void PreferencesAndLiteralMultilineSnippetsSurviveSaveAndReload()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var settings = AppSettings.Load(path, Logger());
        const string literal = "  Mit  Leerzeichen\nORhom Komma ähm\nBeste Grüße  ";
        settings.DictationPreferences = new()
        {
            Language = "en", SmartFormatting = false, RemoveFillers = false, SpokenCommands = false,
            Dictionary = [new() { Cue = "ore home", Replacement = "ORhom" }],
            Snippets = [new() { Cue = "my signature", Replacement = literal }]
        };
        Assert.True(settings.Save(Logger()));
        var loaded = AppSettings.Load(path, Logger()).DictationPreferences;
        Assert.Equal("en", loaded.Language);
        Assert.False(loaded.SmartFormatting);
        Assert.False(loaded.RemoveFillers);
        Assert.False(loaded.SpokenCommands);
        Assert.Equal("ORhom", Assert.Single(loaded.Dictionary).Replacement);
        Assert.Equal(literal, Assert.Single(loaded.Snippets).Replacement);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"language\":\"invalid\",\"dictionary\":null,\"snippets\":null}")]
    public void NullPreferencesAndCollectionsMigrateSafely(string preferencesJson)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"dictationPreferences\":" + preferencesJson + "}");
        var preferences = AppSettings.Load(path, Logger()).DictationPreferences;
        Assert.Equal("de", preferences.Language);
        Assert.Empty(preferences.Dictionary);
        Assert.Empty(preferences.Snippets);
    }

    private AppLogger Logger() => new(Path.Combine(_directory, "logs"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
