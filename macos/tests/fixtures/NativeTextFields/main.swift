import AppKit
import Foundation
import SwiftUI

private enum FixtureError: LocalizedError {
    case missingArgument(String)
    case unsupportedCase(String)

    var errorDescription: String? {
        switch self {
        case .missingArgument(let name):
            return "Missing required fixture argument: \(name)"
        case .unsupportedCase(let name):
            return "Unsupported native field fixture case: \(name)"
        }
    }
}

private enum NativeFieldCase: String, CaseIterable {
    case textField = "text-field"
    case searchField = "search-field"
    case textView = "text-view"
    case comboBox = "combo-box"
    case secureField = "secure-field"
    case readonlyField = "readonly-field"
    case disabledField = "disabled-field"
    case swiftUITextField = "swiftui-text-field"
    case swiftUITextEditor = "swiftui-text-editor"
    case swiftUISecureField = "swiftui-secure-field"

    var displayName: String {
        switch self {
        case .textField:
            return "NSTextField"
        case .searchField:
            return "NSSearchField"
        case .textView:
            return "NSTextView"
        case .comboBox:
            return "NSComboBox"
        case .secureField:
            return "NSSecureTextField"
        case .readonlyField:
            return "NSTextField (read-only)"
        case .disabledField:
            return "NSTextField (disabled)"
        case .swiftUITextField:
            return "SwiftUI TextField"
        case .swiftUITextEditor:
            return "SwiftUI TextEditor"
        case .swiftUISecureField:
            return "SwiftUI SecureField"
        }
    }

    var accessibilityIdentifier: String {
        "at.orhom.tests.native-text-fields.\(rawValue)"
    }

    var shouldAcceptAutomaticInsertion: Bool {
        switch self {
        case .textField, .searchField, .textView, .comboBox,
             .swiftUITextField, .swiftUITextEditor:
            return true
        case .secureField, .readonlyField, .disabledField,
             .swiftUISecureField:
            return false
        }
    }

    var isSwiftUI: Bool {
        switch self {
        case .swiftUITextField, .swiftUITextEditor, .swiftUISecureField:
            return true
        default:
            return false
        }
    }

    var isMultiline: Bool {
        self == .textView || self == .swiftUITextEditor
    }

    var isSecure: Bool {
        self == .secureField || self == .swiftUISecureField
    }
}

private struct FixtureState: Encodable {
    let ready: Bool
    let processIdentifier: Int32
    let caseName: String
    let controlClass: String
    let accessibilityIdentifier: String
    let value: String
    let focused: Bool
    let editable: Bool
    let enabled: Bool
    let secure: Bool
    let shouldAcceptAutomaticInsertion: Bool
}

private final class SwiftUIFieldModel: ObservableObject {
    @Published var value: String {
        didSet {
            onChange?()
        }
    }

    var onChange: (() -> Void)?

    init(value: String) {
        self.value = value
    }
}

private struct SwiftUIFieldFixtureView: View {
    let fieldCase: NativeFieldCase
    @ObservedObject var model: SwiftUIFieldModel
    @FocusState private var isFocused: Bool

    var body: some View {
        target
            .font(.system(size: 17))
            .focused($isFocused)
            .accessibilityIdentifier(fieldCase.accessibilityIdentifier)
            .onAppear {
                DispatchQueue.main.async {
                    isFocused = true
                }
            }
    }

    @ViewBuilder
    private var target: some View {
        switch fieldCase {
        case .swiftUITextField:
            TextField("SwiftUI text field", text: $model.value)
                .textFieldStyle(.roundedBorder)
        case .swiftUITextEditor:
            TextEditor(text: $model.value)
                .border(Color.secondary)
        case .swiftUISecureField:
            SecureField("SwiftUI secure field", text: $model.value)
                .textFieldStyle(.roundedBorder)
        default:
            EmptyView()
        }
    }
}

