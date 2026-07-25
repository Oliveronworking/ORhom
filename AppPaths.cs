using System.IO;

namespace ORhom;

internal sealed record AppPaths(string DataDirectory, string SettingsPath, string LogDirectory)
{
    public static AppPaths Create()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        var dataDirectory = Path.Combine(localAppData, "ORhom");
        var legacyDataDirectories = new[]
        {
            Path.Combine(localAppData, "OliSpeechToText")
        };

        foreach (var legacyDataDirectory in legacyDataDirectories)
        {
            if (Directory.Exists(dataDirectory) || !Directory.Exists(legacyDataDirectory))
            {
                continue;
            }

            try
            {
                Directory.Move(legacyDataDirectory, dataDirectory);
            }
            catch (Exception)
            {
                // Keep using the existing data in place if Windows cannot move it yet.
                dataDirectory = legacyDataDirectory;
            }

            break;
        }

        return new AppPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "settings.json"),
            Path.Combine(dataDirectory, "logs"));
    }

    public void MigrateLegacySettingsIfNeeded(AppLogger logger)
    {
        if (File.Exists(SettingsPath))
        {
            return;
        }

        var legacySettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!File.Exists(legacySettingsPath) ||
            string.Equals(legacySettingsPath, SettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.Copy(legacySettingsPath, SettingsPath, overwrite: false);
            logger.Info($"Legacy settings migrated to '{SettingsPath}'.");
        }
        catch (IOException) when (File.Exists(SettingsPath))
        {
            // Another instance or installer completed the one-time migration first.
        }
        catch (Exception ex)
        {
            logger.Error("Legacy settings could not be migrated; defaults will be used.", ex);
        }
    }
}
