using System.IO;

namespace ORhom;

internal sealed record AppPaths(string DataDirectory, string SettingsPath, string LogDirectory)
{
    private static readonly string[] KnownDataEntryNames =
    [
        "settings.json",
        "dictation-history.json",
        "models",
        "logs"
    ];

    public static AppPaths Create()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Create(localAppData);
    }

    internal static AppPaths Create(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        var dataDirectory = Path.Combine(localAppData, "ORhom");
        var legacyDataDirectories = new[]
        {
            Path.Combine(localAppData, "OpenAIFlow"),
            Path.Combine(localAppData, "OliSpeechToText")
        };

        foreach (var legacyDataDirectory in legacyDataDirectories)
        {
            if (!Directory.Exists(legacyDataDirectory))
            {
                continue;
            }

            if (!Directory.Exists(dataDirectory))
            {
                try
                {
                    Directory.Move(legacyDataDirectory, dataDirectory);
                }
                catch (Exception)
                {
                    // Keep every data consumer on the same usable root when Windows
                    // cannot complete the atomic migration yet.
                    dataDirectory = legacyDataDirectory;
                }

                break;
            }

            MergeMissingKnownData(legacyDataDirectory, dataDirectory);
        }

        return new AppPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "settings.json"),
            Path.Combine(dataDirectory, "logs"));
    }

    private static void MergeMissingKnownData(
        string legacyDataDirectory,
        string dataDirectory)
    {
        foreach (var entryName in KnownDataEntryNames)
        {
            TryMoveMissingEntry(
                Path.Combine(legacyDataDirectory, entryName),
                Path.Combine(dataDirectory, entryName));
        }
    }

    private static void TryMoveMissingEntry(string sourcePath, string targetPath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                {
                    File.Move(sourcePath, targetPath);
                }

                return;
            }

            if (!Directory.Exists(sourcePath))
            {
                return;
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                Directory.Move(sourcePath, targetPath);
                return;
            }

            if (!Directory.Exists(targetPath))
            {
                return;
            }

            foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourcePath))
            {
                TryMoveMissingEntry(
                    sourceEntry,
                    Path.Combine(targetPath, Path.GetFileName(sourceEntry)));
            }
        }
        catch (Exception)
        {
            // Migration is best-effort and never overwrites current ORhom data.
            // Any source that could not be moved remains intact for a later retry.
        }
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
