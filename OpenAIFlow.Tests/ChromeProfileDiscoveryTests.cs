using System.Text.Json;

namespace ChatGptDictationBridge.Tests;

public sealed class ChromeProfileDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenAIFlow.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoversChromeProfileNamesAndPortablePaths()
    {
        var chromeExecutable = CreateFile("Chrome/chrome.exe");
        var userData = Path.Combine(_directory, "User Data");
        CreateFile("User Data/Default/Preferences");
        CreateFile("User Data/Profile 3/Preferences");
        CreateFile("User Data/Profile 8/Preferences");
        CreateFile("User Data/Guest Profile/Preferences");
        File.WriteAllText(
            Path.Combine(userData, "Local State"),
            JsonSerializer.Serialize(new
            {
                profile = new
                {
                    info_cache = new Dictionary<string, object>
                    {
                        ["Default"] = new { name = "Privat" },
                        ["Profile 3"] = new { name = "Arbeit" },
                        ["Profile 9"] = new { name = "Nicht vorhanden" },
                        ["Guest Profile"] = new { name = "Gast" }
                    }
                }
            }));

        var result = new ChromeProfileDiscovery().Discover(chromeExecutable, userData);

        Assert.Equal(chromeExecutable, result.ChromeExecutablePath);
        Assert.Equal(userData, result.ChromeUserDataDirectory);
        Assert.Collection(
            result.Profiles,
            profile =>
            {
                Assert.Equal("Privat", profile.Name);
                Assert.Equal("Default", profile.DirectoryName);
            },
            profile =>
            {
                Assert.Equal("Arbeit", profile.Name);
                Assert.Equal("Profile 3", profile.DirectoryName);
                Assert.Equal("Arbeit (Profile 3)", profile.ToString());
            },
            profile =>
            {
                Assert.Equal("Profile 8", profile.Name);
                Assert.Equal("Profile 8", profile.DirectoryName);
            });
        Assert.All(result.Profiles, profile =>
        {
            Assert.Equal(chromeExecutable, profile.ChromeExecutablePath);
            Assert.Equal(userData, profile.UserDataDirectory);
        });
    }

    [Fact]
    public void FallsBackToProfileFoldersWhenLocalStateIsCorrupt()
    {
        var userData = Path.Combine(_directory, "User Data");
        CreateFile("User Data/Default/Preferences");
        CreateFile("User Data/Profile 2/Preferences");
        CreateFile("User Data/System Profile/Preferences");
        File.WriteAllText(Path.Combine(userData, "Local State"), "{broken-json");

        var result = new ChromeProfileDiscovery().Discover(configuredUserDataDirectory: userData);

        Assert.Equal(new[] { "Default", "Profile 2" }, result.Profiles.Select(profile => profile.DirectoryName));
    }

    [Fact]
    public void StartupDialogListsProfilesAndPreselectsTheSavedProfile()
    {
        var chromeExecutable = CreateFile("Chrome/chrome.exe");
        var userData = Path.Combine(_directory, "User Data");
        CreateFile("User Data/Default/Preferences");
        CreateFile("User Data/Profile 3/Preferences");
        File.WriteAllText(
            Path.Combine(userData, "Local State"),
            """{"profile":{"info_cache":{"Default":{"name":"Privat"},"Profile 3":{"name":"Arbeit"}}}}""");
        var settings = new AppSettings
        {
            ChromeExecutablePath = chromeExecutable,
            ChromeUserDataDir = userData,
            ChromeProfileDirectory = "Profile 3"
        };

        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ChromeProfileSelectionForm(settings, new ChromeProfileDiscovery());
                form.Show();
                Application.DoEvents();
                var profileList = Assert.Single(form.Controls.OfType<ListBox>());
                Assert.Equal(new[] { "Privat (Default)", "Arbeit (Profile 3)" }, profileList.Items.Cast<object>().Select(item => item.ToString()));
                Assert.Equal("Arbeit (Profile 3)", profileList.SelectedItem?.ToString());
                Assert.Contains(form.Controls.OfType<Button>(), button => button.Text == "Profil verwenden" && button.Enabled);
                form.Close();
            }
            catch (Exception ex)
            {
                threadFailure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The startup dialog test did not finish.");
        Assert.Null(threadFailure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
