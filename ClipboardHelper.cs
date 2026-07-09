namespace ChatGptDictationBridge;

internal static class ClipboardHelper
{
    public static IDataObject? Capture(AppLogger logger)
    {
        try
        {
            return Clipboard.GetDataObject();
        }
        catch (Exception ex)
        {
            logger.Error("Clipboard capture failed.", ex);
            return null;
        }
    }

    public static void Restore(IDataObject? dataObject, AppSettings settings, AppLogger logger)
    {
        if (!settings.RestoreClipboard || dataObject is null)
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(dataObject, true);
            logger.Info("Clipboard restored.");
        }
        catch (Exception ex)
        {
            logger.Error("Clipboard restore failed.", ex);
        }
    }
}
