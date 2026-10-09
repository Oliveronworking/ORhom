import AppKit
import Carbon
import CoreAudio
import Foundation

private enum TestFailure: Error {
    case failed(String)
}

private func expect(
    _ condition: @autoclosure () -> Bool,
    _ message: String
) throws {
    if !condition() {
        throw TestFailure.failed(message)
    }
}

private final class FakeAudioVolumeBackend: MacAudioVolumeBackend {
    var defaultDeviceID: AudioDeviceID? = 1
    var volumes: [AudioDeviceID: Float] = [1: 0.8]
    var uids: [AudioDeviceID: String] = [1: "device-one"]
    var writes: [(AudioDeviceID, Float)] = []

    func defaultOutputDeviceID() -> AudioDeviceID? {
        defaultDeviceID
    }

    func deviceUID(for deviceID: AudioDeviceID) -> String? {
        uids[deviceID]
    }

    func volume(for deviceID: AudioDeviceID) -> Float? {
        volumes[deviceID]
    }

    func setVolume(_ volume: Float, for deviceID: AudioDeviceID) -> Bool {
        guard volumes[deviceID] != nil else { return false }
        volumes[deviceID] = volume
        writes.append((deviceID, volume))
        return true
    }
}

private func makeDefaults() -> UserDefaults {
    let name = "at.orhom.tests.\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: name)!
    defaults.removePersistentDomain(forName: name)
    return defaults
}

