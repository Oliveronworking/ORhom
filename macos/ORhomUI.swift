import AppKit
import Carbon
import SwiftUI

enum OverlaySizePreset: String, CaseIterable, Identifiable {
    case small
    case medium
    case large

    static let defaultValue: OverlaySizePreset = .small

    var id: String { rawValue }

    var title: String {
        switch self {
        case .small:
            return "Klein"
        case .medium:
            return "Mittel"
        case .large:
            return "Groß"
        }
    }
}

enum HotKeyConfigurationError: LocalizedError {
    case unsupportedKey
    case modifierRequired

    var errorDescription: String? {
        switch self {
        case .unsupportedKey:
            return "Diese Taste kann nicht als globales Tastenkürzel verwendet werden."
        case .modifierRequired:
            return "Buchstaben, Zahlen und Sondertasten benötigen mindestens ⌘, ⌥ oder ⌃."
        }
    }
}

struct HotKeyConfiguration: Equatable {
    private static let keyCodeDefaultsKey = "hotKeyKeyCode"
    private static let modifiersDefaultsKey = "hotKeyModifiers"
    private static let keyNameDefaultsKey = "hotKeyName"

    let keyCode: UInt32
    let modifiers: UInt32
    let keyName: String

    static let standard = HotKeyConfiguration(
        keyCode: UInt32(kVK_F8),
        modifiers: 0,
        keyName: "F8"
    )

    var displayName: String {
        var result = ""
        if modifiers & UInt32(controlKey) != 0 {
            result += "⌃"
        }
        if modifiers & UInt32(optionKey) != 0 {
            result += "⌥"
        }
        if modifiers & UInt32(shiftKey) != 0 {
            result += "⇧"
        }
        if modifiers & UInt32(cmdKey) != 0 {
            result += "⌘"
        }
        return result + keyName
    }

    static func load(from defaults: UserDefaults = .standard) -> HotKeyConfiguration {
        guard
            defaults.object(forKey: keyCodeDefaultsKey) != nil,
            defaults.object(forKey: modifiersDefaultsKey) != nil,
            let keyName = defaults.string(forKey: keyNameDefaultsKey),
            !keyName.isEmpty
        else {
            return .standard
        }
        return HotKeyConfiguration(
            keyCode: UInt32(defaults.integer(forKey: keyCodeDefaultsKey)),
            modifiers: UInt32(defaults.integer(forKey: modifiersDefaultsKey)),
            keyName: keyName
        )
    }

    func save(to defaults: UserDefaults = .standard) {
        defaults.set(Int(keyCode), forKey: Self.keyCodeDefaultsKey)
        defaults.set(Int(modifiers), forKey: Self.modifiersDefaultsKey)
        defaults.set(keyName, forKey: Self.keyNameDefaultsKey)
    }

    static func capture(from event: NSEvent) throws -> HotKeyConfiguration {
        guard let keyName = keyName(for: event), !keyName.isEmpty else {
            throw HotKeyConfigurationError.unsupportedKey
        }

        var carbonModifiers: UInt32 = 0
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if flags.contains(.command) {
            carbonModifiers |= UInt32(cmdKey)
        }
        if flags.contains(.option) {
            carbonModifiers |= UInt32(optionKey)
        }
        if flags.contains(.control) {
            carbonModifiers |= UInt32(controlKey)
        }
        if flags.contains(.shift) {
            carbonModifiers |= UInt32(shiftKey)
        }

        let keyCode = UInt32(event.keyCode)
        if !functionKeyCodes.contains(keyCode),
           carbonModifiers & UInt32(cmdKey | optionKey | controlKey) == 0 {
            throw HotKeyConfigurationError.modifierRequired
        }

        return HotKeyConfiguration(
            keyCode: keyCode,
            modifiers: carbonModifiers,
            keyName: keyName
        )
    }

    private static let functionKeyCodes: Set<UInt32> = [
        UInt32(kVK_F1), UInt32(kVK_F2), UInt32(kVK_F3), UInt32(kVK_F4),
        UInt32(kVK_F5), UInt32(kVK_F6), UInt32(kVK_F7), UInt32(kVK_F8),
        UInt32(kVK_F9), UInt32(kVK_F10), UInt32(kVK_F11), UInt32(kVK_F12),
        UInt32(kVK_F13), UInt32(kVK_F14), UInt32(kVK_F15), UInt32(kVK_F16),
        UInt32(kVK_F17), UInt32(kVK_F18), UInt32(kVK_F19), UInt32(kVK_F20)
    ]

    private static func keyName(for event: NSEvent) -> String? {
        let specialKeys: [UInt16: String] = [
            UInt16(kVK_F1): "F1", UInt16(kVK_F2): "F2",
            UInt16(kVK_F3): "F3", UInt16(kVK_F4): "F4",
            UInt16(kVK_F5): "F5", UInt16(kVK_F6): "F6",
            UInt16(kVK_F7): "F7", UInt16(kVK_F8): "F8",
            UInt16(kVK_F9): "F9", UInt16(kVK_F10): "F10",
            UInt16(kVK_F11): "F11", UInt16(kVK_F12): "F12",
            UInt16(kVK_F13): "F13", UInt16(kVK_F14): "F14",
            UInt16(kVK_F15): "F15", UInt16(kVK_F16): "F16",
            UInt16(kVK_F17): "F17", UInt16(kVK_F18): "F18",
            UInt16(kVK_F19): "F19", UInt16(kVK_F20): "F20",
            UInt16(kVK_Space): "Leertaste",
            UInt16(kVK_Return): "Return",
            UInt16(kVK_Tab): "Tab",
            UInt16(kVK_Delete): "Löschen",
            UInt16(kVK_ForwardDelete): "Entf",
            UInt16(kVK_Home): "Pos1",
            UInt16(kVK_End): "Ende",
            UInt16(kVK_PageUp): "Bild↑",
            UInt16(kVK_PageDown): "Bild↓",
            UInt16(kVK_LeftArrow): "←",
            UInt16(kVK_RightArrow): "→",
            UInt16(kVK_UpArrow): "↑",
            UInt16(kVK_DownArrow): "↓",
            UInt16(kVK_Escape): "Esc"
        ]
        if let special = specialKeys[event.keyCode] {
            return special
        }
        guard let characters = event.charactersIgnoringModifiers?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !characters.isEmpty,
              characters.unicodeScalars.allSatisfy({
                  !CharacterSet.controlCharacters.contains($0)
              })
        else {
            return nil
        }
        return characters.uppercased()
    }
}