private final class FixtureAppDelegate: NSObject,
    NSApplicationDelegate,
    NSTextFieldDelegate,
    NSTextViewDelegate,
    NSWindowDelegate
{
    private let fieldCase: NativeFieldCase
    private let initialText: String
    private let stateURL: URL

    private var window: NSWindow?
    private weak var textControl: NSTextField?
    private weak var textView: NSTextView?
    private var swiftUIModel: SwiftUIFieldModel?
    private var stateTimer: Timer?
    private var ready = false

    init(fieldCase: NativeFieldCase, initialText: String, stateURL: URL) {
        self.fieldCase = fieldCase
        self.initialText = initialText
        self.stateURL = stateURL
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 700, height: 310),
            styleMask: [.titled, .closable, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        window.title = "ORhom Native Field Fixture — \(fieldCase.displayName)"
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        self.window = window

        let contentView = NSView()
        contentView.translatesAutoresizingMaskIntoConstraints = false
        window.contentView = contentView

        let titleLabel = NSTextField(
            labelWithString: "ORhom native text-field test"
        )
        titleLabel.font = .boldSystemFont(ofSize: 22)

        let caseLabel = NSTextField(
            labelWithString:
                "\(fieldCase.displayName) · \(fieldCase.rawValue)"
        )
        caseLabel.font = .systemFont(ofSize: 15)
        caseLabel.textColor = .secondaryLabelColor

        let instruction = NSTextField(
            wrappingLabelWithString: fieldCase.shouldAcceptAutomaticInsertion
                ? "The diagnostic token must be inserted exactly at the existing caret."
                : "This negative test must remain byte-for-byte unchanged."
        )
        instruction.textColor = .secondaryLabelColor

        let targetView = makeTargetView()
        let stack = NSStackView(
            views: [titleLabel, caseLabel, instruction, targetView]
        )
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(
                equalTo: contentView.leadingAnchor,
                constant: 28
            ),
            stack.trailingAnchor.constraint(
                equalTo: contentView.trailingAnchor,
                constant: -28
            ),
            stack.topAnchor.constraint(
                equalTo: contentView.topAnchor,
                constant: 28
            ),
            targetView.widthAnchor.constraint(equalTo: stack.widthAnchor),
            targetView.heightAnchor.constraint(
                greaterThanOrEqualToConstant:
                    fieldCase.isMultiline ? 105 : 34
            )
        ])

        writeState()
        window.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) {
            [weak self] in
            self?.focusTargetAndPublishReady()
        }

        stateTimer = Timer.scheduledTimer(
            withTimeInterval: 0.05,
            repeats: true
        ) { [weak self] _ in
            self?.writeState()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        stateTimer?.invalidate()
        stateTimer = nil
        writeState()
    }

    func windowWillClose(_ notification: Notification) {
        NSApplication.shared.terminate(nil)
    }

    func controlTextDidChange(_ notification: Notification) {
        writeState()
    }

    func textDidChange(_ notification: Notification) {
        writeState()
    }

    private func makeTargetView() -> NSView {
        switch fieldCase {
        case .textField:
            return configureTextControl(NSTextField(string: initialText))

        case .searchField:
            let field = NSSearchField()
            field.stringValue = initialText
            return configureTextControl(field)

        case .textView:
            let scrollView = NSScrollView()
            scrollView.hasVerticalScroller = true
            scrollView.borderType = .bezelBorder
            let editor = NSTextView()
            editor.string = initialText
            editor.font = .systemFont(ofSize: 17)
            editor.isEditable = true
            editor.isSelectable = true
            editor.delegate = self
            editor.setAccessibilityIdentifier(
                fieldCase.accessibilityIdentifier
            )
            scrollView.documentView = editor
            textView = editor
            return scrollView

        case .comboBox:
            let field = NSComboBox()
            field.addItems(withObjectValues: [
                initialText,
                "A second fixture option"
            ])
            field.stringValue = initialText
            field.isEditable = true
            return configureTextControl(field)

        case .secureField:
            let field = NSSecureTextField()
            field.stringValue = initialText
            return configureTextControl(field)

        case .readonlyField:
            let field = NSTextField(string: initialText)
            field.isEditable = false
            field.isSelectable = true
            return configureTextControl(field)

        case .disabledField:
            let field = NSTextField(string: initialText)
            field.isEditable = true
            field.isSelectable = true
            field.isEnabled = false
            return configureTextControl(field)

        case .swiftUITextField, .swiftUITextEditor, .swiftUISecureField:
            let model = SwiftUIFieldModel(value: initialText)
            model.onChange = { [weak self] in
                self?.writeState()
            }
            swiftUIModel = model
            let hostingView = NSHostingView(
                rootView: SwiftUIFieldFixtureView(
                    fieldCase: fieldCase,
                    model: model
                )
            )
            hostingView.translatesAutoresizingMaskIntoConstraints = false
            return hostingView
        }
    }

    private func configureTextControl(_ field: NSTextField) -> NSTextField {
        field.font = .systemFont(ofSize: 17)
        field.delegate = self
        field.setAccessibilityIdentifier(fieldCase.accessibilityIdentifier)
        field.translatesAutoresizingMaskIntoConstraints = false
        textControl = field
        return field
    }

    private func focusTargetAndPublishReady(attempt: Int = 0) {
        guard let window else { return }
        if fieldCase.isSwiftUI {
            guard window.firstResponder is NSTextView else {
                guard attempt < 40 else {
                    writeState()
                    return
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                    [weak self] in
                    self?.focusTargetAndPublishReady(attempt: attempt + 1)
                }
                return
            }
        } else {
            let firstResponder: NSResponder
            if let textView {
                firstResponder = textView
            } else if let textControl {
                firstResponder = textControl
            } else {
                return
            }
            _ = window.makeFirstResponder(firstResponder)
        }
        moveCaretBeforeFinalCharacter()
        ready = true
        writeState()
    }

    private func moveCaretBeforeFinalCharacter() {
        let length = (currentValue() as NSString).length
        let location = max(0, length - 1)
        if let textView {
            textView.setSelectedRange(
                NSRange(location: location, length: 0)
            )
            return
        }
        if
            fieldCase.isSwiftUI,
            let editor = window?.firstResponder as? NSTextView
        {
            editor.setSelectedRange(NSRange(location: location, length: 0))
            return
        }
        guard
            let textControl,
            let editor = textControl.currentEditor() as? NSTextView
        else {
            return
        }
        editor.setSelectedRange(NSRange(location: location, length: 0))
    }

    private func currentValue() -> String {
        if let textView {
            return textView.string
        }
        if let swiftUIModel {
            return swiftUIModel.value
        }
        return textControl?.stringValue ?? ""
    }

    private func reportsFocused() -> Bool {
        guard let window else { return false }
        if fieldCase.isSwiftUI {
            return window.isKeyWindow &&
                window.firstResponder is NSTextView
        }
        if let textView {
            return window.firstResponder === textView
        }
        guard let textControl else { return false }
        if window.firstResponder === textControl {
            return true
        }
        guard let editor = textControl.currentEditor() else {
            return false
        }
        return window.firstResponder === editor
    }

    private func writeState() {
        let editable: Bool
        let enabled: Bool
        let controlClass: String
        if swiftUIModel != nil {
            editable = true
            enabled = true
            controlClass = fieldCase.displayName
        } else if let textView {
            editable = textView.isEditable
            enabled = true
            controlClass = "NSTextView"
        } else if let textControl {
            editable = textControl.isEditable
            enabled = textControl.isEnabled
            controlClass = String(describing: type(of: textControl))
        } else {
            editable = false
            enabled = false
            controlClass = "missing"
        }

        let state = FixtureState(
            ready: ready,
            processIdentifier: ProcessInfo.processInfo.processIdentifier,
            caseName: fieldCase.rawValue,
            controlClass: controlClass,
            accessibilityIdentifier: fieldCase.accessibilityIdentifier,
            value: currentValue(),
            focused: reportsFocused(),
            editable: editable,
            enabled: enabled,
            secure: fieldCase.isSecure,
            shouldAcceptAutomaticInsertion:
                fieldCase.shouldAcceptAutomaticInsertion
        )
        do {
            let data = try JSONEncoder().encode(state)
            try data.write(to: stateURL, options: .atomic)
        } catch {
            fputs(
                "Failed to write native fixture state: \(error)\n",
                stderr
            )
        }
    }
}

private func argumentValue(_ name: String) -> String? {
    let arguments = CommandLine.arguments
    guard
        let index = arguments.firstIndex(of: name),
        arguments.indices.contains(index + 1)
    else {
        return nil
    }
    return arguments[index + 1]
}

do {
    guard let caseName = argumentValue("--case") else {
        throw FixtureError.missingArgument("--case")
    }
    guard let fieldCase = NativeFieldCase(rawValue: caseName) else {
        throw FixtureError.unsupportedCase(caseName)
    }
    guard let initialText = argumentValue("--initial-text") else {
        throw FixtureError.missingArgument("--initial-text")
    }
    guard let statePath = argumentValue("--state-path") else {
        throw FixtureError.missingArgument("--state-path")
    }

    let application = NSApplication.shared
    application.setActivationPolicy(.regular)
    let delegate = FixtureAppDelegate(
        fieldCase: fieldCase,
        initialText: initialText,
        stateURL: URL(fileURLWithPath: statePath)
    )
    application.delegate = delegate
    application.run()
} catch {
    fputs("Native fixture failed: \(error.localizedDescription)\n", stderr)
    exit(2)
}
