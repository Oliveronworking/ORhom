using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatGptDictationBridge;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToggleHotkey { get; set; } = "F8";
    public string ChatGptDictationHotkey { get; set; } = "Ctrl+Shift+D";
    public string ChatGptUrl { get; set; } = "https://chatgpt.com";
    public string[] ChatGptWindowTitleContains { get; set; } = ["ChatGPT", "chatgpt.com"];
    public string BrowserProfileMode { get; set; } = "ExistingChromeProfile";
    public string ChromeExecutablePath { get; set; } = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    public string ChromeUserDataDir { get; set; } = @"C:\Users\Admin\AppData\Local\Google\Chrome\User Data";
    public string ChromeProfileDirectory { get; set; } = "Profile 3";
    public bool RequireConfiguredChromeProfile { get; set; } = true;
    public bool AllowGuestProfile { get; set; } = false;
    public bool AllowIncognitoProfile { get; set; } = false;
    public bool AllowTemporaryProfile { get; set; } = false;
    public bool OpenChatGptProfileVisibleForSetup { get; set; } = true;
    public bool WarnIfConfiguredChromeProfileUnavailable { get; set; } = true;
    public bool LaunchChatGptIfMissing { get; set; } = true;
    public bool PrepareChatGptOnStartup { get; set; } = true;
    public bool MinimizeChatGptAfterStartup { get; set; } = true;
    public bool RestoreTargetAfterStart { get; set; } = true;
    public bool RestoreClipboard { get; set; } = true;
    public int BrowserChromeExclusionTopPx { get; set; } = 110;
    public int MaxChatGptInputHeightPx { get; set; } = 420;
    public double MaxChatGptInputWindowWidthRatio { get; set; } = 0.92;
    public int RecordingStateTimeoutMs { get; set; } = 5000;
    public int DictationResultTimeoutMs { get; set; } = 30000;
    public int DictationResultPollIntervalMs { get; set; } = 250;
    public int DictationSettleDelayMs { get; set; } = 1500;
    public bool EnableChatGptInputDiagnostics { get; set; } = true;
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
