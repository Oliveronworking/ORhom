import AppKit
import AVFoundation
import CoreAudio
import CryptoKit
import Foundation
import whisper

enum ORhomError: LocalizedError {
    case microphonePermission
    case audioInputUnavailable
    case audioInputInterrupted(String)
    case audioConversion(String)
    case modelDownload(String)
    case modelInvalid
    case modelLoad
    case emptyRecording
    case emptyResult
    case recognition(String)

    var errorDescription: String? {
        switch self {
        case .microphonePermission:
            return "Der Mikrofonzugriff fehlt. Bitte ORhom unter „Datenschutz & Sicherheit > Mikrofon“ erlauben."
        case .audioInputUnavailable:
            return "Auf diesem Mac wurde kein verwendbares Mikrofon gefunden."
        case .audioInputInterrupted(let message):
            return "Die Mikrofonaufnahme wurde unterbrochen: \(message)"
        case .audioConversion(let message):
            return "Die Mikrofonaufnahme konnte nicht verarbeitet werden: \(message)"
        case .modelDownload(let message):
            return "Das lokale Whisper-Modell konnte nicht geladen werden: \(message)"
        case .modelInvalid:
            return "Das lokale Whisper-Modell ist unvollständig oder beschädigt."
        case .modelLoad:
            return "Whisper konnte das lokale Sprachmodell nicht öffnen."
        case .emptyRecording:
            return "Die Aufnahme war zu kurz. Bitte noch einmal diktieren."
        case .emptyResult:
            return "Es wurde keine Sprache erkannt."
        case .recognition(let message):
            return "Die lokale Transkription ist fehlgeschlagen: \(message)"
        }
    }
}

struct HistoryEntry: Codable {
    let date: Date
    let text: String
    let outcome: String
}

struct MicrophoneDevice: Identifiable, Hashable {
    let uniqueID: String
    let name: String
    let isBuiltIn: Bool

    var id: String {
        uniqueID
    }
}

final class AppDataStore {
    private let fileManager = FileManager.default
    private let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()
    private let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }()

    var supportDirectory: URL {
        let base = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("ORhom", isDirectory: true)
    }

    var modelDirectory: URL {
        supportDirectory.appendingPathComponent("models", isDirectory: true)
    }

    var logURL: URL {
        let base = fileManager.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        return base
            .appendingPathComponent("Logs", isDirectory: true)
            .appendingPathComponent("ORhom", isDirectory: true)
            .appendingPathComponent("app.log")
    }

    private var historyURL: URL {
        supportDirectory.appendingPathComponent("dictation-history.json")
    }

    func entries() -> [HistoryEntry] {
        guard
            let data = try? Data(contentsOf: historyURL),
            let entries = try? decoder.decode([HistoryEntry].self, from: data)
        else {
            return []
        }
        return entries.sorted { $0.date > $1.date }
    }

    func add(text: String, outcome: String) {
        let cleaned = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty else { return }

        var current = entries()
        current.insert(HistoryEntry(date: Date(), text: cleaned, outcome: outcome), at: 0)
        save(Array(current.prefix(10)))
    }

    func clearHistory() {
        save([])
    }

    func log(_ message: String) {
        let formatter = ISO8601DateFormatter()
        let line = "\(formatter.string(from: Date())) \(message)\n"
        let directory = logURL.deletingLastPathComponent()
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)

        if !fileManager.fileExists(atPath: logURL.path) {
            try? Data(line.utf8).write(to: logURL, options: .atomic)
            return
        }

        guard let handle = try? FileHandle(forWritingTo: logURL) else { return }
        defer { try? handle.close() }
        do {
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(line.utf8))
        } catch {
            // Logging must never interrupt dictation.
        }
    }

    private func save(_ entries: [HistoryEntry]) {
        do {
            try fileManager.createDirectory(at: supportDirectory, withIntermediateDirectories: true)
            let data = try encoder.encode(entries)
            try data.write(to: historyURL, options: .atomic)
        } catch {
            log("History save failed: \(error.localizedDescription)")
        }
    }
}

