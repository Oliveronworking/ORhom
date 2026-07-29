import Foundation

enum PasteShortcutEvent: CaseIterable, Equatable {
    case commandDown
    case pasteKeyDown
    case pasteKeyUp
    case commandUp
}

enum KeyboardPasteDelivery: Equatable {
    case activeSession
    case targetProcess
}

enum MacPastePolicy {
    static let shortcutSequence = PasteShortcutEvent.allCases

    static func keyboardPasteDelivery(
        usesNonactivatingWindow: Bool
    ) -> KeyboardPasteDelivery {
        usesNonactivatingWindow ? .targetProcess : .activeSession
    }

    static func shouldRetainMappedAXWindow(
        focusedWindowPresent: Bool,
        mappedWindowPresent: Bool,
        focusedElementEditable: Bool,
        elementReportsFocused: Bool,
        windowReportsFocused: Bool
    ) -> Bool {
        focusedWindowPresent &&
            mappedWindowPresent &&
            focusedElementEditable &&
            elementReportsFocused &&
            windowReportsFocused
    }

    static func shouldPreferCurrentFocusedWindow(
        capturedWindowMatchesCurrentFocusedWindow: Bool,
        currentElementEditable: Bool,
        elementReportsFocused: Bool,
        windowReportsFocused: Bool
    ) -> Bool {
        capturedWindowMatchesCurrentFocusedWindow &&
            currentElementEditable &&
            elementReportsFocused &&
            windowReportsFocused
    }

    static func mayUseWindowOnlyTarget(
        inputHistoryStable: Bool,
        capturedWindowMatchesCurrentWindow: Bool,
        knownEditorSurface: Bool,
        secureInputActive: Bool
    ) -> Bool {
        inputHistoryStable &&
            capturedWindowMatchesCurrentWindow &&
            knownEditorSurface &&
            !secureInputActive
    }

    static func mayReuseWebTarget(
        inputHistoryStable: Bool,
        exactEditableElementMatch: Bool
    ) -> Bool {
        inputHistoryStable || exactEditableElementMatch
    }

    static func mayUseNonactivatingFocusedElement(
        exactApplicationFocus: Bool,
        elementReportsFocused: Bool,
        supportsSelectedText: Bool,
        isSecure: Bool
    ) -> Bool {
        exactApplicationFocus &&
            elementReportsFocused &&
            supportsSelectedText &&
            !isSecure
    }

    static func windowTitlesAreCompatible(
        axTitle: String,
        cgTitle: String,
        applicationName: String?
    ) -> Bool {
        guard !axTitle.isEmpty, !cgTitle.isEmpty else {
            return false
        }
        if axTitle == cgTitle {
            return true
        }
        guard let applicationName, !applicationName.isEmpty else {
            return false
        }
        return axTitle == cgTitle + " - " + applicationName
    }

    static func mayPreferTopmostFrameMatch(
        usesNonactivatingWindow: Bool,
        authoritativeFocusedElement: Bool,
        focusedElementEditable: Bool,
        elementReportsFocused: Bool
    ) -> Bool {
        !usesNonactivatingWindow &&
            authoritativeFocusedElement &&
            focusedElementEditable &&
            elementReportsFocused
    }

    static func shouldUseKeyboardPaste(
        isKnownWebViewBundle: Bool,
        focusedRole: String?,
        hasWebAreaAncestor: Bool,
        focusedElementMissing: Bool
    ) -> Bool {
        if hasWebAreaAncestor || focusedRole == "AXWebArea" {
            return true
        }
        guard isKnownWebViewBundle else {
            return false
        }
        return focusedElementMissing ||
            focusedRole == "AXGroup" ||
            focusedRole == "AXScrollArea" ||
            focusedRole == "AXUnknown"
    }
}

enum AudioDuckingPolicy {
    static func factor(for percent: Int) -> Float {
        Float(min(100, max(0, percent))) / 100
    }

    static func targetVolume(original: Float, percent: Int) -> Float {
        min(1, max(0, original)) * factor(for: percent)
    }

    static func shouldRestore(
        current: Float,
        ducked: Float,
        tolerance: Float = 0.02
    ) -> Bool {
        abs(current - ducked) <= max(0, tolerance)
    }
}
