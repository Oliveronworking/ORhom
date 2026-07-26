namespace ORhom.Tests;

public sealed class ChromeProfileIntegrationTests
{
    [ChromeIntegrationFact]
    [Trait("Category", "ChromeIntegration")]
    public async Task CorrelatedSharedUserDataProfileLaunchLeavesExistingChromeWindowsUntouched()
    {
        var discovered = new ChromeProfileDiscovery().Discover();
        Assert.True(File.Exists(discovered.ChromeExecutablePath), "Google Chrome was not found.");

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "ORhom.ChromeIntegration",
            Guid.NewGuid().ToString("N"));
        var testUserDataDirectory = Path.Combine(testRoot, "User Data");
        const string targetProfileDirectory = "Default";
        const string sentinelProfileDirectory = "Profile 1";
        InitializeProfile(testUserDataDirectory, targetProfileDirectory);
        InitializeProfile(testUserDataDirectory, sentinelProfileDirectory);

        var settings = new AppSettings
        {
            ChromeExecutablePath = discovered.ChromeExecutablePath,
            ChromeUserDataDir = testUserDataDirectory,
            ChromeProfileDirectory = targetProfileDirectory,
            ChatGptUrl = "https://chatgpt.com/",
            KeepChatGptWindowHidden = true
        };
        var logger = new AppLogger(Path.Combine(testRoot, "logs"));
        var launcher = new ChromeProfileLauncher(settings, logger);
        var targetIdentity = ChromeProfileIdentity.From(settings);
        var sentinelMarker = $"https://orhom.invalid/integration-sentinel/{Guid.NewGuid():D}";
        var sentinelProcess = default(System.Diagnostics.Process);
        var sentinelWindow = IntPtr.Zero;
        var launchedWindow = IntPtr.Zero;

