using System.IO;

namespace ORhom;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (!WindowsCompatibility.IsCurrentVersionSupported())
        {
            MessageBox.Show(
                "ORhom benötigt Windows 11 (Build 22000) oder neuer. "
                + "Die lokale Whisper-Vulkan-Laufzeit unterstützt diese "
                + "Windows-Version nicht sicher.",
                "ORhom",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Keep the established mutex identity so ORhom and an older installed build
        // cannot record or manipulate the same profile at the same time.
        using var mutex = new Mutex(
            initiallyOwned: true,
            "ChatGptDictationBridge.SingleInstance",
            out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "ORhom läuft bereits im Hintergrund.",
                "ORhom",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        var paths = AppPaths.Create();
        using var logger = new AppLogger(paths.LogDirectory);
        RegisterGlobalExceptionLogging(logger);
        logger.Info($"Application bootstrap. Version={Application.ProductVersion} ProcessId={Environment.ProcessId} Executable='{Environment.ProcessPath ?? Application.ExecutablePath}'.");
        paths.MigrateLegacySettingsIfNeeded(logger);
        var settings = AppSettings.Load(paths.SettingsPath, logger);
        var discovery = new ChromeProfileDiscovery();
        var previousProfileIdentity = ChromeProfileIdentity.From(settings);
        if (!DictationProviders.IsLocal(settings.DictationProvider))
        {
            _ = ChatGptWindowFinder.ReleaseLegacyGenericWindowsToUser(
                previousProfileIdentity,
                logger);
        }
        if (!settings.IsPersistenceAvailable)
        {
            MessageBox.Show(
                "Die vorhandene Einstellungsdatei konnte nicht sicher gelesen werden und wurde nicht überschrieben. Ein bisheriges ORhom-Hintergrundfenster wurde, soweit möglich, sichtbar freigegeben. Bitte Datei- oder Zugriffsproblem beheben und ORhom neu starten.",
                "ORhom",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (!DictationProviders.IsLocal(settings.DictationProvider))
        {
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
                        "ORhom",
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
                        "Das bisherige ORhom-Hintergrundfenster konnte weder geschlossen noch sicher sichtbar freigegeben werden. Die Profilauswahl wurde nicht übernommen.",
                        "ORhom",
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
                    "Das ausgewählte Chrome-Profil konnte nicht gespeichert werden. ORhom wurde nicht gestartet.",
                    "ORhom",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }

        try
        {
            using var applicationContext = new DictationTrayAppContext(
                settings,
                logger,
                discovery,
                paths);
            Application.Run(applicationContext);
        }
        catch (Exception ex)
        {
            logger.Error("Application message loop terminated unexpectedly.", ex);
            if (!DictationProviders.IsLocal(settings.DictationProvider))
            {
                _ = ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                    ChromeProfileIdentity.From(settings),
                    logger);
            }
            try
            {
                MessageBox.Show(
                    $"ORhom hat einen unerwarteten Fehler sicher abgefangen und beendet. Details stehen im Log:\n{logger.LogPath}",
                    "ORhom",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception notificationException)
            {
                logger.Error("Fatal error notification could not be displayed.", notificationException);
            }
        }
    }

    private static void RegisterGlobalExceptionLogging(AppLogger logger)
    {
        Application.ThreadException += (_, e) =>
            logger.Error("Unhandled Windows Forms thread exception was contained.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Error(
                $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}",
                e.ExceptionObject as Exception ??
                new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown exception"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.Error("Unobserved task exception was contained.", e.Exception);
            e.SetObserved();
        };
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

        failureMessage = "Der bisherige ChatGPT-Entwurf konnte nicht sicher geprüft werden. Die Profilauswahl wurde zum Schutz möglicher Texte nicht übernommen; bitte ORhom erneut mit dem bisherigen Profil starten.";
        return false;
    }
}
