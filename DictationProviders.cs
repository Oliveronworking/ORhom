namespace ORhom;

internal static class DictationProviders
{
    public const string LocalWhisper = "LocalWhisper";
    public const string ChatGptBrowser = "ChatGptBrowser";

    public static bool IsLocal(string? provider) =>
        !string.Equals(provider, ChatGptBrowser, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? provider) =>
        string.Equals(provider, ChatGptBrowser, StringComparison.OrdinalIgnoreCase)
            ? ChatGptBrowser
            : LocalWhisper;
}

internal sealed record DictationProviderOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
