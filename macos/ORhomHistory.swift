import AppKit

final class SelectableHistoryTextView: NSTextView {
    var copyPasteboard = NSPasteboard.general

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection(
            .deviceIndependentFlagsMask
        )
        if modifiers == .command,
           event.charactersIgnoringModifiers?.lowercased() == "c" {
            let selection = selectedRange()
            guard selection.length > 0 else {
                NSSound.beep()
                return true
            }
            let copiedText = (string as NSString).substring(with: selection)
            copyPasteboard.clearContents()
            _ = copyPasteboard.setString(copiedText, forType: .string)
            return true
        }
        return super.performKeyEquivalent(with: event)
    }
}