enum OverlayMode: Equatable {
    case preparing
    case idle
    case recording
    case transcribing
    case error
}

final class OverlayViewModel: ObservableObject {
    @Published var mode: OverlayMode = .preparing
    @Published var title = "ORhom wird vorbereitet"
    @Published var detail = "Lokale Spracherkennung"
    @Published var progress: Double?
    @Published var level: Float = 0
    @Published var elapsed = "00:00"
    @Published var sizePreset = OverlaySizePreset.defaultValue

    var onPrimary: (() -> Void)?
    var onCancel: (() -> Void)?
}

final class SettingsViewModel: ObservableObject {
    @Published var engineTitle = "Whisper Large V3 Turbo"
    @Published var engineDetail = "Lokale Engine wird vorbereitet"
    @Published var engineReady = false
    @Published var engineProgress: Double?
    @Published var microphoneName = "Standardmikrofon"
    @Published var microphones: [MicrophoneDevice] = []
    @Published var selectedMicrophoneID = ""
    @Published var hotKey = HotKeyConfiguration.standard
    @Published var microphoneAllowed = false
    @Published var accessibilityAllowed = false
    @Published var eventPostingAllowed = false
    @Published var configurationEnabled = true
    @Published var showOverlay = true
    @Published var overlaySize = OverlaySizePreset.defaultValue
    @Published var audioDuckingEnabled = true
    @Published var audioDuckingVolumePercent = 10
    @Published var launchAtLogin = false
    @Published var errorMessage: String?

    var onRequestPermissions: (() -> Void)?
    var onOpenMicrophonePrivacy: (() -> Void)?
    var onOpenAccessibilityPrivacy: (() -> Void)?
    var onOpenEventPostingPrivacy: (() -> Void)?
    var onMicrophoneChanged: ((String) -> Void)?
    var onHotKeyChanged: ((HotKeyConfiguration) -> Void)?
    var onHotKeyCaptureError: ((String) -> Void)?
    var onHotKeyRecordingChanged: ((Bool) -> Void)?
    var onOverlayChanged: ((Bool) -> Void)?
    var onOverlaySizeChanged: ((OverlaySizePreset) -> Void)?
    var onAudioDuckingEnabledChanged: ((Bool) -> Void)?
    var onAudioDuckingVolumeChanged: ((Int) -> Void)?
    var onLoginChanged: ((Bool) -> Void)?
    var onDone: (() -> Void)?
}

private extension Color {
    static let orhomBackground = Color(red: 0.055, green: 0.063, blue: 0.082)
    static let orhomSurface = Color(red: 0.088, green: 0.102, blue: 0.132)
    static let orhomSurfaceRaised = Color(red: 0.115, green: 0.132, blue: 0.170)
    static let orhomBorder = Color.white.opacity(0.10)
    static let orhomBlue = Color(red: 0.08, green: 0.49, blue: 1.0)
    static let orhomPurple = Color(red: 0.48, green: 0.31, blue: 1.0)
    static let orhomGreen = Color(red: 0.20, green: 0.82, blue: 0.55)
    static let orhomRed = Color(red: 1.0, green: 0.30, blue: 0.36)
    static let orhomSecondary = Color.white.opacity(0.62)
}

private struct OverlayLayout {
    let visibleSize: CGSize
    let shadowInset: CGFloat
    let cornerRadius: CGFloat
    let horizontalPadding: CGFloat
    let itemSpacing: CGFloat
    let indicatorSize: CGFloat
    let recordingTextWidth: CGFloat
    let stopButtonWidth: CGFloat
    let cancelButtonWidth: CGFloat
    let actionHeight: CGFloat
    let titleFontSize: CGFloat
    let hintFontSize: CGFloat
    let recordingTitleFontSize: CGFloat
    let recordingDurationFontSize: CGFloat
    let actionTitleFontSize: CGFloat
    let actionHintFontSize: CGFloat

    var panelSize: CGSize {
        CGSize(
            width: visibleSize.width + shadowInset * 2,
            height: visibleSize.height + shadowInset * 2
        )
    }

    static func value(for preset: OverlaySizePreset) -> OverlayLayout {
        switch preset {
        case .small:
            return OverlayLayout(
                visibleSize: CGSize(width: 210, height: 42),
                shadowInset: 12,
                cornerRadius: 13,
                horizontalPadding: 6,
                itemSpacing: 4,
                indicatorSize: 32,
                recordingTextWidth: 49,
                stopButtonWidth: 72,
                cancelButtonWidth: 32,
                actionHeight: 32,
                titleFontSize: 12,
                hintFontSize: 9,
                recordingTitleFontSize: 10.5,
                recordingDurationFontSize: 11,
                actionTitleFontSize: 10,
                actionHintFontSize: 8.5
            )
        case .medium:
            return OverlayLayout(
                visibleSize: CGSize(width: 255, height: 50),
                shadowInset: 12,
                cornerRadius: 15,
                horizontalPadding: 7.5,
                itemSpacing: 5,
                indicatorSize: 36,
                recordingTextWidth: 69,
                stopButtonWidth: 88,
                cancelButtonWidth: 32,
                actionHeight: 38,
                titleFontSize: 13,
                hintFontSize: 10,
                recordingTitleFontSize: 12,
                recordingDurationFontSize: 12,
                actionTitleFontSize: 10.5,
                actionHintFontSize: 9
            )
        case .large:
            return OverlayLayout(
                visibleSize: CGSize(width: 300, height: 56),
                shadowInset: 12,
                cornerRadius: 16,
                horizontalPadding: 9,
                itemSpacing: 6,
                indicatorSize: 38,
                recordingTextWidth: 92,
                stopButtonWidth: 104,
                cancelButtonWidth: 30,
                actionHeight: 38,
                titleFontSize: 14,
                hintFontSize: 11,
                recordingTitleFontSize: 13,
                recordingDurationFontSize: 13,
                actionTitleFontSize: 11,
                actionHintFontSize: 9.5
            )
        }
    }
}

private struct WaveformMark: View {
    let level: Float
    let active: Bool
    let size: CGFloat
    let color: Color

