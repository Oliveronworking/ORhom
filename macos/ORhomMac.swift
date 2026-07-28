import AppKit
import ApplicationServices
import AVFoundation
import Carbon
import Darwin
import ServiceManagement

private enum RuntimeState {
    case preparing
    case idle
    case starting
    case recording
    case transcribing
    case pasting
    case error
}

private struct PasteTarget {
    let processIdentifier: pid_t
    let bundleIdentifier: String?
    let applicationLaunchDate: Date?
    let usesNonactivatingWindow: Bool
    let underlyingProcessIdentifier: pid_t?
    let underlyingApplicationLaunchDate: Date?
    let focusedWindow: AXUIElement?
    let windowNumber: CGWindowID?
    let windowTitle: String?
    let windowFrame: CGRect?
    let focusedElement: AXUIElement?
    let role: String?
    let subrole: String?
    let identifier: String?
    let elementTitle: String?
    let layoutFingerprint: AXLayoutFingerprint?
    let webAreaRoot: AXUIElement?
    let webAreaFingerprint: AXLayoutFingerprint?
    let preserveWebViewCaret: Bool
}

private struct PasteCaptureSource {
    let application: NSRunningApplication
    let focusedElement: AXUIElement?
    let usesNonactivatingWindow: Bool
    let underlyingProcessIdentifier: pid_t?
    let underlyingApplicationLaunchDate: Date?
}

private struct CGWindowDescriptor {
    let number: CGWindowID
    let title: String?
    let frame: CGRect?
    let layer: Int
}

private struct AXLayoutFingerprint {
    let relativeX: CGFloat
    let relativeY: CGFloat
    let relativeWidth: CGFloat
    let relativeHeight: CGFloat

    func isSimilar(to other: AXLayoutFingerprint) -> Bool {
        abs(relativeX - other.relativeX) <= 0.025 &&
            abs(relativeY - other.relativeY) <= 0.025 &&
            abs(relativeWidth - other.relativeWidth) <= 0.035 &&
            abs(relativeHeight - other.relativeHeight) <= 0.035
    }
}

private struct PasteboardSnapshot {
    let changeCount: Int
    let items: [[NSPasteboard.PasteboardType: Data]]
}

private struct PasteEffectObservation {
    let valueLength: Int?
    let valueHash: Int?
    let selectedRangeLocation: Int?
    let selectedRangeLength: Int?
    let selectedMarkerHash: CFHashCode?

    func differs(from other: PasteEffectObservation) -> Bool {
        let valueChanged =
            valueLength != nil &&
            other.valueLength != nil &&
            (
                valueLength != other.valueLength ||
                valueHash != other.valueHash
            )
        let rangeChanged =
            selectedRangeLocation != nil &&
            other.selectedRangeLocation != nil &&
            (
                selectedRangeLocation != other.selectedRangeLocation ||
                selectedRangeLength != other.selectedRangeLength
            )
        let markerChanged =
            selectedMarkerHash != nil &&
            other.selectedMarkerHash != nil &&
            selectedMarkerHash != other.selectedMarkerHash
        return valueChanged || rangeChanged || markerChanged
    }
}

private enum PasteFocusProbe {
    case match(element: AXUIElement?, kind: String)
    case transient(reason: String)
    case unsafe(reason: String)
}

private let hotKeySignature: OSType = 0x4F52484D // ORHM

private func globalHotKeyHandler(
    _ nextHandler: EventHandlerCallRef?,
    _ event: EventRef?,
    _ userData: UnsafeMutableRawPointer?
) -> OSStatus {
    guard let userData else { return OSStatus(eventNotHandledErr) }
    let delegate = Unmanaged<AppDelegate>.fromOpaque(userData).takeUnretainedValue()
    DispatchQueue.main.async {
        delegate.toggleDictation()
    }
    return noErr
}

