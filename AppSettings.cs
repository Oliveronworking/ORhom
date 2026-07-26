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
    public bool SetupCompleted { get; set; }
    public string ChatGptDictationHotkey { get; set; } = "Ctrl+Shift+D";
    public string ChatGptUrl { get; set; } = "https://chatgpt.com";
    public string[] ChatGptWindowTitleContains { get; set; } = ["ChatGPT", "chatgpt.com"];
    public string BrowserProfileMode { get; set; } = "ExistingChromeProfile";
    public string ChromeExecutablePath { get; set; } = string.Empty;
    public string ChromeUserDataDir { get; set; } = string.Empty;
    public string ChromeProfileDirectory { get; set; } = string.Empty;
    public bool RequireConfiguredChromeProfile { get; set; } = true;
    public bool AllowGuestProfile { get; set; }
    public bool AllowIncognitoProfile { get; set; }
    public bool AllowTemporaryProfile { get; set; }
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
    public int DictationSettleDelayMs { get; set; }
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
            NormalizeDeserializedValues();
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
        RecordingOverlayBottomOffsetPx = Math.Clamp(
            RecordingOverlayBottomOffsetPx,
            0,
            500);
        PushToTalkHoldThresholdMs = Math.Clamp(
            PushToTalkHoldThresholdMs,
            100,
            2_000);
        BrowserChromeExclusionTopPx = Math.Clamp(
            BrowserChromeExclusionTopPx,
            0,
            500);
        MaxChatGptInputHeightPx = Math.Clamp(
            MaxChatGptInputHeightPx,
            100,
            2_000);
        MaxChatGptInputWindowWidthRatio =
            double.IsFinite(MaxChatGptInputWindowWidthRatio)
                ? Math.Clamp(MaxChatGptInputWindowWidthRatio, 0.1, 1)
                : 0.92;
        RecordingStateTimeoutMs = Math.Clamp(
            RecordingStateTimeoutMs,
            3_000,
            5_000);
        DictationStopConfirmationTimeoutMs = Math.Clamp(
            DictationStopConfirmationTimeoutMs,
            1_000,
            30_000);
        DictationResultTimeoutMs = Math.Clamp(
            DictationResultTimeoutMs,
            1_000,
            600_000);
        DictationResultPollIntervalMs = Math.Clamp(
            DictationResultPollIntervalMs,
            100,
            1_000);
        DictationSettleDelayMs = Math.Clamp(
            DictationSettleDelayMs,
            0,
            5_000);
        DictationTextStableMs = Math.Clamp(
            DictationTextStableMs,
            3_000,
            10_000);
        DictationStopGracePeriodMs = Math.Clamp(
            DictationStopGracePeriodMs,
            0,
            1_000);
        LocalMaxRecordingSeconds = Math.Clamp(LocalMaxRecordingSeconds, 30, 600);
        AudioDuckingVolumePercent = Math.Clamp(
            AudioDuckingVolumePercent,
            0,
            100);
        PasteDelayMs = Math.Clamp(PasteDelayMs, 0, 2_000);
        RestoreClipboardDelayMs = Math.Clamp(
            RestoreClipboardDelayMs,
            0,
            5_000);
        ChatGptDictationHotkey = string.IsNullOrWhiteSpace(ChatGptDictationHotkey)
            ? "Ctrl+Shift+D"
            : ChatGptDictationHotkey.Trim();
        ChatGptUrl = ChatGptOriginPolicy.NormalizeConfiguredUrl(ChatGptUrl);
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
        double.IsFinite(coordinate) &&
        coordinate is >= 0 and <= 1;
}