    var body: some View {
        let barWidth = max(2, size * 0.075)
        let maximumHeight = size * 0.64
        HStack(spacing: max(1.6, size * 0.055)) {
            ForEach(0..<5, id: \.self) { index in
                let multiplier: Float = [0.50, 0.82, 1.0, 0.72, 0.42][index]
                let idleHeight = CGFloat([8, 15, 21, 13, 7][index]) * size / 38
                Capsule()
                    .fill(color.opacity(active ? 1 : 0.88))
                    .frame(
                        width: barWidth,
                        height: active
                            ? max(4, CGFloat(level * multiplier) * maximumHeight)
                            : max(4, idleHeight)
                    )
            }
        }
        .frame(width: size * 0.72, height: size * 0.72)
        .animation(.easeOut(duration: 0.10), value: level)
    }
}

private struct RecordingActionButtonStyle: ButtonStyle {
    let layout: OverlayLayout

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .frame(
                width: layout.stopButtonWidth,
                height: layout.actionHeight
            )
            .background(
                RoundedRectangle(
                    cornerRadius: max(8, layout.cornerRadius - 5),
                    style: .continuous
                )
                .fill(Color.white.opacity(configuration.isPressed ? 0.13 : 0.075))
            )
            .overlay(
                RoundedRectangle(
                    cornerRadius: max(8, layout.cornerRadius - 5),
                    style: .continuous
                )
                .stroke(Color.white.opacity(0.11), lineWidth: 1)
            )
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
    }
}

private struct RecordingCancelButtonStyle: ButtonStyle {
    let layout: OverlayLayout

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(
                size: layout.visibleSize == CGSize(width: 210, height: 42) ? 10 : 11,
                weight: .bold
            ))
            .foregroundStyle(
                Color.white.opacity(configuration.isPressed ? 0.58 : 0.84)
            )
            .frame(
                width: layout.cancelButtonWidth,
                height: layout.actionHeight
            )
            .background(
                RoundedRectangle(
                    cornerRadius: max(8, layout.cornerRadius - 5),
                    style: .continuous
                )
                .fill(Color.orhomRed.opacity(configuration.isPressed ? 0.28 : 0.11))
            )
            .overlay(
                RoundedRectangle(
                    cornerRadius: max(8, layout.cornerRadius - 5),
                    style: .continuous
                )
                .stroke(Color.orhomRed.opacity(0.22), lineWidth: 1)
            )
    }
}

struct DictationBarView: View {
    @ObservedObject var model: OverlayViewModel

    private var layout: OverlayLayout {
        OverlayLayout.value(for: model.sizePreset)
    }

    var body: some View {
        HStack(spacing: layout.itemSpacing) {
            statusIndicator

            statusText
                .frame(
                    width: model.mode == .recording
                        ? layout.recordingTextWidth
                        : nil,
                    alignment: .leading
                )
                .frame(
                    maxWidth: model.mode == .recording ? nil : .infinity,
                    alignment: .leading
                )

            if model.mode == .recording {
                recordingActions
            }
        }
        .padding(.horizontal, layout.horizontalPadding)
        .frame(
            width: layout.visibleSize.width,
            height: layout.visibleSize.height
        )
        .background(
            RoundedRectangle(cornerRadius: layout.cornerRadius, style: .continuous)
                .fill(
                    LinearGradient(
                        colors: [
                            Color(red: 0.133, green: 0.137, blue: 0.165),
                            Color(red: 0.071, green: 0.075, blue: 0.090)
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                )
        )
        .overlay(
            RoundedRectangle(cornerRadius: layout.cornerRadius, style: .continuous)
                .stroke(Color.white.opacity(0.18), lineWidth: 1)
        )
        .contentShape(
            RoundedRectangle(cornerRadius: layout.cornerRadius, style: .continuous)
        )
        .shadow(color: .black.opacity(0.38), radius: 12, x: 0, y: 5)
        .onTapGesture {
            switch model.mode {
            case .idle, .error:
                model.onPrimary?()
            case .preparing, .recording, .transcribing:
                break
            }
        }
        .help(bodyHelp)
        .accessibilityElement(children: .contain)
        .accessibilityAction(named: Text(bodyAccessibilityAction)) {
            if model.mode == .idle || model.mode == .error {
                model.onPrimary?()
            }
        }
        .padding(layout.shadowInset)
        .frame(
            width: layout.panelSize.width,
            height: layout.panelSize.height
        )
    }

    private var statusIndicator: some View {
        ZStack {
            Circle()
                .fill(indicatorBackground)

            switch model.mode {
            case .preparing, .transcribing:
                ProgressView()
                    .controlSize(model.sizePreset == .small ? .mini : .small)
                    .tint(Color.orhomBlue)
            case .error:
                Image(systemName: "exclamationmark")
                    .font(.system(
                        size: layout.indicatorSize * 0.36,
                        weight: .bold
                    ))
                    .foregroundStyle(Color.orhomRed)
            case .recording:
                WaveformMark(
                    level: model.level,
                    active: true,
                    size: layout.indicatorSize,
                    color: Color.orhomRed
                )
            case .idle:
                Image(systemName: "mic.fill")
                    .font(.system(
                        size: layout.indicatorSize * 0.40,
                        weight: .semibold
                    ))
                    .foregroundStyle(Color.orhomBlue)
            }
        }
        .frame(width: layout.indicatorSize, height: layout.indicatorSize)
    }

    private var statusText: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(displayTitle)
                .font(.system(
                    size: model.mode == .recording
                        ? layout.recordingTitleFontSize
                        : layout.titleFontSize,
                    weight: .bold
                ))
                .foregroundStyle(.white)
                .lineLimit(1)

            Text(model.mode == .recording ? model.elapsed : model.detail)
                .font(.system(
                    size: model.mode == .recording
                        ? layout.recordingDurationFontSize
                        : layout.hintFontSize,
                    weight: model.mode == .recording ? .semibold : .regular
                ))
                .foregroundStyle(
                    model.mode == .recording
                        ? Color(red: 0.98, green: 0.44, blue: 0.52)
                        : Color.orhomSecondary
                )
                .lineLimit(1)
        }
    }