private func testPastePolicy() throws {
    try expect(
        MacPastePolicy.dictationStartDisposition(
            targetCaptured: false,
            targetPotentiallyDeliverable: false,
            targetProtected: false
        ) == .clipboard,
        "Dictation without a detected target must record for the clipboard."
    )
    try expect(
        MacPastePolicy.dictationStartDisposition(
            targetCaptured: true,
            targetPotentiallyDeliverable: false,
            targetProtected: false
        ) == .clipboard,
        "Dictation without a writable target must record for the clipboard."
    )
    try expect(
        MacPastePolicy.dictationStartDisposition(
            targetCaptured: true,
            targetPotentiallyDeliverable: true,
            targetProtected: false
        ) == .automaticPaste,
        "A verified writable target must retain automatic paste."
    )
    try expect(
        MacPastePolicy.dictationStartDisposition(
            targetCaptured: true,
            targetPotentiallyDeliverable: true,
            targetProtected: true
        ) == .rejectProtectedTarget,
        "A protected target must be rejected before recording."
    )
    try expect(
        MacPastePolicy
            .chromiumEnhancedAccessibilityRequestWasDelivered(
                resultRawValue: -25208
            ),
        "Chromium handles AXEnhancedUserInterface before AppKit reports notImplemented, so the request must be balanced."
    )
    try expect(
        !MacPastePolicy
            .chromiumEnhancedAccessibilityRequestWasDelivered(
                resultRawValue: -25204
            ),
        "A genuinely incomplete accessibility request must not be treated as delivered."
    )
    try expect(
        MacPastePolicy.electronAccessibilityDelayRemaining(
            elapsed: 0.2,
            enhancedExposureChanged: true,
            focusedElementPresent: false
        ) > 2.9,
        "A newly enabled Electron accessibility tree must receive its macOS activation delay."
    )
    try expect(
        MacPastePolicy.electronAccessibilityDelayRemaining(
            elapsed: 4,
            enhancedExposureChanged: true,
            focusedElementPresent: false
        ) == 0,
        "An already activated Electron accessibility tree must not delay insertion."
    )
    try expect(
        MacPastePolicy.electronAccessibilityDelayRemaining(
            elapsed: 0,
            enhancedExposureChanged: true,
            focusedElementPresent: true
        ) == 0,
        "An exact focused element must not be delayed."
    )
    try expect(
        MacPastePolicy.electronAccessibilityDelayRemaining(
            elapsed: 0,
            enhancedExposureChanged: false,
            focusedElementPresent: false
        ) == 0,
        "ORhom must not delay when it did not change enhanced accessibility."
    )
    try expect(
        MacPastePolicy.unobservableEditorPasteSettlementDelay >= 0.75,
        "An unobservable editor must retain the temporary clipboard long enough for a delayed Electron event loop to consume the paste."
    )
    try expect(
        MacPastePolicy.shouldEnableTemporaryAccessibility(
            bundleIdentifier: "com.microsoft.VSCode",
            focusedEditableElementPresent: false,
            manualAttributeSettable: true,
            manualAttributeValue: false,
            enhancedAttributeSettable: true,
            enhancedAttributeValue: false,
            leaseAlreadyActive: false
        ),
        "VS Code must expose its editor accessibility tree temporarily when no focused element is available."
    )
    try expect(
        MacPastePolicy.shouldEnableTemporaryAccessibility(
            bundleIdentifier: "com.google.Chrome",
            focusedEditableElementPresent: false,
            manualAttributeSettable: false,
            manualAttributeValue: nil,
            enhancedAttributeSettable: true,
            enhancedAttributeValue: false,
            leaseAlreadyActive: false
        ),
        "Chrome must use its enhanced accessibility attribute even when manual accessibility is unavailable."
    )
    try expect(
        !MacPastePolicy.shouldEnableTemporaryAccessibility(
            bundleIdentifier: "com.microsoft.VSCode",
            focusedEditableElementPresent: true,
            manualAttributeSettable: true,
            manualAttributeValue: false,
            enhancedAttributeSettable: true,
            enhancedAttributeValue: false,
            leaseAlreadyActive: false
        ),
        "An already visible VS Code editor must not have its accessibility mode changed."
    )
    try expect(
        !MacPastePolicy.shouldEnableTemporaryAccessibility(
            bundleIdentifier: "com.microsoft.VSCode",
            focusedEditableElementPresent: false,
            manualAttributeSettable: true,
            manualAttributeValue: true,
            enhancedAttributeSettable: true,
            enhancedAttributeValue: false,
            leaseAlreadyActive: false
        ),
        "A user-enabled accessibility mode must never be leased or later disabled by ORhom."
    )
    try expect(
        !MacPastePolicy.shouldEnableTemporaryAccessibility(
            bundleIdentifier: "com.apple.TextEdit",
            focusedEditableElementPresent: false,
            manualAttributeSettable: true,
            manualAttributeValue: false,
            enhancedAttributeSettable: true,
            enhancedAttributeValue: false,
            leaseAlreadyActive: false
        ),
        "ORhom must not change AXManualAccessibility in arbitrary native applications."
    )
    try expect(
        MacPastePolicy.shortcutSequence == [
            .commandDown,
            .pasteKeyDown,
            .pasteKeyUp,
            .commandUp
        ],
        "Paste shortcut must contain explicit Command key events."
    )
    try expect(
        MacPastePolicy.shouldUseKeyboardPaste(
            isKnownWebViewBundle: true,
            focusedRole: "AXTextArea",
            hasWebAreaAncestor: true,
            focusedElementMissing: false
        ),
        "A Chrome text area below AXWebArea must use keyboard paste."
    )
    try expect(
        !MacPastePolicy.shouldUseKeyboardPaste(
            isKnownWebViewBundle: true,
            focusedRole: "AXTextField",
            hasWebAreaAncestor: false,
            focusedElementMissing: false
        ),
        "A browser address field must remain eligible for direct AX insertion."
    )
    try expect(
        !MacPastePolicy.shouldUseKeyboardPaste(
            isKnownWebViewBundle: false,
            focusedRole: "AXTextArea",
            hasWebAreaAncestor: false,
            focusedElementMissing: false
        ),
        "A native text area must remain eligible for direct AX insertion."
    )
    try expect(
        !MacPastePolicy.shouldUseKeyboardPaste(
            isKnownWebViewBundle: false,
            focusedRole: nil,
            hasWebAreaAncestor: false,
            focusedElementMissing: true
        ),
        "An unknown app with no exposed focused element must fail closed."
    )
    try expect(
        MacPastePolicy.shouldUseKeyboardPaste(
            isKnownWebViewBundle: true,
            focusedRole: nil,
            hasWebAreaAncestor: false,
            focusedElementMissing: true
        ),
        "A known editor app may use its exact stable window when no focused element is exposed."
    )
    try expect(
        MacPastePolicy.keyboardPasteDelivery(
            usesNonactivatingWindow: false
        ) == .activeSession,
        "A regular foreground app must receive the same session-level paste as a physical Command-V."
    )
    try expect(
        MacPastePolicy.keyboardPasteDelivery(
            usesNonactivatingWindow: true
        ) == .targetProcess,
        "A nonactivating panel must never send a global paste into the underlying app."
    )
    try expect(
        MacPastePolicy.shouldRetainMappedAXWindow(
            focusedWindowPresent: true,
            mappedWindowPresent: true,
            focusedElementEditable: true,
            elementReportsFocused: true,
            windowReportsFocused: true
        ),
        "A visible AX-focused window must win over an auxiliary topmost CG window."
    )
    try expect(
        !MacPastePolicy.shouldRetainMappedAXWindow(
            focusedWindowPresent: true,
            mappedWindowPresent: true,
            focusedElementEditable: false,
            elementReportsFocused: true,
            windowReportsFocused: true
        ),
        "A mapped AX window without a proven editor must not override the topmost window."
    )
    try expect(
        MacPastePolicy.shouldPreferCurrentFocusedWindow(
            capturedWindowMatchesCurrentFocusedWindow: true,
            currentElementEditable: true,
            elementReportsFocused: true,
            windowReportsFocused: true
        ),
        "An unchanged captured AX-focused window must remain authoritative during paste."
    )
    try expect(
        !MacPastePolicy.shouldPreferCurrentFocusedWindow(
            capturedWindowMatchesCurrentFocusedWindow: true,
            currentElementEditable: true,
            elementReportsFocused: true,
            windowReportsFocused: false
        ),
        "A non-key AX window must not redirect a session-level paste behind the topmost window."
    )
    try expect(
        MacPastePolicy.mayUseWindowOnlyTarget(
            inputHistoryStable: true,
            capturedWindowMatchesCurrentWindow: true,
            knownEditorSurface: true,
            secureInputActive: false
        ),
        "A stable exact window-only target may paste only on a known editor surface."
    )
    try expect(
        !MacPastePolicy.mayUseWindowOnlyTarget(
            inputHistoryStable: false,
            capturedWindowMatchesCurrentWindow: true,
            knownEditorSurface: true,
            secureInputActive: false
        ),
        "Intervening input must invalidate an ambiguous window-only target."
    )
    try expect(
        !MacPastePolicy.mayUseWindowOnlyTarget(
            inputHistoryStable: true,
            capturedWindowMatchesCurrentWindow: true,
            knownEditorSurface: false,
            secureInputActive: false
        ),
        "An unknown window-only surface must fail closed."
    )
    try expect(
        !MacPastePolicy.mayUseWindowOnlyTarget(
            inputHistoryStable: true,
            capturedWindowMatchesCurrentWindow: true,
            knownEditorSurface: true,
            secureInputActive: true,
            inputMonitorAvailable: true
        ),
        "Secure input must block a window-only paste."
    )
    try expect(
        !MacPastePolicy.mayUseWindowOnlyTarget(
            inputHistoryStable: true,
            capturedWindowMatchesCurrentWindow: true,
            knownEditorSurface: true,
            secureInputActive: false,
            inputMonitorAvailable: false
        ),
        "A missing global input monitor must invalidate a window-only paste target."
    )
    try expect(
        MacPastePolicy.mayReuseWebTarget(
            inputHistoryStable: true,
            exactEditableElementMatch: false
        ),
        "An unchanged input history must keep the captured web target."
    )
    try expect(
        MacPastePolicy.mayReuseWebTarget(
            inputHistoryStable: false,
            exactEditableElementMatch: true
        ),
        "Intervening input must not invalidate the exact editable web target."
    )
    try expect(
        !MacPastePolicy.mayReuseWebTarget(
            inputHistoryStable: false,
            exactEditableElementMatch: false
        ),
        "Intervening input must still reject a changed web target."
    )
    try expect(
        MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: true,
            exactFocusedEditableElementMatch: true,
            capturedWindowMatchesCurrentWindow: true
        ),
        "Stable input history must retain an exact nonactivating target."
    )
    try expect(
        MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: true,
            exactFocusedEditableElementMatch: true,
            capturedWindowMatchesCurrentWindow: false
        ),
        "Stable input history must retain a nonactivating target before exact revalidation."
    )
    try expect(
        MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: true,
            exactFocusedEditableElementMatch: false,
            capturedWindowMatchesCurrentWindow: true
        ),
        "Stable input history must retain a nonactivating target when only its window matches."
    )
    try expect(
        MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: true,
            exactFocusedEditableElementMatch: false,
            capturedWindowMatchesCurrentWindow: false
        ),
        "Stable input history must retain a nonactivating target for subsequent focus validation."
    )
    try expect(
        MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: false,
            exactFocusedEditableElementMatch: true,
            capturedWindowMatchesCurrentWindow: true
        ),
        "Intervening input must retain an exact editable element in the exact captured window."
    )
    try expect(
        !MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: false,
            exactFocusedEditableElementMatch: false,
            capturedWindowMatchesCurrentWindow: true
        ),
        "Intervening input must reject a changed element even when the window still matches."
    )
    try expect(
        !MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: false,
            exactFocusedEditableElementMatch: true,
            capturedWindowMatchesCurrentWindow: false
        ),
        "Intervening input must reject an exact element outside the captured window."
    )
    try expect(
        !MacPastePolicy.mayReuseNonactivatingTarget(
            inputHistoryStable: false,
            exactFocusedEditableElementMatch: false,
            capturedWindowMatchesCurrentWindow: false
        ),
        "Intervening input must reject a changed nonactivating target."
    )
    try expect(
        MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: "AXWindow",
            windowSubrole: "AXSystemDialog",
            windowLayer: 8
        ),
        "The elevated ChatGPT Quick Chat system dialog must support pointer-free discovery."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: "AXWindow",
            windowSubrole: "AXSystemDialog",
            windowLayer: -1
        ),
        "A ChatGPT dialog behind normal windows must not win pointer-free discovery."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.codex",
            windowRole: "AXWindow",
            windowSubrole: "AXSystemDialog",
            windowLayer: 8
        ),
        "Another OpenAI app must not be mistaken for ChatGPT Quick Chat."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: nil,
            windowRole: "AXWindow",
            windowSubrole: "AXSystemDialog",
            windowLayer: 8
        ),
        "A window without a bundle identifier must fail pointer-free Quick Chat recognition."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: "AXDialog",
            windowSubrole: "AXSystemDialog",
            windowLayer: 8
        ),
        "A ChatGPT window with the wrong accessibility role must fail recognition."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: nil,
            windowSubrole: "AXSystemDialog",
            windowLayer: 8
        ),
        "A ChatGPT window without an accessibility role must fail recognition."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: "AXWindow",
            windowSubrole: "AXDialog",
            windowLayer: 8
        ),
        "A regular ChatGPT dialog must not be mistaken for Quick Chat."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: "AXWindow",
            windowSubrole: nil,
            windowLayer: 8
        ),
        "A ChatGPT window without an accessibility subrole must fail recognition."
    )
    try expect(
        !MacPastePolicy.isPointerFreeChatGPTQuickChatWindow(
            bundleIdentifier: "com.openai.chat",
            windowRole: "AXWindow",
            windowSubrole: "AXSystemDialog",
            windowLayer: 0
        ),
        "A normal-layer ChatGPT system dialog must not be treated as nonactivating Quick Chat."
    )
    try expect(
        MacPastePolicy.mayUseNonactivatingFocusedElement(
            exactApplicationFocus: true,
            elementReportsFocused: true,
            supportsSelectedText: true,
            isSecure: false
        ),
        "An exact writable nonactivating editor must be accepted."
    )
    try expect(
        !MacPastePolicy.mayUseNonactivatingFocusedElement(
            exactApplicationFocus: false,
            elementReportsFocused: true,
            supportsSelectedText: true,
            isSecure: false
        ),
        "A stale app-local focus must not override the foreground app."
    )
    try expect(
        !MacPastePolicy.mayUseNonactivatingFocusedElement(
            exactApplicationFocus: true,
            elementReportsFocused: true,
            supportsSelectedText: false,
            isSecure: false
        ),
        "A non-writable floating element must not become the paste target."
    )
    try expect(
        !MacPastePolicy.mayUseNonactivatingFocusedElement(
            exactApplicationFocus: true,
            elementReportsFocused: true,
            supportsSelectedText: true,
            isSecure: true
        ),
        "A secure floating text field must never become the paste target."
    )
    try expect(
        MacPastePolicy.windowTitlesAreCompatible(
            axTitle: "Draft - Google Chrome",
            cgTitle: "Draft",
            applicationName: "Google Chrome"
        ),
        "A browser AX title may contain the app suffix omitted by Core Graphics."
    )
    try expect(
        MacPastePolicy.windowTitlesAreCompatible(
            axTitle: "Draft",
            cgTitle: "Draft",
            applicationName: "Google Chrome"
        ),
        "Identical AX and Core Graphics window titles must match."
    )
    try expect(
        !MacPastePolicy.windowTitlesAreCompatible(
            axTitle: "GitHub - Pull Request - Google Chrome",
            cgTitle: "GitHub",
            applicationName: "Google Chrome"
        ),
        "A partial document-title prefix must not match another browser window."
    )
    try expect(
        !MacPastePolicy.windowTitlesAreCompatible(
            axTitle: "Draft",
            cgTitle: "Draft - Unrelated",
            applicationName: "Google Chrome"
        ),
        "A Core Graphics title with an unrelated suffix must not match."
    )
    try expect(
        MacPastePolicy.mayPreferTopmostFrameMatch(
            usesNonactivatingWindow: false,
            authoritativeFocusedElement: true,
            focusedElementEditable: true,
            elementReportsFocused: true
        ),
        "A system-wide focused editor may resolve an otherwise ambiguous topmost frame."
    )
    try expect(
        !MacPastePolicy.mayPreferTopmostFrameMatch(
            usesNonactivatingWindow: true,
            authoritativeFocusedElement: true,
            focusedElementEditable: true,
            elementReportsFocused: true
        ),
        "A nonactivating panel must not use the topmost-frame fallback."
    )
    try expect(
        !MacPastePolicy.mayPreferTopmostFrameMatch(
            usesNonactivatingWindow: false,
            authoritativeFocusedElement: false,
            focusedElementEditable: true,
            elementReportsFocused: true
        ),
        "App-local stale focus must not use the topmost-frame fallback."
    )
    try expect(
        !MacPastePolicy.mayPreferTopmostFrameMatch(
            usesNonactivatingWindow: false,
            authoritativeFocusedElement: true,
            focusedElementEditable: false,
            elementReportsFocused: true
        ),
        "A non-editor element must not use the topmost-frame fallback."
    )
    try expect(
        !MacPastePolicy.mayPreferTopmostFrameMatch(
            usesNonactivatingWindow: false,
            authoritativeFocusedElement: true,
            focusedElementEditable: true,
            elementReportsFocused: false
        ),
        "An element without focused state must not use the topmost-frame fallback."
    )
}

