namespace ORhom.Tests;

public sealed class UiStatePresentationTests
{
    [Theory]
    [InlineData(
        0, // Idle
        "Bereit",
        "ORhom – Bereit",
        "Diktieren",
        "F8 drücken, um zu diktieren",
        true,
        false,
        false)]
    [InlineData(
        1, // Starting
        "Wird gestartet",
        "ORhom – Wird gestartet",
        "Stoppen",
        "F8 drücken, um direkt wieder zu stoppen",
        true,
        false,
        true)]
    [InlineData(
        2, // Recording
        "Hört zu",
        "ORhom – Hört zu",
        "Aufnahme stoppen",
        "F8 zum Stoppen · Esc zum Verwerfen",
        true,
        true,
        false)]
    [InlineData(
        3, // Stopping
        "Aufnahme wird beendet",
        "ORhom – Aufnahme wird beendet",
        "Verarbeitung abbrechen",
        "F8 oder Esc zum Abbrechen",
        true,
        true,
        true)]
    [InlineData(
        4, // ReadingText
        "Wird transkribiert",
        "ORhom – Wird transkribiert",
        "Verarbeitung abbrechen",
        "F8 oder Esc zum Abbrechen",
        true,
        true,
        true)]
    [InlineData(
        5, // Pasting
        "Wird eingefügt",
        "ORhom – Wird eingefügt",
        "Bitte warten",
        "Der Text erscheint gleich im zuletzt aktiven Feld",
        false,
        false,
        true)]
    public void ForReturnsStableGermanPresentationForEveryStatus(
        int status,
        string visibleStatus,
        string trayTooltip,
        string primaryAction,
        string hint,
        bool canToggle,
        bool canAbort,
        bool isBusy)
    {
        var presentation = UiStatePresentation.For((AppStatus)status, "F8");

        Assert.Equal(visibleStatus, presentation.VisibleStatus);
        Assert.Equal(trayTooltip, presentation.TrayTooltip);
        Assert.Equal(primaryAction, presentation.PrimaryAction);
        Assert.Equal(hint, presentation.Hint);
        Assert.Equal(canToggle, presentation.CanToggle);
        Assert.Equal(canAbort, presentation.CanAbort);
        Assert.Equal(isBusy, presentation.IsBusy);
    }

    [Theory]
    [InlineData(0)] // Idle
    [InlineData(1)] // Starting
    [InlineData(2)] // Recording
    [InlineData(3)] // Stopping
    [InlineData(4)] // ReadingText
    public void ActionableStateHintUsesCurrentHotkey(int status)
    {
        var presentation = UiStatePresentation.For(
            (AppStatus)status,
            "Strg + Alt + D");

        Assert.Contains("Strg + Alt + D", presentation.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("F8", presentation.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void HotkeyIsTrimmedBeforeItIsShown()
    {
        var presentation = UiStatePresentation.For(AppStatus.Idle, "  F9  ");

        Assert.Equal("F9 drücken, um zu diktieren", presentation.Hint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingHotkeyIsRejected(string? hotkey)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => UiStatePresentation.For(AppStatus.Idle, hotkey!));
    }

    [Fact]
    public void UnknownStatusIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UiStatePresentation.For((AppStatus)int.MaxValue, "F8"));
    }

    [Fact]
    public void EveryDeclaredStatusHasAPresentation()
    {
        foreach (var status in Enum.GetValues<AppStatus>())
        {
            var presentation = UiStatePresentation.For(status, "F8");

            Assert.False(string.IsNullOrWhiteSpace(presentation.VisibleStatus));
            Assert.False(string.IsNullOrWhiteSpace(presentation.TrayTooltip));
            Assert.False(string.IsNullOrWhiteSpace(presentation.PrimaryAction));
            Assert.False(string.IsNullOrWhiteSpace(presentation.Hint));
        }
    }
}