final class WhisperModelManager: NSObject, URLSessionDownloadDelegate {
    static let fileName = "ggml-large-v3-turbo-q5_0.bin"
    static let expectedSize: Int64 = 574_041_195
    static let expectedSHA256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"
    static let downloadURL = URL(
        string: "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin"
    )!

    private let store: AppDataStore
    private let queue = DispatchQueue(label: "at.orhom.model", qos: .utility)
    private var progressHandler: ((Double, String) -> Void)?
    private var completionHandler: ((Result<URL, Error>) -> Void)?
    private var downloadSucceeded = false
    private var downloadFailure: Error?
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 60 * 60
        return URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }()

    var modelURL: URL {
        store.modelDirectory.appendingPathComponent(Self.fileName)
    }

    private var verificationURL: URL {
        store.modelDirectory.appendingPathComponent("\(Self.fileName).sha256")
    }

    init(store: AppDataStore) {
        self.store = store
        super.init()
    }

    func prepare(
        progress: @escaping (Double, String) -> Void,
        completion: @escaping (Result<URL, Error>) -> Void
    ) {
        progressHandler = progress
        completionHandler = completion
        progress(0, "Lokales Sprachmodell wird geprüft")

        queue.async { [weak self] in
            guard let self else { return }
            if self.isExpectedSize(self.modelURL) {
                let storedHash = try? String(contentsOf: self.verificationURL, encoding: .utf8)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                if storedHash == Self.expectedSHA256 {
                    self.finish(.success(self.modelURL))
                    return
                }

                self.report(0.02, "Sprachmodell wird verifiziert")
                if self.sha256(of: self.modelURL) == Self.expectedSHA256 {
                    try? Self.expectedSHA256.write(
                        to: self.verificationURL,
                        atomically: true,
                        encoding: .utf8
                    )
                    self.finish(.success(self.modelURL))
                    return
                }
            }

            self.startDownload()
        }
    }

    private func startDownload() {
        do {
            try FileManager.default.createDirectory(
                at: store.modelDirectory,
                withIntermediateDirectories: true
            )
            report(0, "Whisper Large V3 Turbo wird geladen · 574 MB")
            downloadSucceeded = false
            downloadFailure = nil
            session.downloadTask(with: Self.downloadURL).resume()
        } catch {
            finish(.failure(ORhomError.modelDownload(error.localizedDescription)))
        }
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        let total = totalBytesExpectedToWrite > 0
            ? totalBytesExpectedToWrite
            : Self.expectedSize
        let fraction = min(1, max(0, Double(totalBytesWritten) / Double(total)))
        let downloadedMB = Int(totalBytesWritten / 1_000_000)
        report(fraction, "Sprachmodell wird geladen · \(downloadedMB) von 574 MB")
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        do {
            guard isExpectedSize(location) else {
                throw ORhomError.modelInvalid
            }
            report(0.99, "Sprachmodell wird sicher geprüft")
            guard sha256(of: location) == Self.expectedSHA256 else {
                throw ORhomError.modelInvalid
            }

            let fileManager = FileManager.default
            let staging = store.modelDirectory.appendingPathComponent(".\(Self.fileName).ready")
            if fileManager.fileExists(atPath: staging.path) {
                try fileManager.removeItem(at: staging)
            }
            try fileManager.moveItem(at: location, to: staging)
            if fileManager.fileExists(atPath: modelURL.path) {
                try fileManager.removeItem(at: modelURL)
            }
            try fileManager.moveItem(at: staging, to: modelURL)
            try Self.expectedSHA256.write(
                to: verificationURL,
                atomically: true,
                encoding: .utf8
            )
            downloadSucceeded = true
        } catch {
            downloadFailure = error
        }
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        if let error {
            finish(.failure(ORhomError.modelDownload(error.localizedDescription)))
        } else if let downloadFailure {
            finish(.failure(downloadFailure))
        } else if downloadSucceeded {
            finish(.success(modelURL))
        } else {
            finish(.failure(ORhomError.modelInvalid))
        }
    }

    private func isExpectedSize(_ url: URL) -> Bool {
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes?[.size] as? NSNumber)?.int64Value == Self.expectedSize
    }

    private func sha256(of url: URL) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        var hasher = SHA256()

        do {
            while let data = try handle.read(upToCount: 8 * 1_024 * 1_024), !data.isEmpty {
                hasher.update(data: data)
            }
            return hasher.finalize().map { String(format: "%02x", $0) }.joined()
        } catch {
            return nil
        }
    }

    private func report(_ fraction: Double, _ message: String) {
        DispatchQueue.main.async { [weak self] in
            self?.progressHandler?(fraction, message)
        }
    }

    private func finish(_ result: Result<URL, Error>) {
        DispatchQueue.main.async { [weak self] in
            self?.completionHandler?(result)
            self?.completionHandler = nil
            self?.progressHandler = nil
        }
    }
}

