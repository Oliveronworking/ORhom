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
        try testPastePolicy()
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
        print("ORhom macOS tests passed (17 test groups).")
    }
}
