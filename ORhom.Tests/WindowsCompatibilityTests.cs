namespace ORhom.Tests;

public sealed class WindowsCompatibilityTests
{
    [Theory]
    [InlineData(9, 9, 99999, false)]
    [InlineData(10, 0, 21999, false)]
    [InlineData(10, 0, 22000, true)]
    [InlineData(10, 0, 26100, true)]
    [InlineData(10, 1, 0, true)]
    [InlineData(11, 0, 0, true)]
    public void SupportStartsAtTheFirstWindows11Build(
        int major,
        int minor,
        int build,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibility.IsSupportedVersion(
                new Version(major, minor, build)));
    }
}
