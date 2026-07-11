namespace ChatGptDictationBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "ChatGptDictationBridge.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "OpenAI Flow Dictation laeuft bereits im Hintergrund.",
                "OpenAI Flow Dictation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        var paths = AppPaths.Create();
        var logger = new AppLogger(paths.LogDirectory);
        paths.MigrateLegacySettingsIfNeeded(logger);
        var settings = AppSettings.Load(paths.SettingsPath, logger);
        var discovery = new ChromeProfileDiscovery();
        var previousBackgroundWindow = ChatGptWindowFinder.CloseOwnedBackgroundWindow(settings, logger);
        if (previousBackgroundWindow != IntPtr.Zero)
        {
            Thread.Sleep(250);
        }

        using var profileSelection = new ChromeProfileSelectionForm(settings, discovery);
        if (profileSelection.ShowDialog() != DialogResult.OK || profileSelection.SelectedProfile is not { } profile)
        {
            return;
        }

        var profileChanged =
            !settings.ChromeUserDataDir.Equals(profile.UserDataDirectory, StringComparison.OrdinalIgnoreCase) ||
            !settings.ChromeProfileDirectory.Equals(profile.DirectoryName, StringComparison.OrdinalIgnoreCase);
        settings.ChromeExecutablePath = profile.ChromeExecutablePath;
        settings.ChromeUserDataDir = profile.UserDataDirectory;
        settings.ChromeProfileDirectory = profile.DirectoryName;
        settings.Save(logger);

        Application.Run(new DictationTrayAppContext(settings, logger, discovery, profileChanged));
    }
}
