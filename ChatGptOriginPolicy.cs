namespace ORhom;

internal static class ChatGptOriginPolicy
{
    public const string DefaultUrl = "https://chatgpt.com";

    private const string AllowedHost = "chatgpt.com";

    public static string NormalizeConfiguredUrl(string? configuredUrl)
    {
        var candidate = configuredUrl?.Trim();
        return TryCreateAllowedUri(candidate, allowElidedScheme: false, out _)
            ? candidate!
            : DefaultUrl;
    }

    public static bool IsAllowedObservedUrl(string? observedUrl) =>
        TryCreateAllowedUri(
            observedUrl?.Trim(),
            allowElidedScheme: true,
            out _);

    private static bool TryCreateAllowedUri(
        string? value,
        bool allowElidedScheme,
        out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (allowElidedScheme &&
            !value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0)
        {
            return false;
        }

        return uri.IdnHost.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase);
    }
}
