using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatGptDictationBridge;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToggleHotkey { get; set; } = "F8";
    public bool RestoreClipboard { get; set; } = true;
    public string AudioTempFolder { get; set; } = "temp";
    public string TranscriptionProvider { get; set; } = "openai";
    public string TranscriptionModel { get; set; } = "gpt-4o-mini-transcribe";
    public string? Language { get; set; } = "de";
    public string? OpenAIApiKey { get; set; }
    public string OpenAIApiBaseUrl { get; set; } = "https://api.openai.com";
    public bool LogTranscribedText { get; set; } = false;
    public int PasteDelayMs { get; set; } = 100;
    public int RestoreClipboardDelayMs { get; set; } = 300;
    public bool BlockPasswordFields { get; set; } = true;

    [JsonIgnore]
    public string SettingsPath { get; private set; } = string.Empty;

    public static AppSettings Load(string path, AppLogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new AppSettings { SettingsPath = path };
                defaults.Save(logger);
                return defaults;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.SettingsPath = path;
            return settings;
        }
        catch (Exception ex)
        {
            logger.Error("Settings could not be loaded, using defaults.", ex);
            return new AppSettings { SettingsPath = path };
        }
    }

    public void Save(AppLogger logger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            logger.Error("Settings could not be saved.", ex);
        }
    }
}
