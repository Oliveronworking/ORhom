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
    public string ChatGptDictationHotkey { get; set; } = "Ctrl+Shift+D";
    public string ChatGptUrl { get; set; } = "https://chatgpt.com";
    public string[] ChatGptWindowTitleContains { get; set; } = ["ChatGPT", "chatgpt.com"];
    public bool LaunchChatGptIfMissing { get; set; } = true;
    public bool RestoreTargetAfterStart { get; set; } = true;
    public bool RestoreClipboard { get; set; } = true;
    public int BrowserChromeExclusionTopPx { get; set; } = 120;
    public int MaxChatGptInputHeightPx { get; set; } = 260;
    public double MaxChatGptInputWindowWidthRatio { get; set; } = 0.85;
    public int SettleDelayMs { get; set; } = 500;
    public int ReadTextTimeoutMs { get; set; } = 12000;
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
