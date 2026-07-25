namespace ORhom.Tests;

public sealed class ChromeWindowUrlCorrelationTests
{
    private const string MarkerUrl =
        "https://orhom.invalid/microphone/84a7792c-80b3-46fb-8296-72c94f9dbb15";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://orhom.invalid/")]
    [InlineData("https://orhom.invalid/microphone/84a7792c-80b3-46fb-8296-72c94f9dbb15 ")]
    [InlineData("HTTPS://ORHOM.INVALID/MICROPHONE/84A7792C-80B3-46FB-8296-72C94F9DBB15")]
    [InlineData("prefix-https://orhom.invalid/microphone/84a7792c-80b3-46fb-8296-72c94f9dbb15")]
    public void MarkerUrlRequiresAnOrdinalExactMatch(string? observedUrl)
    {
        Assert.False(ChromeWindowUrlCorrelation.IsExactMatch(observedUrl, MarkerUrl));
    }

    [Fact]
    public void ExactMarkerUrlMatches()
    {
        Assert.True(ChromeWindowUrlCorrelation.IsExactMatch(MarkerUrl, MarkerUrl));
    }

    [Fact]
    public void ExactChromeOmniboxValueMatchesWhenOnlyHttpsSchemeIsElided()
    {
        Assert.True(ChromeWindowUrlCorrelation.IsExactMatch(
            "orhom.invalid/microphone/84a7792c-80b3-46fb-8296-72c94f9dbb15",
            MarkerUrl));
    }

    [Fact]
    public void CorrelationSkipsUnverifiedWindowsAndSelectsOnlyTheExactMarker()
    {
        var observations = new[]
        {
            new ChromeWindowUrlObservation(new IntPtr(101), "https://chatgpt.com/"),
            new ChromeWindowUrlObservation(new IntPtr(102), null),
            new ChromeWindowUrlObservation(new IntPtr(103), MarkerUrl),
            new ChromeWindowUrlObservation(new IntPtr(104), MarkerUrl + "-different")
        };

        Assert.Equal(new IntPtr(103), ChromeWindowUrlCorrelation.FindExactMatch(observations, MarkerUrl));
    }

    [Fact]
    public void CorrelationReturnsNoWindowWhenNoExactMarkerExists()
    {
        var observations = new[]
        {
            new ChromeWindowUrlObservation(new IntPtr(101), "https://chatgpt.com/"),
            new ChromeWindowUrlObservation(new IntPtr(102), MarkerUrl + "-different")
        };

        Assert.Equal(IntPtr.Zero, ChromeWindowUrlCorrelation.FindExactMatch(observations, MarkerUrl));
    }
}

public sealed class ChromeProfileLauncherArgumentTests
{
    [Fact]
    public void BackgroundMarkerContainsTheExactCorrelationGuid()
    {
        var correlationId = Guid.Parse("41934fdd-fe29-4751-a1c5-1047f33531e2");

        Assert.Equal(
            "https://orhom.invalid/background/41934fdd-fe29-4751-a1c5-1047f33531e2",
            ChatGptWindowFinder.CreateBackgroundLaunchMarkerUrl(correlationId));
    }

    [Fact]
    public void EachBackgroundLaunchUsesANewMarker()
    {
        var first = ChatGptWindowFinder.CreateBackgroundLaunchMarkerUrl();
        var second = ChatGptWindowFinder.CreateBackgroundLaunchMarkerUrl();

        Assert.NotEqual(first, second);
        Assert.StartsWith("https://orhom.invalid/background/", first, StringComparison.Ordinal);
    }

    [Fact]
    public void MicrophoneMarkerContainsTheExactCorrelationGuid()
    {
        var correlationId = Guid.Parse("84a7792c-80b3-46fb-8296-72c94f9dbb15");

        Assert.Equal(
            "https://orhom.invalid/microphone/84a7792c-80b3-46fb-8296-72c94f9dbb15",
            ChromeProfileLauncher.CreateMicrophoneMarkerUrl(correlationId));
    }

    [Fact]
    public void EachMicrophoneLaunchMarkerIsUniqueAndContainsAValidGuid()
    {
        const string prefix = "https://orhom.invalid/microphone/";

        var first = ChromeProfileLauncher.CreateMicrophoneMarkerUrl();
        var second = ChromeProfileLauncher.CreateMicrophoneMarkerUrl();

        Assert.NotEqual(first, second);
        Assert.StartsWith(prefix, first, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(first[prefix.Length..], "D", out _));
    }

    [Fact]
    public void VisibleChromeLaunchUsesSeparateArgumentsWithoutStartMinimized()
    {
        var startInfo = ChromeProfileLauncher.CreateChromeStartInfo(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Users\Test User\Chrome Data",
            "Profile 7",
            "https://chatgpt.com/#orhom-window-correlation",
            startMinimized: false);

        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            new[]
            {
                @"--user-data-dir=C:\Users\Test User\Chrome Data",
                "--profile-directory=Profile 7",
                "--new-window",
                "--no-first-run",
                "--no-default-browser-check",
                "https://chatgpt.com/#orhom-window-correlation"
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void BackgroundChromeLaunchAddsStartMinimized()
    {
        var startInfo = ChromeProfileLauncher.CreateChromeStartInfo(
            "chrome.exe",
            "User Data",
            "Default",
            "https://chatgpt.com/",
            startMinimized: true);

        Assert.Contains("--start-minimized", startInfo.ArgumentList);
        Assert.Equal("https://chatgpt.com/", startInfo.ArgumentList[^1]);
    }
}
