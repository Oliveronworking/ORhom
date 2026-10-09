import AudioToolbox
import CoreAudio
import Foundation

protocol MacAudioVolumeBackend {
    func defaultOutputDeviceID() -> AudioDeviceID?
    func deviceUID(for deviceID: AudioDeviceID) -> String?
    func volume(for deviceID: AudioDeviceID) -> Float?
    @discardableResult
    func setVolume(_ volume: Float, for deviceID: AudioDeviceID) -> Bool
}

struct CoreAudioVolumeBackend: MacAudioVolumeBackend {
    func defaultOutputDeviceID() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            &size,
            &deviceID
        )
        return status == noErr && deviceID != kAudioObjectUnknown
            ? deviceID
            : nil
    }

    func deviceUID(for deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) {
            AudioObjectGetPropertyData(
                deviceID,
                &address,
                0,
                nil,
                &size,
                UnsafeMutableRawPointer($0)
            )
        }
        return status == noErr
            ? value?.takeUnretainedValue() as String?
            : nil
    }

    func volume(for deviceID: AudioDeviceID) -> Float? {
        var address = virtualMainVolumeAddress
        guard AudioObjectHasProperty(deviceID, &address) else {
            return nil
        }
        var value = Float32.zero
        var size = UInt32(MemoryLayout<Float32>.size)
        let status = AudioObjectGetPropertyData(
            deviceID,
            &address,
            0,
            nil,
            &size,
            &value
        )
        return status == noErr && value.isFinite
            ? min(1, max(0, value))
            : nil
    }

    @discardableResult
    func setVolume(_ volume: Float, for deviceID: AudioDeviceID) -> Bool {
        var address = virtualMainVolumeAddress
        var isSettable = DarwinBoolean(false)
        guard
            AudioObjectHasProperty(deviceID, &address),
            AudioObjectIsPropertySettable(
                deviceID,
                &address,
                &isSettable
            ) == noErr,
            isSettable.boolValue
        else {
            return false
        }
        var value = Float32(min(1, max(0, volume)))
        return AudioObjectSetPropertyData(
            deviceID,
            &address,
            0,
            nil,
            UInt32(MemoryLayout<Float32>.size),
            &value
        ) == noErr
    }

    private var virtualMainVolumeAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector:
                kAudioHardwareServiceDeviceProperty_VirtualMainVolume,
            mScope: kAudioDevicePropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain
        )
    }
}

final class MacAudioDuckingService {
    static let enabledDefaultsKey = "enableAudioDucking"
    static let volumePercentDefaultsKey = "audioDuckingVolumePercent"

    private static let recoveryDefaultsKey = "audioDuckingRecoverySnapshotV1"

    private struct Snapshot: Codable {
        let deviceID: UInt32
        let deviceUID: String
        let originalVolume: Float
        let duckedVolume: Float
    }

    private let backend: MacAudioVolumeBackend
    private let defaults: UserDefaults
    private let log: (String) -> Void
    private var snapshot: Snapshot?
    private(set) var isActive = false

    init(
        backend: MacAudioVolumeBackend = CoreAudioVolumeBackend(),
        defaults: UserDefaults = .standard,
        log: @escaping (String) -> Void
    ) {
        self.backend = backend
        self.defaults = defaults
        self.log = log
    }

    var isEnabled: Bool {
        if defaults.object(forKey: Self.enabledDefaultsKey) == nil {
            return true
        }
        return defaults.bool(forKey: Self.enabledDefaultsKey)
    }

    var volumePercent: Int {
        if defaults.object(forKey: Self.volumePercentDefaultsKey) == nil {
            return 10
        }
        return min(
            100,
            max(0, defaults.integer(forKey: Self.volumePercentDefaultsKey))
        )
    }

