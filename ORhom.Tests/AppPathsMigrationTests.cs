namespace ORhom.Tests;

public sealed class AppPathsMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ORhom.AppPaths.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImmediateOpenAiFlowPredecessorIsMigratedWithAllUserData()
    {
        var legacyDirectory = Path.Combine(_directory, "OpenAIFlow");
        var legacyModel = Path.Combine(
            legacyDirectory,
            "models",
            "whisper.cpp",
            "ggml-large-v3-turbo.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModel)!);
        File.WriteAllText(Path.Combine(legacyDirectory, "settings.json"), "{}");
        File.WriteAllText(
            Path.Combine(legacyDirectory, "dictation-history.json"),
            "[]");
        File.WriteAllBytes(legacyModel, [1, 2, 3, 4]);

        var paths = AppPaths.Create(_directory);

        Assert.Equal(Path.Combine(_directory, "ORhom"), paths.DataDirectory);
        Assert.False(Directory.Exists(legacyDirectory));
        Assert.True(File.Exists(paths.SettingsPath));
        Assert.True(File.Exists(
            Path.Combine(paths.DataDirectory, "dictation-history.json")));
        Assert.Equal(
            [1, 2, 3, 4],
            File.ReadAllBytes(Path.Combine(
                paths.DataDirectory,
                "models",
                "whisper.cpp",
                "ggml-large-v3-turbo.bin")));
    }

    [Fact]
    public void ImmediatePredecessorTakesPriorityOverOlderProductData()
    {
        var immediatePredecessor = Path.Combine(_directory, "OpenAIFlow");
        var olderProduct = Path.Combine(_directory, "OliSpeechToText");
        Directory.CreateDirectory(immediatePredecessor);
        Directory.CreateDirectory(olderProduct);
        File.WriteAllText(
            Path.Combine(immediatePredecessor, "settings.json"),
            "immediate");
        File.WriteAllText(
            Path.Combine(olderProduct, "settings.json"),
            "older");

        var paths = AppPaths.Create(_directory);

        Assert.Equal(
            "immediate",
            File.ReadAllText(paths.SettingsPath));
        Assert.True(Directory.Exists(olderProduct));
    }

    [Fact]
    public void ExistingOrhomDataIsNeverOverwrittenByLegacyData()
    {
        var currentDirectory = Path.Combine(_directory, "ORhom");
        var legacyDirectory = Path.Combine(_directory, "OpenAIFlow");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(
            Path.Combine(currentDirectory, "settings.json"),
            "current");
        File.WriteAllText(
            Path.Combine(legacyDirectory, "settings.json"),
            "legacy");

        var paths = AppPaths.Create(_directory);

        Assert.Equal(
            "current",
            File.ReadAllText(paths.SettingsPath));
        Assert.True(Directory.Exists(legacyDirectory));
    }

    [Fact]
    public void ExistingOrhomRootReceivesOnlyMissingLegacyData()
    {
        var currentDirectory = Path.Combine(_directory, "ORhom");
        var legacyDirectory = Path.Combine(_directory, "OpenAIFlow");
        var currentSettings = Path.Combine(currentDirectory, "settings.json");
        var legacySettings = Path.Combine(legacyDirectory, "settings.json");
        var legacyModel = Path.Combine(
            legacyDirectory,
            "models",
            "whisper.cpp",
            "ggml-large-v3-turbo.bin");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModel)!);
        File.WriteAllText(currentSettings, "current");
        File.WriteAllText(legacySettings, "legacy");
        File.WriteAllBytes(legacyModel, [4, 3, 2, 1]);

        var paths = AppPaths.Create(_directory);

        Assert.Equal("current", File.ReadAllText(paths.SettingsPath));
        Assert.Equal("legacy", File.ReadAllText(legacySettings));
        Assert.Equal(
            [4, 3, 2, 1],
            File.ReadAllBytes(Path.Combine(
                paths.DataDirectory,
                "models",
                "whisper.cpp",
                "ggml-large-v3-turbo.bin")));
        Assert.False(File.Exists(legacyModel));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