private func testPasteboardWriter() throws {
    let pasteboard = NSPasteboard.withUniqueName()
    defer { pasteboard.releaseGlobally() }

    pasteboard.clearContents()
    try expect(
        pasteboard.setString(
            "vorheriger Inhalt",
            forType: .string
        ),
        "The private pasteboard sentinel must be writable."
    )
    let expectedInitialChangeCount = pasteboard.changeCount
    var lastOwnedChangeCount: Int?
    let writtenChangeCount = MacPasteboardWriter.writeText(
        "Erkanntes Diktat",
        to: pasteboard,
        expectedInitialChangeCount: expectedInitialChangeCount,
        lastOwnedChangeCount: &lastOwnedChangeCount,
        delay: { _ in }
    )
    try expect(
        writtenChangeCount != nil &&
            writtenChangeCount == lastOwnedChangeCount,
        "The fallback writer must report ownership of a successful write."
    )
    try expect(
        pasteboard.string(forType: .string) == "Erkanntes Diktat",
        "The recognized dictation must replace the previous clipboard text."
    )

    let staleChangeCount = pasteboard.changeCount
    pasteboard.clearContents()
    try expect(
        pasteboard.setString(
            "neuer externer Inhalt",
            forType: .string
        ),
        "The simulated external clipboard update must succeed."
    )
    var staleOwnedChangeCount: Int?
    let rejectedWrite = MacPasteboardWriter.writeText(
        "Darf externen Inhalt nicht überschreiben",
        to: pasteboard,
        expectedInitialChangeCount: staleChangeCount,
        lastOwnedChangeCount: &staleOwnedChangeCount,
        delay: { _ in }
    )
    try expect(
        rejectedWrite == nil && staleOwnedChangeCount == nil,
        "A stale clipboard owner must not be allowed to write."
    )
    try expect(
        pasteboard.string(forType: .string) ==
            "neuer externer Inhalt",
        "A newer external clipboard value must remain untouched."
    )
}

