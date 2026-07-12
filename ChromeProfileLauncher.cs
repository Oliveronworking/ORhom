using System.Diagnostics;
using System.IO;

namespace ChatGptDictationBridge;

internal sealed class ChromeProfileLauncher
{
    private const string ExistingChromeProfileMode = "ExistingChromeProfile";

    private readonly AppSettings _settings;
    private readonly AppLogger _logger;

    public ChromeProfileLauncher(AppSettings settings, AppLogger logger)
    {
        _settings = settings;
        _logger = logger;
        LogConfiguration();
    }

    public ChromeProfileValidationResult ValidateConfiguredProfile()
    {
        if (!string.Equals(_settings.BrowserProfileMode, ExistingChromeProfileMode, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid($"BrowserProfileMode must be '{ExistingChromeProfileMode}' but was '{_settings.BrowserProfileMode}'.");
        }

        if (!_settings.RequireConfiguredChromeProfile)
        {
            return Invalid("RequireConfiguredChromeProfile must be enabled.");
        }

        if (_settings.AllowGuestProfile || _settings.AllowIncognitoProfile || _settings.AllowTemporaryProfile)
        {
            return Invalid("Guest, incognito and temporary Chrome profiles must all be disabled.");
        }

        if (!File.Exists(_settings.ChromeExecutablePath))
        {
            return Invalid("ChromeExecutablePath does not exist.");
        }

        if (!Directory.Exists(_settings.ChromeUserDataDir))
        {
            return Invalid("ChromeUserDataDir does not exist.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ChromeProfileDirectory) ||
            Path.IsPathRooted(_settings.ChromeProfileDirectory) ||
            _settings.ChromeProfileDirectory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            _settings.ChromeProfileDirectory.Contains("..", StringComparison.Ordinal))
        {
            return Invalid("ChromeProfileDirectory is not a valid profile directory name.");
        }

        var profileDirectory = Path.Combine(_settings.ChromeUserDataDir, _settings.ChromeProfileDirectory);
        if (!Directory.Exists(profileDirectory))
        {
            return Invalid("Configured Chrome profile directory does not exist.");
        }

        if (!File.Exists(Path.Combine(profileDirectory, "Preferences")))
        {
            return Invalid("Configured Chrome profile Preferences file does not exist.");
        }

        _logger.Info("Configured Chrome profile validation succeeded.");
        return new ChromeProfileValidationResult(true, profileDirectory, string.Empty);
    }

    public bool TryOpenChatGptProfile(out string failureReason) =>
        TryOpenChatGptProfile(_settings.ChatGptUrl, startMinimized: true, out failureReason);

    public bool TryOpenChatGptProfile(string targetUrl, out string failureReason) =>
        TryOpenChatGptProfile(targetUrl, startMinimized: true, out failureReason);

    public bool TryOpenChatGptProfileVisible(out string failureReason) =>
        TryOpenChatGptProfile(_settings.ChatGptUrl, startMinimized: false, out failureReason);

    private bool TryOpenChatGptProfile(string targetUrl, bool startMinimized, out string failureReason)
    {
        var validation = ValidateConfiguredProfile();
        if (!validation.IsValid)
        {
            failureReason = validation.FailureReason;
            return false;
        }

        try
        {
            var startInfo = CreateChromeStartInfo(
                _settings.ChromeExecutablePath,
                _settings.ChromeUserDataDir,
                _settings.ChromeProfileDirectory,
                targetUrl,
                startMinimized);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                failureReason = "Chrome could not be started with the configured profile.";
                return false;
            }

            _logger.Info($"Configured Chrome profile launched for ChatGPT. UserDataDir='{_settings.ChromeUserDataDir}' ProfileDirectory='{_settings.ChromeProfileDirectory}' StartMinimized={startMinimized}.");
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Could not launch configured Chrome profile for ChatGPT.", ex);
            failureReason = "Chrome could not be started with the configured profile.";
            return false;
        }
    }

    public bool TryOpenMicrophoneSettings(out string failureReason) =>
        TryOpenMicrophoneSettings(startMinimized: false, "chrome://settings/content/microphone", out failureReason);

    public bool TryOpenMicrophoneSettingsHidden(out string markerUrl, out string failureReason)
    {
        markerUrl = CreateMicrophoneMarkerUrl();
        return TryOpenMicrophoneSettings(startMinimized: false, markerUrl, out failureReason);
    }

    private bool TryOpenMicrophoneSettings(bool startMinimized, string targetUrl, out string failureReason)
    {
        var validation = ValidateConfiguredProfile();
        if (!validation.IsValid)
        {
            failureReason = validation.FailureReason;
            return false;
        }

        try
        {
            var startInfo = CreateChromeStartInfo(
                _settings.ChromeExecutablePath,
                _settings.ChromeUserDataDir,
                _settings.ChromeProfileDirectory,
                targetUrl,
                startMinimized);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                failureReason = "Chrome microphone settings could not be opened.";
                return false;
            }

            _logger.Info($"Chrome microphone settings opened for configured profile. ProfileDirectory='{_settings.ChromeProfileDirectory}' StartMinimized={startMinimized}.");
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Could not open Chrome microphone settings for the configured profile.", ex);
            failureReason = "Chrome microphone settings could not be opened.";
            return false;
        }
    }

    internal static string CreateMicrophoneMarkerUrl() =>
        CreateMicrophoneMarkerUrl(Guid.NewGuid());

    internal static string CreateMicrophoneMarkerUrl(Guid correlationId) =>
        $"https://openai-flow.invalid/microphone/{correlationId:D}";

    internal static ProcessStartInfo CreateChromeStartInfo(
        string chromeExecutablePath,
        string chromeUserDataDirectory,
        string chromeProfileDirectory,
        string targetUrl,
        bool startMinimized)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = chromeExecutablePath,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--user-data-dir={chromeUserDataDirectory}");
        startInfo.ArgumentList.Add($"--profile-directory={chromeProfileDirectory}");
        startInfo.ArgumentList.Add("--new-window");
        if (startMinimized)
        {
            startInfo.ArgumentList.Add("--start-minimized");
        }

        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add(targetUrl);
        return startInfo;
    }

    private ChromeProfileValidationResult Invalid(string reason)
    {
        _logger.Info($"Configured Chrome profile validation failed. Reason={reason}");
        return new ChromeProfileValidationResult(false, string.Empty, reason);
    }

    private void LogConfiguration()
    {
        _logger.Info($"Browser profile configuration: BrowserProfileMode='{_settings.BrowserProfileMode}', ChromeExecutablePath='{_settings.ChromeExecutablePath}', ChromeUserDataDir='{_settings.ChromeUserDataDir}', ChromeProfileDirectory='{_settings.ChromeProfileDirectory}', RequireConfiguredChromeProfile={_settings.RequireConfiguredChromeProfile}, AllowGuestProfile={_settings.AllowGuestProfile}, AllowIncognitoProfile={_settings.AllowIncognitoProfile}, AllowTemporaryProfile={_settings.AllowTemporaryProfile}.");
    }
}

internal sealed record ChromeProfileValidationResult(bool IsValid, string ProfileDirectoryPath, string FailureReason);
