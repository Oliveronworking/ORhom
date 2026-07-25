using System.Drawing;
using System.Runtime.ExceptionServices;

namespace ORhom.Tests;

public sealed class SettingsFormLayoutTests
{
    [Theory]
    [InlineData(Keys.A, Keys.None, false)]
    [InlineData(Keys.D7, Keys.None, false)]
    [InlineData(Keys.Space, Keys.None, false)]
    [InlineData(Keys.F8, Keys.None, true)]
    [InlineData(Keys.F24, Keys.None, true)]
    [InlineData(Keys.A, Keys.Control, true)]
    [InlineData(Keys.Space, Keys.Control | Keys.Shift, true)]
    public void GlobalHotkeySelectionRejectsEasyToTriggerBareKeys(
        Keys key,
        Keys modifiers,
        bool expected)
    {
        Assert.Equal(
            expected,
            SettingsForm.IsSafeHotkeySelection(key, modifiers));
    }

    [Fact]
    public void MainWindowKeepsThreeStepsAndPrimaryActionsVisible()
    {
        RunOnStaThread(() =>
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"orhom-settings-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var logger = new AppLogger(
                    Path.Combine(temporaryDirectory, "logs"));
                var settings = AppSettings.Load(
                    Path.Combine(temporaryDirectory, "settings.json"),
                    logger);
                settings.ToggleHotkey = "F8";
                settings.DictationProvider = DictationProviders.LocalWhisper;

                using var form = new SettingsForm(
                    settings,
                    new AudioInputDeviceService(logger),
                    new ChromeProfileDiscovery(),
                    values => Task.FromResult(
                        SettingsApplyResult.Success(
                            values.MicrophoneName,
                            values.Hotkey)))
                {
                    Opacity = 0,
                    ShowInTaskbar = false
                };

                form.Show();
                Application.DoEvents();

                var scrollPanel = Assert.IsType<Panel>(
                    FindControl(form, "settingsScrollPanel"));
                var footer = FindControl(form, "settingsFooter");
                var saveButton = Assert.IsType<Button>(
                    FindControl(form, "saveAndStartButton"));
                var hideButton = Assert.IsType<Button>(
                    FindControl(form, "hideToTrayButton"));
                var overlaySizeCombo = Assert.IsType<ComboBox>(
                    FindControl(form, "recordingOverlaySizeCombo"));

                Assert.True(scrollPanel.AutoScroll);
                Assert.NotNull(FindControl(form, "providerStepCard"));
                Assert.NotNull(FindControl(form, "microphoneStepCard"));
                Assert.NotNull(FindControl(form, "hotkeyStepCard"));
                Assert.Equal(
                    ["Klein", "Mittel", "Groß"],
                    overlaySizeCombo.Items
                        .Cast<object>()
                        .Select(item => item.ToString()!)
                        .ToArray());
                Assert.Equal("Klein", overlaySizeCombo.Text);
                Assert.Equal(
                    "Größe des Sprachfelds",
                    overlaySizeCombo.AccessibleName);
                Assert.Contains(
                    "Klein, Mittel und Groß",
                    overlaySizeCombo.AccessibleDescription ?? string.Empty);
                Assert.Equal("Speichern & losdiktieren", saveButton.Text);
                Assert.Equal("Im Hintergrund schließen", hideButton.Text);
                Assert.False(string.IsNullOrWhiteSpace(
                    saveButton.AccessibleDescription));
                Assert.False(string.IsNullOrWhiteSpace(
                    hideButton.AccessibleDescription));
                Assert.True(footer.Bottom <= form.ClientSize.Height);
                Assert.True(saveButton.Bottom <= footer.Height);
                Assert.True(hideButton.Bottom <= footer.Height);

                var root = FindControl(form, "settingsRootLayout");
                using var bitmap = new Bitmap(root.Width, root.Height);
                root.DrawToBitmap(
                    bitmap,
                    new Rectangle(Point.Empty, bitmap.Size));
                Assert.True(bitmap.Width >= 560);
                Assert.True(bitmap.Height >= 440);

                var previewPath = Environment.GetEnvironmentVariable(
                    "ORHOM_SETTINGS_PREVIEW_PATH");
                if (!string.IsNullOrWhiteSpace(previewPath))
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(
                            Path.GetFullPath(previewPath))!);
                    bitmap.Save(previewPath);
                }

                form.ClosePermanently();
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void RecordingBarSizeSynchronizesFromSettingsAndIsPassedToSave()
    {
        RunOnStaThread(() =>
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"orhom-settings-size-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var logger = new AppLogger(
                    Path.Combine(temporaryDirectory, "logs"));
                var settings = AppSettings.Load(
                    Path.Combine(temporaryDirectory, "settings.json"),
                    logger);
                settings.ToggleHotkey = "F8";
                settings.DictationProvider = DictationProviders.LocalWhisper;
                settings.RecordingOverlaySize = RecordingOverlaySize.Medium;

                SettingsFormValues? savedValues = null;
                using var form = new SettingsForm(
                    settings,
                    new AudioInputDeviceService(logger),
                    new ChromeProfileDiscovery(),
                    values =>
                    {
                        savedValues = values;
                        settings.RecordingOverlaySize = values.RecordingOverlaySize;
                        return Task.FromResult(
                            SettingsApplyResult.Fail("Test beendet."));
                    })
                {
                    Opacity = 0,
                    ShowInTaskbar = false
                };

                form.Show();
                Application.DoEvents();

                var overlaySizeCombo = Assert.IsType<ComboBox>(
                    FindControl(form, "recordingOverlaySizeCombo"));
                Assert.Equal("Mittel", overlaySizeCombo.Text);

                settings.RecordingOverlaySize = RecordingOverlaySize.Large;
                form.ShowAndActivate();
                Application.DoEvents();
                Assert.Equal("Groß", overlaySizeCombo.Text);

                overlaySizeCombo.SelectedIndex = 0;
                var microphoneCombo = Assert.IsType<ComboBox>(
                    FindControl(form, "microphoneCombo"));
                var microphone = new AudioInputDeviceInfo(
                    "settings-size-test",
                    "Testmikrofon");
                microphoneCombo.Items.Clear();
                microphoneCombo.Items.Add(microphone);
                microphoneCombo.SelectedItem = microphone;

                var saveButton = Assert.IsType<Button>(
                    FindControl(form, "saveAndStartButton"));
                saveButton.PerformClick();
                Application.DoEvents();

                Assert.NotNull(savedValues);
                Assert.Equal(
                    RecordingOverlaySize.Small,
                    savedValues.RecordingOverlaySize);
                Assert.Equal(
                    RecordingOverlaySize.Small,
                    settings.RecordingOverlaySize);

                form.ClosePermanently();
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    private static Control FindControl(Control root, string name)
    {
        if (root.Name.Equals(name, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (Control child in root.Controls)
        {
            var match = FindControlOrNull(child, name);
            if (match is not null)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Control '{name}' was not found.");
    }

    private static Control? FindControlOrNull(Control root, string name)
    {
        if (root.Name.Equals(name, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (Control child in root.Controls)
        {
            var match = FindControlOrNull(child, name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(15)),
            "STA settings layout test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