private func testHistoryCommandCopy() throws {
    let textView = SelectableHistoryTextView(
        frame: NSRect(x: 0, y: 0, width: 300, height: 120)
    )
    textView.string = "Erstes Diktat\nZweites Diktat"
    textView.setSelectedRange(
        (textView.string as NSString).range(of: "Zweites Diktat")
    )
    let pasteboard = NSPasteboard.withUniqueName()
    textView.copyPasteboard = pasteboard
    guard let event = NSEvent.keyEvent(
        with: .keyDown,
        location: .zero,
        modifierFlags: .command,
        timestamp: 0,
        windowNumber: 0,
        context: nil,
        characters: "c",
        charactersIgnoringModifiers: "c",
        isARepeat: false,
        keyCode: UInt16(kVK_ANSI_C)
    ) else {
        throw TestFailure.failed("A Command-C test event could not be created.")
    }
    try expect(
        textView.performKeyEquivalent(with: event),
        "The history view must handle Command-C."
    )
    try expect(
        pasteboard.string(forType: .string) == "Zweites Diktat",
        "Command-C must copy the selected history text."
    )
}

private func testAudioDuckingRestores() throws {
    let backend = FakeAudioVolumeBackend()
    let defaults = makeDefaults()
    let service = MacAudioDuckingService(
        backend: backend,
        defaults: defaults,
        log: { _ in }
    )
    service.begin()
    try expect(
        abs((backend.volumes[1] ?? -1) - 0.08) < 0.0001,
        "Ducking must apply ten percent of the original volume."
    )
    service.begin()
    try expect(
        backend.writes.count == 1,
        "Repeated begin must not duck twice."
    )
    service.restore()
    try expect(
        abs((backend.volumes[1] ?? -1) - 0.8) < 0.0001,
        "Restore must reinstate the original volume."
    )
    service.restore()
    try expect(
        backend.writes.count == 2,
        "Repeated restore must be harmless."
    )
}

