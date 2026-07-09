using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal sealed record FocusTarget(
    IntPtr WindowHandle,
    string WindowTitle,
    string WindowClass,
    AutomationElement? FocusedElement,
    bool AvoidAutomationElementFocus,
    bool IsPasswordField,
    IDataObject? OriginalClipboard);
