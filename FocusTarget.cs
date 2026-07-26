using System.Windows.Automation;

namespace ORhom;

internal sealed record FocusTarget(
    IntPtr WindowHandle,
    uint OwningProcessId,
    string WindowTitle,
    string WindowClass,
    AutomationElement? FocusedElement,
    SafeFocusMetadata FocusMetadata,
    string WebViewRootIdentity,
    SafeFocusLayoutFingerprint LayoutFingerprint,
    bool AvoidAutomationElementFocus,
    bool IsPasswordFieldOrUnverifiable);

internal readonly record struct SafeFocusLayoutFingerprint(
    double RelativeLeft,
    double RelativeTop,
    double RelativeWidth,
    double RelativeHeight)
{
    public static SafeFocusLayoutFingerprint Empty { get; }

    public bool IsValid =>
        double.IsFinite(RelativeLeft) &&
        double.IsFinite(RelativeTop) &&
        double.IsFinite(RelativeWidth) &&
        double.IsFinite(RelativeHeight) &&
        RelativeLeft >= -0.1 &&
        RelativeTop >= -0.1 &&
        RelativeWidth > 0.002 &&
        RelativeHeight > 0.002 &&
        RelativeLeft + RelativeWidth <= 1.1 &&
        RelativeTop + RelativeHeight <= 1.1;
}