private func testAudioDuckingPreservesUserChange() throws {
    let backend = FakeAudioVolumeBackend()
    let service = MacAudioDuckingService(
        backend: backend,
        defaults: makeDefaults(),
        log: { _ in }
    )
    service.begin()
    backend.volumes[1] = 0.35
    service.restore()
    try expect(
        abs((backend.volumes[1] ?? -1) - 0.35) < 0.0001,
        "Restore must not overwrite a manual volume change."
    )
}

private func testAudioDuckingFollowsOutputDevice() throws {
    let backend = FakeAudioVolumeBackend()
    backend.volumes[2] = 0.6
    backend.uids[2] = "device-two"
    let service = MacAudioDuckingService(
        backend: backend,
        defaults: makeDefaults(),
        log: { _ in }
    )
    service.begin()
    backend.defaultDeviceID = 2
    service.refresh()
    try expect(
        abs((backend.volumes[1] ?? -1) - 0.8) < 0.0001,
        "Changing output must restore the previous device."
    )
    try expect(
        abs((backend.volumes[2] ?? -1) - 0.06) < 0.0001,
        "Changing output must duck the new device."
    )
    service.restore()
    try expect(
        abs((backend.volumes[2] ?? -1) - 0.6) < 0.0001,
        "The new output device must be restored."
    )
}

