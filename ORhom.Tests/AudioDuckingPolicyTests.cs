namespace ORhom.Tests;

public sealed class AudioDuckingPolicyTests
{
    [Theory]
    [InlineData(0.1f, 0.1f, true)]
    [InlineData(0.1009f, 0.1f, true)]
    [InlineData(0.25f, 0.1f, false)]
    [InlineData(0f, 0.1f, false)]
    public void RestoreDoesNotOverwriteAUsersVolumeChange(
        float currentVolume,
        float appliedVolume,
        bool expected)
    {
        Assert.Equal(
            expected,
            AudioDuckingService.ShouldRestoreVolume(
                currentVolume,
                appliedVolume));
    }

    [Theory]
    [InlineData("endpoint", "instance", "endpoint|instance")]
    [InlineData(" endpoint ", " instance ", "endpoint|instance")]
    [InlineData("", "instance", null)]
    [InlineData("endpoint", "", null)]
    [InlineData("endpoint", "   ", null)]
    public void DuckingRequiresAUniqueSessionInstanceIdentifier(
        string endpointId,
        string sessionInstanceId,
        string? expected)
    {
        Assert.Equal(
            expected,
            AudioDuckingService.CreateSessionKey(
                endpointId,
                sessionInstanceId));
    }
}
