import Foundation

/// A quick press toggles; releasing a held start press requests one stop.
struct HybridHotKeyGesture {
    private var pressedAt: TimeInterval?
    private var pressStartedDictation = false

    mutating func press(at time: TimeInterval, startsDictation: Bool) -> Bool {
        guard pressedAt == nil else { return false }
        pressedAt = time
        pressStartedDictation = startsDictation
        return true
    }

    mutating func release(at time: TimeInterval, enabled: Bool) -> Bool {
        defer { reset() }
        guard let pressedAt, enabled, pressStartedDictation else { return false }
        return time - pressedAt >= 0.35
    }

    mutating func reset() {
        pressedAt = nil
        pressStartedDictation = false
    }
}
