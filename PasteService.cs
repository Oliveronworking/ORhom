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

        var dispatched = false;
        try
        {
            if (!RestoreTargetFocus(target))
            {
                _logger.Info("Paste skipped because the original target focus could not be confirmed.");
                return false;
            }

            var focusedBeforePaste = AutomationHelpers.GetFocusedElement(_logger);
            if (settings.BlockPasswordFields && AutomationHelpers.IsPasswordElement(focusedBeforePaste))
            {
                _logger.Info("Paste skipped because current target is a password field.");
                return false;
            }

            Clipboard.SetText(text);
            Thread.Sleep(Math.Max(settings.PasteDelayMs, 0));
            if (NativeMethods.GetForegroundWindow() != target.WindowHandle)
            {
                _logger.Info("Paste skipped because the target lost foreground focus before shortcut dispatch.");
                return false;
            }

            dispatched = NativeMethods.SendPasteShortcut();
            var method = "SendInput";
            if (!dispatched)
            {
                SendKeys.SendWait("^v");
                dispatched = true;
                method = "SendKeysFallback";
            }

            _logger.Info($"Paste shortcut dispatched. Success={dispatched} Method={method} ForegroundVerified=True PreserveWebViewFocus={target.AvoidAutomationElementFocus} TextLength={text.Length}");
            Thread.Sleep(Math.Max(settings.RestoreClipboardDelayMs, 0));
            return dispatched;
        }
        catch (Exception ex)
        {
            _logger.Error("Paste failed.", ex);
            return false;
        }
        finally
        {
            ClipboardHelper.Restore(target.OriginalClipboard, settings, _logger);
        }
    }

    public void RestoreClipboard(FocusTarget? target, AppSettings settings)
    {
        if (target is not null)
        {
            ClipboardHelper.Restore(target.OriginalClipboard, settings, _logger);
        }
    }

    public bool RestoreTargetFocus(FocusTarget target)
    {
        if (target.WindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(target.WindowHandle))
        {
            _logger.Info("Target focus restore failed because the original window no longer exists.");
            return false;
        }

        if (!NativeMethods.ForceForegroundWindow(target.WindowHandle))
        {
            _logger.Info($"Target focus restore failed because foreground activation was not confirmed. TargetWindow=0x{target.WindowHandle.ToInt64():X} ActualForeground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            return false;
        }

        var elementFocusAttempted = false;
        var elementFocusSucceeded = false;
        if (!target.AvoidAutomationElementFocus && target.FocusedElement is not null)
        {
            elementFocusAttempted = true;
            elementFocusSucceeded = AutomationHelpers.TryFocusElement(target.FocusedElement, _logger);
        }
        else if (target.AvoidAutomationElementFocus)
        {
            _logger.Info("UI Automation focus restore skipped for WebView RootWebArea so the internal active element and caret are preserved.");
        }

        var focused = AutomationHelpers.GetFocusedElement(_logger);
        var focusedMetadata = AutomationHelpers.GetSafeFocusMetadata(focused);
        var focusedInsideTarget = AutomationHelpers.IsElementInWindow(focused, target.WindowHandle);
        var foregroundConfirmed = NativeMethods.GetForegroundWindow() == target.WindowHandle;
        var focusConfirmed = foregroundConfirmed &&
                             (target.AvoidAutomationElementFocus ||
                              target.FocusedElement is null ||
                              elementFocusSucceeded ||
                              focusedInsideTarget);
        _logger.Info($"Target focus restore completed. Success={focusConfirmed} ForegroundConfirmed={foregroundConfirmed} ElementFocusAttempted={elementFocusAttempted} ElementFocusSucceeded={elementFocusSucceeded} FocusedInsideTarget={focusedInsideTarget} FocusedControlType='{focusedMetadata.ControlType}' FocusedClass='{focusedMetadata.ClassName}' FocusedAutomationId='{focusedMetadata.AutomationId}'");
        return focusConfirmed;
    }
}