final class LocalWhisperEngine {
    private let queue = DispatchQueue(label: "at.orhom.whisper", qos: .userInitiated)
    private var context: OpaquePointer?

    var isReady: Bool {
        context != nil
    }

    func loadModel(
        at url: URL,
        completion: @escaping (Result<String, Error>) -> Void
    ) {
        queue.async { [weak self] in
            guard let self else { return }
            if self.context != nil {
                DispatchQueue.main.async {
                    completion(.success(self.systemInfo))
                }
                return
            }

            var parameters = whisper_context_default_params()
            parameters.use_gpu = true
            parameters.flash_attn = true
            parameters.gpu_device = 0
            let loaded = url.path.withCString {
                whisper_init_from_file_with_params($0, parameters)
            }
            guard let loaded else {
                DispatchQueue.main.async {
                    completion(.failure(ORhomError.modelLoad))
                }
                return
            }
            self.context = loaded
            DispatchQueue.main.async {
                completion(.success(self.systemInfo))
            }
        }
    }

    func transcribe(
        samples: [Float],
        completion: @escaping (Result<String, Error>) -> Void
    ) {
        queue.async { [weak self] in
            guard let self, let context = self.context else {
                DispatchQueue.main.async {
                    completion(.failure(ORhomError.modelLoad))
                }
                return
            }

            var parameters = whisper_full_default_params(WHISPER_SAMPLING_GREEDY)
            parameters.n_threads = Int32(min(8, max(2, ProcessInfo.processInfo.activeProcessorCount)))
            parameters.translate = false
            parameters.no_context = true
            parameters.no_timestamps = true
            parameters.single_segment = false
            parameters.print_special = false
            parameters.print_progress = false
            parameters.print_realtime = false
            parameters.print_timestamps = false
            parameters.detect_language = false
            parameters.suppress_blank = true
            parameters.suppress_nst = true
            parameters.temperature = 0

            let prompt = "ORhom, Codex, GitHub, macOS, Apple Silicon, Metal, Whisper"
            let resultCode: Int32 = "de".withCString { language in
                prompt.withCString { promptPointer in
                    parameters.language = language
                    parameters.initial_prompt = promptPointer
                    return samples.withUnsafeBufferPointer { buffer in
                        whisper_full(
                            context,
                            parameters,
                            buffer.baseAddress,
                            Int32(buffer.count)
                        )
                    }
                }
            }

            guard resultCode == 0 else {
                DispatchQueue.main.async {
                    completion(.failure(ORhomError.recognition("whisper.cpp Fehler \(resultCode)")))
                }
                return
            }

            let segmentCount = whisper_full_n_segments(context)
            var text = ""
            if segmentCount > 0 {
                for index in 0..<segmentCount {
                    if let pointer = whisper_full_get_segment_text(context, index) {
                        text += String(cString: pointer)
                    }
                }
            }
            let cleaned = text
                .replacingOccurrences(of: "[BLANK_AUDIO]", with: "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            DispatchQueue.main.async {
                completion(cleaned.isEmpty
                    ? .failure(ORhomError.emptyResult)
                    : .success(cleaned))
            }
        }
    }

    private var systemInfo: String {
        guard let pointer = whisper_print_system_info() else {
            return "whisper.cpp · Metal"
        }
        return String(cString: pointer)
    }

    // Keep the Metal-backed Whisper context alive for the lifetime of the process.
    // whisper.cpp 1.9.1 can abort while tearing down Metal residency sets on
    // macOS 26. The OS reclaims the context when this menu-bar app exits.
}