    func recoverInterruptedSession() {
        guard
            let data = defaults.data(forKey: Self.recoveryDefaultsKey),
            let saved = try? JSONDecoder().decode(Snapshot.self, from: data)
        else {
            defaults.removeObject(forKey: Self.recoveryDefaultsKey)
            return
        }

        defer {
            defaults.removeObject(forKey: Self.recoveryDefaultsKey)
        }
        let deviceID = AudioDeviceID(saved.deviceID)
        guard
            backend.deviceUID(for: deviceID) == saved.deviceUID,
            let current = backend.volume(for: deviceID),
            AudioDuckingPolicy.shouldRestore(
                current: current,
                ducked: saved.duckedVolume
            )
        else {
            log(
                "Audio ducking recovery skipped because device or volume changed."
            )
            return
        }
        let restored = backend.setVolume(
            saved.originalVolume,
            for: deviceID
        )
        log("Audio ducking recovery attempted. Restored=\(restored).")
    }

    func begin() {
        guard !isActive, isEnabled, volumePercent < 100 else {
            return
        }
        isActive = true
        applyToCurrentOutput()
    }

    func refresh() {
        guard isActive else { return }
        guard isEnabled, volumePercent < 100 else {
            restore()
            return
        }
        guard let currentDeviceID = backend.defaultOutputDeviceID() else {
            return
        }
        if snapshot?.deviceID != UInt32(currentDeviceID) {
            restoreSnapshot(reason: "output-device-changed")
            apply(to: currentDeviceID)
        }
    }

    func restartWithCurrentSettings() {
        let wasActive = isActive
        restore()
        if wasActive {
            begin()
        }
    }

    func restore() {
        guard isActive || snapshot != nil else { return }
        isActive = false
        restoreSnapshot(reason: "dictation-ended")
    }

    private func applyToCurrentOutput() {
        guard let deviceID = backend.defaultOutputDeviceID() else {
            log("Audio ducking skipped. Reason='default-output-unavailable'.")
            isActive = false
            return
        }
        apply(to: deviceID)
    }

    private func apply(to deviceID: AudioDeviceID) {
        guard
            let deviceUID = backend.deviceUID(for: deviceID),
            let original = backend.volume(for: deviceID)
        else {
            log("Audio ducking skipped. Reason='volume-read-unavailable'.")
            return
        }

        let requested = AudioDuckingPolicy.targetVolume(
            original: original,
            percent: volumePercent
        )
        guard abs(requested - original) > 0.0001 else {
            return
        }
        let pending = Snapshot(
            deviceID: UInt32(deviceID),
            deviceUID: deviceUID,
            originalVolume: original,
            duckedVolume: requested
        )
        persist(pending)
        guard backend.setVolume(requested, for: deviceID) else {
            defaults.removeObject(forKey: Self.recoveryDefaultsKey)
            log("Audio ducking skipped. Reason='volume-write-rejected'.")
            return
        }

        let actual = backend.volume(for: deviceID) ?? requested
        let applied = Snapshot(
            deviceID: UInt32(deviceID),
            deviceUID: deviceUID,
            originalVolume: original,
            duckedVolume: actual
        )
        snapshot = applied
        persist(applied)
        log(
            "Audio ducking started. VolumePercent=\(volumePercent) Device=\(deviceID)."
        )
    }

    private func restoreSnapshot(reason: String) {
        guard let snapshot else {
            defaults.removeObject(forKey: Self.recoveryDefaultsKey)
            return
        }
        defer {
            self.snapshot = nil
            defaults.removeObject(forKey: Self.recoveryDefaultsKey)
        }

        let deviceID = AudioDeviceID(snapshot.deviceID)
        guard
            backend.deviceUID(for: deviceID) == snapshot.deviceUID,
            let current = backend.volume(for: deviceID)
        else {
            log(
                "Audio ducking restore skipped. Reason='\(reason) device-unavailable'."
            )
            return
        }
        guard AudioDuckingPolicy.shouldRestore(
            current: current,
            ducked: snapshot.duckedVolume
        ) else {
            log(
                "Audio ducking restore skipped. Reason='\(reason) user-volume-changed'."
            )
            return
        }
        let restored = backend.setVolume(
            snapshot.originalVolume,
            for: deviceID
        )
        log(
            "Audio ducking stopped. Reason='\(reason)' Restored=\(restored)."
        )
    }

    private func persist(_ snapshot: Snapshot) {
        if let data = try? JSONEncoder().encode(snapshot) {
            defaults.set(data, forKey: Self.recoveryDefaultsKey)
        }
    }
}
