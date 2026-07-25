namespace ORhom.Tests;

public sealed class ChromeProfileIdentityTests
{
    [Fact]
    public void EquivalentWindowsPathsProduceTheSameOwnershipMarker()
    {
        var first = ChromeProfileIdentity.Create(@"C:\Users\Oliver\Chrome\User Data", "Profile 3");
        var second = ChromeProfileIdentity.Create(@"c:/users/oliver/chrome/user data/", " profile 3 ");

        Assert.Equal(first, second);
        Assert.Equal(first.OwnershipPropertyName, second.OwnershipPropertyName);
    }

    [Fact]
    public void DifferentProfilesCannotShareAnOwnershipMarker()
    {
        var first = ChromeProfileIdentity.Create(@"C:\Chrome\User Data", "Default");
        var second = ChromeProfileIdentity.Create(@"C:\Chrome\User Data", "Profile 3");

        Assert.NotEqual(first.OwnershipPropertyName, second.OwnershipPropertyName);
    }

    [Fact]
    public void DifferentUserDataDirectoriesCannotShareAnOwnershipMarker()
    {
        var first = ChromeProfileIdentity.Create(@"C:\Chrome\User Data", "Default");
        var second = ChromeProfileIdentity.Create(@"D:\Portable Chrome\User Data", "Default");

        Assert.NotEqual(first.OwnershipPropertyName, second.OwnershipPropertyName);
    }

    [Fact]
    public void OwnershipMarkerDoesNotExposeLocalPathsOrProfileNames()
    {
        var identity = ChromeProfileIdentity.Create(@"C:\Users\Oliver\Secret Folder", "Work Profile");

        Assert.StartsWith("ORhom.ChatGptBackgroundWindow.", identity.OwnershipPropertyName);
        Assert.DoesNotContain("Oliver", identity.OwnershipPropertyName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Work", identity.OwnershipPropertyName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeWindowOwnedByOneProfileIsInvisibleToAnotherProfileLookup()
    {
        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var testDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "ORhom.Tests",
                    Guid.NewGuid().ToString("N"));
                using var window = new Form { ShowInTaskbar = false };
                window.Show();
                var first = ChromeProfileIdentity.Create(
                    testDirectory,
                    "Profile 1");
                var second = ChromeProfileIdentity.Create(
                    first.UserDataDirectory,
                    "Profile 2");
                var logger = new AppLogger(Path.Combine(testDirectory, "logs"));

                Assert.True(NativeMethods.MarkWindow(
                    window.Handle,
                    ChatGptWindowFinder.BackgroundWindowProperty));
                Assert.Equal(
                    IntPtr.Zero,
                    ChatGptWindowFinder.FindOwnedBackgroundWindow(first));
                Assert.True(ChatGptWindowFinder.ReleaseLegacyGenericWindowToUser(
                    window.Handle,
                    first,
                    logger));
                Assert.False(NativeMethods.HasWindowMark(
                    window.Handle,
                    ChatGptWindowFinder.BackgroundWindowProperty));
                Assert.True(NativeMethods.MarkWindow(
                    window.Handle,
                    ChatGptWindowFinder.BackgroundWindowProperty));
                Assert.True(NativeMethods.MarkWindow(window.Handle, first.OwnershipPropertyName));
                Assert.Equal(window.Handle, ChatGptWindowFinder.FindOwnedBackgroundWindow(first));
                Assert.Equal(IntPtr.Zero, ChatGptWindowFinder.FindOwnedBackgroundWindow(second));
                Assert.True(ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                    window.Handle,
                    first,
                    logger));
                Assert.Equal(IntPtr.Zero, ChatGptWindowFinder.FindOwnedBackgroundWindow(first));
                Assert.True(NativeMethods.IsWindowVisible(window.Handle));
                Assert.False(NativeMethods.IsIconic(window.Handle));
                window.Close();
                Directory.Delete(testDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                threadFailure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The native profile ownership test did not finish.");
        Assert.Null(threadFailure);
    }
}