    private var recordingActions: some View {
        HStack(spacing: layout.itemSpacing) {
            Button(action: { model.onPrimary?() }) {
                HStack(spacing: model.sizePreset == .small ? 4 : 6) {
                    RoundedRectangle(cornerRadius: 2, style: .continuous)
                        .fill(Color(red: 0.98, green: 0.44, blue: 0.52))
                        .frame(
                            width: model.sizePreset == .small ? 8 : 9,
                            height: model.sizePreset == .small ? 8 : 9
                        )

                    VStack(alignment: .leading, spacing: -1) {
                        Text("Stopp")
                            .font(.system(
                                size: layout.actionTitleFontSize,
                                weight: .bold
                            ))
                        Text("& einfügen")
                            .font(.system(
                                size: layout.actionHintFontSize,
                                weight: .regular
                            ))
                            .foregroundStyle(Color.orhomSecondary)
                    }
                    .lineLimit(1)
                }
                .foregroundStyle(.white)
            }
            .buttonStyle(RecordingActionButtonStyle(layout: layout))
            .help("Aufnahme stoppen und Text einfügen")
            .accessibilityLabel("Aufnahme stoppen und Text einfügen")

            Button(action: { model.onCancel?() }) {
                Image(systemName: "xmark")
            }
            .buttonStyle(RecordingCancelButtonStyle(layout: layout))
            .help("Aufnahme verwerfen")
            .accessibilityLabel("Aufnahme verwerfen")
        }
    }

    private var displayTitle: String {
        if model.mode == .recording && model.sizePreset == .small {
            return "Aufn."
        }
        return model.title
    }

    private var indicatorBackground: Color {
        switch model.mode {
        case .recording, .error:
            return Color.orhomRed.opacity(0.16)
        case .preparing, .idle, .transcribing:
            return Color.orhomBlue.opacity(0.15)
        }
    }

    private var bodyHelp: String {
        switch model.mode {
        case .idle:
            return "Klicken zum Diktieren · ziehen zum Verschieben"
        case .recording:
            return "Zum Verschieben am Statusbereich ziehen"
        case .error:
            return "Klicken, um es erneut zu versuchen"
        case .preparing, .transcribing:
            return "ORhom verarbeitet lokal"
        }
    }

    private var bodyAccessibilityAction: String {
        model.mode == .error ? "Erneut versuchen" : "Diktierung starten"
    }
}

private struct SettingsActionButtonStyle: ButtonStyle {
    let prominent: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(prominent ? Color.white : Color.white.opacity(0.84))
            .padding(.horizontal, 15)
            .frame(height: 34)
            .background(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(
                        prominent
                            ? Color.orhomBlue.opacity(configuration.isPressed ? 0.75 : 1)
                            : Color.white.opacity(configuration.isPressed ? 0.12 : 0.075)
                    )
            )
    }
}

private struct InfoCard<Content: View>: View {
    let icon: String
    let title: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 8) {
                Image(systemName: icon)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Color.orhomBlue)
                    .frame(width: 23, height: 23)
                    .background(
                        RoundedRectangle(cornerRadius: 7, style: .continuous)
                            .fill(Color.orhomBlue.opacity(0.12))
                    )
                Text(title.uppercased())
                    .font(.system(size: 10.5, weight: .bold))
                    .tracking(0.6)
                    .foregroundStyle(Color.orhomSecondary)
            }
            content()
        }
        .padding(14)
        .frame(maxWidth: .infinity, minHeight: 116, alignment: .topLeading)
        .background(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .fill(Color.orhomSurface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .stroke(Color.orhomBorder, lineWidth: 1)
        )
    }
}

private struct PermissionRow: View {
    let title: String
    let detail: String
    let allowed: Bool
    let action: () -> Void

    var body: some View {
        HStack(spacing: 11) {
            Image(systemName: allowed ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.system(size: 17))
                .foregroundStyle(allowed ? Color.orhomGreen : Color.orhomRed)
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.system(size: 12.5, weight: .semibold))
                    .foregroundStyle(.white)
                Text(detail)
                    .font(.system(size: 10.5))
                    .foregroundStyle(Color.orhomSecondary)
            }
            Spacer()
            if !allowed {
                Button("Öffnen", action: action)
                    .buttonStyle(SettingsActionButtonStyle(prominent: false))
            }
        }
    }
}

private final class HotKeyRecorderButton: NSButton {
    var configuration = HotKeyConfiguration.standard {
        didSet {
            if !isRecording {
                title = configuration.displayName
            }
        }
    }
    var onCapture: ((HotKeyConfiguration) -> Void)?
    var onError: ((String) -> Void)?
    var onRecordingChanged: ((Bool) -> Void)?
    private var isRecording = false

    override var acceptsFirstResponder: Bool {
        true
    }

    override func mouseDown(with event: NSEvent) {
        _ = window?.makeFirstResponder(self)
        isRecording = true
        onRecordingChanged?(true)
        title = "Tastenkürzel drücken …"
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == UInt16(kVK_Escape),
           event.modifierFlags
            .intersection(.deviceIndependentFlagsMask)
            .isDisjoint(with: [.command, .option, .control, .shift]) {
            finishRecording()
            return
        }

        do {
            let captured = try HotKeyConfiguration.capture(from: event)
            configuration = captured
            onCapture?(captured)
            finishRecording()
        } catch {
            onError?(error.localizedDescription)
            NSSound.beep()
            title = "Andere Kombination drücken …"
        }
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard isRecording, event.type == .keyDown else {
            return super.performKeyEquivalent(with: event)
        }
        keyDown(with: event)
        return true
    }

    override func resignFirstResponder() -> Bool {
        let result = super.resignFirstResponder()
        if isRecording {
            isRecording = false
            onRecordingChanged?(false)
        }
        title = configuration.displayName
        return result
    }

    private func finishRecording() {
        if isRecording {
            isRecording = false
            onRecordingChanged?(false)
        }
        title = configuration.displayName
        window?.makeFirstResponder(nil)
    }
}

private struct HotKeyRecorder: NSViewRepresentable {
    let configuration: HotKeyConfiguration
    let onCapture: (HotKeyConfiguration) -> Void
    let onError: (String) -> Void
    let onRecordingChanged: (Bool) -> Void

    func makeNSView(context: Context) -> HotKeyRecorderButton {
        let button = HotKeyRecorderButton(title: configuration.displayName, target: nil, action: nil)
        button.configuration = configuration
        button.bezelStyle = .rounded
        button.font = .monospacedSystemFont(ofSize: 12.5, weight: .semibold)
        button.toolTip = "Klicken und gewünschtes Tastenkürzel drücken"
        button.onCapture = onCapture
        button.onError = onError
        button.onRecordingChanged = onRecordingChanged
        return button
    }

