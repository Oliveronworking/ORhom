namespace ORhom.Tests;

public sealed class LocalModelControlPresentationTests
{
    [Fact]
    public void BrowserProviderHidesLocalModelControls()
    {
        var presentation = LocalModelControlPresentation.For(
            isLocalProvider: false,
            AppStatus.Idle,
            exitInProgress: false,
            settingsVisible: false,
            modelReady: true,
            preparationInProgress: false);

        Assert.False(presentation.Visible);
        Assert.False(presentation.CanReload);
        Assert.False(presentation.CanRelease);
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public void BusyUiDisablesModelMutations(
        int statusValue,
        bool exitInProgress,
        bool settingsVisible)
    {
        var presentation = LocalModelControlPresentation.For(
            isLocalProvider: true,
            (AppStatus)statusValue,
            exitInProgress,
            settingsVisible,
            modelReady: true,
            preparationInProgress: false);

        Assert.True(presentation.Visible);
        Assert.False(presentation.CanReload);
        Assert.False(presentation.CanRelease);
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    public void IdleLocalModeSeparatesReloadAndReleaseAvailability(
        bool modelReady,
        bool preparationInProgress,
        bool expectedReload,
        bool expectedRelease)
    {
        var presentation = LocalModelControlPresentation.For(
            isLocalProvider: true,
            AppStatus.Idle,
            exitInProgress: false,
            settingsVisible: false,
            modelReady,
            preparationInProgress);

        Assert.True(presentation.Visible);
        Assert.Equal(expectedReload, presentation.CanReload);
        Assert.Equal(expectedRelease, presentation.CanRelease);
    }
}
