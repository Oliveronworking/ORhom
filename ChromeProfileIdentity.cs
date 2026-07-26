using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace ORhom;

internal sealed record ChromeProfileIdentity
{
    private const string OwnershipPropertyPrefix = "ORhom.ChatGptBackgroundWindow";

    private ChromeProfileIdentity(string userDataDirectory, string profileDirectory)
    {
        UserDataDirectory = userDataDirectory;
        ProfileDirectory = profileDirectory;

        var identityBytes = Encoding.UTF8.GetBytes($"{userDataDirectory}\0{profileDirectory}");
        var hash = SHA256.HashData(identityBytes);
        OwnershipPropertyName = $"{OwnershipPropertyPrefix}.{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    public string UserDataDirectory { get; }

    public string ProfileDirectory { get; }

    public string OwnershipPropertyName { get; }

    public bool IsConfigured =>
        UserDataDirectory.Length > 0 &&
        ProfileDirectory.Length > 0;

    public static ChromeProfileIdentity From(AppSettings settings) =>
        Create(settings.ChromeUserDataDir, settings.ChromeProfileDirectory);

    public static ChromeProfileIdentity Create(string? userDataDirectory, string? profileDirectory) =>
        new(
            NormalizeUserDataDirectory(userDataDirectory),
            (profileDirectory ?? string.Empty).Trim().ToUpperInvariant());

    private static string NormalizeUserDataDirectory(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            IOException or
            NotSupportedException or
            PathTooLongException or
            SecurityException or
            UnauthorizedAccessException)
        {
            // Validation reports malformed paths elsewhere. Keeping a stable
            // normalized value here still prevents an unrelated profile from
            // sharing the same ownership marker.
        }

        return Path.TrimEndingDirectorySeparator(normalized).ToUpperInvariant();
    }
}
