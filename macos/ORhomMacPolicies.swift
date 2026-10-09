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

enum DictationStartDisposition: Equatable {
    case automaticPaste
    case clipboard
    case rejectProtectedTarget
}

enum MacPastePolicy {
    private static let axSuccessRawValue: Int32 = 0
    private static let axNotImplementedRawValue: Int32 = -25208
    static let electronAccessibilityActivationDelay: TimeInterval = 3.2
    static let unobservableEditorPasteSettlementDelay: TimeInterval = 0.8
    static let shortcutSequence = PasteShortcutEvent.allCases

    static func dictationStartDisposition(
        targetCaptured: Bool,
        targetPotentiallyDeliverable: Bool,
        targetProtected: Bool
    ) -> DictationStartDisposition {
        if targetCaptured && targetProtected {
            return .rejectProtectedTarget
        }
        if targetCaptured && targetPotentiallyDeliverable {
            return .automaticPaste
        }
        return .clipboard
    }

    static func chromiumEnhancedAccessibilityRequestWasDelivered(
        resultRawValue: Int32
    ) -> Bool {
        resultRawValue == axSuccessRawValue ||
            resultRawValue == axNotImplementedRawValue
    }

    static func electronAccessibilityDelayRemaining(
        elapsed: TimeInterval,
        enhancedExposureChanged: Bool,
        focusedElementPresent: Bool
    ) -> TimeInterval {
        guard
            enhancedExposureChanged,
            !focusedElementPresent
        else {
            return 0
        }
        return max(
            0,
            electronAccessibilityActivationDelay - max(0, elapsed)
        )
    }

    static func shouldEnableTemporaryAccessibility(
        bundleIdentifier: String?,
        focusedEditableElementPresent: Bool,
        manualAttributeSettable: Bool,
        manualAttributeValue: Bool?,
        enhancedAttributeSettable: Bool,
        enhancedAttributeValue: Bool?,
        leaseAlreadyActive: Bool
    ) -> Bool {
        guard let bundleIdentifier else { return false }
        let supportedApplications: Set<String> = [
            "com.microsoft.VSCode",
            "com.microsoft.VSCodeInsiders",
            "com.cursor.Cursor",
            "com.todesktop.230313mzl4w4u92",
            "com.openai.chat",
            "com.openai.codex",
            "com.google.Chrome",
            "com.google.Chrome.beta",
            "com.google.Chrome.canary",
            "org.chromium.Chromium",
            "com.microsoft.edgemac",
            "com.brave.Browser"
        ]
        return supportedApplications.contains(bundleIdentifier) &&
            !focusedEditableElementPresent &&
            !leaseAlreadyActive &&
            manualAttributeValue != true &&
            enhancedAttributeValue != true &&
            (
                (
                    enhancedAttributeSettable &&
                    enhancedAttributeValue == false
                ) ||
                (
                    manualAttributeSettable &&
                    manualAttributeValue == false
                )
            )
    }

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
        secureInputActive: Bool,
        inputMonitorAvailable: Bool = true
    ) -> Bool {
        inputHistoryStable &&
            capturedWindowMatchesCurrentWindow &&
            knownEditorSurface &&
            inputMonitorAvailable &&
            !secureInputActive
    }

    static func mayReuseWebTarget(
        inputHistoryStable: Bool,
        exactEditableElementMatch: Bool
    ) -> Bool {
        inputHistoryStable || exactEditableElementMatch
    }

    static func mayReuseNonactivatingTarget(
        inputHistoryStable: Bool,
        exactFocusedEditableElementMatch: Bool,
        capturedWindowMatchesCurrentWindow: Bool
    ) -> Bool {
        inputHistoryStable ||
            (
                exactFocusedEditableElementMatch &&
                    capturedWindowMatchesCurrentWindow
            )
    }

    static func isPointerFreeChatGPTQuickChatWindow(
        bundleIdentifier: String?,
        windowRole: String?,
        windowSubrole: String?,
        windowLayer: Int
    ) -> Bool {
        bundleIdentifier == "com.openai.chat" &&
            windowRole == "AXWindow" &&
            windowSubrole == "AXSystemDialog" &&
            windowLayer > 0
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
