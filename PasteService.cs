namespace ChatGptDictationBridge;

internal sealed class PasteService
{
    private readonly AppLogger _logger;

    public PasteService(AppLogger logger)
    {
        _logger = logger;
    }

    public bool PasteIntoTarget(string text, FocusTarget target, AppSettings settings)
    {
        if (target.IsPasswordField)
        {
            _logger.Info("Paste skipped because captured target is a password field.");
            return false;
        }

        try
        {
            RestoreTargetFocus(target);
            if (settings.BlockPasswordFields && AutomationHelpers.IsPasswordElement(AutomationHelpers.GetFocusedElement(_logger)))
            {
                _logger.Info("Paste skipped because current target is a password field.");
                return false;
            }

            Clipboard.SetText(text);
            Thread.Sleep(Math.Max(settings.PasteDelayMs, 0));
            SendKeys.SendWait("^v");
            _logger.Info($"Paste successful. TextLength={text.Length}");
            Thread.Sleep(Math.Max(settings.RestoreClipboardDelayMs, 0));
            ClipboardHelper.Restore(target.OriginalClipboard, settings, _logger);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Paste failed.", ex);
            ClipboardHelper.Restore(target.OriginalClipboard, settings, _logger);
            return false;
        }
    }

    public void RestoreClipboard(FocusTarget? target, AppSettings settings)
    {
        if (target is not null)
        {
            ClipboardHelper.Restore(target.OriginalClipboard, settings, _logger);
        }
    }

    private void RestoreTargetFocus(FocusTarget target)
    {
        if (target.WindowHandle != IntPtr.Zero &&
            NativeMethods.IsWindow(target.WindowHandle) &&
            NativeMethods.GetForegroundWindow() != target.WindowHandle)
        {
            NativeMethods.SetForegroundWindow(target.WindowHandle);
            Thread.Sleep(120);
        }

        _ = AutomationHelpers.TryFocusElement(target.FocusedElement, _logger);
    }
}
