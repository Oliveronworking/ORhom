using System.IO;
using System.Text.Json;

namespace ORhom;

internal sealed class ChromeProfileDiscovery
{
    public ChromeProfileDiscoveryResult Discover(string? configuredExecutablePath = null, string? configuredUserDataDirectory = null)
    {
        var executablePath = FirstExistingFile(
            configuredExecutablePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));
        var userDataDirectory = FirstExistingDirectory(
            configuredUserDataDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"));

        if (string.IsNullOrWhiteSpace(userDataDirectory))
        {
            return new ChromeProfileDiscoveryResult(executablePath ?? string.Empty, string.Empty, []);
        }

        var profiles = ReadProfileNames(userDataDirectory);
        foreach (var directory in Directory.EnumerateDirectories(userDataDirectory))
        {
            var directoryName = Path.GetFileName(directory);
            if (!IsProfileDirectory(directoryName) || !File.Exists(Path.Combine(directory, "Preferences")))
            {
                continue;
            }

            profiles.TryAdd(directoryName, directoryName == "Default" ? "Standard" : directoryName);
        }

        var discoveredProfiles = profiles
            .Where(item =>
                IsProfileDirectory(item.Key) &&
                IsSafeDirectoryName(item.Key) &&
                File.Exists(Path.Combine(userDataDirectory, item.Key, "Preferences")))
            .Select(item => new ChromeProfileInfo(
                item.Value,
                item.Key,
                userDataDirectory,
                executablePath ?? string.Empty))
            .OrderBy(profile => profile.DirectoryName == "Default" ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new ChromeProfileDiscoveryResult(executablePath ?? string.Empty, userDataDirectory, discoveredProfiles);
    }

    private static Dictionary<string, string> ReadProfileNames(string userDataDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(userDataDirectory, "Local State")));
            if (!document.RootElement.TryGetProperty("profile", out var profile) ||
                !profile.TryGetProperty("info_cache", out var infoCache) ||
                infoCache.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var entry in infoCache.EnumerateObject())
            {
                var name = entry.Value.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                result[entry.Name] = string.IsNullOrWhiteSpace(name) ? entry.Name : name.Trim();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static string? FirstExistingFile(params string?[] paths) =>
        paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? FirstExistingDirectory(params string?[] paths) =>
        paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    private static bool IsProfileDirectory(string directoryName) =>
        directoryName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
        directoryName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeDirectoryName(string directoryName) =>
        !string.IsNullOrWhiteSpace(directoryName) &&
        !Path.IsPathRooted(directoryName) &&
        directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !directoryName.Contains("..", StringComparison.Ordinal);
}

internal sealed record ChromeProfileDiscoveryResult(
    string ChromeExecutablePath,
    string ChromeUserDataDirectory,
    IReadOnlyList<ChromeProfileInfo> Profiles);

internal sealed record ChromeProfileInfo(
    string Name,
    string DirectoryName,
    string UserDataDirectory,
    string ChromeExecutablePath)
{
    public override string ToString() => $"{Name} ({DirectoryName})";
}
