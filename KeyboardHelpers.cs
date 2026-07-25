namespace ORhom;

internal static class KeyboardHelpers
{
    public static void SendHotkey(string hotkey)
    {
        SendKeys.SendWait(ToSendKeys(hotkey));
    }

    private static string ToSendKeys(string hotkey)
    {
        var result = string.Empty;
        foreach (var rawPart in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                result += "^";
            }
            else if (rawPart.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                result += "+";
            }
            else if (rawPart.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                result += "%";
            }
            else if (rawPart.Length == 1)
            {
                result += rawPart.ToLowerInvariant();
            }
            else
            {
                result += "{" + rawPart.ToUpperInvariant() + "}";
            }
        }

        return result;
    }
}