private func testAudioDuckingDisabled() throws {
    let backend = FakeAudioVolumeBackend()
    let defaults = makeDefaults()
    defaults.set(false, forKey: MacAudioDuckingService.enabledDefaultsKey)
    let service = MacAudioDuckingService(
        backend: backend,
        defaults: defaults,
        log: { _ in }
    )
    service.begin()
    try expect(
        backend.writes.isEmpty,
        "Disabled ducking must not touch the output volume."
    )
}

private func testAudioDuckingRecoversInterruptedSession() throws {
    let backend = FakeAudioVolumeBackend()
    let defaults = makeDefaults()
    let interruptedService = MacAudioDuckingService(
        backend: backend,
        defaults: defaults,
        log: { _ in }
    )
    interruptedService.begin()
    try expect(
        abs((backend.volumes[1] ?? -1) - 0.08) < 0.0001,
        "The interrupted session setup must be ducked."
    )
    let recoveredService = MacAudioDuckingService(
        backend: backend,
        defaults: defaults,
        log: { _ in }
    )
    recoveredService.recoverInterruptedSession()
    try expect(
        abs((backend.volumes[1] ?? -1) - 0.8) < 0.0001,
        "A matching interrupted session must restore on next launch."
    )
}

private func testAudioDuckingHardwareRoundTrip() throws {
    let backend = CoreAudioVolumeBackend()
    guard
        let deviceID = backend.defaultOutputDeviceID(),
        let original = backend.volume(for: deviceID)
    else {
        throw TestFailure.failed(
            "The default output device does not expose a writable virtual main volume."
        )
    }
    let defaults = makeDefaults()
    defaults.set(
        90,
        forKey: MacAudioDuckingService.volumePercentDefaultsKey
    )
    let service = MacAudioDuckingService(
        backend: backend,
        defaults: defaults,
        log: { _ in }
    )
    service.begin()
    defer { service.restore() }
    guard let ducked = backend.volume(for: deviceID) else {
        throw TestFailure.failed(
            "The output volume could not be read after ducking."
        )
    }
    if original > 0.02 {
        try expect(
            ducked < original,
            "The hardware output volume must be lower while ducking."
        )
    }
    service.restore()
    guard let restored = backend.volume(for: deviceID) else {
        throw TestFailure.failed(
            "The output volume could not be read after restore."
        )
    }
    try expect(
        abs(restored - original) <= 0.02,
        "The hardware output volume must be restored."
    )
    print(
        "CoreAudio round trip passed (original \(original), ducked \(ducked), restored \(restored))."
    )
}