private final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = AppDataStore()
    private let whisper = LocalWhisperEngine()
    private let recorder = MicrophoneRecorder()
    private lazy var audioDucking = MacAudioDuckingService(
        log: store.log
    )
    private let audioStopQueue = DispatchQueue(
        label: "at.orhom.audio-stop",
        qos: .userInitiated
    )
    private let overlayModel = OverlayViewModel()
    private let settingsModel = SettingsViewModel()
    private lazy var modelManager = WhisperModelManager(store: store)
    private lazy var overlay = OverlayPanelController(model: overlayModel)
    private lazy var settingsWindow = SettingsWindowController(model: settingsModel)

    private var state: RuntimeState = .preparing
    private var engineReady = false
    private var pasteTarget: PasteTarget?
    private var recordingStart: Date?
    private var lastAudioDuckingRefresh: Date?
    private var recordingTimer: Timer?
    private var statusItem: NSStatusItem!
    private var statusMenuItem: NSMenuItem!
    private var primaryMenuItem: NSMenuItem!
    private var cancelMenuItem: NSMenuItem!
    private var lastDictationMenuItem: NSMenuItem!
    private var overlayMenuItem: NSMenuItem!
    private var loginMenuItem: NSMenuItem!
    private var hotKeyHandlerRef: EventHandlerRef?
    private var hotKeyRef: EventHotKeyRef?
    private var activeHotKey = HotKeyConfiguration.load()
    private var activeHotKeyIsTemporaryFallback = false
    private var nextHotKeyID: UInt32 = 1
    private var hotKeyTemporarilySuspended = false
    private var permissionRefreshTimer: Timer?
    private var didPromptForAutomaticPasteThisRun = false
    private var didPromptForEventPostingThisRun = false
    private let inputActivityLock = NSLock()
    private var inputActivityGeneration: UInt64 = 0
    private var pasteTargetInputGeneration: UInt64 = 0
    private var lastPointerInputLocation: CGPoint?
    private var globalInputMonitor: Any?
    private var diagnosticPasteboardSnapshot: PasteboardSnapshot?
    private var diagnosticText: String?

    private var showOverlay: Bool {
        get {
            if UserDefaults.standard.object(forKey: "showOverlay") == nil {
                return true
            }
            return UserDefaults.standard.bool(forKey: "showOverlay")
        }
        set {
            UserDefaults.standard.set(newValue, forKey: "showOverlay")
        }
    }

    private var overlaySizePreset: OverlaySizePreset {
        get {
            guard
                let rawValue = UserDefaults.standard.string(
                    forKey: "recordingOverlaySize"
                ),
                let preset = OverlaySizePreset(rawValue: rawValue)
            else {
                return .defaultValue
            }
            return preset
        }
        set {
            UserDefaults.standard.set(
                newValue.rawValue,
                forKey: "recordingOverlaySize"
            )
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard enforceSingleInstance() else { return }
        let version = Bundle.main.object(
            forInfoDictionaryKey: "CFBundleShortVersionString"
        ) as? String ?? "unknown"
        store.log("Application started. Version=\(version) Engine=whisper.cpp")

        NSApp.setActivationPolicy(.accessory)
        configureViewModels()
        audioDucking.recoverInterruptedSession()
        createStatusItem()
        registerGlobalHotKeys()
        installInputActivityMonitor()
        refreshPermissionState()
        let accessibilityAllowed = AXIsProcessTrusted()
        let eventPostingAllowed = CGPreflightPostEventAccess()
        store.log(
            "Configuration ready. Microphone='\(recorder.microphoneName)' MicrophoneID='\(recorder.selectedMicrophoneUniqueID ?? "none")' Hotkey='\(activeHotKey.displayName)' MicrophonePermission=\(settingsModel.microphoneAllowed) AccessibilityPermission=\(accessibilityAllowed) EventPostingPermission=\(eventPostingAllowed)."
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(captureDevicesChanged),
            name: AVCaptureDevice.wasConnectedNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(captureDevicesChanged),
            name: AVCaptureDevice.wasDisconnectedNotification,
            object: nil
        )

        if showOverlay {
            overlay.show()
        }

        if CommandLine.arguments.contains("--diagnose-paste") {
            engineReady = true
            settingsModel.engineReady = true
            setIdle(
                message: "Einfügetest",
                detail: "Browser-Ziel wird geprüft"
            )
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                [weak self] in
                self?.runPasteDiagnostic()
            }
        } else if CommandLine.arguments.contains("--diagnose-microphone") {
            runMicrophoneDiagnostic()
        } else {
            prepareLocalEngine()
        }

        if !UserDefaults.standard.bool(forKey: "welcomeShownV2") {
            UserDefaults.standard.set(true, forKey: "welcomeShownV2")
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.55) { [weak self] in
                self?.openSettings()
            }
        }
        requestAutomaticPastePermissionOnFirstRun()
    }

    func applicationWillTerminate(_ notification: Notification) {
        recordingTimer?.invalidate()
        permissionRefreshTimer?.invalidate()
        NotificationCenter.default.removeObserver(self)
        audioDucking.restore()
        audioStopQueue.sync {
            recorder.stopAndDiscard()
        }
        if let hotKeyRef {
            UnregisterEventHotKey(hotKeyRef)
        }
        if let hotKeyHandlerRef {
            RemoveEventHandler(hotKeyHandlerRef)
        }
        if let globalInputMonitor {
            NSEvent.removeMonitor(globalInputMonitor)
        }
        store.log("Application stopped.")

        // The prebuilt whisper.cpp 1.9.1 framework currently aborts in its
        // process-global Metal residency-set destructor on macOS 26. All app
        // state is already saved and the log is closed here, so exit without
        // running that faulty C++ destructor. macOS reclaims its Metal memory.
        Darwin._exit(EXIT_SUCCESS)
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        refreshPermissionState()
    }

    func menuWillOpen(_ menu: NSMenu) {
        refreshPermissionState()
        refreshMenu()
    }

    @objc func toggleDictation() {
        guard !hotKeyTemporarilySuspended else {
            NSSound.beep()
            return
        }
        switch state {
        case .idle:
            beginDictation()
        case .recording:
            finishDictation()
        case .error:
            if engineReady {
                beginDictation()
            } else {
                prepareLocalEngine()
            }
        case .preparing, .starting, .transcribing, .pasting:
            NSSound.beep()
        }
    }

    private func installInputActivityMonitor() {
        globalInputMonitor = NSEvent.addGlobalMonitorForEvents(
            matching: [
                .leftMouseDown,
                .rightMouseDown,
                .otherMouseDown,
                .keyDown
            ]
        ) { [weak self] event in
            guard
                let self,
                !self.isConfiguredHotKeyEvent(event)
            else {
                return
            }
            self.inputActivityLock.lock()
            self.inputActivityGeneration &+= 1
            if event.type == .leftMouseDown ||
                event.type == .rightMouseDown ||
                event.type == .otherMouseDown {
                self.lastPointerInputLocation =
                    event.cgEvent?.location
            }
            self.inputActivityLock.unlock()
        }
    }

    private func isConfiguredHotKeyEvent(_ event: NSEvent) -> Bool {
        guard
            event.type == .keyDown,
            UInt32(event.keyCode) == activeHotKey.keyCode
        else {
            return false
        }
        let modifiers = event.modifierFlags.intersection(
            .deviceIndependentFlagsMask
        )
        var carbonModifiers: UInt32 = 0
        if modifiers.contains(.command) {
            carbonModifiers |= UInt32(cmdKey)
        }
        if modifiers.contains(.option) {
            carbonModifiers |= UInt32(optionKey)
        }
        if modifiers.contains(.control) {
            carbonModifiers |= UInt32(controlKey)
        }
        if modifiers.contains(.shift) {
            carbonModifiers |= UInt32(shiftKey)
        }
        return carbonModifiers == activeHotKey.modifiers
    }

    private func currentInputActivityGeneration() -> UInt64 {
        inputActivityLock.lock()
        defer { inputActivityLock.unlock() }
        return inputActivityGeneration
    }

    private func currentPointerInputLocation() -> CGPoint? {
        let environment = ProcessInfo.processInfo.environment
        if let xText = environment[
            "ORHOM_DIAGNOSTIC_POINTER_X"
        ],
           let yText = environment[
               "ORHOM_DIAGNOSTIC_POINTER_Y"
           ],
           let x = Double(xText),
           let y = Double(yText),
           x.isFinite,
           y.isFinite {
            return CGPoint(x: x, y: y)
        }
        inputActivityLock.lock()
        let lastLocation = lastPointerInputLocation
        inputActivityLock.unlock()
        return lastLocation
    }

    private func ambiguousTargetInputStayedStable() -> Bool {
        currentInputActivityGeneration() == pasteTargetInputGeneration
    }

    private func configureViewModels() {
        let savedOverlaySize = overlaySizePreset
        overlayModel.sizePreset = savedOverlaySize
        overlayModel.onPrimary = { [weak self] in
            self?.toggleDictation()
        }
        overlayModel.onCancel = { [weak self] in
            self?.cancelDictation()
        }
        recorder.onCaptureFailure = { [weak self] error in
            guard let self, self.state == .recording else { return }
            self.store.log(
                "Microphone capture interrupted. Error='\(error.localizedDescription)'."
            )
            if let appError = error as? ORhomError {
                self.presentError(appError)
            } else {
                self.presentError(
                    ORhomError.audioInputInterrupted(error.localizedDescription)
                )
            }
        }

        settingsModel.showOverlay = showOverlay
        settingsModel.overlaySize = savedOverlaySize
        settingsModel.audioDuckingEnabled = audioDucking.isEnabled
        settingsModel.audioDuckingVolumePercent =
            audioDucking.volumePercent
        settingsModel.launchAtLogin = SMAppService.mainApp.status == .enabled
        settingsModel.hotKey = activeHotKey
        refreshMicrophoneSettings()
        settingsModel.onRequestPermissions = { [weak self] in
            self?.requestPermissions()
        }
        settingsModel.onOpenMicrophonePrivacy = { [weak self] in
            self?.openPrivacyPane("Privacy_Microphone")
        }
        settingsModel.onOpenAccessibilityPrivacy = { [weak self] in
            self?.openPrivacyPane("Privacy_Accessibility")
            self?.startPermissionRefreshTimer()
        }
        settingsModel.onOpenEventPostingPrivacy = { [weak self] in
            self?.promptForEventPostingPermission()
            self?.openPrivacyPane("Privacy_Accessibility")
            self?.startPermissionRefreshTimer()
        }
        settingsModel.onMicrophoneChanged = { [weak self] uniqueID in
            self?.selectMicrophone(uniqueID: uniqueID)
        }
        settingsModel.onHotKeyChanged = { [weak self] configuration in
            self?.applyHotKey(configuration)
        }
        settingsModel.onHotKeyCaptureError = { [weak self] message in
            self?.settingsModel.errorMessage = message
        }
        settingsModel.onHotKeyRecordingChanged = { [weak self] recording in
            self?.setHotKeyTemporarilySuspended(recording)
        }
        settingsModel.onOverlayChanged = { [weak self] enabled in
            self?.setOverlayEnabled(enabled)
        }
        settingsModel.onOverlaySizeChanged = { [weak self] preset in
            self?.setOverlaySize(preset)
        }
        settingsModel.onAudioDuckingEnabledChanged = { [weak self] enabled in
            self?.setAudioDuckingEnabled(enabled)
        }
        settingsModel.onAudioDuckingVolumeChanged = { [weak self] percent in
            self?.setAudioDuckingVolumePercent(percent)
        }
        settingsModel.onLoginChanged = { [weak self] enabled in
            self?.setLaunchAtLogin(enabled)
        }
        settingsModel.onDone = { [weak self] in
            self?.settingsWindow.closeToMenuBar()
        }
    }

    private func prepareLocalEngine() {
        state = .preparing
        engineReady = false
        settingsModel.engineReady = false
        settingsModel.engineProgress = 0
        settingsModel.errorMessage = nil
        setOverlay(
            mode: .preparing,
            title: "Lokale Engine wird vorbereitet",
            detail: "Whisper · Apple Silicon",
            progress: 0
        )
        refreshMenu()

        modelManager.prepare(
            progress: { [weak self] fraction, message in
                guard let self else { return }
                self.settingsModel.engineDetail = message
                self.settingsModel.engineProgress = fraction
                self.setOverlay(
                    mode: .preparing,
                    title: fraction > 0.98 ? "Whisper wird gestartet" : "Lokales Modell",
                    detail: message,
                    progress: fraction
                )
            },
            completion: { [weak self] result in
                guard let self else { return }
                switch result {
                case .success(let modelURL):
                    self.settingsModel.engineDetail = "Modell wird in Metal geladen"
                    self.settingsModel.engineProgress = nil
                    self.setOverlay(
                        mode: .preparing,
                        title: "Whisper wird gestartet",
                        detail: "Metal-Beschleunigung wird aktiviert"
                    )
                    self.whisper.loadModel(at: modelURL) { [weak self] loadResult in
                        guard let self else { return }
                        switch loadResult {
                        case .success(let systemInfo):
                            self.engineReady = true
                            self.settingsModel.engineReady = true
                            self.settingsModel.engineProgress = nil
                            self.settingsModel.engineDetail = "Apple Silicon · Metal · vollständig lokal"
                            self.store.log("Whisper ready. \(systemInfo)")
                            self.setIdle()
                        case .failure(let error):
                            self.presentError(error)
                        }
                    }
                case .failure(let error):
                    self.presentError(error)
                }
            }
        )
    }

    private func beginDictation() {
        guard engineReady else {
            prepareLocalEngine()
            return
        }

        guard let capturedTarget = captureCurrentPasteTarget() else {
            settingsModel.errorMessage =
                "Bitte zuerst den Cursor in das gewünschte Textfeld setzen."
            setIdle(
                message: "Kein Ziel ausgewählt",
                detail: "Zuerst in ein Textfeld klicken"
            )
            NSSound.beep()
            return
        }
        pasteTarget = capturedTarget
        pasteTargetInputGeneration = currentInputActivityGeneration()
        state = .starting
        setOverlay(
            mode: .preparing,
            title: "Mikrofon wird aktiviert",
            detail: recorder.microphoneName
        )
        refreshMenu()

        recorder.requestPermission { [weak self] allowed in
            guard let self, self.state == .starting else { return }
            self.refreshPermissionState()
            guard allowed else {
                self.presentError(ORhomError.microphonePermission, openSettings: true)
                return
            }

            do {
                try self.recorder.start()
                self.audioDucking.begin()
                self.lastAudioDuckingRefresh = Date()
                self.state = .recording
                self.recordingStart = Date()
                self.overlayModel.mode = .recording
                self.overlayModel.title = "Aufnahme"
                self.overlayModel.detail = self.recorder.microphoneName
                self.overlayModel.elapsed = "00:00"
                self.overlayModel.progress = nil
                self.startRecordingTimer()
                self.refreshMenu()
                self.store.log("Dictation started. Engine=whisper.cpp Metal=true Microphone='\(self.recorder.microphoneName)' \(self.recorder.diagnostics)")
                self.reprobePasteTargetAfterCaptureStarted()
            } catch {
                self.presentError(error)
            }
        }
    }

    private func finishDictation() {
        guard state == .recording else { return }
        recordingTimer?.invalidate()
        recordingTimer = nil
        audioDucking.restore()
        lastAudioDuckingRefresh = nil
        state = .transcribing
        setOverlay(
            mode: .transcribing,
            title: "Text wird erkannt",
            detail: "Whisper Large V3 Turbo · Metal"
        )
        refreshMenu()

        audioStopQueue.async { [weak self] in
            guard let self else { return }
            let stopResult: Result<([Float], String), Error>
            do {
                let samples = try self.recorder.stop()
                stopResult = .success((samples, self.recorder.diagnostics))
            } catch {
                stopResult = .failure(error)
            }

            DispatchQueue.main.async { [weak self] in
                guard let self, self.state == .transcribing else { return }
                switch stopResult {
                case .success(let (samples, diagnostics)):
                    self.store.log(
                        "Recording stopped. \(diagnostics) Samples=\(samples.count) DurationSeconds=\(String(format: "%.2f", Double(samples.count) / 16_000))"
                    )
                    self.transcribeStoppedRecording(samples)
                case .failure(let error):
                    self.store.log(
                        "Recording stop failed. \(self.recorder.diagnostics) Error='\(error.localizedDescription)'"
                    )
                    self.presentError(error)
                }
            }
        }
    }

    private func transcribeStoppedRecording(_ samples: [Float]) {
        whisper.transcribe(samples: samples) { [weak self] result in
            guard let self, self.state == .transcribing else { return }
            switch result {
            case .success(let text):
                self.store.add(text: text, outcome: "Erfolgreich")
                self.store.log("Dictation completed. TextLength=\(text.count)")
                let target = self.pasteTarget
                self.state = .pasting
                self.setOverlay(
                    mode: .transcribing,
                    title: "Diktat erkannt",
                    detail: "Text wird eingefügt"
                )
                self.refreshMenu()
                self.paste(text, into: target)
            case .failure(let error):
                self.presentError(error)
            }
        }
    }

    @objc private func cancelDictation() {
        guard state == .recording || state == .starting else { return }
        recordingTimer?.invalidate()
        recordingTimer = nil
        audioDucking.restore()
        lastAudioDuckingRefresh = nil
        recorder.stopAndDiscard()
        store.log("Dictation cancelled.")
        setIdle(message: "Aufnahme verworfen", detail: "Bereit für ein neues Diktat")
    }

    private func startRecordingTimer() {
        recordingTimer?.invalidate()
        let timer = Timer(timeInterval: 0.10, repeats: true) { [weak self] _ in
            guard
                let self,
                self.state == .recording,
                let start = self.recordingStart
            else { return }
            let elapsed = Date().timeIntervalSince(start)
            let totalSeconds = Int(elapsed)
            self.overlayModel.elapsed = String(
                format: "%02d:%02d",
                totalSeconds / 60,
                totalSeconds % 60
            )
            self.overlayModel.level = self.recorder.level
            if self.lastAudioDuckingRefresh.map({
                Date().timeIntervalSince($0) >= 0.5
            }) != false {
                self.audioDucking.refresh()
                self.lastAudioDuckingRefresh = Date()
            }
            if elapsed >= 300 {
                self.finishDictation()
            }
        }
        recordingTimer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    private func runMicrophoneDiagnostic() {
        store.log("Microphone diagnostic requested.")
        state = .starting
        setOverlay(
            mode: .preparing,
            title: "Mikrofontest",
            detail: "Drei Sekunden · es wird nichts gespeichert"
        )
        recorder.requestPermission { [weak self] allowed in
            guard let self else { return }
            guard allowed else {
                self.store.log("Microphone diagnostic failed. Permission=false")
                NSApp.terminate(nil)
                return
            }
            do {
                try self.recorder.start()
                self.state = .recording
                self.setOverlay(
                    mode: .recording,
                    title: "Mikrofontest läuft",
                    detail: self.recorder.microphoneName
                )
                DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
                    guard let self else { return }
                    do {
                        let samples = try self.recorder.stop()
                        self.store.log(
                            "Microphone diagnostic succeeded. \(self.recorder.diagnostics) ConvertedSamples=\(samples.count)"
                        )
                    } catch {
                        self.store.log(
                            "Microphone diagnostic failed. \(self.recorder.diagnostics) Error='\(error.localizedDescription)'"
                        )
                    }
                    self.recorder.stopAndDiscard()
                    NSApp.terminate(nil)
                }
            } catch {
                self.store.log(
                    "Microphone diagnostic start failed. Error='\(error.localizedDescription)'"
                )
                NSApp.terminate(nil)
            }
        }
    }

    private func runPasteDiagnostic() {
        let text = ProcessInfo.processInfo.environment[
            "ORHOM_DIAGNOSTIC_TEXT"
        ]?.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let text, !text.isEmpty else {
            store.log("Paste diagnostic failed. Reason='test-text-missing'.")
            NSApp.terminate(nil)
            return
        }
        guard let target = captureCurrentPasteTarget() else {
            store.log("Paste diagnostic failed. Reason='target-missing'.")
            NSApp.terminate(nil)
            return
        }
        diagnosticPasteboardSnapshot = captureStablePasteboardSnapshot(
            NSPasteboard.general
        )
        diagnosticText = text
        pasteTarget = target
        pasteTargetInputGeneration = currentInputActivityGeneration()
        if ProcessInfo.processInfo.environment[
            "ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT"
        ] == "1" {
            inputActivityLock.lock()
            inputActivityGeneration &+= 1
            let simulatedGeneration = inputActivityGeneration
            inputActivityLock.unlock()
            store.log(
                "Paste diagnostic simulated intervening input. Generation=\(simulatedGeneration)."
            )
        }
        state = .pasting
        setOverlay(
            mode: .transcribing,
            title: "Einfügetest",
            detail: "Chrome-Textfeld wird geprüft"
        )
        paste(text, into: target)
        finishPasteDiagnosticWhenSettled(remainingAttempts: 50)
    }

    private func finishPasteDiagnosticWhenSettled(
        remainingAttempts: Int
    ) {
        if state != .pasting || remainingAttempts <= 1 {
            restoreDiagnosticPasteboardIfNeeded()
            store.log(
                "Paste diagnostic finished. Settled=\(state != .pasting)."
            )
            NSApp.terminate(nil)
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.10) {
            [weak self] in
            self?.finishPasteDiagnosticWhenSettled(
                remainingAttempts: remainingAttempts - 1
            )
        }
    }

    private func restoreDiagnosticPasteboardIfNeeded() {
        defer {
            diagnosticPasteboardSnapshot = nil
            diagnosticText = nil
        }
        guard
            let snapshot = diagnosticPasteboardSnapshot,
            let text = diagnosticText
        else {
            return
        }
        let pasteboard = NSPasteboard.general
        guard pasteboard.string(forType: .string) == text else {
            return
        }
        _ = restorePasteboardSnapshot(
            snapshot,
            pasteboard: pasteboard,
            fallbackText: text
        )
    }

    private func presentError(_ error: Error, openSettings: Bool = false) {
        let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        state = .error
        recordingTimer?.invalidate()
        recordingTimer = nil
        audioDucking.restore()
        lastAudioDuckingRefresh = nil
        recorder.stopAndDiscard()
        settingsModel.errorMessage = message
        setOverlay(
            mode: .error,
            title: "Nicht geklappt",
            detail: shortened(message, maximum: 54)
        )
        refreshMenu()
        store.log("User-visible error: \(message)")
        if openSettings {
            self.openSettings()
        }
    }

    private func setIdle(message: String? = nil, detail: String? = nil) {
        audioDucking.restore()
        lastAudioDuckingRefresh = nil
        state = .idle
        recordingStart = nil
        pasteTarget = nil
        setOverlay(
            mode: .idle,
            title: message ?? "Bereit",
            detail: detail ?? "\(activeHotKey.displayName) · lokal mit Whisper"
        )
        refreshMenu()

        if message != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.4) { [weak self] in
                guard let self, self.state == .idle else { return }
                self.setOverlay(
                    mode: .idle,
                    title: "Bereit",
                    detail: "\(self.activeHotKey.displayName) · lokal mit Whisper"
                )
            }
        }
    }

    private func setOverlay(
        mode: OverlayMode,
        title: String,
        detail: String,
        progress: Double? = nil
    ) {
        overlayModel.mode = mode
        overlayModel.title = title
        overlayModel.detail = detail
        overlayModel.progress = progress
        settingsModel.configurationEnabled =
            state == .preparing || state == .idle || state == .error
        if mode != .recording {
            overlayModel.level = 0
        }
        if showOverlay {
            overlay.show()
        }
        statusMenuItem?.title = "\(title) · \(detail)"
        statusItem?.button?.toolTip = "ORhom · \(title)"
    }

    private func captureCurrentPasteTarget() -> PasteTarget? {
        var fallback: PasteTarget?
        var stableCandidate: PasteTarget?
        var previousCandidate: PasteTarget?
        var coherentSamples = 0
        var consecutiveCompatibleSamples = 0

        for attempt in 1...3 {
            guard
                let sourceBefore = currentPasteCaptureSource(),
                let frontmostApplicationBefore =
                    NSWorkspace.shared.frontmostApplication
            else {
                return fallback
            }
            let applicationBefore = sourceBefore.application
            guard
                applicationBefore.processIdentifier !=
                    ProcessInfo.processInfo.processIdentifier,
                !applicationBefore.isTerminated
            else {
                return fallback
            }

            let foregroundWindowBefore = topmostWindowDescriptor(
                for: applicationBefore.processIdentifier,
                includeNonstandardLayers:
                    sourceBefore.usesNonactivatingWindow
            )
            let rawCandidate = makePasteTarget(
                application: applicationBefore,
                focusedElementOverride: sourceBefore.focusedElement,
                usesNonactivatingWindow:
                    sourceBefore.usesNonactivatingWindow,
                underlyingProcessIdentifier:
                    sourceBefore.underlyingProcessIdentifier,
                underlyingApplicationLaunchDate:
                    sourceBefore.underlyingApplicationLaunchDate
            )
            let sourceAfter = currentPasteCaptureSource()
            let frontmostApplicationAfter =
                NSWorkspace.shared.frontmostApplication
            let foregroundWindowAfter = topmostWindowDescriptor(
                for: applicationBefore.processIdentifier,
                includeNonstandardLayers:
                    sourceBefore.usesNonactivatingWindow
            )
            let coherentApplication =
                sourceAfter?.application.processIdentifier ==
                    applicationBefore.processIdentifier &&
                sourceAfter?.application.bundleIdentifier ==
                    applicationBefore.bundleIdentifier &&
                sourceAfter?.application.launchDate ==
                    applicationBefore.launchDate &&
                sourceAfter?.usesNonactivatingWindow ==
                    sourceBefore.usesNonactivatingWindow &&
                sourceAfter?.underlyingProcessIdentifier ==
                    sourceBefore.underlyingProcessIdentifier &&
                sourceAfter?.underlyingApplicationLaunchDate ==
                    sourceBefore.underlyingApplicationLaunchDate &&
                frontmostApplicationAfter?.processIdentifier ==
                    frontmostApplicationBefore.processIdentifier &&
                frontmostApplicationAfter?.launchDate ==
                    frontmostApplicationBefore.launchDate
            let coherentWindow =
                foregroundWindowBefore?.number ==
                    foregroundWindowAfter?.number
            let coherent = coherentApplication && coherentWindow

            if coherent {
                let candidate: PasteTarget
                if pasteTarget(
                    rawCandidate,
                    matches: foregroundWindowBefore
                ) {
                    candidate = rawCandidate
                } else {
                    candidate = makeWindowOnlyPasteTarget(
                        application: applicationBefore,
                        window: foregroundWindowBefore
                    )
                    store.log(
                        "Focus capture ignored an AX element outside the stable foreground window. Attempt=\(attempt) AXWindowNumber=\(rawCandidate.windowNumber?.description ?? "none") ForegroundWindowNumber=\(foregroundWindowBefore?.number.description ?? "none")."
                    )
                }
                coherentSamples += 1
                fallback = candidate

                if let previousCandidate,
                   sameApplicationInstance(previousCandidate, candidate),
                   windowsAreCompatible(previousCandidate, candidate) {
                    consecutiveCompatibleSamples += 1
                    let preferred =
                        pasteTargetStrength(candidate) >=
                            pasteTargetStrength(previousCandidate)
                        ? candidate
                        : previousCandidate
                    if stableCandidate == nil ||
                        pasteTargetStrength(preferred) >
                            pasteTargetStrength(stableCandidate!) {
                        stableCandidate = preferred
                    }
                } else {
                    consecutiveCompatibleSamples = 1
                    stableCandidate = nil
                }
                previousCandidate = candidate
            } else {
                store.log(
                    "Focus capture retry. Reason='foreground-app-or-window-changed' Attempt=\(attempt)."
                )
            }

            if attempt < 3 {
                Thread.sleep(forTimeInterval: 0.015)
            }
        }

        let selected: PasteTarget?
        if consecutiveCompatibleSamples >= 2 {
            selected = stableCandidate ?? fallback
        } else if fallback?.focusedElement != nil {
            selected = fallback
        } else {
            selected = nil
            store.log(
                "Focus capture failed. Reason='window-only-target-not-stable' CoherentSamples=\(coherentSamples)."
            )
        }
        if let selected {
            logPasteTargetCapture(
                selected,
                attempts: 3,
                coherentSamples: coherentSamples
            )
        }
        return selected
    }

    private func currentPasteCaptureSource() -> PasteCaptureSource? {
        guard
            let frontmostApplication =
                NSWorkspace.shared.frontmostApplication,
            !frontmostApplication.isTerminated
        else {
            return nil
        }
        if let nonactivatingSource =
            nonactivatingPasteCaptureSource(
                frontmostApplication: frontmostApplication
            ) {
            return nonactivatingSource
        }
        return PasteCaptureSource(
            application: frontmostApplication,
            focusedElement: nil,
            usesNonactivatingWindow: false,
            underlyingProcessIdentifier: nil,
            underlyingApplicationLaunchDate: nil
        )
    }

    private func nonactivatingPasteCaptureSource(
        frontmostApplication: NSRunningApplication
    ) -> PasteCaptureSource? {
        guard
            AXIsProcessTrusted(),
            let pointerLocation = currentPointerInputLocation()
        else {
            return nil
        }
        let systemWideElement = AXUIElementCreateSystemWide()
        _ = AXUIElementSetMessagingTimeout(systemWideElement, 0.08)
        var hitElement: AXUIElement?
        let hitResult = AXUIElementCopyElementAtPosition(
            systemWideElement,
            Float(pointerLocation.x),
            Float(pointerLocation.y),
            &hitElement
        )
        let traceDiagnostic =
            ProcessInfo.processInfo.environment[
                "ORHOM_DIAGNOSTIC_TRACE_FOCUS"
            ] == "1"
        if traceDiagnostic {
            store.log(
                "Focus diagnostic pointer probe. X=\(pointerLocation.x) Y=\(pointerLocation.y) Result=\(hitResult.rawValue) HitRole='\(hitElement.flatMap { copyAXString(from: $0, attribute: kAXRoleAttribute) } ?? "none")'."
            )
        }
        guard
            hitResult == .success,
            let hitElement,
            let editableElement =
                nearestEditableAXElement(from: hitElement)
        else {
            return nil
        }

        var processIdentifier = pid_t()
        guard
            AXUIElementGetPid(
                editableElement,
                &processIdentifier
            ) == .success,
            processIdentifier !=
                frontmostApplication.processIdentifier,
            processIdentifier !=
                ProcessInfo.processInfo.processIdentifier,
            let application = NSRunningApplication(
                processIdentifier: processIdentifier
            ),
            !application.isTerminated
        else {
            if traceDiagnostic {
                store.log(
                    "Focus diagnostic rejected pointer element before app validation."
                )
            }
            return nil
        }

        let applicationElement = AXUIElementCreateApplication(
            processIdentifier
        )
        _ = AXUIElementSetMessagingTimeout(applicationElement, 0.12)
        let applicationFocusedElement = copyAXElement(
            from: applicationElement,
            attribute: kAXFocusedUIElementAttribute
        )
        guard MacPastePolicy.mayUseNonactivatingFocusedElement(
            exactApplicationFocus:
                applicationFocusedElement.map {
                    CFEqual(editableElement, $0)
                } ?? false,
            elementReportsFocused: copyAXBoolean(
                from: editableElement,
                attribute: kAXFocusedAttribute
            ) == true,
            supportsSelectedText: isAXAttributeSettable(
                editableElement,
                attribute: kAXSelectedTextAttribute
            ),
            isSecure: isSecureAXElement(editableElement)
        ) else {
            if traceDiagnostic {
                store.log(
                    "Focus diagnostic rejected pointer element during focused/editable validation. TargetPID=\(processIdentifier)."
                )
            }
            return nil
        }

        let focusedWindow =
            copyAXElement(
                from: editableElement,
                attribute: kAXWindowAttribute
            ) ??
            copyAncestorWindow(from: editableElement)
        guard let focusedWindow else {
            if traceDiagnostic {
                store.log(
                    "Focus diagnostic rejected pointer element because its AX window is missing. TargetPID=\(processIdentifier)."
                )
            }
            return nil
        }
        let window = matchingWindowDescriptor(
            for: processIdentifier,
            focusedWindow: focusedWindow,
            title: copyAXString(
                from: focusedWindow,
                attribute: kAXTitleAttribute
            ),
            frame: copyAXFrame(focusedWindow),
            includeNonstandardLayers: true
        )
        guard let window, window.layer != 0 else {
            if traceDiagnostic {
                store.log(
                    "Focus diagnostic rejected pointer window. TargetPID=\(processIdentifier) WindowNumber=\(window?.number.description ?? "none") Layer=\(window?.layer.description ?? "none")."
                )
            }
            return nil
        }
        if traceDiagnostic {
            store.log(
                "Focus diagnostic accepted nonactivating target. TargetPID=\(processIdentifier) WindowNumber=\(window.number) Layer=\(window.layer)."
            )
        }
        return PasteCaptureSource(
            application: application,
            focusedElement: editableElement,
            usesNonactivatingWindow: true,
            underlyingProcessIdentifier:
                frontmostApplication.processIdentifier,
            underlyingApplicationLaunchDate:
                frontmostApplication.launchDate
        )
    }

    private func nearestEditableAXElement(
        from initialElement: AXUIElement
    ) -> AXUIElement? {
        var element = initialElement
        for _ in 0..<8 {
            if isEditableKeyboardPasteElement(element) {
                return element
            }
            guard
                let parent = copyAXElement(
                    from: element,
                    attribute: kAXParentAttribute
                )
            else {
                return nil
            }
            element = parent
        }
        return nil
    }

    private func makeWindowOnlyPasteTarget(
        application: NSRunningApplication,
        window: CGWindowDescriptor?
    ) -> PasteTarget {
        PasteTarget(
            processIdentifier: application.processIdentifier,
            bundleIdentifier: application.bundleIdentifier,
            applicationLaunchDate: application.launchDate,
            usesNonactivatingWindow: false,
            underlyingProcessIdentifier: nil,
            underlyingApplicationLaunchDate: nil,
            focusedWindow: nil,
            windowNumber: window?.number,
            windowTitle: window?.title,
            windowFrame: window?.frame,
            focusedElement: nil,
            role: nil,
            subrole: nil,
            identifier: nil,
            elementTitle: nil,
            layoutFingerprint: nil,
            webAreaRoot: nil,
            webAreaFingerprint: nil,
            preserveWebViewCaret: isKnownWebViewBundle(
                application.bundleIdentifier
            )
        )
    }

    private func pasteTarget(
        _ target: PasteTarget,
        matches foregroundWindow: CGWindowDescriptor?
    ) -> Bool {
        guard let foregroundWindow else {
            return target.windowNumber == nil && target.focusedWindow == nil
        }
        return windowIdentity(
            number: target.windowNumber,
            matches: foregroundWindow
        )
    }

    private func windowIdentity(
        number: CGWindowID?,
        matches descriptor: CGWindowDescriptor
    ) -> Bool {
        number == descriptor.number
    }

    private func makePasteTarget(
        application: NSRunningApplication,
        focusedElementOverride: AXUIElement? = nil,
        usesNonactivatingWindow: Bool = false,
        underlyingProcessIdentifier: pid_t? = nil,
        underlyingApplicationLaunchDate: Date? = nil
    ) -> PasteTarget {
        let processIdentifier = application.processIdentifier
        guard AXIsProcessTrusted() else {
            let window = topmostWindowDescriptor(
                for: processIdentifier
            )
            return PasteTarget(
                processIdentifier: processIdentifier,
                bundleIdentifier: application.bundleIdentifier,
                applicationLaunchDate: application.launchDate,
                usesNonactivatingWindow: false,
                underlyingProcessIdentifier: nil,
                underlyingApplicationLaunchDate: nil,
                focusedWindow: nil,
                windowNumber: window?.number,
                windowTitle: window?.title,
                windowFrame: window?.frame,
                focusedElement: nil,
                role: nil,
                subrole: nil,
                identifier: nil,
                elementTitle: nil,
                layoutFingerprint: nil,
                webAreaRoot: nil,
                webAreaFingerprint: nil,
                preserveWebViewCaret: isKnownWebViewBundle(
                    application.bundleIdentifier
                )
            )
        }

        let applicationElement = AXUIElementCreateApplication(processIdentifier)
        _ = AXUIElementSetMessagingTimeout(applicationElement, 0.12)
        let focusedElement =
            focusedElementOverride ??
            focusedAXElement(
                for: processIdentifier,
                applicationElement: applicationElement
            )
        let focusedWindow =
            focusedElement.flatMap {
                copyAXElement(from: $0, attribute: kAXWindowAttribute)
            } ??
            copyAXElement(
                from: applicationElement,
                attribute: kAXFocusedWindowAttribute
            ) ??
            focusedElement.flatMap {
                copyAncestorWindow(from: $0)
            }
        let role = focusedElement.flatMap {
            copyAXString(from: $0, attribute: kAXRoleAttribute)
        }
        let axWindowTitle = focusedWindow.flatMap {
            copyAXString(from: $0, attribute: kAXTitleAttribute)
        }
        let axWindowFrame = focusedWindow.flatMap(copyAXFrame)
        let window = matchingWindowDescriptor(
            for: processIdentifier,
            focusedWindow: focusedWindow,
            title: axWindowTitle,
            frame: axWindowFrame,
            includeNonstandardLayers: usesNonactivatingWindow
        )
        let windowFrame = axWindowFrame ?? window?.frame
        let elementFrame = focusedElement.flatMap(copyAXFrame)
        let webAreaRoot = focusedElement.flatMap(copyWebAreaAncestor)
        let webAreaFrame = webAreaRoot.flatMap(copyAXFrame)

        return PasteTarget(
            processIdentifier: processIdentifier,
            bundleIdentifier: application.bundleIdentifier,
            applicationLaunchDate: application.launchDate,
            usesNonactivatingWindow: usesNonactivatingWindow,
            underlyingProcessIdentifier:
                underlyingProcessIdentifier,
            underlyingApplicationLaunchDate:
                underlyingApplicationLaunchDate,
            focusedWindow: focusedWindow,
            windowNumber: window?.number,
            windowTitle: axWindowTitle ?? window?.title,
            windowFrame: windowFrame,
            focusedElement: focusedElement,
            role: role,
            subrole: focusedElement.flatMap {
                copyAXString(from: $0, attribute: kAXSubroleAttribute)
            },
            identifier: focusedElement.flatMap {
                copyAXString(from: $0, attribute: kAXIdentifierAttribute)
            },
            elementTitle: focusedElement.flatMap {
                copyAXString(from: $0, attribute: kAXTitleAttribute)
            },
            layoutFingerprint: makeLayoutFingerprint(
                elementFrame: elementFrame,
                windowFrame: windowFrame
            ),
            webAreaRoot: webAreaRoot,
            webAreaFingerprint: makeLayoutFingerprint(
                elementFrame: webAreaFrame,
                windowFrame: windowFrame
            ),
            preserveWebViewCaret: shouldPreserveWebViewCaret(
                bundleIdentifier: application.bundleIdentifier,
                role: role,
                hasWebAreaAncestor: webAreaRoot != nil,
                elementMissing: focusedElement == nil
            )
        )
    }

    private func logPasteTargetCapture(
        _ target: PasteTarget,
        attempts: Int,
        coherentSamples: Int
    ) {
        store.log(
            "Focus captured. TargetPID=\(target.processIdentifier) Bundle='\(target.bundleIdentifier ?? "unknown")' Attempts=\(attempts) CoherentSamples=\(coherentSamples) NonactivatingWindow=\(target.usesNonactivatingWindow) WindowPresent=\(target.focusedWindow != nil) WindowNumber=\(target.windowNumber.map(String.init) ?? "none") ElementPresent=\(target.focusedElement != nil) Role='\(target.role ?? "unknown")' Subrole='\(target.subrole ?? "unknown")' IdentifierPresent=\(!(target.identifier ?? "").isEmpty) LayoutPresent=\(target.layoutFingerprint != nil) WebAreaPresent=\(target.webAreaRoot != nil) PreserveWebViewCaret=\(target.preserveWebViewCaret)."
        )
    }

    private func reprobePasteTargetAfterCaptureStarted() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) { [weak self] in
            guard
                let self,
                self.state == .recording,
                let original = self.pasteTarget,
                let candidate = self.captureCurrentPasteTarget(),
                self.sameApplicationInstance(original, candidate),
                self.windowsAreCompatible(original, candidate)
            else {
                return
            }

            let originalScore = self.pasteTargetStrength(original)
            let candidateScore = self.pasteTargetStrength(candidate)
            let semanticPromotion = self.isSemanticFocusPromotion(
                from: original,
                to: candidate
            )
            guard candidateScore > originalScore || semanticPromotion else {
                self.store.log(
                    "Focus re-probe retained original target. OriginalScore=\(originalScore) CandidateScore=\(candidateScore)."
                )
                return
            }

            self.pasteTarget = candidate
            self.store.log(
                "Focus re-probe promoted target. OriginalScore=\(originalScore) CandidateScore=\(candidateScore) SemanticPromotion=\(semanticPromotion) Role='\(candidate.role ?? "unknown")' PreserveWebViewCaret=\(candidate.preserveWebViewCaret)."
            )
        }
    }

    private func isSemanticFocusPromotion(
        from original: PasteTarget,
        to candidate: PasteTarget
    ) -> Bool {
        if original.focusedElement == nil && candidate.focusedElement != nil {
            return true
        }
        if original.webAreaRoot == nil && candidate.webAreaRoot != nil {
            return true
        }
        guard
            let originalRole = original.role,
            let candidateRole = candidate.role,
            originalRole != candidateRole
        else {
            return false
        }
        let originalIsTransientRoot =
            originalRole == "AXWebArea" ||
            originalRole == "AXGroup" ||
            originalRole == "AXScrollArea" ||
            originalRole == "AXUnknown"
        return originalIsTransientRoot &&
            (
                isEditableAXRole(candidateRole) ||
                !(candidate.identifier ?? "").isEmpty
            )
    }

    private func pasteTargetStrength(_ target: PasteTarget) -> Int {
        (target.focusedWindow == nil ? 0 : 2) +
            (target.windowNumber == nil ? 0 : 3) +
            (target.focusedElement == nil ? 0 : 4) +
            ((target.identifier ?? "").isEmpty ? 0 : 2) +
            ((target.role ?? "").isEmpty ? 0 : 1) +
            (target.layoutFingerprint == nil ? 0 : 1) +
            (target.webAreaRoot == nil ? 0 : 2)
    }

    private func sameApplicationInstance(
        _ lhs: PasteTarget,
        _ rhs: PasteTarget
    ) -> Bool {
        lhs.processIdentifier == rhs.processIdentifier &&
            lhs.bundleIdentifier == rhs.bundleIdentifier &&
            (
                lhs.applicationLaunchDate == nil ||
                rhs.applicationLaunchDate == nil ||
                lhs.applicationLaunchDate == rhs.applicationLaunchDate
            )
    }

    private func sameApplicationInstance(
        _ target: PasteTarget,
        _ application: NSRunningApplication
    ) -> Bool {
        !application.isTerminated &&
            application.processIdentifier == target.processIdentifier &&
            (
                target.bundleIdentifier == nil ||
                application.bundleIdentifier == target.bundleIdentifier
            ) &&
            (
                target.applicationLaunchDate == nil ||
                application.launchDate == nil ||
                target.applicationLaunchDate == application.launchDate
            )
    }

    private func windowsAreCompatible(
        _ lhs: PasteTarget,
        _ rhs: PasteTarget
    ) -> Bool {
        if lhs.windowNumber != nil || rhs.windowNumber != nil {
            return lhs.windowNumber == rhs.windowNumber
        }
        if let left = lhs.focusedWindow,
           let right = rhs.focusedWindow,
           CFEqual(left, right) {
            return true
        }
        if let leftFrame = lhs.windowFrame,
           let rightFrame = rhs.windowFrame,
           windowFramesMatch(leftFrame, rightFrame) {
            let leftTitle = lhs.windowTitle ?? ""
            let rightTitle = rhs.windowTitle ?? ""
            return !leftTitle.isEmpty &&
                !rightTitle.isEmpty &&
                leftTitle == rightTitle
        }
        return false
    }

    private func paste(_ text: String, into target: PasteTarget?) {
        guard let target else {
            leaveDictationOnClipboard(
                text,
                reason: "target-missing",
                promptForPermission: false
            )
            return
        }
        guard
            let application = NSRunningApplication(
                processIdentifier: target.processIdentifier
            ),
            sameApplicationInstance(target, application)
        else {
            leaveDictationOnClipboard(
                text,
                reason: "target-closed-or-changed",
                promptForPermission: false
            )
            return
        }

        guard AXIsProcessTrusted() else {
            leaveDictationOnClipboard(
                text,
                reason: "permission accessibility=false",
                promptForPermission: true
            )
            return
        }
        guard target.subrole != (kAXSecureTextFieldSubrole as String) else {
            leaveDictationOnClipboard(
                text,
                reason: "secure-text-field",
                promptForPermission: false
            )
            settingsModel.errorMessage =
                "In Passwortfelder fügt ORhom aus Sicherheitsgründen nicht automatisch ein."
            return
        }

        let targetWasFrontmost =
            NSWorkspace.shared.frontmostApplication?.processIdentifier ==
                target.processIdentifier
        let targetWindowWasFrontmost = pasteTarget(
            target,
            matches: topmostWindowDescriptor(
                for: target.processIdentifier,
                includeNonstandardLayers:
                    target.usesNonactivatingWindow
            )
        )
        let activationRequested =
            target.usesNonactivatingWindow ||
            targetWasFrontmost ||
            application.activate(options: [.activateIgnoringOtherApps])
        if !targetWindowWasFrontmost,
           !target.usesNonactivatingWindow,
           let focusedWindow = target.focusedWindow {
            _ = AXUIElementPerformAction(
                focusedWindow,
                kAXRaiseAction as CFString
            )
        }
        store.log(
            "Paste activation requested. TargetPID=\(target.processIdentifier) Bundle='\(target.bundleIdentifier ?? "unknown")' NonactivatingWindow=\(target.usesNonactivatingWindow) AppAlreadyFrontmost=\(targetWasFrontmost) WindowAlreadyFrontmost=\(targetWindowWasFrontmost) Accepted=\(activationRequested) WindowPresent=\(target.focusedWindow != nil) ElementPresent=\(target.focusedElement != nil) Role='\(target.role ?? "unknown")' PreserveWebViewCaret=\(target.preserveWebViewCaret)."
        )
        guard activationRequested else {
            leaveDictationOnClipboard(
                text,
                reason: "target-activation-rejected",
                promptForPermission: false
            )
            return
        }
        waitForPasteTarget(
            text: text,
            target: target,
            application: application,
            targetWasFrontmost: targetWasFrontmost,
            remainingAttempts: 16,
            consecutiveMatches: 0,
            didAttemptElementFocus: false
        )
    }

    private func waitForPasteTarget(
        text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool,
        remainingAttempts: Int,
        consecutiveMatches: Int,
        didAttemptElementFocus: Bool
    ) {
        guard state == .pasting else { return }
        guard sameApplicationInstance(target, application) else {
            leaveDictationOnClipboard(
                text,
                reason: "target-closed-during-activation",
                promptForPermission: false
            )
            return
        }

        let probe = probePasteFocus(
            target,
            application: application,
            allowProcessScopedMatch: targetWasFrontmost
        )
        switch probe {
        case .match(let currentElement, let kind):
            let matches = consecutiveMatches + 1
            guard matches >= 2 else {
                guard remainingAttempts > 1 else {
                    leaveDictationOnClipboard(
                        text,
                        reason: "focus-confirmation-timeout consecutive-match",
                        promptForPermission: false
                    )
                    return
                }
                retryPasteTargetProbe(
                    text: text,
                    target: target,
                    application: application,
                    targetWasFrontmost: targetWasFrontmost,
                    remainingAttempts: remainingAttempts,
                    consecutiveMatches: matches,
                    didAttemptElementFocus: didAttemptElementFocus
                )
                return
            }
            store.log(
                "Target focus confirmed. Match='\(kind)' AttemptsUsed=\(17 - remainingAttempts) PreserveWebViewCaret=\(target.preserveWebViewCaret)."
            )
            insertDictation(
                text,
                into: target,
                currentFocusedElement: currentElement,
                application: application,
                targetWasFrontmost: targetWasFrontmost
            )

        case .transient(let reason):
            var attemptedFocus = didAttemptElementFocus
            if !attemptedFocus,
               !target.preserveWebViewCaret,
               let focusedElement = target.focusedElement,
               NSWorkspace.shared.frontmostApplication?.processIdentifier ==
                    target.processIdentifier {
                attemptedFocus = true
                let result = AXUIElementSetAttributeValue(
                    focusedElement,
                    kAXFocusedAttribute as CFString,
                    kCFBooleanTrue
                )
                store.log(
                    "Native target focus requested. Result=\(result.rawValue) Reason='\(reason)'."
                )
            } else if !target.usesNonactivatingWindow,
                      let focusedWindow = target.focusedWindow {
                _ = AXUIElementPerformAction(
                    focusedWindow,
                    kAXRaiseAction as CFString
                )
            }

            guard remainingAttempts > 1 else {
                leaveDictationOnClipboard(
                    text,
                    reason: "focus-confirmation-timeout \(reason)",
                    promptForPermission: false
                )
                return
            }
            retryPasteTargetProbe(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                remainingAttempts: remainingAttempts,
                consecutiveMatches: 0,
                didAttemptElementFocus: attemptedFocus
            )

        case .unsafe(let reason):
            leaveDictationOnClipboard(
                text,
                reason: "unsafe-target \(reason)",
                promptForPermission: false
            )
        }
    }

    private func retryPasteTargetProbe(
        text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool,
        remainingAttempts: Int,
        consecutiveMatches: Int,
        didAttemptElementFocus: Bool
    ) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.025) { [weak self] in
            self?.waitForPasteTarget(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                remainingAttempts: remainingAttempts - 1,
                consecutiveMatches: consecutiveMatches,
                didAttemptElementFocus: didAttemptElementFocus
            )
        }
    }

    private func probePasteFocus(
        _ target: PasteTarget,
        application: NSRunningApplication,
        allowProcessScopedMatch: Bool
    ) -> PasteFocusProbe {
        guard sameApplicationInstance(target, application) else {
            return .unsafe(reason: "application-instance-changed")
        }
        if target.usesNonactivatingWindow {
            guard ambiguousTargetInputStayedStable() else {
                return .transient(
                    reason: "nonactivating-target-after-user-input"
                )
            }
            guard
                let underlyingApplication =
                    NSWorkspace.shared.frontmostApplication,
                underlyingApplication.processIdentifier ==
                    target.underlyingProcessIdentifier,
                target.underlyingApplicationLaunchDate == nil ||
                    underlyingApplication.launchDate == nil ||
                    underlyingApplication.launchDate ==
                        target.underlyingApplicationLaunchDate
            else {
                return .transient(
                    reason: "underlying-application-changed"
                )
            }
        } else {
            guard
                application.isActive,
                NSWorkspace.shared.frontmostApplication?.processIdentifier ==
                    target.processIdentifier
            else {
                return .transient(reason: "application-not-frontmost")
            }
        }

        let foregroundWindowBefore = topmostWindowDescriptor(
            for: target.processIdentifier,
            includeNonstandardLayers:
                target.usesNonactivatingWindow
        )
        let applicationElement = AXUIElementCreateApplication(
            target.processIdentifier
        )
        _ = AXUIElementSetMessagingTimeout(applicationElement, 0.12)
        let currentElement: AXUIElement?
        if target.usesNonactivatingWindow {
            currentElement = copyAXElement(
                from: applicationElement,
                attribute: kAXFocusedUIElementAttribute
            )
        } else {
            currentElement = focusedAXElement(
                for: target.processIdentifier,
                applicationElement: applicationElement
            )
        }
        let currentWindow =
            currentElement.flatMap {
                copyAXElement(from: $0, attribute: kAXWindowAttribute)
            } ??
            copyAXElement(
                from: applicationElement,
                attribute: kAXFocusedWindowAttribute
            ) ??
            currentElement.flatMap {
                copyAncestorWindow(from: $0)
            }
        let currentWindowTitle = currentWindow.flatMap {
            copyAXString(from: $0, attribute: kAXTitleAttribute)
        }
        let currentAXWindowFrame = currentWindow.flatMap(copyAXFrame)
        let currentWindowDescriptor = matchingWindowDescriptor(
            for: target.processIdentifier,
            focusedWindow: currentWindow,
            title: currentWindowTitle,
            frame: currentAXWindowFrame,
            includeNonstandardLayers:
                target.usesNonactivatingWindow
        )
        let foregroundWindowAfter = topmostWindowDescriptor(
            for: target.processIdentifier,
            includeNonstandardLayers:
                target.usesNonactivatingWindow
        )
        guard
            foregroundWindowBefore?.number ==
                foregroundWindowAfter?.number
        else {
            return .transient(reason: "foreground-window-changed-during-probe")
        }
        let authoritativeWindow =
            foregroundWindowAfter ?? currentWindowDescriptor
        if let currentWindow,
           let authoritativeWindow {
            let exactCapturedAXWindowIsForeground =
                target.windowNumber == authoritativeWindow.number &&
                (
                    target.focusedWindow.map {
                        CFEqual($0, currentWindow)
                    } ?? false
                )
            guard exactCapturedAXWindowIsForeground ||
                windowIdentity(
                    number: currentWindowDescriptor?.number,
                    matches: authoritativeWindow
                )
            else {
                return .transient(
                    reason: "focused-element-outside-foreground-window"
                )
            }
        }
        guard focusedWindowMatches(
            target,
            currentWindow: currentWindow,
            currentWindowNumber: authoritativeWindow?.number,
            currentWindowTitle:
                authoritativeWindow?.title ?? currentWindowTitle,
            currentWindowFrame:
                authoritativeWindow?.frame ?? currentAXWindowFrame,
            allowProcessScopedMatch: allowProcessScopedMatch
        ) else {
            return .transient(
                reason:
                    "window-mismatch capturedAX=\(target.focusedWindow != nil) currentAX=\(currentWindow != nil) capturedNumber=\(target.windowNumber.map(String.init) ?? "none") currentNumber=\(authoritativeWindow?.number.description ?? "none")"
            )
        }

        if IsSecureEventInputEnabled() ||
            isSecureAXElement(target.focusedElement) ||
            isSecureAXElement(currentElement) {
            return .unsafe(reason: "secure-text-field")
        }

        if let expectedWebArea = target.webAreaRoot {
            guard
                let currentElement,
                let currentWebArea = copyWebAreaAncestor(
                    from: currentElement
                )
            else {
                return .transient(reason: "current-web-area-missing")
            }
            let exactWebArea = CFEqual(
                expectedWebArea,
                currentWebArea
            )
            let currentWebAreaFingerprint = makeLayoutFingerprint(
                elementFrame: copyAXFrame(currentWebArea),
                windowFrame:
                    authoritativeWindow?.frame ?? currentAXWindowFrame
            )
            let semanticWebArea =
                target.webAreaFingerprint.map { expected in
                    currentWebAreaFingerprint.map {
                        expected.isSimilar(to: $0)
                    } ?? false
                } ?? false
            guard exactWebArea || semanticWebArea else {
                return .transient(reason: "web-area-identity-changed")
            }
        }

        if target.preserveWebViewCaret {
            let inputHistoryStable = ambiguousTargetInputStayedStable()
            let exactEditableElementMatch: Bool
            if let expected = target.focusedElement,
               let currentElement {
                exactEditableElementMatch =
                    isWebKeyboardPasteElement(currentElement) &&
                    CFEqual(expected, currentElement)
            } else {
                exactEditableElementMatch = false
            }
            guard MacPastePolicy.mayReuseWebTarget(
                inputHistoryStable: inputHistoryStable,
                exactEditableElementMatch: exactEditableElementMatch
            ) else {
                return .transient(
                    reason: "ambiguous-web-target-after-user-input"
                )
            }
            if let expected = target.focusedElement,
               let currentElement {
                guard isWebKeyboardPasteElement(currentElement) else {
                    return .transient(
                        reason: "current-web-element-not-editable"
                    )
                }
                if exactEditableElementMatch {
                    let matchKind = inputHistoryStable
                        ? "web-exact"
                        : "web-exact-after-intervening-input"
                    return .match(
                        element: currentElement,
                        kind: matchKind
                    )
                }
                if isElement(
                    currentElement,
                    within: expected
                ) {
                    return .match(
                        element: currentElement,
                        kind: "web-descendant"
                    )
                }
                if elementsSemanticallyMatch(
                    target,
                    current: currentElement
                ) {
                    return .match(
                        element: currentElement,
                        kind: "web-semantic"
                    )
                }
                return .transient(reason: "web-element-changed")
            }
            if target.focusedElement != nil {
                return .transient(reason: "current-web-element-missing")
            }
            guard allowsNilElementWindowPaste(target.bundleIdentifier) else {
                return .transient(reason: "captured-web-element-missing")
            }
            if let currentElement,
               !isWebKeyboardPasteElement(currentElement) {
                return .transient(
                    reason: "window-only-current-element-not-editable"
                )
            }
            return .match(
                element: currentElement,
                kind: "editor-window-no-element"
            )
        }

        guard let expected = target.focusedElement else {
            return .transient(reason: "captured-element-missing")
        }
        guard let currentElement else {
            return .transient(reason: "current-element-missing")
        }
        if CFEqual(expected, currentElement) {
            return .match(element: currentElement, kind: "element-exact")
        }
        if elementsSemanticallyMatch(target, current: currentElement) {
            return .match(element: currentElement, kind: "element-semantic")
        }
        return .transient(reason: "element-identity-changed")
    }

    private func focusedWindowMatches(
        _ target: PasteTarget,
        currentWindow: AXUIElement?,
        currentWindowNumber: CGWindowID?,
        currentWindowTitle: String?,
        currentWindowFrame: CGRect?,
        allowProcessScopedMatch: Bool
    ) -> Bool {
        if target.windowNumber != nil || currentWindowNumber != nil {
            return target.windowNumber == currentWindowNumber
        }
        if let expectedWindow = target.focusedWindow,
           let currentWindow,
           CFEqual(expectedWindow, currentWindow) {
            return true
        }
        if let expectedFrame = target.windowFrame,
           let currentWindowFrame,
           windowFramesMatch(expectedFrame, currentWindowFrame) {
            let expectedTitle = target.windowTitle ?? ""
            let currentTitle = currentWindowTitle ?? ""
            return !expectedTitle.isEmpty &&
                !currentTitle.isEmpty &&
                expectedTitle == currentTitle
        }
        guard target.focusedWindow == nil, currentWindow == nil else {
            return false
        }
        return target.preserveWebViewCaret &&
                allowsNilElementWindowPaste(target.bundleIdentifier) &&
                allowProcessScopedMatch &&
                visibleWindowDescriptors(
                    for: target.processIdentifier,
                    includeNonstandardLayers:
                        target.usesNonactivatingWindow
                ).count == 1
    }

    private func elementsSemanticallyMatch(
        _ target: PasteTarget,
        current: AXUIElement
    ) -> Bool {
        let currentRole = copyAXString(
            from: current,
            attribute: kAXRoleAttribute
        )
        guard
            let expectedRole = target.role,
            !expectedRole.isEmpty,
            currentRole == expectedRole
        else {
            return false
        }

        let currentIdentifier = copyAXString(
            from: current,
            attribute: kAXIdentifierAttribute
        )
        let currentTitle = copyAXString(
            from: current,
            attribute: kAXTitleAttribute
        )
        let currentSubrole = copyAXString(
            from: current,
            attribute: kAXSubroleAttribute
        )
        let currentWindow =
            copyAXElement(from: current, attribute: kAXWindowAttribute) ??
            copyAXElement(
                from: AXUIElementCreateApplication(
                    target.processIdentifier
                ),
                attribute: kAXFocusedWindowAttribute
            )
        let currentFingerprint = makeLayoutFingerprint(
            elementFrame: copyAXFrame(current),
            windowFrame: currentWindow.flatMap(copyAXFrame)
        )
        guard
            let expectedFingerprint = target.layoutFingerprint,
            let currentFingerprint,
            expectedFingerprint.isSimilar(to: currentFingerprint)
        else {
            return false
        }

        if let expectedIdentifier = target.identifier,
           !expectedIdentifier.isEmpty {
            return currentIdentifier == expectedIdentifier
        }
        if let expectedTitle = target.elementTitle,
           !expectedTitle.isEmpty,
           isEditableAXRole(expectedRole) {
            return currentTitle == expectedTitle &&
                currentSubrole == target.subrole
        }
        return false
    }

    private func insertDictation(
        _ text: String,
        into target: PasteTarget,
        currentFocusedElement: AXUIElement?,
        application: NSRunningApplication,
        targetWasFrontmost: Bool
    ) {
        if !target.preserveWebViewCaret,
           let currentFocusedElement,
           let verifiedFocusedElement = verifiedDirectAXInsertionElement(
               target: target,
               expectedElement: currentFocusedElement,
               application: application,
               targetWasFrontmost: targetWasFrontmost
           ),
           isAXAttributeSettable(
               verifiedFocusedElement,
               attribute: kAXSelectedTextAttribute
           ),
           AXUIElementSetAttributeValue(
               verifiedFocusedElement,
               kAXSelectedTextAttribute as CFString,
               text as CFString
           ) == .success {
            setIdle(message: "Eingefügt", detail: "Am ursprünglichen Cursor")
            store.log(
                "Paste completed. Method=AXSelectedText TargetPID=\(target.processIdentifier) ClipboardChanged=false."
            )
            return
        }

        guard
            target.preserveWebViewCaret ||
            currentFocusedElement.map(isEditableKeyboardPasteElement) == true
        else {
            leaveDictationOnClipboard(
                text,
                reason: "focused-element-not-editable",
                promptForPermission: false
            )
            return
        }
        prepareKeyboardPaste(
            text,
            target: target,
            application: application,
            targetWasFrontmost: targetWasFrontmost
        )
    }

    private func verifiedDirectAXInsertionElement(
        target: PasteTarget,
        expectedElement: AXUIElement,
        application: NSRunningApplication,
        targetWasFrontmost: Bool
    ) -> AXUIElement? {
        guard case .match(let currentElement, _) = probePasteFocus(
            target,
            application: application,
            allowProcessScopedMatch: targetWasFrontmost
        ),
            let currentElement,
            CFEqual(currentElement, expectedElement)
        else {
            return nil
        }
        return currentElement
    }

    private func prepareKeyboardPaste(
        _ text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool
    ) {
        guard CGPreflightPostEventAccess() else {
            leaveDictationOnClipboard(
                text,
                reason: "permission eventPosting=false",
                promptForPermission: false
            )
            settingsModel.errorMessage =
                "Für das Einfügen in diese App benötigt ORhom zusätzlich die Freigabe zum Senden von Tastaturereignissen."
            promptForEventPostingPermission()
            return
        }

        let pasteboard = NSPasteboard.general
        guard let snapshot = captureStablePasteboardSnapshot(pasteboard) else {
            settingsModel.errorMessage =
                "Die Zwischenablage war gerade in Benutzung. Der Text bleibt im Diktierverlauf."
            setIdle(
                message: "Einfügen nicht ausgelöst",
                detail: "Zwischenablage war nicht stabil"
            )
            store.log("Paste failed. Reason=clipboard-snapshot-unstable.")
            return
        }
        var lastOwnedChangeCount: Int?
        guard let ownedChangeCount = writeTextToPasteboard(
            text,
            pasteboard: pasteboard,
            expectedInitialChangeCount: snapshot.changeCount,
            lastOwnedChangeCount: &lastOwnedChangeCount
        ) else {
            let restored =
                lastOwnedChangeCount != nil &&
                pasteboard.changeCount == lastOwnedChangeCount &&
                restorePasteboardSnapshot(
                    snapshot,
                    pasteboard: pasteboard,
                    fallbackText: text
                )
            let dictationRetained =
                pasteboard.string(forType: .string) == text
            settingsModel.errorMessage =
                "Der erkannte Text konnte nicht in die Zwischenablage geschrieben werden."
            setIdle(
                message: "Einfügen fehlgeschlagen",
                detail: restored
                    ? "Zwischenablage wurde wiederhergestellt"
                    : dictationRetained
                        ? "Diktat liegt in der Zwischenablage"
                        : "Text bleibt im Diktierverlauf"
            )
            store.log(
                "Paste failed. Reason=clipboard-write-failed ClipboardRestored=\(restored)."
            )
            return
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.025) { [weak self] in
            self?.confirmKeyboardPasteDispatch(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                snapshot: snapshot,
                ownedChangeCount: ownedChangeCount,
                remainingAttempts: 6,
                consecutiveMatches: 0
            )
        }
    }

    private func confirmKeyboardPasteDispatch(
        text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool,
        snapshot: PasteboardSnapshot,
        ownedChangeCount: Int,
        remainingAttempts: Int,
        consecutiveMatches: Int
    ) {
        guard state == .pasting else { return }
        let pasteboard = NSPasteboard.general
        guard
            pasteboard.changeCount == ownedChangeCount,
            pasteboard.string(forType: .string) == text
        else {
            setIdle(
                message: "Einfügen abgebrochen",
                detail: "Zwischenablage wurde zwischenzeitlich geändert"
            )
            store.log(
                "Paste dispatch aborted. Reason='clipboard-ownership-changed'."
            )
            return
        }

        switch probePasteFocus(
            target,
            application: application,
            allowProcessScopedMatch: targetWasFrontmost
        ) {
        case .match(_, let kind):
            let matches = consecutiveMatches + 1
            if matches >= 2 {
                dispatchKeyboardPaste(
                    text: text,
                    target: target,
                    application: application,
                    targetWasFrontmost: targetWasFrontmost,
                    snapshot: snapshot,
                    ownedChangeCount: ownedChangeCount,
                    focusMatchKind: kind
                )
                return
            }
            guard remainingAttempts > 1 else {
                finishWithOwnedClipboardFallback(
                    reason: "dispatch-focus-timeout consecutive-match",
                    text: text,
                    ownedChangeCount: ownedChangeCount
                )
                return
            }
            retryKeyboardPasteConfirmation(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                snapshot: snapshot,
                ownedChangeCount: ownedChangeCount,
                remainingAttempts: remainingAttempts,
                consecutiveMatches: matches
            )

        case .transient(let reason):
            guard remainingAttempts > 1 else {
                finishWithOwnedClipboardFallback(
                    reason: "dispatch-focus-timeout \(reason)",
                    text: text,
                    ownedChangeCount: ownedChangeCount
                )
                return
            }
            retryKeyboardPasteConfirmation(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                snapshot: snapshot,
                ownedChangeCount: ownedChangeCount,
                remainingAttempts: remainingAttempts,
                consecutiveMatches: 0
            )

        case .unsafe(let reason):
            finishWithOwnedClipboardFallback(
                reason: "dispatch-unsafe \(reason)",
                text: text,
                ownedChangeCount: ownedChangeCount
            )
        }
    }

    private func retryKeyboardPasteConfirmation(
        text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool,
        snapshot: PasteboardSnapshot,
        ownedChangeCount: Int,
        remainingAttempts: Int,
        consecutiveMatches: Int
    ) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.015) { [weak self] in
            self?.confirmKeyboardPasteDispatch(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                snapshot: snapshot,
                ownedChangeCount: ownedChangeCount,
                remainingAttempts: remainingAttempts - 1,
                consecutiveMatches: consecutiveMatches
            )
        }
    }

    private func dispatchKeyboardPaste(
        text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool,
        snapshot: PasteboardSnapshot,
        ownedChangeCount: Int,
        focusMatchKind: String,
        modifierReleaseAttempts: Int = 12
    ) {
        guard state == .pasting else { return }
        let pasteboard = NSPasteboard.general
        guard
            pasteboard.changeCount == ownedChangeCount,
            pasteboard.string(forType: .string) == text
        else {
            setIdle(
                message: "Einfügen abgebrochen",
                detail: "Zwischenablage wurde zwischenzeitlich geändert"
            )
            store.log(
                "Paste dispatch aborted. Reason='clipboard-changed-before-event'."
            )
            return
        }
        guard case .match(let confirmedElement, _) = probePasteFocus(
            target,
            application: application,
            allowProcessScopedMatch: targetWasFrontmost
        ) else {
            finishWithOwnedClipboardFallback(
                reason: "focus-changed-before-event",
                text: text,
                ownedChangeCount: ownedChangeCount
            )
            return
        }
        let relevantModifierFlags: CGEventFlags = [
            .maskCommand,
            .maskAlternate,
            .maskControl,
            .maskShift
        ]
        let activeModifierFlags = CGEventSource.flagsState(
            .combinedSessionState
        ).intersection(relevantModifierFlags)
        guard activeModifierFlags.isEmpty else {
            guard modifierReleaseAttempts > 1 else {
                finishWithOwnedClipboardFallback(
                    reason: "physical-modifier-still-pressed",
                    text: text,
                    ownedChangeCount: ownedChangeCount
                )
                return
            }
            DispatchQueue.main.asyncAfter(
                deadline: .now() + 0.025
            ) { [weak self] in
                self?.dispatchKeyboardPaste(
                    text: text,
                    target: target,
                    application: application,
                    targetWasFrontmost: targetWasFrontmost,
                    snapshot: snapshot,
                    ownedChangeCount: ownedChangeCount,
                    focusMatchKind: focusMatchKind,
                    modifierReleaseAttempts:
                        modifierReleaseAttempts - 1
                )
            }
            store.log(
                "Paste dispatch waiting for physical modifiers to be released. RemainingAttempts=\(modifierReleaseAttempts - 1)."
            )
            return
        }

        guard let source = CGEventSource(stateID: .hidSystemState) else {
            finishWithOwnedClipboardFallback(
                reason: "event-source-creation-failed",
                text: text,
                ownedChangeCount: ownedChangeCount
            )
            return
        }
        let events = MacPastePolicy.shortcutSequence.compactMap {
            step -> CGEvent? in
            let virtualKey: CGKeyCode
            let keyDown: Bool
            switch step {
            case .commandDown:
                virtualKey = CGKeyCode(kVK_Command)
                keyDown = true
            case .pasteKeyDown:
                virtualKey = CGKeyCode(kVK_ANSI_V)
                keyDown = true
            case .pasteKeyUp:
                virtualKey = CGKeyCode(kVK_ANSI_V)
                keyDown = false
            case .commandUp:
                virtualKey = CGKeyCode(kVK_Command)
                keyDown = false
            }
            guard let event = CGEvent(
                keyboardEventSource: source,
                virtualKey: virtualKey,
                keyDown: keyDown
            ) else {
                return nil
            }
            event.flags = step == .commandUp ? [] : .maskCommand
            return event
        }
        guard events.count == MacPastePolicy.shortcutSequence.count else {
            finishWithOwnedClipboardFallback(
                reason: "event-creation-failed",
                text: text,
                ownedChangeCount: ownedChangeCount
            )
            return
        }
        let observationBefore = confirmedElement.flatMap(
            pasteEffectObservation
        )
        for event in events {
            event.postToPid(target.processIdentifier)
        }
        store.log(
            "Paste dispatched. Method=CGEventPostToPid Sequence=CmdDown-VDown-VUp-CmdUp TargetPID=\(target.processIdentifier) FocusMatch='\(focusMatchKind)' ClipboardOwned=true."
        )

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.10) { [weak self] in
            self?.confirmKeyboardPasteEffect(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                snapshot: snapshot,
                ownedChangeCount: ownedChangeCount,
                observationBefore: observationBefore,
                remainingAttempts: 10
            )
        }
    }

    private func confirmKeyboardPasteEffect(
        text: String,
        target: PasteTarget,
        application: NSRunningApplication,
        targetWasFrontmost: Bool,
        snapshot: PasteboardSnapshot,
        ownedChangeCount: Int,
        observationBefore: PasteEffectObservation?,
        remainingAttempts: Int
    ) {
        guard state == .pasting else { return }
        let pasteboard = NSPasteboard.general
        guard
            pasteboard.changeCount == ownedChangeCount,
            pasteboard.string(forType: .string) == text
        else {
            setIdle(
                message: "Einfügen ausgelöst",
                detail: "Zwischenablage wurde zwischenzeitlich geändert"
            )
            store.log(
                "Paste verification stopped. Reason='clipboard-ownership-changed'."
            )
            return
        }

        let probe = probePasteFocus(
            target,
            application: application,
            allowProcessScopedMatch: targetWasFrontmost
        )
        if case .match(let currentElement, _) = probe,
           let observationBefore,
           let observationAfter = currentElement.flatMap(
               pasteEffectObservation
           ),
           observationAfter.differs(from: observationBefore) {
            let restored = restorePasteboardSnapshot(
                snapshot,
                pasteboard: pasteboard,
                fallbackText: text
            )
            let dictationRetained =
                pasteboard.string(forType: .string) == text
            setIdle(
                message: "Eingefügt",
                detail: restored
                    ? "Web-Editor bestätigt · Zwischenablage wiederhergestellt"
                    : dictationRetained
                        ? "Web-Editor bestätigt · Diktat in Zwischenablage"
                        : "Web-Editor bestätigt"
            )
            store.log(
                "Paste dispatch completed. Method=CGEventPostToPid TargetPID=\(target.processIdentifier) Verified=true ClipboardRestored=\(restored)."
            )
            return
        }

        guard remainingAttempts > 1 else {
            finishWithOwnedClipboardFallback(
                reason: "paste-effect-unconfirmed",
                text: text,
                ownedChangeCount: ownedChangeCount
            )
            store.log(
                "Paste dispatch completed. Method=CGEventPostToPid TargetPID=\(target.processIdentifier) Verified=false ClipboardRestored=false."
            )
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.10) { [weak self] in
            self?.confirmKeyboardPasteEffect(
                text: text,
                target: target,
                application: application,
                targetWasFrontmost: targetWasFrontmost,
                snapshot: snapshot,
                ownedChangeCount: ownedChangeCount,
                observationBefore: observationBefore,
                remainingAttempts: remainingAttempts - 1
            )
        }
    }

    private func captureStablePasteboardSnapshot(
        _ pasteboard: NSPasteboard
    ) -> PasteboardSnapshot? {
        let retryDelays: [TimeInterval] = [0, 0.010, 0.020, 0.040, 0.080]
        for delay in retryDelays {
            if delay > 0 {
                Thread.sleep(forTimeInterval: delay)
            }
            let changeCountBefore = pasteboard.changeCount
            var complete = true
            var items: [[NSPasteboard.PasteboardType: Data]] = []
            for item in pasteboard.pasteboardItems ?? [] {
                var contents: [NSPasteboard.PasteboardType: Data] = [:]
                for type in item.types {
                    guard let data = item.data(forType: type) else {
                        complete = false
                        break
                    }
                    contents[type] = data
                }
                guard complete else { break }
                items.append(contents)
            }
            if complete && pasteboard.changeCount == changeCountBefore {
                return PasteboardSnapshot(
                    changeCount: changeCountBefore,
                    items: items
                )
            }
        }
        return nil
    }

    private func writeTextToPasteboard(
        _ text: String,
        pasteboard: NSPasteboard,
        expectedInitialChangeCount: Int,
        lastOwnedChangeCount: inout Int?
    ) -> Int? {
        let retryDelays: [TimeInterval] = [0, 0.020, 0.040, 0.060]
        for delay in retryDelays {
            if delay > 0 {
                Thread.sleep(forTimeInterval: delay)
            }
            let expectedChangeCount =
                lastOwnedChangeCount ?? expectedInitialChangeCount
            guard pasteboard.changeCount == expectedChangeCount else {
                return nil
            }
            pasteboard.clearContents()
            lastOwnedChangeCount = pasteboard.changeCount
            guard pasteboard.setString(text, forType: .string) else {
                continue
            }
            let ownedChangeCount = pasteboard.changeCount
            lastOwnedChangeCount = ownedChangeCount
            if pasteboard.string(forType: .string) == text &&
                pasteboard.changeCount == ownedChangeCount {
                return ownedChangeCount
            }
        }
        return nil
    }

    private func restorePasteboardSnapshot(
        _ snapshot: PasteboardSnapshot,
        pasteboard: NSPasteboard,
        fallbackText: String
    ) -> Bool {
        var expectedOwnedChangeCount = pasteboard.changeCount
        let retryDelays: [TimeInterval] = [0, 0.020, 0.050]

        for delay in retryDelays {
            if delay > 0 {
                Thread.sleep(forTimeInterval: delay)
            }
            guard pasteboard.changeCount == expectedOwnedChangeCount else {
                return false
            }

            var restoredItems: [NSPasteboardItem] = []
            var itemsAreComplete = true
            for contents in snapshot.items {
                let item = NSPasteboardItem()
                for (type, data) in contents {
                    guard item.setData(data, forType: type) else {
                        itemsAreComplete = false
                        break
                    }
                }
                guard itemsAreComplete else { break }
                restoredItems.append(item)
            }
            guard itemsAreComplete else { break }

            pasteboard.clearContents()
            let clearedChangeCount = pasteboard.changeCount
            let wroteSnapshot =
                restoredItems.isEmpty ||
                pasteboard.writeObjects(restoredItems)
            let postWriteChangeCount = pasteboard.changeCount
            if wroteSnapshot &&
                pasteboardMatchesSnapshot(
                    snapshot,
                    pasteboard: pasteboard
                ) {
                return true
            }
            if !wroteSnapshot &&
                postWriteChangeCount != clearedChangeCount {
                return false
            }
            expectedOwnedChangeCount = postWriteChangeCount
        }

        guard pasteboard.changeCount == expectedOwnedChangeCount else {
            return false
        }
        pasteboard.clearContents()
        let fallbackWritten = pasteboard.setString(
            fallbackText,
            forType: .string
        )
        let fallbackChangeCount = pasteboard.changeCount
        let fallbackVerified = fallbackWritten &&
            pasteboard.string(forType: .string) == fallbackText &&
            pasteboard.changeCount == fallbackChangeCount
        if !fallbackVerified {
            store.log(
                "Clipboard recovery failed after snapshot restore failure."
            )
        }
        return false
    }

    private func pasteboardMatchesSnapshot(
        _ snapshot: PasteboardSnapshot,
        pasteboard: NSPasteboard
    ) -> Bool {
        let currentItems = pasteboard.pasteboardItems ?? []
        guard currentItems.count == snapshot.items.count else {
            return false
        }
        for (currentItem, expectedContents) in zip(
            currentItems,
            snapshot.items
        ) {
            for (type, expectedData) in expectedContents {
                guard currentItem.data(forType: type) == expectedData else {
                    return false
                }
            }
        }
        return true
    }

    private func finishWithOwnedClipboardFallback(
        reason: String,
        text: String,
        ownedChangeCount: Int
    ) {
        let pasteboard = NSPasteboard.general
        let clipboardStillOwned =
            pasteboard.changeCount == ownedChangeCount &&
            pasteboard.string(forType: .string) == text
        setIdle(
            message: clipboardStillOwned
                ? "In Zwischenablage"
                : "Einfügen abgebrochen",
            detail: clipboardStillOwned
                ? "Mit ⌘V am Cursor einfügen"
                : "Text bleibt im Diktierverlauf"
        )
        store.log(
            "Paste fallback. Reason='\(reason)' ClipboardStillOwned=\(clipboardStillOwned)."
        )
    }

    private func focusedAXElement(
        for processIdentifier: pid_t,
        applicationElement: AXUIElement
    ) -> AXUIElement? {
        let systemWideElement = AXUIElementCreateSystemWide()
        _ = AXUIElementSetMessagingTimeout(systemWideElement, 0.08)
        if let globalElement = copyAXElement(
            from: systemWideElement,
            attribute: kAXFocusedUIElementAttribute
        ) {
            var elementProcessIdentifier = pid_t()
            if AXUIElementGetPid(
                globalElement,
                &elementProcessIdentifier
            ) == .success,
               elementProcessIdentifier == processIdentifier {
                return globalElement
            }
        }
        return copyAXElement(
            from: applicationElement,
            attribute: kAXFocusedUIElementAttribute
        )
    }

    private func matchingWindowDescriptor(
        for processIdentifier: pid_t,
        focusedWindow: AXUIElement?,
        title: String?,
        frame: CGRect?,
        includeNonstandardLayers: Bool = false
    ) -> CGWindowDescriptor? {
        let windows = visibleWindowDescriptors(
            for: processIdentifier,
            includeNonstandardLayers: includeNonstandardLayers
        )
        guard !windows.isEmpty else { return nil }
        guard focusedWindow != nil else {
            return windows.first
        }

        if let frame {
            let frameMatches = windows.filter {
                guard let candidateFrame = $0.frame else { return false }
                return windowFramesMatch(frame, candidateFrame)
            }
            if frameMatches.count == 1 {
                return frameMatches[0]
            }
            if frameMatches.count > 1,
               let title,
               !title.isEmpty,
               let titledMatch = frameMatches.first(where: {
                   $0.title == title
               }) {
                return titledMatch
            }
        }

        if let title, !title.isEmpty {
            let titleMatches = windows.filter { $0.title == title }
            if titleMatches.count == 1 {
                return titleMatches[0]
            }
        }
        return windows.count == 1 ? windows[0] : nil
    }

    private func topmostWindowDescriptor(
        for processIdentifier: pid_t,
        includeNonstandardLayers: Bool = false
    ) -> CGWindowDescriptor? {
        visibleWindowDescriptors(
            for: processIdentifier,
            includeNonstandardLayers: includeNonstandardLayers
        ).first
    }

    private func visibleWindowDescriptors(
        for processIdentifier: pid_t,
        includeNonstandardLayers: Bool = false
    ) -> [CGWindowDescriptor] {
        guard
            let windowInfo = CGWindowListCopyWindowInfo(
                [.optionOnScreenOnly, .excludeDesktopElements],
                kCGNullWindowID
            ) as? [[String: Any]]
        else {
            return []
        }

        var windows: [CGWindowDescriptor] = []
        for entry in windowInfo {
            guard
                (entry[kCGWindowOwnerPID as String] as? NSNumber)?
                    .int32Value == processIdentifier,
                let layer = (
                    entry[kCGWindowLayer as String] as? NSNumber
                )?.intValue,
                includeNonstandardLayers || layer == 0,
                let number = entry[kCGWindowNumber as String] as? NSNumber
            else {
                continue
            }
            var frame: CGRect?
            if let bounds =
                entry[kCGWindowBounds as String] as? [String: Any],
               let x = (bounds["X"] as? NSNumber)?.doubleValue,
               let y = (bounds["Y"] as? NSNumber)?.doubleValue,
               let width = (bounds["Width"] as? NSNumber)?.doubleValue,
               let height = (bounds["Height"] as? NSNumber)?.doubleValue {
                frame = CGRect(
                    x: x,
                    y: y,
                    width: width,
                    height: height
                )
            }
            windows.append(
                CGWindowDescriptor(
                    number: CGWindowID(number.uint32Value),
                    title: entry[kCGWindowName as String] as? String,
                    frame: frame,
                    layer: layer
                )
            )
        }
        return windows
    }

    private func windowFramesMatch(_ lhs: CGRect, _ rhs: CGRect) -> Bool {
        abs(lhs.minX - rhs.minX) <= 4 &&
            abs(lhs.minY - rhs.minY) <= 4 &&
            abs(lhs.width - rhs.width) <= 6 &&
            abs(lhs.height - rhs.height) <= 6
    }

    private func copyAXFrame(_ element: AXUIElement) -> CGRect? {
        var positionValue: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                kAXPositionAttribute as CFString,
                &positionValue
            ) == .success,
            let positionValue,
            CFGetTypeID(positionValue) == AXValueGetTypeID()
        else {
            return nil
        }
        var sizeValue: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                kAXSizeAttribute as CFString,
                &sizeValue
            ) == .success,
            let sizeValue,
            CFGetTypeID(sizeValue) == AXValueGetTypeID()
        else {
            return nil
        }

        let positionAXValue = positionValue as! AXValue
        let sizeAXValue = sizeValue as! AXValue
        guard
            AXValueGetType(positionAXValue) == .cgPoint,
            AXValueGetType(sizeAXValue) == .cgSize
        else {
            return nil
        }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard
            AXValueGetValue(positionAXValue, .cgPoint, &position),
            AXValueGetValue(sizeAXValue, .cgSize, &size)
        else {
            return nil
        }
        return CGRect(origin: position, size: size)
    }

    private func makeLayoutFingerprint(
        elementFrame: CGRect?,
        windowFrame: CGRect?
    ) -> AXLayoutFingerprint? {
        guard
            let elementFrame,
            let windowFrame,
            windowFrame.width > 1,
            windowFrame.height > 1,
            elementFrame.width > 0,
            elementFrame.height > 0
        else {
            return nil
        }
        return AXLayoutFingerprint(
            relativeX: (elementFrame.minX - windowFrame.minX) /
                windowFrame.width,
            relativeY: (elementFrame.minY - windowFrame.minY) /
                windowFrame.height,
            relativeWidth: elementFrame.width / windowFrame.width,
            relativeHeight: elementFrame.height / windowFrame.height
        )
    }

    private func isElement(
        _ element: AXUIElement,
        within ancestor: AXUIElement
    ) -> Bool {
        var current: AXUIElement? = element
        let deadline = CFAbsoluteTimeGetCurrent() + 0.18
        for _ in 0..<80 {
            guard CFAbsoluteTimeGetCurrent() < deadline else {
                return false
            }
            guard let candidate = current else {
                return false
            }
            if CFEqual(candidate, ancestor) {
                return true
            }
            current = copyAXElement(
                from: candidate,
                attribute: kAXParentAttribute
            )
        }
        return false
    }

    private func copyAncestorWindow(
        from element: AXUIElement
    ) -> AXUIElement? {
        var current: AXUIElement? = element
        let deadline = CFAbsoluteTimeGetCurrent() + 0.18
        for _ in 0..<80 {
            guard
                CFAbsoluteTimeGetCurrent() < deadline,
                let candidate = current
            else {
                return nil
            }
            if copyAXString(
                from: candidate,
                attribute: kAXRoleAttribute
            ) == (kAXWindowRole as String) {
                return candidate
            }
            current = copyAXElement(
                from: candidate,
                attribute: kAXParentAttribute
            )
        }
        return nil
    }

    private func copyWebAreaAncestor(
        from element: AXUIElement
    ) -> AXUIElement? {
        var current: AXUIElement? = element
        let deadline = CFAbsoluteTimeGetCurrent() + 0.18
        for _ in 0..<80 {
            guard
                CFAbsoluteTimeGetCurrent() < deadline,
                let candidate = current
            else {
                return nil
            }
            if copyAXString(
                from: candidate,
                attribute: kAXRoleAttribute
            ) == "AXWebArea" {
                return candidate
            }
            current = copyAXElement(
                from: candidate,
                attribute: kAXParentAttribute
            )
        }
        return nil
    }

    private func allowsNilElementWindowPaste(
        _ bundleIdentifier: String?
    ) -> Bool {
        guard let bundleIdentifier else { return false }
        return [
            "com.microsoft.VSCode",
            "com.microsoft.VSCodeInsiders",
            "com.cursor.Cursor",
            "com.todesktop.230313mzl4w4u92"
        ].contains(bundleIdentifier)
    }

    private func shouldPreserveWebViewCaret(
        bundleIdentifier: String?,
        role: String?,
        hasWebAreaAncestor: Bool,
        elementMissing: Bool
    ) -> Bool {
        MacPastePolicy.shouldUseKeyboardPaste(
            isKnownWebViewBundle: isKnownWebViewBundle(bundleIdentifier),
            focusedRole: role,
            hasWebAreaAncestor: hasWebAreaAncestor,
            focusedElementMissing: elementMissing
        )
    }

    private func isKnownWebViewBundle(_ bundleIdentifier: String?) -> Bool {
        guard let bundleIdentifier else { return false }
        let exactMatches: Set<String> = [
            "com.microsoft.VSCode",
            "com.microsoft.VSCodeInsiders",
            "com.cursor.Cursor",
            "com.todesktop.230313mzl4w4u92",
            "com.google.Chrome",
            "com.google.Chrome.beta",
            "com.google.Chrome.canary",
            "org.chromium.Chromium",
            "com.microsoft.edgemac",
            "com.brave.Browser",
            "company.thebrowser.Browser",
            "org.mozilla.firefox",
            "com.apple.Safari",
            "com.openai.chat"
        ]
        return exactMatches.contains(bundleIdentifier) ||
            bundleIdentifier.hasPrefix("com.operasoftware.")
    }

    private func isEditableAXRole(_ role: String) -> Bool {
        role == (kAXTextFieldRole as String) ||
            role == (kAXTextAreaRole as String) ||
            role == (kAXComboBoxRole as String) ||
            role == "AXSearchField"
    }

    private func isEditableKeyboardPasteElement(
        _ element: AXUIElement
    ) -> Bool {
        guard
            let role = copyAXString(
                from: element,
                attribute: kAXRoleAttribute
            ),
            isEditableAXRole(role),
            !isSecureAXElement(element)
        else {
            return false
        }
        return copyAXBoolean(
            from: element,
            attribute: kAXEnabledAttribute
        ) != false
    }

    private func isWebKeyboardPasteElement(
        _ element: AXUIElement
    ) -> Bool {
        guard
            let role = copyAXString(
                from: element,
                attribute: kAXRoleAttribute
            ),
            !isSecureAXElement(element)
        else {
            return false
        }
        if isEditableAXRole(role) {
            return copyAXBoolean(
                from: element,
                attribute: kAXEnabledAttribute
            ) != false
        }
        let supportedWebRootRole =
            role == "AXWebArea" ||
            role == "AXGroup" ||
            role == "AXScrollArea"
        guard supportedWebRootRole else {
            return false
        }
        let hasPositiveEditingSignal =
            copyAXBoolean(
                from: element,
                attribute: "AXEditable"
            ) == true ||
            copyAXElement(
                from: element,
                attribute: "AXEditableAncestor"
            ) != nil ||
            copyAXElement(
                from: element,
                attribute: "AXHighestEditableAncestor"
            ) != nil
        return hasPositiveEditingSignal &&
            copyAXBoolean(
                from: element,
                attribute: kAXEnabledAttribute
            ) != false
    }

    private func isSecureAXElement(_ element: AXUIElement?) -> Bool {
        guard let element else { return false }
        return copyAXString(
            from: element,
            attribute: kAXSubroleAttribute
        ) == (kAXSecureTextFieldSubrole as String)
    }

    private func leaveDictationOnClipboard(
        _ text: String,
        reason: String,
        promptForPermission: Bool
    ) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        let copied = pasteboard.setString(text, forType: .string)
        setIdle(
            message: copied ? "In Zwischenablage" : "Einfügen fehlgeschlagen",
            detail: copied
                ? "Mit ⌘V am Cursor einfügen"
                : "Text bleibt im Diktierverlauf"
        )
        store.log(
            "Paste fallback. Reason='\(reason)' ClipboardWritten=\(copied)."
        )
        if promptForPermission {
            settingsModel.errorMessage =
                "Für automatisches Einfügen muss ORhom unter „Bedienungshilfen“ erlaubt sein."
            promptForAutomaticPastePermission()
        }
    }

    private func copyAXElement(
        from element: AXUIElement,
        attribute: String
    ) -> AXUIElement? {
        var value: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                attribute as CFString,
                &value
            ) == .success,
            let value,
            CFGetTypeID(value) == AXUIElementGetTypeID()
        else {
            return nil
        }
        return (value as! AXUIElement)
    }

    private func copyAXString(
        from element: AXUIElement,
        attribute: String
    ) -> String? {
        var value: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                attribute as CFString,
                &value
            ) == .success
        else {
            return nil
        }
        return value as? String
    }

    private func copyAXBoolean(
        from element: AXUIElement,
        attribute: String
    ) -> Bool? {
        var value: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                attribute as CFString,
                &value
            ) == .success,
            let value
        else {
            return nil
        }
        if CFGetTypeID(value) == CFBooleanGetTypeID() {
            return CFBooleanGetValue((value as! CFBoolean))
        }
        return (value as? NSNumber)?.boolValue
    }

    private func pasteEffectObservation(
        _ element: AXUIElement
    ) -> PasteEffectObservation? {
        let value = copyAXString(
            from: element,
            attribute: kAXValueAttribute
        )
        let range = copyAXRange(
            from: element,
            attribute: kAXSelectedTextRangeAttribute
        )
        let markerHash = copyAXValueHash(
            from: element,
            attribute: "AXSelectedTextMarkerRange"
        )
        guard value != nil || range != nil || markerHash != nil else {
            return nil
        }
        return PasteEffectObservation(
            valueLength: value?.count,
            valueHash: value?.hashValue,
            selectedRangeLocation: range?.location,
            selectedRangeLength: range?.length,
            selectedMarkerHash: markerHash
        )
    }

    private func copyAXRange(
        from element: AXUIElement,
        attribute: String
    ) -> CFRange? {
        var value: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                attribute as CFString,
                &value
            ) == .success,
            let value,
            CFGetTypeID(value) == AXValueGetTypeID()
        else {
            return nil
        }
        let axValue = value as! AXValue
        guard AXValueGetType(axValue) == .cfRange else {
            return nil
        }
        var range = CFRange()
        return AXValueGetValue(axValue, .cfRange, &range)
            ? range
            : nil
    }

    private func copyAXValueHash(
        from element: AXUIElement,
        attribute: String
    ) -> CFHashCode? {
        var value: CFTypeRef?
        guard
            AXUIElementCopyAttributeValue(
                element,
                attribute as CFString,
                &value
            ) == .success,
            let value
        else {
            return nil
        }
        return CFHash(value)
    }

    private func hasAXAttributeValue(
        _ element: AXUIElement,
        attribute: String
    ) -> Bool {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(
            element,
            attribute as CFString,
            &value
        ) == .success && value != nil
    }

    private func isAXAttributeSettable(
        _ element: AXUIElement,
        attribute: String
    ) -> Bool {
        var settable = DarwinBoolean(false)
        return AXUIElementIsAttributeSettable(
            element,
            attribute as CFString,
            &settable
        ) == .success && settable.boolValue
    }

    private func createStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(
                systemSymbolName: "waveform.circle.fill",
                accessibilityDescription: "ORhom"
            )
            button.image?.isTemplate = true
            button.toolTip = "ORhom"
        }

        let menu = NSMenu()
        menu.delegate = self
        statusMenuItem = NSMenuItem(
            title: "Lokale Engine wird vorbereitet",
            action: nil,
            keyEquivalent: ""
        )
        statusMenuItem.isEnabled = false

        primaryMenuItem = NSMenuItem(
            title: "Diktierung starten",
            action: #selector(toggleDictation),
            keyEquivalent: ""
        )
        primaryMenuItem.target = self
        cancelMenuItem = NSMenuItem(
            title: "Aufnahme verwerfen",
            action: #selector(cancelDictation),
            keyEquivalent: ""
        )
        cancelMenuItem.target = self
        lastDictationMenuItem = NSMenuItem(
            title: "Noch kein Diktat zum Einfügen",
            action: #selector(pasteLastDictation),
            keyEquivalent: ""
        )
        lastDictationMenuItem.target = self

        let history = NSMenuItem(
            title: "Diktierverlauf …",
            action: #selector(showHistory),
            keyEquivalent: ""
        )
        history.target = self
        overlayMenuItem = NSMenuItem(
            title: "Diktierleiste anzeigen",
            action: #selector(toggleOverlayFromMenu),
            keyEquivalent: ""
        )
        overlayMenuItem.target = self
        loginMenuItem = NSMenuItem(
            title: "Beim Anmelden starten",
            action: #selector(toggleLoginFromMenu),
            keyEquivalent: ""
        )
        loginMenuItem.target = self

        let permissions = NSMenuItem(
            title: "Berechtigungen prüfen …",
            action: #selector(requestPermissions),
            keyEquivalent: ""
        )
        permissions.target = self
        let settings = NSMenuItem(
            title: "ORhom öffnen …",
            action: #selector(openSettings),
            keyEquivalent: ","
        )
        settings.target = self
        let quit = NSMenuItem(
            title: "Beenden",
            action: #selector(self.quit),
            keyEquivalent: "q"
        )
        quit.target = self

        menu.addItem(statusMenuItem)
        menu.addItem(.separator())
        menu.addItem(primaryMenuItem)
        menu.addItem(cancelMenuItem)
        menu.addItem(lastDictationMenuItem)
        menu.addItem(history)
        menu.addItem(.separator())
        menu.addItem(overlayMenuItem)
        menu.addItem(loginMenuItem)
        menu.addItem(permissions)
        menu.addItem(settings)
        menu.addItem(.separator())
        menu.addItem(quit)
        statusItem.menu = menu
        refreshMenu()
    }

    private func refreshMenu() {
        guard primaryMenuItem != nil else { return }
        switch state {
        case .idle:
            primaryMenuItem.title = "Diktierung starten"
            primaryMenuItem.isEnabled = engineReady
            cancelMenuItem.isHidden = true
        case .recording:
            primaryMenuItem.title = "Stopp & einfügen"
            primaryMenuItem.isEnabled = true
            cancelMenuItem.isHidden = false
        case .error:
            primaryMenuItem.title = "Erneut versuchen"
            primaryMenuItem.isEnabled = true
            cancelMenuItem.isHidden = true
        case .preparing, .starting, .transcribing, .pasting:
            if state == .transcribing {
                primaryMenuItem.title = "Transkription läuft"
            } else if state == .pasting {
                primaryMenuItem.title = "Text wird eingefügt"
            } else {
                primaryMenuItem.title = "Wird vorbereitet"
            }
            primaryMenuItem.isEnabled = false
            cancelMenuItem.isHidden = true
        }

        if store.entries().first?.text.isEmpty == false {
            lastDictationMenuItem.title = "Letztes Diktat einfügen"
            lastDictationMenuItem.isEnabled = state == .idle || state == .error
        } else {
            lastDictationMenuItem.title = "Noch kein Diktat zum Einfügen"
            lastDictationMenuItem.isEnabled = false
        }
        overlayMenuItem.state = showOverlay ? .on : .off
        loginMenuItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }

    @objc private func pasteLastDictation() {
        guard
            state == .idle || state == .error,
            let text = store.entries().first?.text
        else {
            NSSound.beep()
            return
        }
        let target = captureCurrentPasteTarget()
        pasteTargetInputGeneration = currentInputActivityGeneration()
        state = .pasting
        setOverlay(
            mode: .transcribing,
            title: "Letztes Diktat",
            detail: "Text wird eingefügt"
        )
        refreshMenu()
        paste(text, into: target)
    }

    @objc private func showHistory() {
        let entries = store.entries()
        let alert = NSAlert()
        alert.messageText = "Diktierverlauf"
        alert.informativeText = entries.isEmpty
            ? "Noch keine Diktate vorhanden."
            : "Text markieren und mit ⌘C kopieren. Das neueste Diktat ist bereits ausgewählt."
        var initialSelection = NSRange(location: 0, length: 0)
        if !entries.isEmpty {
            let dateFormatter = DateFormatter()
            dateFormatter.locale = Locale(identifier: "de_AT")
            dateFormatter.dateStyle = .short
            dateFormatter.timeStyle = .short
            let formatted = NSMutableString()
            for (index, entry) in entries.enumerated() {
                if index > 0 {
                    formatted.append("\n\n")
                }
                formatted.append(
                    "\(index + 1). \(entry.outcome) · \(dateFormatter.string(from: entry.date))\n"
                )
                let textStart = formatted.length
                formatted.append(entry.text)
                if index == 0 {
                    initialSelection = NSRange(
                        location: textStart,
                        length: (entry.text as NSString).length
                    )
                }
            }

            let scrollView = NSScrollView(
                frame: NSRect(x: 0, y: 0, width: 590, height: 350)
            )
            scrollView.hasVerticalScroller = true
            scrollView.hasHorizontalScroller = false
            scrollView.autohidesScrollers = true
            scrollView.borderType = .bezelBorder
            let textView = SelectableHistoryTextView(
                frame: scrollView.contentView.bounds
            )
            textView.string = formatted as String
            textView.isEditable = false
            textView.isSelectable = true
            textView.isRichText = false
            textView.font = NSFont.monospacedSystemFont(
                ofSize: 12.5,
                weight: .regular
            )
            textView.textContainerInset = NSSize(width: 10, height: 10)
            textView.autoresizingMask = [.width]
            textView.minSize = NSSize(width: 0, height: 350)
            textView.maxSize = NSSize(
                width: CGFloat.greatestFiniteMagnitude,
                height: CGFloat.greatestFiniteMagnitude
            )
            textView.isVerticallyResizable = true
            textView.isHorizontallyResizable = false
            textView.textContainer?.containerSize = NSSize(
                width: scrollView.contentSize.width,
                height: CGFloat.greatestFiniteMagnitude
            )
            textView.textContainer?.widthTracksTextView = true
            textView.setSelectedRange(initialSelection)
            scrollView.documentView = textView
            alert.accessoryView = scrollView
            alert.window.initialFirstResponder = textView
        }
        alert.addButton(withTitle: "Schließen")
        if !entries.isEmpty {
            alert.addButton(withTitle: "Verlauf löschen")
        }
        NSApp.activate(ignoringOtherApps: true)
        if let textView =
            (alert.accessoryView as? NSScrollView)?.documentView
                as? SelectableHistoryTextView {
            alert.window.makeFirstResponder(textView)
        }
        if alert.runModal() == .alertSecondButtonReturn {
            store.clearHistory()
            refreshMenu()
        }
    }

    @objc private func requestPermissions() {
        let options = [
            kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true
        ] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        if !CGPreflightPostEventAccess() {
            _ = CGRequestPostEventAccess()
        }
        startPermissionRefreshTimer()
        recorder.requestPermission { [weak self] _ in
            self?.refreshPermissionState()
        }
    }

    private func refreshPermissionState() {
        settingsModel.microphoneAllowed =
            AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        settingsModel.accessibilityAllowed = AXIsProcessTrusted()
        settingsModel.eventPostingAllowed = CGPreflightPostEventAccess()
        refreshMicrophoneSettings()
        settingsModel.launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    private func requestAutomaticPastePermissionOnFirstRun() {
        let defaultsKey = "setupPermissionPromptShownV2"
        guard
            !settingsModel.accessibilityAllowed ||
                !settingsModel.eventPostingAllowed ||
                !settingsModel.microphoneAllowed,
            !UserDefaults.standard.bool(forKey: defaultsKey)
        else {
            return
        }
        UserDefaults.standard.set(true, forKey: defaultsKey)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in
            self?.openSettings()
            self?.requestPermissions()
        }
    }

    private func promptForAutomaticPastePermission() {
        guard !settingsModel.accessibilityAllowed else { return }
        if !didPromptForAutomaticPasteThisRun {
            didPromptForAutomaticPasteThisRun = true
            let options = [
                kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true
            ] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
        }
        openSettings()
        startPermissionRefreshTimer()
    }

    private func promptForEventPostingPermission() {
        guard !CGPreflightPostEventAccess() else { return }
        if !didPromptForEventPostingThisRun {
            didPromptForEventPostingThisRun = true
            _ = CGRequestPostEventAccess()
        }
        openSettings()
    }

    private func startPermissionRefreshTimer() {
        permissionRefreshTimer?.invalidate()
        let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] timer in
            guard let self else {
                timer.invalidate()
                return
            }
            self.refreshPermissionState()
            if (
                self.settingsModel.accessibilityAllowed &&
                self.settingsModel.eventPostingAllowed
            ) ||
                !self.settingsWindow.isVisible {
                timer.invalidate()
                self.permissionRefreshTimer = nil
            }
        }
        permissionRefreshTimer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    @objc private func captureDevicesChanged() {
        guard Thread.isMainThread else {
            DispatchQueue.main.async { [weak self] in
                self?.handleCaptureDevicesChanged()
            }
            return
        }
        handleCaptureDevicesChanged()
    }

    private func handleCaptureDevicesChanged() {
        refreshMicrophoneSettings()
        if state == .recording, recorder.selectedMicrophone == nil {
            store.log("Selected microphone disconnected during recording.")
            presentError(
                ORhomError.audioInputInterrupted(
                    "Das ausgewählte Mikrofon wurde getrennt."
                )
            )
        }
    }

    private func refreshMicrophoneSettings() {
        let unavailableMessage =
            "Das ausgewählte Mikrofon ist nicht verbunden. Bitte ein verfügbares Mikrofon wählen."
        let microphones = recorder.availableMicrophones
        settingsModel.microphones = microphones
        settingsModel.selectedMicrophoneID =
            recorder.selectedMicrophoneUniqueID ?? ""
        settingsModel.microphoneName = recorder.microphoneName

        if recorder.selectedMicrophoneUniqueID != nil,
           recorder.selectedMicrophone == nil {
            settingsModel.errorMessage = unavailableMessage
        } else if settingsModel.errorMessage == unavailableMessage {
            settingsModel.errorMessage = nil
        }
    }

    private func selectMicrophone(uniqueID: String) {
        guard
            state != .starting,
            state != .recording,
            state != .transcribing,
            state != .pasting
        else {
            refreshMicrophoneSettings()
            settingsModel.errorMessage =
                "Das Mikrofon kann erst nach der laufenden Diktierung gewechselt werden."
            return
        }
        guard recorder.availableMicrophones.contains(where: { $0.id == uniqueID }) else {
            refreshMicrophoneSettings()
            settingsModel.errorMessage =
                "Dieses Mikrofon ist nicht mehr verbunden."
            return
        }

        recorder.selectedMicrophoneUniqueID = uniqueID
        refreshMicrophoneSettings()
        settingsModel.errorMessage = nil
        store.log(
            "Microphone selection changed. ID='\(uniqueID)' Name='\(recorder.microphoneName)'."
        )
        if state == .idle || (state == .error && engineReady) {
            setIdle(
                message: "Mikrofon gespeichert",
                detail: recorder.microphoneName
            )
        }
    }

    private func openPrivacyPane(_ pane: String) {
        guard let url = URL(
            string: "x-apple.systempreferences:com.apple.preference.security?\(pane)"
        ) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func openSettings() {
        refreshPermissionState()
        NSApp.setActivationPolicy(.regular)
        settingsWindow.present()
        if !settingsModel.accessibilityAllowed ||
            !settingsModel.eventPostingAllowed {
            startPermissionRefreshTimer()
        }
    }

    @objc private func toggleOverlayFromMenu() {
        setOverlayEnabled(!showOverlay)
    }

    private func setOverlayEnabled(_ enabled: Bool) {
        showOverlay = enabled
        settingsModel.showOverlay = enabled
        if enabled {
            overlay.show()
        } else {
            overlay.hide()
        }
        refreshMenu()
    }

    private func setOverlaySize(_ preset: OverlaySizePreset) {
        overlaySizePreset = preset
        settingsModel.overlaySize = preset
        overlayModel.sizePreset = preset
        overlay.applySizePreset(preset)
        store.log(
            "Recording overlay size changed. Preset='\(preset.rawValue)'."
        )
    }

    private func setAudioDuckingEnabled(_ enabled: Bool) {
        UserDefaults.standard.set(
            enabled,
            forKey: MacAudioDuckingService.enabledDefaultsKey
        )
        settingsModel.audioDuckingEnabled = enabled
        audioDucking.restartWithCurrentSettings()
        store.log("Audio ducking setting changed. Enabled=\(enabled).")
    }

    private func setAudioDuckingVolumePercent(_ percent: Int) {
        let clamped = min(40, max(0, percent))
        UserDefaults.standard.set(
            clamped,
            forKey: MacAudioDuckingService.volumePercentDefaultsKey
        )
        settingsModel.audioDuckingVolumePercent = clamped
        audioDucking.restartWithCurrentSettings()
        store.log(
            "Audio ducking volume changed. VolumePercent=\(clamped)."
        )
    }

    @objc private func toggleLoginFromMenu() {
        setLaunchAtLogin(SMAppService.mainApp.status != .enabled)
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            settingsModel.launchAtLogin = enabled
        } catch {
            settingsModel.errorMessage =
                "Autostart konnte nicht geändert werden: \(error.localizedDescription)"
            settingsModel.launchAtLogin = SMAppService.mainApp.status == .enabled
        }
        refreshMenu()
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    private func registerGlobalHotKeys() {
        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )
        let userData = Unmanaged.passUnretained(self).toOpaque()
        let installStatus = InstallEventHandler(
            GetApplicationEventTarget(),
            globalHotKeyHandler,
            1,
            &eventType,
            userData,
            &hotKeyHandlerRef
        )
        guard installStatus == noErr else {
            store.log("Global hotkey handler registration failed. Status=\(installStatus)")
            settingsModel.errorMessage =
                "Das globale Tastenkürzel konnte nicht aktiviert werden."
            return
        }

        let registration = registerHotKey(activeHotKey)
        if let reference = registration.reference {
            hotKeyRef = reference
            store.log("Global hotkey registered. Shortcut='\(activeHotKey.displayName)'.")
            return
        }

        store.log(
            "Saved hotkey unavailable. Shortcut='\(activeHotKey.displayName)' Status=\(registration.status)."
        )
        guard activeHotKey != .standard else {
            settingsModel.errorMessage =
                "Das Standard-Tastenkürzel F8 ist bereits belegt. Bitte ein anderes auswählen."
            return
        }

        let savedHotKey = activeHotKey
        let fallback = registerHotKey(.standard)
        hotKeyRef = fallback.reference
        if fallback.reference != nil {
            activeHotKey = .standard
            activeHotKeyIsTemporaryFallback = true
            settingsModel.hotKey = .standard
            settingsModel.errorMessage =
                "\(savedHotKey.displayName) ist derzeit belegt. Vorübergehend ist F8 aktiv; die gespeicherte Auswahl bleibt erhalten."
        } else {
            settingsModel.hotKey = savedHotKey
            settingsModel.errorMessage =
                "\(savedHotKey.displayName) und F8 sind bereits belegt. Bitte ein anderes Tastenkürzel auswählen."
        }
    }

    private func registerHotKey(
        _ configuration: HotKeyConfiguration
    ) -> (reference: EventHotKeyRef?, status: OSStatus) {
        let identifier = EventHotKeyID(
            signature: hotKeySignature,
            id: nextHotKeyID
        )
        nextHotKeyID &+= 1
        var reference: EventHotKeyRef?
        let status = RegisterEventHotKey(
            configuration.keyCode,
            configuration.modifiers,
            identifier,
            GetApplicationEventTarget(),
            OptionBits(kEventHotKeyExclusive),
            &reference
        )
        guard status == noErr else {
            return (nil, status)
        }
        return (reference, status)
    }

    private func applyHotKey(_ configuration: HotKeyConfiguration) {
        guard settingsModel.configurationEnabled else {
            settingsModel.hotKey = activeHotKey
            settingsModel.errorMessage =
                "Das Tastenkürzel kann erst nach der laufenden Diktierung geändert werden."
            return
        }
        settingsModel.errorMessage = nil
        if configuration == activeHotKey, let hotKeyRef {
            if activeHotKeyIsTemporaryFallback {
                configuration.save()
                activeHotKeyIsTemporaryFallback = false
                store.log(
                    "Temporary hotkey fallback was explicitly accepted. Shortcut='\(configuration.displayName)'."
                )
            }
            settingsModel.hotKey = configuration
            hotKeyTemporarilySuspended = false
            self.hotKeyRef = hotKeyRef
            return
        }

        let candidate = registerHotKey(configuration)
        guard let candidateReference = candidate.reference else {
            settingsModel.hotKey = activeHotKey
            settingsModel.errorMessage =
                "Dieses Tastenkürzel ist bereits belegt. \(activeHotKey.displayName) bleibt aktiv."
            restoreActiveHotKeyAfterFailedChange()
            store.log(
                "Hotkey change rejected. Requested='\(configuration.displayName)' Status=\(candidate.status)."
            )
            return
        }

        if let previousReference = hotKeyRef {
            let unregisterStatus = UnregisterEventHotKey(previousReference)
            guard unregisterStatus == noErr else {
                UnregisterEventHotKey(candidateReference)
                settingsModel.hotKey = activeHotKey
                settingsModel.errorMessage =
                    "Das bisherige Tastenkürzel konnte nicht sicher ersetzt werden."
                store.log(
                    "Hotkey change rollback. OldUnregisterStatus=\(unregisterStatus)."
                )
                return
            }
        }

        hotKeyRef = candidateReference
        activeHotKey = configuration
        activeHotKeyIsTemporaryFallback = false
        activeHotKey.save()
        settingsModel.hotKey = configuration
        hotKeyTemporarilySuspended = false
        settingsModel.errorMessage = nil
        store.log("Hotkey changed. Shortcut='\(configuration.displayName)'.")
        if state == .idle || (state == .error && engineReady) {
            setIdle(
                message: "Tastenkürzel gespeichert",
                detail: configuration.displayName
            )
        }
    }

    private func restoreActiveHotKeyAfterFailedChange() {
        guard hotKeyRef == nil else { return }
        let recovery = registerHotKey(activeHotKey)
        hotKeyRef = recovery.reference
        hotKeyTemporarilySuspended = false
        if recovery.reference == nil {
            settingsModel.errorMessage =
                "Das neue Kürzel war belegt und \(activeHotKey.displayName) konnte nicht reaktiviert werden. Bitte ORhom neu starten."
            store.log(
                "Hotkey recovery failed. Shortcut='\(activeHotKey.displayName)' Status=\(recovery.status)."
            )
        }
    }

    private func setHotKeyTemporarilySuspended(_ suspended: Bool) {
        if suspended {
            guard settingsModel.configurationEnabled else {
                settingsModel.errorMessage =
                    "Das Tastenkürzel kann erst nach der laufenden Diktierung geändert werden."
                return
            }
            guard !hotKeyTemporarilySuspended else { return }
            hotKeyTemporarilySuspended = true
            if let hotKeyRef {
                let status = UnregisterEventHotKey(hotKeyRef)
                if status == noErr {
                    self.hotKeyRef = nil
                } else {
                    store.log(
                        "Hotkey could not be suspended for recording. Status=\(status)."
                    )
                }
            }
            return
        }

        guard hotKeyTemporarilySuspended else { return }
        hotKeyTemporarilySuspended = false
        guard hotKeyRef == nil else { return }
        let registration = registerHotKey(activeHotKey)
        hotKeyRef = registration.reference
        if registration.reference == nil {
            settingsModel.errorMessage =
                "\(activeHotKey.displayName) konnte nicht wieder aktiviert werden."
            store.log(
                "Hotkey resume failed. Shortcut='\(activeHotKey.displayName)' Status=\(registration.status)."
            )
        }
    }

    private func enforceSingleInstance() -> Bool {
        guard let bundleIdentifier = Bundle.main.bundleIdentifier else { return true }
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier)
        guard running.count > 1 else { return true }
        running.first(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier })?
            .activate(options: [.activateIgnoringOtherApps])
        NSApp.terminate(nil)
        return false
    }

    private func shortened(_ text: String, maximum: Int) -> String {
        text.count > maximum ? String(text.prefix(maximum - 1)) + "…" : text
    }
}

@main
private enum ORhomApplication {
    static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.run()
    }
}
