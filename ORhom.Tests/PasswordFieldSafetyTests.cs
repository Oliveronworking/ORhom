namespace ORhom.Tests;

public sealed class PasswordFieldSafetyTests
{
    [Fact]
    public void PropertyReadFailureIsUnknownAndBlockedWhenProtectionIsEnabled()
    {
        var state = AutomationHelpers.ProbePasswordField(
            () => throw new InvalidOperationException("UI Automation provider failed."));

        Assert.Equal(PasswordFieldState.Unknown, state);
        Assert.True(AutomationHelpers.ShouldBlockPasswordField(
            state,
            blockPasswordFields: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void BooleanPropertyValuesAreClassifiedAndEnforced(
        bool propertyValue,
        bool expectedBlocked)
    {
        var state = AutomationHelpers.ProbePasswordField(() => propertyValue);
        var expectedState = propertyValue
            ? PasswordFieldState.Password
            : PasswordFieldState.NotPassword;

        Assert.Equal(expectedState, state);
        Assert.Equal(
            expectedBlocked,
            AutomationHelpers.ShouldBlockPasswordField(
                state,
                blockPasswordFields: true));
    }

    [Fact]
    public void UnsupportedPropertyValueIsUnknownAndBlocked()
    {
        var state = AutomationHelpers.ProbePasswordField(() => new object());

        Assert.Equal(PasswordFieldState.Unknown, state);
        Assert.True(AutomationHelpers.ShouldBlockPasswordField(
            state,
            blockPasswordFields: true));
    }

    [Fact]
    public void MissingFocusedElementIsBlockedWhenProtectionIsEnabled()
    {
        Assert.True(AutomationHelpers.ShouldBlockPasswordField(
            element: null,
            blockPasswordFields: true));
    }

    [Fact]
    public void ProtectionCanStillBeExplicitlyDisabled()
    {
        Assert.False(AutomationHelpers.ShouldBlockPasswordField(
            PasswordFieldState.Unknown,
            blockPasswordFields: false));
        Assert.False(AutomationHelpers.ShouldBlockPasswordField(
            PasswordFieldState.Password,
            blockPasswordFields: false));
        Assert.False(AutomationHelpers.ShouldBlockPasswordField(
            element: null,
            blockPasswordFields: false));
    }
}