@main
private enum ORhomMacTests {
    static func main() throws {
        try runORhomAudioProcessingTests()
        try runORhomDictationTextTests()
        var gesture = HybridHotKeyGesture()
        try expect(gesture.press(at: 1, startsDictation: true), "Initial press must toggle")
        try expect(!gesture.press(at: 1.1, startsDictation: true), "Key repeat must not toggle")
        try expect(!gesture.release(at: 1.2, enabled: true), "Quick release keeps hands-free recording")
        try expect(gesture.press(at: 2, startsDictation: true), "Next press starts a fresh gesture")
        try expect(gesture.release(at: 2.5, enabled: true), "Held release must request stop")
        try expect(!gesture.release(at: 2.6, enabled: true), "Duplicate release must not stop again")
        _ = gesture.press(at: 3, startsDictation: false)
        try expect(!gesture.release(at: 4, enabled: true), "A stop press must not stop twice")
        _ = gesture.press(at: 5, startsDictation: true)
        try expect(!gesture.release(at: 6, enabled: false), "Disabled push-to-talk stays toggle only")
        _ = gesture.press(at: 7, startsDictation: true)
        gesture.reset()
        try expect(!gesture.release(at: 8, enabled: true), "Cancelled gesture cannot stop a later recording")
        try testPastePolicy()
        try testPasteboardWriter()
        try testHistoryCommandCopy()
        try testAudioDuckingRestores()
        try testAudioDuckingPreservesUserChange()
        try testAudioDuckingFollowsOutputDevice()
        try testAudioDuckingDisabled()
        try testAudioDuckingRecoversInterruptedSession()
        if ProcessInfo.processInfo.environment[
            "ORHOM_RUN_AUDIO_HARDWARE_TEST"
        ] == "1" {
            try testAudioDuckingHardwareRoundTrip()
        }
        print("ORhom macOS tests passed.")
    }
}
