import AppKit
import Foundation

private enum FixtureKind: String {
    case textField = "textfield"
    case textArea = "textarea"
    case searchField = "search"
    case comboBox = "combo"
    case secureTextField = "secure"
}

private struct FixtureArguments {
    let kind: FixtureKind
    let resultPath: String
    let runIdentifier: String

    static func parse() -> FixtureArguments? {
        let arguments = CommandLine.arguments

        func value(after option: String) -> String? {
            guard
                let index = arguments.firstIndex(of: option),
                arguments.indices.contains(index + 1)
            else {
                return nil
            }
            return arguments[index + 1]
        }

        guard
            let kindText = value(after: "--case"),
            let kind = FixtureKind(rawValue: kindText),
            let resultPath = value(after: "--result"),
            !resultPath.isEmpty,
            let runIdentifier = value(after: "--run-id"),
            !runIdentifier.isEmpty
        else {
            return nil
        }

        return FixtureArguments(
            kind: kind,
            resultPath: resultPath,
            runIdentifier: runIdentifier
        )
    }
}

private final class FixtureDelegate: NSObject, NSApplicationDelegate {
    private static let initialText = "LEFTRIGHT"
    private static let insertionLocation = 4

    private let arguments: FixtureArguments
    private var window: NSWindow?
    private var textField: NSTextField?
    private var textView: NSTextView?
    private var timer: Timer?
    private var lastValue = ""
    private var changeCount = 0
    private var ready = false

    init(arguments: FixtureArguments) {
        self.arguments = arguments
    }

    func applicationDidFinishLaunching(
        _ notification: Notification
    ) {
        NSApp.setActivationPolicy(.regular)

        let window = NSWindow(
            contentRect: NSRect(
                x: 0,
                y: 0,
                width: 620,
                height: 260
            ),
            styleMask: [
                .titled,
                .closable,
                .miniaturizable
            ],
            backing: .buffered,
            defer: false
        )
        window.title =
            "ORhom Text Field Fixture \(arguments.runIdentifier) \(arguments.kind.rawValue)"
        window.isReleasedWhenClosed = false
        window.center()

        let content = NSView(frame: window.contentView?.bounds ?? .zero)
        content.autoresizingMask = [.width, .height]
        window.contentView = content

        let label = NSTextField(labelWithString:
            "Controlled ORhom insertion target: \(arguments.kind.rawValue)"
        )
        label.frame = NSRect(x: 32, y: 190, width: 556, height: 24)
        content.addSubview(label)

        installTarget(in: content, window: window)

        self.window = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            [weak self] in
            self?.focusTargetAndPrepareCaret()
        }

        timer = Timer.scheduledTimer(
            timeInterval: 0.04,
            target: self,
            selector: #selector(pollValue),
            userInfo: nil,
            repeats: true
        )
        writeResult()
    }

    func applicationShouldTerminateAfterLastWindowClosed(
        _ sender: NSApplication
    ) -> Bool {
        true
    }

    private func installTarget(
        in content: NSView,
        window: NSWindow
    ) {
        let frame = NSRect(x: 32, y: 72, width: 556, height: 84)

        switch arguments.kind {
        case .textField:
            let field = NSTextField(frame: NSRect(
                x: frame.minX,
                y: frame.midY - 16,
                width: frame.width,
                height: 32
            ))
            configure(field)
            content.addSubview(field)
            textField = field

        case .textArea:
            let scrollView = NSScrollView(frame: frame)
            scrollView.hasVerticalScroller = true
            scrollView.borderType = .bezelBorder
            let view = NSTextView(frame: scrollView.contentView.bounds)
            view.isRichText = false
            view.isEditable = true
            view.isSelectable = true
            view.string = Self.initialText
            view.setAccessibilityIdentifier(
                "orhom-fixture-\(arguments.kind.rawValue)"
            )
            view.setAccessibilityLabel(
                "ORhom controlled \(arguments.kind.rawValue)"
            )
            scrollView.documentView = view
            content.addSubview(scrollView)
            textView = view

        case .searchField:
            let field = NSSearchField(frame: NSRect(
                x: frame.minX,
                y: frame.midY - 16,
                width: frame.width,
                height: 32
            ))
            configure(field)
            content.addSubview(field)
            textField = field

        case .comboBox:
            let field = NSComboBox(frame: NSRect(
                x: frame.minX,
                y: frame.midY - 16,
                width: frame.width,
                height: 32
            ))
            field.isEditable = true
            field.completes = false
            configure(field)
            content.addSubview(field)
            textField = field

        case .secureTextField:
            let field = NSSecureTextField(frame: NSRect(
                x: frame.minX,
                y: frame.midY - 16,
                width: frame.width,
                height: 32
            ))
            configure(field)
            content.addSubview(field)
            textField = field
        }
    }

    private func configure(_ field: NSTextField) {
        field.isEditable = true
        field.isSelectable = true
        field.isEnabled = true
        field.stringValue = Self.initialText
        field.setAccessibilityIdentifier(
            "orhom-fixture-\(arguments.kind.rawValue)"
        )
        field.setAccessibilityLabel(
            "ORhom controlled \(arguments.kind.rawValue)"
        )
    }

    private func focusTargetAndPrepareCaret() {
        guard let window else { return }

        if let textView {
            window.makeFirstResponder(textView)
            textView.setSelectedRange(
                NSRange(
                    location: Self.insertionLocation,
                    length: 0
                )
            )
        } else if let textField {
            window.makeFirstResponder(textField)
            if let editor = textField.currentEditor() as? NSTextView {
                editor.setSelectedRange(
                    NSRange(
                        location: Self.insertionLocation,
                        length: 0
                    )
                )
            }
        }

        window.makeKey()
        NSApp.activate(ignoringOtherApps: true)
        ready = true
        lastValue = currentValue()
        writeResult()
    }

    @objc private func pollValue() {
        let value = currentValue()
        if ready, value != lastValue {
            changeCount += 1
            lastValue = value
            writeResult()
        }
    }

    private func currentValue() -> String {
        textView?.string ?? textField?.stringValue ?? ""
    }

    private func writeResult() {
        let payload: [String: Any] = [
            "case": arguments.kind.rawValue,
            "changeCount": changeCount,
            "pid": ProcessInfo.processInfo.processIdentifier,
            "ready": ready,
            "runIdentifier": arguments.runIdentifier,
            "value": currentValue()
        ]
        guard
            JSONSerialization.isValidJSONObject(payload),
            let data = try? JSONSerialization.data(
                withJSONObject: payload,
                options: [.sortedKeys]
            )
        else {
            return
        }
        try? data.write(
            to: URL(fileURLWithPath: arguments.resultPath),
            options: .atomic
        )
    }
}

@main
private enum ORhomTextFieldsFixture {
    static func main() {
        guard let arguments = FixtureArguments.parse() else {
            FileHandle.standardError.write(
                Data(
                    "Usage: ORhomTextFieldsFixture --case <textfield|textarea|search|combo|secure> --result <path> --run-id <id>\n".utf8
                )
            )
            exit(64)
        }

        let application = NSApplication.shared
        let delegate = FixtureDelegate(arguments: arguments)
        application.delegate = delegate
        application.run()
        withExtendedLifetime(delegate) {}
    }
}