    func updateNSView(_ button: HotKeyRecorderButton, context: Context) {
        button.configuration = configuration
        button.onCapture = onCapture
        button.onError = onError
        button.onRecordingChanged = onRecordingChanged
    }
}

struct ORhomSettingsView: View {
    @ObservedObject var model: SettingsViewModel

    private let columns = [
        GridItem(.flexible(), spacing: 12),
        GridItem(.flexible(), spacing: 12)
    ]

    var body: some View {
        ZStack {
            Color.orhomBackground.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 17) {
                    header
                    if let error = model.errorMessage {
                        errorBanner(error)
                    }
                    LazyVGrid(columns: columns, spacing: 12) {
                        engineCard
                        inputCard
                            .disabled(!model.configurationEnabled)
                            .opacity(model.configurationEnabled ? 1 : 0.58)
                    }
                    permissionsCard
                    preferences
                    footer
                }
                .padding(.horizontal, 28)
                .padding(.top, 28)
                .padding(.bottom, 24)
            }
        }
        .frame(minWidth: 720, minHeight: 640)
        .preferredColorScheme(.dark)
    }

    private var header: some View {
        HStack(spacing: 15) {
            ZStack {
                RoundedRectangle(cornerRadius: 17, style: .continuous)
                    .fill(
                        LinearGradient(
                            colors: [.orhomBlue, .orhomPurple],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )
                    .frame(width: 58, height: 58)
                    .shadow(color: Color.orhomBlue.opacity(0.28), radius: 14, y: 6)
                Image(systemName: "waveform.and.mic")
                    .font(.system(size: 27, weight: .medium))
                    .foregroundStyle(.white)
            }

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text("ORhom")
                        .font(.system(size: 25, weight: .bold))
                        .foregroundStyle(.white)
                    Text("MAC")
                        .font(.system(size: 9.5, weight: .heavy))
                        .tracking(0.8)
                        .foregroundStyle(Color.orhomBlue)
                        .padding(.horizontal, 7)
                        .padding(.vertical, 4)
                        .background(
                            Capsule().fill(Color.orhomBlue.opacity(0.13))
                        )
                }
                Text("Diktieren, wo du schreibst.")
                    .font(.system(size: 14))
                    .foregroundStyle(Color.orhomSecondary)
            }
            Spacer()
            HStack(spacing: 6) {
                Circle()
                    .fill(model.engineReady ? Color.orhomGreen : Color.orange)
                    .frame(width: 7, height: 7)
                Text(model.engineReady ? "Lokal bereit" : "Wird vorbereitet")
                    .font(.system(size: 11.5, weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.80))
            }
            .padding(.horizontal, 11)
            .frame(height: 30)
            .background(Capsule().fill(Color.white.opacity(0.065)))
        }
    }

    private var engineCard: some View {
        InfoCard(icon: "cpu", title: "Lokale Engine") {
            VStack(alignment: .leading, spacing: 5) {
                Text(model.engineTitle)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(.white)
                Text(model.engineDetail)
                    .font(.system(size: 11.5))
                    .foregroundStyle(Color.orhomSecondary)
                    .lineLimit(2)
                if let progress = model.engineProgress {
                    ProgressView(value: progress)
                        .tint(Color.orhomBlue)
                        .padding(.top, 3)
                }
            }
        }
    }

    private var inputCard: some View {
        InfoCard(icon: "slider.horizontal.3", title: "Eingabe") {
            VStack(alignment: .leading, spacing: 11) {
                HStack(spacing: 9) {
                    Text("Mikrofon")
                        .font(.system(size: 11.5, weight: .medium))
                        .foregroundStyle(Color.orhomSecondary)
                    Spacer(minLength: 8)
                    Picker(
                        "",
                        selection: Binding(
                            get: { model.selectedMicrophoneID },
                            set: {
                                model.selectedMicrophoneID = $0
                                model.onMicrophoneChanged?($0)
                            }
                        )
                    ) {
                        if model.microphones.isEmpty {
                            Text("Kein Mikrofon verfügbar").tag("")
                        } else {
                            ForEach(model.microphones) { microphone in
                                Text(
                                    microphone.isBuiltIn
                                        ? "\(microphone.name) · intern"
                                        : microphone.name
                                )
                                .tag(microphone.id)
                            }
                        }
                    }
                    .labelsHidden()
                    .pickerStyle(.menu)
                    .frame(maxWidth: 220)
                }

                Divider().overlay(Color.orhomBorder)

                HStack(spacing: 9) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Tastenkürzel")
                            .font(.system(size: 11.5, weight: .medium))
                            .foregroundStyle(Color.orhomSecondary)
                        Text("Klicken, dann Kombination drücken")
                            .font(.system(size: 9.5))
                            .foregroundStyle(Color.orhomSecondary.opacity(0.82))
                    }
                    Spacer(minLength: 8)
                    HotKeyRecorder(
                        configuration: model.hotKey,
                        onCapture: { model.onHotKeyChanged?($0) },
                        onError: { model.onHotKeyCaptureError?($0) },
                        onRecordingChanged: {
                            model.onHotKeyRecordingChanged?($0)
                        }
                    )
                    .frame(width: 180, height: 30)
                }
            }
        }
    }

    private var permissionsCard: some View {
        VStack(alignment: .leading, spacing: 13) {
            HStack {
                Text("BERECHTIGUNGEN")
                    .font(.system(size: 10.5, weight: .bold))
                    .tracking(0.6)
                    .foregroundStyle(Color.orhomSecondary)
                Spacer()
                Button("Jetzt prüfen") {
                    model.onRequestPermissions?()
                }
                .buttonStyle(SettingsActionButtonStyle(prominent: false))
            }
            PermissionRow(
                title: "Mikrofon",
                detail: model.microphoneAllowed
                    ? model.microphoneName
                    : "Wird für die lokale Aufnahme benötigt",
                allowed: model.microphoneAllowed,
                action: { model.onOpenMicrophonePrivacy?() }
            )
            Divider().overlay(Color.orhomBorder)
            PermissionRow(
                title: "Bedienungshilfen",
                detail: model.accessibilityAllowed
                    ? "Direktes Einfügen am Cursor ist freigegeben"
                    : "Ohne Freigabe bleibt der Text sicher in der Zwischenablage",
                allowed: model.accessibilityAllowed,
                action: { model.onOpenAccessibilityPrivacy?() }
            )
            Divider().overlay(Color.orhomBorder)
            PermissionRow(
                title: "Tastatursteuerung",
                detail: model.eventPostingAllowed
                    ? "Einfügen in Chrome und Web-Editoren ist freigegeben"
                    : "Wird für ⌘V in Browser-Textfeldern benötigt",
                allowed: model.eventPostingAllowed,
                action: { model.onOpenEventPostingPrivacy?() }
            )
        }
        .padding(15)
        .background(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .fill(Color.orhomSurface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .stroke(Color.orhomBorder, lineWidth: 1)
        )
    }

    private var preferences: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: "rectangle.inset.filled")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Color.orhomBlue)
                    .frame(width: 23, height: 23)
                    .background(
                        RoundedRectangle(cornerRadius: 7, style: .continuous)
                            .fill(Color.orhomBlue.opacity(0.12))
                    )
                Text("ANZEIGE & AUDIO")
                    .font(.system(size: 10.5, weight: .bold))
                    .tracking(0.6)
                    .foregroundStyle(Color.orhomSecondary)
            }

            HStack(spacing: 12) {
                preferenceLabel(
                    title: "Diktierleiste anzeigen",
                    detail: "Status und Aufnahmeaktionen immer griffbereit"
                )
                Spacer(minLength: 12)
                Toggle(
                    "",
                    isOn: Binding(
                        get: { model.showOverlay },
                        set: {
                            model.showOverlay = $0
                            model.onOverlayChanged?($0)
                        }
                    )
                )
                .labelsHidden()
                .accessibilityLabel("Diktierleiste anzeigen")
            }

            Divider().overlay(Color.orhomBorder)

            HStack(spacing: 12) {
                preferenceLabel(
                    title: "Andere Audioquellen leiser",
                    detail: model.audioDuckingEnabled
                        ? "Während der Aufnahme auf \(model.audioDuckingVolumePercent) % des Ausgangspegels"
                        : "Musik und Videos unverändert weiterlaufen lassen"
                )
                Spacer(minLength: 12)
                Toggle(
                    "",
                    isOn: Binding(
                        get: { model.audioDuckingEnabled },
                        set: {
                            model.audioDuckingEnabled = $0
                            model.onAudioDuckingEnabledChanged?($0)
                        }
                    )
                )
                .labelsHidden()
                .accessibilityLabel("Andere Audioquellen leiser")
            }

            if model.audioDuckingEnabled {
                HStack(spacing: 12) {
                    Text("Verbleibende Lautstärke")
                        .font(.system(size: 10.5))
                        .foregroundStyle(Color.orhomSecondary)
                    Spacer(minLength: 12)
                    Slider(
                        value: Binding(
                            get: {
                                Double(model.audioDuckingVolumePercent)
                            },
                            set: {
                                let value = Int($0.rounded())
                                model.audioDuckingVolumePercent = value
                                model.onAudioDuckingVolumeChanged?(value)
                            }
                        ),
                        in: 0...40,
                        step: 5
                    )
                    .frame(width: 170)
                    Text("\(model.audioDuckingVolumePercent) %")
                        .font(.system(size: 10.5, weight: .semibold))
                        .monospacedDigit()
                        .foregroundStyle(.white)
                        .frame(width: 38, alignment: .trailing)
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Verbleibende Lautstärke")
                .accessibilityValue("\(model.audioDuckingVolumePercent) Prozent")
            }

            Divider().overlay(Color.orhomBorder)

            HStack(spacing: 12) {
                preferenceLabel(
                    title: "Größe der Diktierleiste",
                    detail: "Klein spart Platz, Groß bietet mehr Lesbarkeit"
                )
                Spacer(minLength: 12)
                Picker(
                    "Größe der Diktierleiste",
                    selection: Binding(
                        get: { model.overlaySize },
                        set: {
                            model.overlaySize = $0
                            model.onOverlaySizeChanged?($0)
                        }
                    )
                ) {
                    ForEach(OverlaySizePreset.allCases) { preset in
                        Text(preset.title).tag(preset)
                    }
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                .frame(width: 220)
                .disabled(!model.configurationEnabled)
                .accessibilityLabel("Größe der Diktierleiste")
                .accessibilityValue(model.overlaySize.title)
            }

            Divider().overlay(Color.orhomBorder)

            HStack(spacing: 12) {
                preferenceLabel(
                    title: "Beim Anmelden starten",
                    detail: "ORhom automatisch im Hintergrund bereitstellen"
                )
                Spacer(minLength: 12)
                Toggle(
                    "",
                    isOn: Binding(
                        get: { model.launchAtLogin },
                        set: {
                            model.launchAtLogin = $0
                            model.onLoginChanged?($0)
                        }
                    )
                )
                .labelsHidden()
                .accessibilityLabel("Beim Anmelden starten")
            }
        }
        .toggleStyle(.switch)
        .tint(Color.orhomBlue)
        .padding(15)
        .background(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .fill(Color.orhomSurface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .stroke(Color.orhomBorder, lineWidth: 1)
        )
    }

    private func preferenceLabel(title: String, detail: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(.white)
            Text(detail)
                .font(.system(size: 9.5))
                .foregroundStyle(Color.orhomSecondary)
                .lineLimit(1)
        }
    }

    private var footer: some View {
        HStack {
            Image(systemName: "lock.shield.fill")
                .foregroundStyle(Color.orhomGreen)
            Text("Alles bleibt lokal · Änderungen werden sofort gespeichert.")
                .font(.system(size: 11.5))
                .foregroundStyle(Color.orhomSecondary)
            Spacer()
            Button("Fertig") {
                model.onDone?()
            }
            .buttonStyle(SettingsActionButtonStyle(prominent: true))
            .keyboardShortcut(.defaultAction)
        }
        .padding(.top, 2)
    }

    private func errorBanner(_ message: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Color.orhomRed)
            Text(message)
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(.white)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
            Button {
                model.errorMessage = nil
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.plain)
            .foregroundStyle(Color.orhomSecondary)
        }
        .padding(12)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(Color.orhomRed.opacity(0.10))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(Color.orhomRed.opacity(0.25), lineWidth: 1)
        )
    }

    private func keycap(_ value: String) -> some View {
        Text(value)
            .font(.system(size: 11, weight: .semibold, design: .rounded))
            .foregroundStyle(.white)
            .padding(.horizontal, 8)
            .frame(height: 25)
            .background(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(Color.orhomSurfaceRaised)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .stroke(Color.white.opacity(0.12), lineWidth: 1)
            )
    }
}