        try
        {
            var windowsBeforeSentinel = FindVisibleChromeWindows().ToHashSet();
            var sentinelStartInfo = ChromeProfileLauncher.CreateChromeStartInfo(
                discovered.ChromeExecutablePath,
                testUserDataDirectory,
                sentinelProfileDirectory,
                sentinelMarker,
                startMinimized: false);
            sentinelStartInfo.ArgumentList.Insert(
                sentinelStartInfo.ArgumentList.Count - 1,
                "--disable-background-mode");
            sentinelProcess = System.Diagnostics.Process.Start(sentinelStartInfo);
            Assert.NotNull(sentinelProcess);

            sentinelWindow = await WaitForCorrelatedWindowAsync(
                windowsBeforeSentinel,
                sentinelMarker,
                TimeSpan.FromSeconds(15));
            Assert.NotEqual(IntPtr.Zero, sentinelWindow);

            var preexistingWindows = FindVisibleChromeWindows()
                .Select(window => CaptureWindow(window, targetIdentity))
                .ToArray();
            var sentinelUrlBefore = ChromeWindowUrlCorrelation.TryReadOmniboxUrl(sentinelWindow);
            Assert.True(ChromeWindowUrlCorrelation.IsExactMatch(sentinelUrlBefore, sentinelMarker));

            launchedWindow = await ChatGptWindowFinder.LaunchConfiguredProfileAsync(
                settings,
                logger,
                launcher,
                IntPtr.Zero);

            Assert.NotEqual(IntPtr.Zero, launchedWindow);
            Assert.NotEqual(sentinelWindow, launchedWindow);
            Assert.True(NativeMethods.IsWindow(launchedWindow));
            Assert.True(NativeMethods.HasWindowMark(
                launchedWindow,
                targetIdentity.OwnershipPropertyName));
            Assert.Equal(
                NativeMethods.GetOwningProcessId(sentinelWindow),
                NativeMethods.GetOwningProcessId(launchedWindow));
            Assert.All(preexistingWindows, snapshot => AssertWindowUnchanged(snapshot, targetIdentity));
            Assert.True(ChromeWindowUrlCorrelation.IsExactMatch(
                ChromeWindowUrlCorrelation.TryReadOmniboxUrl(sentinelWindow),
                sentinelMarker));
        }
        finally
        {
            var launchedWindowClosed = true;
            var sentinelWindowClosed = true;
            if (launchedWindow != IntPtr.Zero)
            {
                _ = NativeMethods.RequestWindowClose(launchedWindow);
                launchedWindowClosed = await WaitForWindowClosedAsync(
                    launchedWindow,
                    TimeSpan.FromSeconds(5));
            }

            if (sentinelWindow != IntPtr.Zero)
            {
                _ = NativeMethods.RequestWindowClose(sentinelWindow);
                sentinelWindowClosed = await WaitForWindowClosedAsync(
                    sentinelWindow,
                    TimeSpan.FromSeconds(5));
            }

            TryTerminateIsolatedProcess(sentinelProcess);
            sentinelProcess?.Dispose();
            TryDeleteDirectory(testRoot);
            Assert.True(launchedWindowClosed, "The target Chrome integration window did not close.");
            Assert.True(sentinelWindowClosed, "The sentinel Chrome integration window did not close.");
        }
    }

    private static void InitializeProfile(string userDataDirectory, string profileDirectoryName)
    {
        var profileDirectory = Path.Combine(userDataDirectory, profileDirectoryName);
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "Preferences"), "{}");
    }

    private static ChromeWindowSnapshot CaptureWindow(
        IntPtr window,
        ChromeProfileIdentity targetIdentity) =>
        new(
            window,
            NativeMethods.IsWindowVisible(window),
            NativeMethods.IsIconic(window),
            NativeMethods.HasWindowMark(window, ChatGptWindowFinder.BackgroundWindowProperty),
            NativeMethods.HasWindowMark(window, targetIdentity.OwnershipPropertyName));

    private static void AssertWindowUnchanged(
        ChromeWindowSnapshot snapshot,
        ChromeProfileIdentity targetIdentity)
    {
        Assert.True(NativeMethods.IsWindow(snapshot.Handle));
        Assert.Equal(snapshot.WasVisible, NativeMethods.IsWindowVisible(snapshot.Handle));
        Assert.Equal(snapshot.WasMinimized, NativeMethods.IsIconic(snapshot.Handle));
        Assert.Equal(
            snapshot.HadGenericOwnershipMark,
            NativeMethods.HasWindowMark(snapshot.Handle, ChatGptWindowFinder.BackgroundWindowProperty));
        Assert.Equal(
            snapshot.HadTargetProfileOwnershipMark,
            NativeMethods.HasWindowMark(snapshot.Handle, targetIdentity.OwnershipPropertyName));
    }

    private static List<IntPtr> FindVisibleChromeWindows()
    {
        var windows = new List<IntPtr>();
        _ = NativeMethods.EnumWindows((window, _) =>
        {
            if (NativeMethods.IsWindowVisible(window) &&
                NativeMethods.GetWindowClass(window).Contains(
                    "Chrome_WidgetWin",
                    StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static async Task<IntPtr> WaitForCorrelatedWindowAsync(
        HashSet<IntPtr> existingWindows,
        string markerUrl,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var observations = FindVisibleChromeWindows()
                .Where(window => !existingWindows.Contains(window))
                .Select(window => new ChromeWindowUrlObservation(
                    window,
                    ChromeWindowUrlCorrelation.TryReadOmniboxUrl(window)));
            var match = ChromeWindowUrlCorrelation.FindExactMatch(observations, markerUrl);
            if (match != IntPtr.Zero)
            {
                return match;
            }

            await Task.Delay(100);
        }

        return IntPtr.Zero;
    }

    private static async Task<bool> WaitForWindowClosedAsync(IntPtr window, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (NativeMethods.IsWindow(window) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        return !NativeMethods.IsWindow(window);
    }

    private static void TryTerminateIsolatedProcess(System.Diagnostics.Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The isolated Chrome process already exited between the checks.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Window cleanup above is authoritative; never touch a real profile.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Chrome may finish releasing its isolated test profile just after
            // the window closes. The unique temp directory is safe to leave.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above; never touch a real user profile during cleanup.
        }
    }

    private sealed record ChromeWindowSnapshot(
        IntPtr Handle,
        bool WasVisible,
        bool WasMinimized,
        bool HadGenericOwnershipMark,
        bool HadTargetProfileOwnershipMark);
}

internal sealed class ChromeIntegrationFactAttribute : FactAttribute
{
    public ChromeIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ORHOM_RUN_CHROME_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set ORHOM_RUN_CHROME_INTEGRATION=1 to run the isolated real-Chrome test.";
        }
    }
}
