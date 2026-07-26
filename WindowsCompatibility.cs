namespace ORhom;

internal static class WindowsCompatibility
{
    public const int MinimumMajorVersion = 10;
    public const int MinimumMinorVersion = 0;
    public const int MinimumBuild = 22000;

    public static bool IsCurrentVersionSupported() =>
        OperatingSystem.IsWindowsVersionAtLeast(
            MinimumMajorVersion,
            MinimumMinorVersion,
            MinimumBuild);

    internal static bool IsSupportedVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version.Major > MinimumMajorVersion ||
               version.Major == MinimumMajorVersion &&
               (version.Minor > MinimumMinorVersion ||
                version.Minor == MinimumMinorVersion &&
                version.Build >= MinimumBuild);
    }
}