final class OverlayPanelController: NSObject, NSWindowDelegate {
    private enum PositionDefaultsKey {
        static let screenIdentifier = "recordingOverlayScreenIdentifier"
        static let relativeX = "recordingOverlayRelativeX"
        static let relativeY = "recordingOverlayRelativeY"
    }

    private struct PersistedPosition {
        let screenIdentifier: String
        let relativeX: CGFloat
        let relativeY: CGFloat
    }

    let model: OverlayViewModel
    private let panel: NSPanel
    private let defaults: UserDefaults
    private var hasPositionedPanel = false
    private var isAdjustingPanelFrame = false
    private var moveSettlementWorkItem: DispatchWorkItem?
    private var screenParametersObserver: NSObjectProtocol?

    init(
        model: OverlayViewModel,
        defaults: UserDefaults = .standard
    ) {
        self.model = model
        self.defaults = defaults
        let initialLayout = OverlayLayout.value(for: model.sizePreset)
        panel = NSPanel(
            contentRect: NSRect(
                origin: .zero,
                size: initialLayout.panelSize
            ),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        super.init()
        panel.contentView = NSHostingView(rootView: DictationBarView(model: model))
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.delegate = self
        screenParametersObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.screenParametersDidChange()
        }
    }

    deinit {
        moveSettlementWorkItem?.cancel()
        if let screenParametersObserver {
            NotificationCenter.default.removeObserver(screenParametersObserver)
        }
    }

