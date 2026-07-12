using System.IO;

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
                "OpenAI Flow Dictation läuft bereits im Hintergrund.",
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
        var previousProfileIdentity = ChromeProfileIdentity.From(settings);
        _ = ChatGptWindowFinder.ReleaseLegacyGenericWindowsToUser(
            previousProfileIdentity,
            logger);
        if (!settings.IsPersistenceAvailable)
        {
            MessageBox.Show(
                "Die vorhandene Einstellungsdatei konnte nicht sicher gelesen werden und wurde nicht überschrieben. Ein bisheriges OpenAI-Flow-Hintergrundfenster wurde, soweit möglich, sichtbar freigegeben. Bitte Datei- oder Zugriffsproblem beheben und OpenAI Flow neu starten.",
                "OpenAI Flow Dictation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        using var profileSelection = new ChromeProfileSelectionForm(settings, discovery);
        if (profileSelection.ShowDialog() != DialogResult.OK || profileSelection.SelectedProfile is not { } profile)
        {
            return;
        }

        var profileChanged =
            !settings.ChromeUserDataDir.Equals(profile.UserDataDirectory, StringComparison.OrdinalIgnoreCase) ||
            !settings.ChromeProfileDirectory.Equals(profile.DirectoryName, StringComparison.OrdinalIgnoreCase);
        if (profileChanged &&
            previousProfileIdentity.IsConfigured)
        {
            if (!TryPreservePendingComposer(settings, paths, logger, out var preservationFailure))
            {
                var handedToUser = ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                    previousProfileIdentity,
                    logger);
                MessageBox.Show(
                    handedToUser
                        ? $"{preservationFailure} Das bisherige Hintergrundfenster wurde sichtbar zur manuellen Textrettung freigegeben."
                        : preservationFailure,
                    "OpenAI Flow Dictation",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            var previousWindowReleased = ChatGptWindowFinder.CloseOwnedBackgroundWindowAndWait(
                previousProfileIdentity,
                logger);
            if (!previousWindowReleased)
            {
                previousWindowReleased = ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                    previousProfileIdentity,
                    logger);
            }

            if (!previousWindowReleased)
            {
                MessageBox.Show(
                    "Das bisherige OpenAI-Flow-Hintergrundfenster konnte weder geschlossen noch sicher sichtbar freigegeben werden. Die Profilauswahl wurde nicht übernommen.",
                    "OpenAI Flow Dictation",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }

        settings.ChromeExecutablePath = profile.ChromeExecutablePath;
        settings.ChromeUserDataDir = profile.UserDataDirectory;
        settings.ChromeProfileDirectory = profile.DirectoryName;
        if (!settings.Save(logger))
        {
            MessageBox.Show(
                "Das ausgewählte Chrome-Profil konnte nicht gespeichert werden. OpenAI Flow wurde nicht gestartet.",
                "OpenAI Flow Dictation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new DictationTrayAppContext(settings, logger, discovery));
    }

    private static bool TryPreservePendingComposer(
        AppSettings settings,
        AppPaths paths,
        AppLogger logger,
        out string failureMessage)
    {
        if (ChatGptWindowFinder.FindOwnedBackgroundWindow(settings) == IntPtr.Zero)
        {
            failureMessage = string.Empty;
            return true;
        }

        var launcher = new ChromeProfileLauncher(settings, logger);
        using var controller = new ChatGptDictationController(settings, logger, launcher);
        var inspection = controller.InspectPendingComposer();
        var history = new DictationHistoryStore(
            Path.Combine(paths.DataDirectory, "dictation-history.json"),
            logger);
        var preservation = PendingComposerPreservation.Preserve(
            inspection,
            history.ContainsText,
            text => history.TryAdd(
                text,
                DictationHistoryOutcomes.PreservedBeforeClose,
                out _));
        if (PendingComposerPreservation.IsSafeToClose(preservation))
        {
            if (inspection.State == PendingComposerState.Text)
            {
                logger.Info($"Pending composer text preserved before startup profile switch. TextLength={inspection.Text.Length} Outcome={preservation}");
            }

            failureMessage = string.Empty;
            return true;
        }

        if (preservation == PendingComposerPreservationOutcome.PersistenceFailed)
        {
            failureMessage = "Im bisherigen ChatGPT-Entwurf liegt noch Text, der nicht im Diktierverlauf gespeichert werden konnte. Die Profilauswahl wurde zum Schutz des Textes nicht übernommen.";
            return false;
        }

        failureMessage = "Der bisherige ChatGPT-Entwurf konnte nicht sicher geprüft werden. Die Profilauswahl wurde zum Schutz möglicher Texte nicht übernommen; bitte OpenAI Flow erneut mit dem bisherigen Profil starten.";
        return false;
    }
}