final class MicrophoneRecorder: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    static let selectedMicrophoneDefaultsKey = "selectedMicrophoneUniqueID"

    private let lock = NSLock()
    private let captureQueue = DispatchQueue(label: "at.orhom.capture", qos: .userInitiated)
    private let userDefaults: UserDefaults
    private var captureSession: AVCaptureSession?
    private var captureOutput: AVCaptureAudioDataOutput?
    private var sessionObserverTokens: [NSObjectProtocol] = []
    private var samples: [Float] = []
    private var sourceSampleRate: Double = 0
    private var recordingError: Error?
    private var measuredLevel: Float = 0
    private var capturedBuffers = 0
    private var capturedFrames: Int64 = 0
    private var activeFormatDescription = ""
    private var activeMicrophoneName = ""
    private var activeMicrophoneUniqueID = ""

    var onCaptureFailure: ((Error) -> Void)?

    override convenience init() {
        self.init(userDefaults: .standard)
    }

    init(userDefaults: UserDefaults) {
        self.userDefaults = userDefaults
        super.init()
        _ = selectedMicrophoneUniqueID
    }

    var availableMicrophones: [MicrophoneDevice] {
        Self.captureDevices().map(Self.descriptor(for:))
    }

    var selectedMicrophoneUniqueID: String? {
        get {
            if let stored = userDefaults.string(forKey: Self.selectedMicrophoneDefaultsKey)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
               !stored.isEmpty {
                return stored
            }

            guard let preferred = Self.preferredInitialDevice(from: Self.captureDevices()) else {
                return nil
            }
            userDefaults.set(preferred.uniqueID, forKey: Self.selectedMicrophoneDefaultsKey)
            return preferred.uniqueID
        }
        set {
            guard
                let normalized = newValue?.trimmingCharacters(in: .whitespacesAndNewlines),
                !normalized.isEmpty
            else {
                userDefaults.removeObject(forKey: Self.selectedMicrophoneDefaultsKey)
                return
            }
            userDefaults.set(normalized, forKey: Self.selectedMicrophoneDefaultsKey)
        }
    }

    var selectedMicrophone: MicrophoneDevice? {
        guard
            let uniqueID = selectedMicrophoneUniqueID,
            let device = AVCaptureDevice(uniqueID: uniqueID),
            device.hasMediaType(.audio),
            device.isConnected
        else {
            return nil
        }
        return Self.descriptor(for: device)
    }

    var level: Float {
        lock.lock()
        defer { lock.unlock() }
        return measuredLevel
    }

    var microphoneName: String {
        if let selectedMicrophone {
            return selectedMicrophone.name
        }
        return selectedMicrophoneUniqueID == nil
            ? "Kein Mikrofon verfügbar"
            : "Ausgewähltes Mikrofon nicht verfügbar"
    }

    var diagnostics: String {
        let selectedID = selectedMicrophoneUniqueID ?? "none"
        lock.lock()
        defer { lock.unlock() }
        let activeName = activeMicrophoneName.isEmpty ? "none" : activeMicrophoneName
        let activeID = activeMicrophoneUniqueID.isEmpty ? "none" : activeMicrophoneUniqueID
        return "SelectedMicrophoneID='\(selectedID)' ActiveMicrophone='\(activeName)' ActiveMicrophoneID='\(activeID)' Format='\(activeFormatDescription)' Buffers=\(capturedBuffers) Frames=\(capturedFrames) Samples=\(samples.count)"
    }

    func requestPermission(completion: @escaping (Bool) -> Void) {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            completion(true)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { allowed in
                DispatchQueue.main.async { completion(allowed) }
            }
        case .denied, .restricted:
            completion(false)
        @unknown default:
            completion(false)
        }
    }

    func start() throws {
        stopAndDiscard()

        guard
            let selectedID = selectedMicrophoneUniqueID,
            let device = AVCaptureDevice(uniqueID: selectedID),
            device.hasMediaType(.audio),
            device.isConnected
        else {
            throw ORhomError.audioInputUnavailable
        }
        let input = try AVCaptureDeviceInput(device: device)
        let session = AVCaptureSession()
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
        guard session.canAddInput(input), session.canAddOutput(output) else {
            throw ORhomError.audioInputUnavailable
        }
        session.beginConfiguration()
        session.addInput(input)
        session.addOutput(output)
        session.commitConfiguration()
        output.setSampleBufferDelegate(self, queue: captureQueue)

        lock.lock()
        samples.removeAll(keepingCapacity: true)
        sourceSampleRate = 0
        measuredLevel = 0
        recordingError = nil
        capturedBuffers = 0
        capturedFrames = 0
        activeFormatDescription = "Warte auf ersten Audiobuffer"
        activeMicrophoneName = device.localizedName
        activeMicrophoneUniqueID = device.uniqueID
        lock.unlock()
        captureSession = session
        captureOutput = output
        observeSessionFailures(session)

        captureQueue.sync {
            session.startRunning()
        }
        guard session.isRunning else {
            stopAndDiscard()
            throw ORhomError.audioInputUnavailable
        }
    }

    func stop() throws -> [Float] {
        stopCapture()
        lock.lock()
        let capturedError = recordingError
        let sourceSamples = samples
        let sampleRate = sourceSampleRate
        lock.unlock()
        if let capturedError {
            throw ORhomError.audioConversion(capturedError.localizedDescription)
        }
        guard sourceSamples.count >= 1_600, sampleRate > 0 else {
            throw ORhomError.emptyRecording
        }

        let recordingURL = try writeNativeRecording(
            samples: sourceSamples,
            sampleRate: sampleRate
        )
        defer { try? FileManager.default.removeItem(at: recordingURL) }
        let converted = try convertRecording(at: recordingURL)
        guard converted.count >= 1_600 else {
            throw ORhomError.emptyRecording
        }
        return converted
    }

    func stopAndDiscard() {
        stopCapture()
        lock.lock()
        samples.removeAll(keepingCapacity: false)
        sourceSampleRate = 0
        measuredLevel = 0
        recordingError = nil
        capturedBuffers = 0
        capturedFrames = 0
        activeFormatDescription = ""
        activeMicrophoneName = ""
        activeMicrophoneUniqueID = ""
        lock.unlock()
    }

    private static func captureDevices() -> [AVCaptureDevice] {
        let deviceTypes: [AVCaptureDevice.DeviceType]
        if #available(macOS 14.0, *) {
            deviceTypes = [.microphone, .external]
        } else {
            deviceTypes = [.builtInMicrophone, .externalUnknown]
        }
        let discovered = AVCaptureDevice.DiscoverySession(
            deviceTypes: deviceTypes,
            mediaType: .audio,
            position: .unspecified
        ).devices

        var devicesByID: [String: AVCaptureDevice] = [:]
        for device in discovered where device.hasMediaType(.audio) && device.isConnected {
            devicesByID[device.uniqueID] = device
        }
        return devicesByID.values.sorted(by: compareDevices)
    }

    private static func descriptor(
        for device: AVCaptureDevice
    ) -> MicrophoneDevice {
        MicrophoneDevice(
            uniqueID: device.uniqueID,
            name: device.localizedName,
            isBuiltIn: isBuiltIn(device)
        )
    }

    private static func preferredInitialDevice(
        from devices: [AVCaptureDevice]
    ) -> AVCaptureDevice? {
        if let builtIn = devices.first(where: isBuiltIn) {
            return builtIn
        }
        if
            let systemDefault = AVCaptureDevice.default(for: .audio),
            let matchingDefault = devices.first(where: {
                $0.uniqueID == systemDefault.uniqueID
            })
        {
            return matchingDefault
        }
        return devices.first
    }

    private static func isBuiltIn(_ device: AVCaptureDevice) -> Bool {
        device.transportType == Int32(bitPattern: kAudioDeviceTransportTypeBuiltIn)
    }

    private static func compareDevices(
        _ left: AVCaptureDevice,
        _ right: AVCaptureDevice
    ) -> Bool {
        let leftIsBuiltIn = isBuiltIn(left)
        let rightIsBuiltIn = isBuiltIn(right)
        if leftIsBuiltIn != rightIsBuiltIn {
            return leftIsBuiltIn
        }

        let nameOrder = left.localizedName.localizedCaseInsensitiveCompare(
            right.localizedName
        )
        if nameOrder != .orderedSame {
            return nameOrder == .orderedAscending
        }
        return left.uniqueID < right.uniqueID
    }

    private func stopCapture() {
        guard let session = captureSession else { return }
        removeSessionObservers()
        if session.isRunning {
            captureQueue.sync {
                session.stopRunning()
            }
        }
        captureOutput?.setSampleBufferDelegate(nil, queue: nil)
        captureQueue.sync {}
        captureOutput = nil
        captureSession = nil
    }

    private func observeSessionFailures(_ session: AVCaptureSession) {
        removeSessionObservers()
        let center = NotificationCenter.default
        sessionObserverTokens = [
            center.addObserver(
                forName: AVCaptureSession.runtimeErrorNotification,
                object: session,
                queue: nil
            ) { [weak self] notification in
                let error = notification.userInfo?[AVCaptureSessionErrorKey] as? Error
                    ?? ORhomError.audioInputInterrupted(
                        "macOS hat einen Laufzeitfehler des Audiogeräts gemeldet."
                    )
                self?.reportCaptureFailure(error)
            },
            center.addObserver(
                forName: AVCaptureSession.wasInterruptedNotification,
                object: session,
                queue: nil
            ) { [weak self] _ in
                self?.reportCaptureFailure(
                    ORhomError.audioInputInterrupted(
                        "Das ausgewählte Mikrofon ist vorübergehend nicht verfügbar."
                    )
                )
            }
        ]
    }

    private func removeSessionObservers() {
        let center = NotificationCenter.default
        for token in sessionObserverTokens {
            center.removeObserver(token)
        }
        sessionObserverTokens.removeAll(keepingCapacity: false)
    }

    private func reportCaptureFailure(_ error: Error) {
        lock.lock()
        let isFirstFailure = recordingError == nil
        if isFirstFailure {
            recordingError = error
        }
        lock.unlock()
        guard isFirstFailure else { return }
        DispatchQueue.main.async { [weak self] in
            self?.onCaptureFailure?(error)
        }
    }

    func captureOutput(
        _ output: AVCaptureOutput,
        didOutput sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        guard
            CMSampleBufferDataIsReady(sampleBuffer),
            let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
            let streamDescription = CMAudioFormatDescriptionGetStreamBasicDescription(
                formatDescription
            )?.pointee,
            streamDescription.mFormatID == kAudioFormatLinearPCM,
            streamDescription.mBitsPerChannel == 32,
            streamDescription.mFormatFlags & kAudioFormatFlagIsFloat != 0
        else {
            return
        }

        var requiredSize = 0
        let flags = UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment)
        let sizeStatus = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: &requiredSize,
            bufferListOut: nil,
            bufferListSize: 0,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: flags,
            blockBufferOut: nil
        )
        guard sizeStatus == noErr, requiredSize > 0 else { return }

        let rawList = UnsafeMutableRawPointer.allocate(
            byteCount: requiredSize,
            alignment: MemoryLayout<AudioBufferList>.alignment
        )
        defer { rawList.deallocate() }
        let audioBufferList = rawList.bindMemory(to: AudioBufferList.self, capacity: 1)
        var retainedBlockBuffer: CMBlockBuffer?
        let listStatus = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: audioBufferList,
            bufferListSize: requiredSize,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: flags,
            blockBufferOut: &retainedBlockBuffer
        )
        guard listStatus == noErr else { return }

        let frameCount = CMSampleBufferGetNumSamples(sampleBuffer)
        let channelCount = max(1, Int(streamDescription.mChannelsPerFrame))
        let buffers = UnsafeMutableAudioBufferListPointer(audioBufferList)
        var mono = [Float](repeating: 0, count: frameCount)

        if buffers.count == 1, let data = buffers[0].mData {
            let availableValues = Int(buffers[0].mDataByteSize) / MemoryLayout<Float>.size
            let values = data.assumingMemoryBound(to: Float.self)
            let availableFrames = min(frameCount, availableValues / channelCount)
            for frame in 0..<availableFrames {
                var mixed: Float = 0
                for channel in 0..<channelCount {
                    mixed += values[(frame * channelCount) + channel]
                }
                mono[frame] = mixed / Float(channelCount)
            }
            if availableFrames < frameCount {
                mono.removeLast(frameCount - availableFrames)
            }
        } else {
            let availableFrames = buffers.reduce(frameCount) { current, buffer in
                min(
                    current,
                    Int(buffer.mDataByteSize) / MemoryLayout<Float>.size
                )
            }
            guard availableFrames > 0 else { return }
            mono.removeLast(frameCount - availableFrames)
            for buffer in buffers {
                guard let data = buffer.mData else { continue }
                let values = data.assumingMemoryBound(to: Float.self)
                for frame in 0..<availableFrames {
                    mono[frame] += values[frame] / Float(buffers.count)
                }
            }
        }
        guard !mono.isEmpty else { return }

        var energy: Float = 0
        for sample in mono {
            energy += sample * sample
        }
        let rms = sqrt(energy / Float(mono.count))
        let normalizedLevel = min(1, max(0.02, rms * 8))

        lock.lock()
        if sourceSampleRate == 0 {
            sourceSampleRate = streamDescription.mSampleRate
            activeFormatDescription =
                "\(Int(streamDescription.mSampleRate))Hz \(channelCount)ch Float32"
        }
        samples.append(contentsOf: mono)
        capturedBuffers += 1
        capturedFrames += Int64(mono.count)
        measuredLevel = (measuredLevel * 0.55) + (normalizedLevel * 0.45)
        lock.unlock()
    }

    func captureOutput(
        _ output: AVCaptureOutput,
        didDrop sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        // A dropped real-time buffer is acceptable; following buffers remain usable.
    }

    private func writeNativeRecording(
        samples: [Float],
        sampleRate: Double
    ) throws -> URL {
        guard
            let format = AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: sampleRate,
                channels: 1,
                interleaved: false
            ),
            let buffer = AVAudioPCMBuffer(
                pcmFormat: format,
                frameCapacity: AVAudioFrameCount(samples.count)
            ),
            let channel = buffer.floatChannelData?[0]
        else {
            throw ORhomError.audioInputUnavailable
        }
        samples.withUnsafeBufferPointer { source in
            channel.update(from: source.baseAddress!, count: source.count)
        }
        buffer.frameLength = AVAudioFrameCount(samples.count)

        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ORhom-\(UUID().uuidString).caf")
        let file = try AVAudioFile(
            forWriting: url,
            settings: format.settings,
            commonFormat: .pcmFormatFloat32,
            interleaved: false
        )
        try file.write(from: buffer)
        return url
    }

    func convertRecording(at url: URL) throws -> [Float] {
        let convertedURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("ORhom-16k-\(UUID().uuidString).caf")
        defer {
            try? FileManager.default.removeItem(at: convertedURL)
        }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/afconvert")
        process.arguments = [
            "-f", "caff",
            "-d", "LEF32@16000",
            "-c", "1",
            url.path,
            convertedURL.path
        ]
        let errorPipe = Pipe()
        process.standardError = errorPipe
        do {
            try process.run()
            process.waitUntilExit()
        } catch {
            throw ORhomError.audioConversion(error.localizedDescription)
        }
        guard process.terminationStatus == 0 else {
            let data = errorPipe.fileHandleForReading.readDataToEndOfFile()
            let detail = String(data: data, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            throw ORhomError.audioConversion(
                detail?.isEmpty == false ? detail! : "afconvert Fehler \(process.terminationStatus)"
            )
        }

        let convertedFile = try AVAudioFile(forReading: convertedURL)
        guard
            convertedFile.length > 0,
            convertedFile.processingFormat.sampleRate == 16_000,
            convertedFile.processingFormat.channelCount == 1,
            let buffer = AVAudioPCMBuffer(
                pcmFormat: convertedFile.processingFormat,
                frameCapacity: AVAudioFrameCount(convertedFile.length)
            )
        else {
            throw ORhomError.emptyRecording
        }
        try convertedFile.read(into: buffer)
        guard
            buffer.frameLength > 0,
            let channel = buffer.floatChannelData?[0]
        else {
            throw ORhomError.emptyRecording
        }
        return Array(
            UnsafeBufferPointer(
                start: channel,
                count: Int(buffer.frameLength)
            )
        )
    }
}