    func applySizePreset(_ preset: OverlaySizePreset) {
        guard Thread.isMainThread else {
            DispatchQueue.main.async { [weak self] in
                self?.applySizePreset(preset)
            }
            return
        }

        let newSize = OverlayLayout.value(for: preset).panelSize
        let oldFrame = panel.frame
        model.sizePreset = preset

        guard oldFrame.size != newSize else {
            if hasPositionedPanel {
                clampPanelToVisibleFrame()
                persistPanelPosition()
            }
            return
        }

        let shouldPreservePosition = hasPositionedPanel || panel.isVisible
        let proposedOrigin: NSPoint
        if shouldPreservePosition,
           let screen = bestScreen(for: oldFrame) {
            proposedOrigin = resizedOrigin(
                from: oldFrame,
                to: newSize,
                in: screen.visibleFrame
            )
        } else {
            proposedOrigin = oldFrame.origin
        }

        setPanelFrame(
            NSRect(origin: proposedOrigin, size: newSize),
            display: panel.isVisible
        )
        if shouldPreservePosition {
            hasPositionedPanel = true
            clampPanelToVisibleFrame()
            persistPanelPosition()
        }
    }

    func show() {
        positionIfNeeded()
        clampPanelToVisibleFrame()
        persistPanelPosition()
        panel.orderFrontRegardless()
    }

    func hide() {
        settlePanelPosition()
        panel.orderOut(nil)
    }

    private func positionIfNeeded() {
        guard !hasPositionedPanel else {
            return
        }

        if restorePersistedPosition() {
            hasPositionedPanel = true
            persistPanelPosition()
            return
        }

        guard let screen = NSScreen.main ?? NSScreen.screens.first else {
            return
        }
        let visible = screen.visibleFrame
        setPanelOrigin(
            NSPoint(
                x: visible.midX - panel.frame.width / 2,
                y: visible.minY + 16
            )
        )
        hasPositionedPanel = true
        persistPanelPosition()
    }

    private func resizedOrigin(
        from oldFrame: NSRect,
        to newSize: CGSize,
        in visibleFrame: NSRect
    ) -> NSPoint {
        let oldMovableWidth = max(visibleFrame.width - oldFrame.width, 0)
        let oldMovableHeight = max(visibleFrame.height - oldFrame.height, 0)
        let relativeX = oldMovableWidth > 0
            ? min(max((oldFrame.minX - visibleFrame.minX) / oldMovableWidth, 0), 1)
            : 0
        let relativeY = oldMovableHeight > 0
            ? min(max((oldFrame.minY - visibleFrame.minY) / oldMovableHeight, 0), 1)
            : 0
        let newMovableWidth = max(visibleFrame.width - newSize.width, 0)
        let newMovableHeight = max(visibleFrame.height - newSize.height, 0)
        return NSPoint(
            x: visibleFrame.minX + relativeX * newMovableWidth,
            y: visibleFrame.minY + relativeY * newMovableHeight
        )
    }

    private func clampPanelToVisibleFrame() {
        guard let screen = bestScreen(for: panel.frame) else { return }
        let visible = screen.visibleFrame
        let maximumX = max(visible.minX, visible.maxX - panel.frame.width)
        let maximumY = max(visible.minY, visible.maxY - panel.frame.height)
        let clampedOrigin = NSPoint(
            x: min(max(panel.frame.minX, visible.minX), maximumX),
            y: min(max(panel.frame.minY, visible.minY), maximumY)
        )
        if clampedOrigin != panel.frame.origin {
            setPanelOrigin(clampedOrigin)
        }
    }

    private func bestScreen(for frame: NSRect) -> NSScreen? {
        let center = NSPoint(x: frame.midX, y: frame.midY)
        if let containingScreen = NSScreen.screens.first(where: {
            $0.frame.contains(center)
        }) {
            return containingScreen
        }

        guard let bestIntersectingScreen = NSScreen.screens.max(by: { lhs, rhs in
            intersectionArea(frame, lhs.frame) < intersectionArea(frame, rhs.frame)
        }), intersectionArea(frame, bestIntersectingScreen.frame) > 0 else {
            return NSScreen.main ?? NSScreen.screens.first
        }
        return bestIntersectingScreen
    }

    private func intersectionArea(_ lhs: NSRect, _ rhs: NSRect) -> CGFloat {
        let intersection = lhs.intersection(rhs)
        guard !intersection.isNull else { return 0 }
        return intersection.width * intersection.height
    }

