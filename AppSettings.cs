using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ORhom;

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
    public string DictationProvider { get; set; } = DictationProviders.LocalWhisper;
    public string PreferredMicrophoneId { get; set; } = string.Empty;
    public string PreferredMicrophoneName { get; set; } = string.Empty;
    public bool SetupCompleted { get; set; } = false;
    public string ChatGptDictationHotkey { get; set; } = "Ctrl+Shift+D";
    public string ChatGptUrl { get; set; } = "https://chatgpt.com";
    public string[] ChatGptWindowTitleContains { get; set; } = ["ChatGPT", "chatgpt.com"];
    public string BrowserProfileMode { get; set; } = "ExistingChromeProfile";
    public string ChromeExecutablePath { get; set; } = string.Empty;
    public string ChromeUserDataDir { get; set; } = string.Empty;
    public string ChromeProfileDirectory { get; set; } = string.Empty;
    public bool RequireConfiguredChromeProfile { get; set; } = true;
    public bool AllowGuestProfile { get; set; } = false;
    public bool AllowIncognitoProfile { get; set; } = false;
    public bool AllowTemporaryProfile { get; set; } = false;
    public bool OpenChatGptProfileVisibleForSetup { get; set; } = true;
    public bool WarnIfConfiguredChromeProfileUnavailable { get; set; } = true;
    public bool LaunchChatGptIfMissing { get; set; } = true;
    public bool PrepareChatGptOnStartup { get; set; } = true;
    public bool MinimizeChatGptAfterStartup { get; set; } = true;
    public bool KeepChatGptWindowHidden { get; set; } = true;
    public bool ShowRecordingOverlay { get; set; } = true;
    public RecordingOverlaySize RecordingOverlaySize { get; set; } =
        global::ORhom.RecordingOverlaySize.Small;
    public int RecordingOverlayBottomOffsetPx { get; set; } = 72;
    public string RecordingOverlayMonitorDeviceName { get; set; } = string.Empty;
    public double? RecordingOverlayRelativeX { get; set; }
    public double? RecordingOverlayRelativeY { get; set; }
    public bool EnableHybridPushToTalk { get; set; } = true;
    public int PushToTalkHoldThresholdMs { get; set; } = 350;
    public bool RestoreTargetAfterStart { get; set; } = true;
    public bool RestoreClipboard { get; set; } = true;
    public int BrowserChromeExclusionTopPx { get; set; } = 110;
    public int MaxChatGptInputHeightPx { get; set; } = 420;
    public double MaxChatGptInputWindowWidthRatio { get; set; } = 0.92;
    public int RecordingStateTimeoutMs { get; set; } = 5000;
    public int DictationStopConfirmationTimeoutMs { get; set; } = 9000;
    public int DictationResultTimeoutMs { get; set; } = 300000;
    public int DictationResultPollIntervalMs { get; set; } = 100;
    public int DictationSettleDelayMs { get; set; } = 0;
    public int DictationTextStableMs { get; set; } = 3000;
    public int DictationStopGracePeriodMs { get; set; } = 250;
    public int LocalMaxRecordingSeconds { get; set; } = 300;
    public bool EnableAudioDucking { get; set; } = true;
    public int AudioDuckingVolumePercent { get; set; } = 10;
    public bool EnableChatGptInputDiagnostics { get; set; } = true;
    public int PasteDelayMs { get; set; } = 25;
    public int RestoreClipboardDelayMs { get; set; } = 180;
    public bool BlockPasswordFields { get; set; } = true;

    [JsonIgnore]
    public string SettingsPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public bool IsPersistenceAvailable { get; private set; } = true;

    public static AppSettings Load(string path, AppLogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new AppSettings { SettingsPath = path };
                if (!defaults.Save(logger))
                {
                    defaults.IsPersistenceAvailable = false;
                }
                return defaults;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ??
                           throw new JsonException("Settings JSON root is null.");
            settings.SettingsPath = path;
            settings.NormalizeDeserializedValues();
            return settings;
        }
        catch (JsonException ex)
        {
            logger.Error("Settings JSON could not be parsed.", ex);
            if (TryQuarantineUnreadableSettings(path, logger))
            {
                var defaults = new AppSettings { SettingsPath = path };
                if (!defaults.Save(logger))
                {
                    defaults.IsPersistenceAvailable = false;
                }
                return defaults;
            }

            return new AppSettings
            {
                SettingsPath = path,
                IsPersistenceAvailable = false
            };
        }
        catch (Exception ex)
        {
            logger.Error("Settings could not be loaded; saving is disabled to protect the existing file.", ex);
            return new AppSettings
            {
                SettingsPath = path,
                IsPersistenceAvailable = false
            };
        }
    }

    public bool Save(AppLogger logger)
    {
        string? temporaryPath = null;
        if (!IsPersistenceAvailable)
        {
            logger.Info("Settings save blocked because the existing settings file was not loaded safely.");
            return false;
        }

        try
        {
            var settingsPath = Path.GetFullPath(SettingsPath);
            var settingsDirectory = Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(settingsDirectory);

            temporaryPath = Path.Combine(
                settingsDirectory,
                $".{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));

            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, settingsPath);
            }

            temporaryPath = null;
            IsPersistenceAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Settings could not be saved.", ex);
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
                    logger.Error("Temporary settings file could not be removed.", ex);
                }
            }
        }
    }

    private static bool TryQuarantineUnreadableSettings(
        string path,
        AppLogger logger)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var quarantinePath = Path.Combine(
                directory,
                $"{fileName}.unreadable-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}{extension}");
            File.Move(path, quarantinePath);
            logger.Info($"Unreadable settings were preserved as '{quarantinePath}'.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Unreadable settings could not be quarantined.", ex);
            return false;
        }
    }

    private void NormalizeDeserializedValues()
    {
        ToggleHotkey = string.IsNullOrWhiteSpace(ToggleHotkey) ? "F8" : ToggleHotkey.Trim();
        DictationProvider = DictationProviders.Normalize(DictationProvider);
        PreferredMicrophoneId ??= string.Empty;
        PreferredMicrophoneName ??= string.Empty;
        LocalMaxRecordingSeconds = Math.Clamp(LocalMaxRecordingSeconds, 30, 600);
        ChatGptDictationHotkey = string.IsNullOrWhiteSpace(ChatGptDictationHotkey)
            ? "Ctrl+Shift+D"
            : ChatGptDictationHotkey.Trim();
        ChatGptUrl = string.IsNullOrWhiteSpace(ChatGptUrl)
            ? "https://chatgpt.com"
            : ChatGptUrl.Trim();
        ChatGptWindowTitleContains = ChatGptWindowTitleContains?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray() ?? [];
        if (ChatGptWindowTitleContains.Length == 0)
        {
            ChatGptWindowTitleContains = ["ChatGPT", "chatgpt.com"];
        }

        BrowserProfileMode = string.IsNullOrWhiteSpace(BrowserProfileMode)
            ? "ExistingChromeProfile"
            : BrowserProfileMode.Trim();
        ChromeExecutablePath ??= string.Empty;
        ChromeUserDataDir ??= string.Empty;
        ChromeProfileDirectory ??= string.Empty;
        if (!Enum.IsDefined(RecordingOverlaySize))
        {
            RecordingOverlaySize = global::ORhom.RecordingOverlaySize.Small;
        }

        RecordingOverlayMonitorDeviceName ??= string.Empty;
        if (!IsValidRelativePosition(RecordingOverlayRelativeX) ||
            !IsValidRelativePosition(RecordingOverlayRelativeY))
        {
            RecordingOverlayMonitorDeviceName = string.Empty;
            RecordingOverlayRelativeX = null;
            RecordingOverlayRelativeY = null;
        }
    }

    private static bool IsValidRelativePosition(double? value) =>
        value is { } coordinate &&
        !double.IsNaN(coordinate) &&
        !double.IsInfinity(coordinate);
}