    private func restorePersistedPosition() -> Bool {
        guard let persistedPosition = loadPersistedPosition() else {
            return false
        }

        let targetScreen = screen(
            matching: persistedPosition.screenIdentifier
        ) ?? NSScreen.main ?? NSScreen.screens.first
        guard let targetScreen else {
            return false
        }

        setPanelOrigin(
            origin(
                for: persistedPosition,
                panelSize: panel.frame.size,
                visibleFrame: targetScreen.visibleFrame
            )
        )
        clampPanelToVisibleFrame()
        persistPanelPosition(on: targetScreen)
        return true
    }

    private func loadPersistedPosition() -> PersistedPosition? {
        guard
            let screenIdentifier = defaults.string(
                forKey: PositionDefaultsKey.screenIdentifier
            ),
            !screenIdentifier.isEmpty,
            let relativeXNumber = defaults.object(
                forKey: PositionDefaultsKey.relativeX
            ) as? NSNumber,
            let relativeYNumber = defaults.object(
                forKey: PositionDefaultsKey.relativeY
            ) as? NSNumber
        else {
            return nil
        }

        let relativeX = CGFloat(relativeXNumber.doubleValue)
        let relativeY = CGFloat(relativeYNumber.doubleValue)
        guard relativeX.isFinite, relativeY.isFinite else {
            return nil
        }

        return PersistedPosition(
            screenIdentifier: screenIdentifier,
            relativeX: min(max(relativeX, 0), 1),
            relativeY: min(max(relativeY, 0), 1)
        )
    }

    private func persistPanelPosition(on explicitScreen: NSScreen? = nil) {
        guard
            hasPositionedPanel,
            let targetScreen = explicitScreen ?? bestScreen(for: panel.frame),
            let screenIdentifier = screenIdentifier(for: targetScreen)
        else {
            return
        }

        let visibleFrame = targetScreen.visibleFrame
        let movableWidth = max(visibleFrame.width - panel.frame.width, 0)
        let movableHeight = max(visibleFrame.height - panel.frame.height, 0)
        let relativeX = movableWidth > 0
            ? min(max((panel.frame.minX - visibleFrame.minX) / movableWidth, 0), 1)
            : 0
        let relativeY = movableHeight > 0
            ? min(max((panel.frame.minY - visibleFrame.minY) / movableHeight, 0), 1)
            : 0

        defaults.set(
            screenIdentifier,
            forKey: PositionDefaultsKey.screenIdentifier
        )
        defaults.set(
            Double(relativeX),
            forKey: PositionDefaultsKey.relativeX
        )
        defaults.set(
            Double(relativeY),
            forKey: PositionDefaultsKey.relativeY
        )
    }

    private func origin(
        for persistedPosition: PersistedPosition,
        panelSize: NSSize,
        visibleFrame: NSRect
    ) -> NSPoint {
        let movableWidth = max(visibleFrame.width - panelSize.width, 0)
        let movableHeight = max(visibleFrame.height - panelSize.height, 0)
        return NSPoint(
            x: visibleFrame.minX + persistedPosition.relativeX * movableWidth,
            y: visibleFrame.minY + persistedPosition.relativeY * movableHeight
        )
    }

    private func screen(matching identifier: String) -> NSScreen? {
        NSScreen.screens.first {
            screenIdentifier(for: $0) == identifier
        }
    }

    private func screenIdentifier(for screen: NSScreen) -> String? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        guard let displayNumber = screen.deviceDescription[key] as? NSNumber else {
            return nil
        }
        return String(displayNumber.uint32Value)
    }

    private func setPanelOrigin(_ origin: NSPoint) {
        isAdjustingPanelFrame = true
        panel.setFrameOrigin(origin)
        isAdjustingPanelFrame = false
    }

    private func setPanelFrame(_ frame: NSRect, display: Bool) {
        isAdjustingPanelFrame = true
        panel.setFrame(frame, display: display)
        isAdjustingPanelFrame = false
    }

    private func scheduleMoveSettlement() {
        moveSettlementWorkItem?.cancel()
        let workItem = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.moveSettlementWorkItem = nil
            self.settlePanelPosition()
        }
        moveSettlementWorkItem = workItem
        DispatchQueue.main.asyncAfter(
            deadline: .now() + 0.15,
            execute: workItem
        )
    }

    private func settlePanelPosition() {
        guard hasPositionedPanel, !isAdjustingPanelFrame else { return }
        moveSettlementWorkItem?.cancel()
        moveSettlementWorkItem = nil
        clampPanelToVisibleFrame()
        persistPanelPosition()
    }

    private func screenParametersDidChange() {
        guard hasPositionedPanel else { return }
        moveSettlementWorkItem?.cancel()
        moveSettlementWorkItem = nil

        if !restorePersistedPosition() {
            clampPanelToVisibleFrame()
            persistPanelPosition()
        }
    }

    func windowDidMove(_ notification: Notification) {
        guard
            let movedWindow = notification.object as? NSWindow,
            movedWindow === panel,
            hasPositionedPanel,
            !isAdjustingPanelFrame
        else {
            return
        }

        persistPanelPosition()
        scheduleMoveSettlement()
    }

    func windowDidChangeScreen(_ notification: Notification) {
        guard
            let movedWindow = notification.object as? NSWindow,
            movedWindow === panel,
            hasPositionedPanel,
            !isAdjustingPanelFrame
        else {
            return
        }
        scheduleMoveSettlement()
    }
}

final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    let model: SettingsViewModel

    var isVisible: Bool {
        window?.isVisible == true
    }

    init(model: SettingsViewModel) {
        self.model = model
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 760, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "ORhom"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(
            calibratedRed: 0.055,
            green: 0.063,
            blue: 0.082,
            alpha: 1
        )
        window.minSize = NSSize(width: 720, height: 640)
        window.isReleasedWhenClosed = false
        window.center()
        window.contentView = NSHostingView(rootView: ORhomSettingsView(model: model))
        super.init(window: window)
        window.delegate = self
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func present() {
        showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func closeToMenuBar() {
        window?.makeFirstResponder(nil)
        window?.orderOut(nil)
        NSApp.setActivationPolicy(.accessory)
    }

    func windowWillClose(_ notification: Notification) {
        window?.makeFirstResponder(nil)
        NSApp.setActivationPolicy(.accessory)
    }

    func windowDidResignKey(_ notification: Notification) {
        window?.makeFirstResponder(nil)
    }
}
