import AppKit
import ApplicationServices
import Foundation

private enum ProbeError: LocalizedError {
    case invalidArguments
    case applicationMissing
    case omniboxMissing
    case focusRejected
    case windowMissing
    case closeRejected

    var errorDescription: String? {
        switch self {
        case .invalidArguments:
            return "Usage: ORhomAXFieldProbe <focus-omnibox|read-omnibox> <pid>, <window-count> <pid> <bundle>, or <focus-window|read-window|close-window> <pid> <bundle> <title-prefix|window-number>"
        case .applicationMissing:
            return "The expected Chrome process is not running."
        case .omniboxMissing:
            return "Chrome did not expose an address/search field outside its web content."
        case .focusRejected:
            return "Chrome did not accept exact accessibility focus for its address field."
        case .windowMissing:
            return "The expected accessibility window could not be mapped uniquely."
        case .closeRejected:
            return "The marked accessibility window could not be closed safely."
        }
    }
}

private struct Candidate {
    let element: AXUIElement
    let window: AXUIElement?
    let role: String
    let subrole: String
    let value: String
    let score: Int
}

private struct TraversalNode {
    let element: AXUIElement
    let window: AXUIElement?
    let insideWebArea: Bool
    let depth: Int
}

private func copyAXValue(
    _ element: AXUIElement,
    attribute: CFString
) -> CFTypeRef? {
    var value: CFTypeRef?
    guard
        AXUIElementCopyAttributeValue(
            element,
            attribute,
            &value
        ) == .success
    else {
        return nil
    }
    return value
}

private func copyAXString(
    _ element: AXUIElement,
    attribute: CFString
) -> String? {
    copyAXValue(element, attribute: attribute) as? String
}

private func copyAXBoolean(
    _ element: AXUIElement,
    attribute: CFString
) -> Bool? {
    if let number =
        copyAXValue(element, attribute: attribute) as? NSNumber {
        return number.boolValue
    }
    return nil
}

private func copyAXElement(
    _ element: AXUIElement,
    attribute: CFString
) -> AXUIElement? {
    guard
        let value = copyAXValue(element, attribute: attribute),
        CFGetTypeID(value) == AXUIElementGetTypeID()
    else {
        return nil
    }
    return (value as! AXUIElement)
}

private func copyAXRange(
    _ element: AXUIElement,
    attribute: CFString
) -> CFRange? {
    guard
        let rawValue = copyAXValue(
            element,
            attribute: attribute
        ),
        CFGetTypeID(rawValue) == AXValueGetTypeID(),
        AXValueGetType(rawValue as! AXValue) == .cfRange
    else {
        return nil
    }
    var range = CFRange()
    guard AXValueGetValue(
        rawValue as! AXValue,
        .cfRange,
        &range
    ) else {
        return nil
    }
    return range
}

private func copyAXChildren(
    _ element: AXUIElement
) -> [AXUIElement] {
    copyAXValue(
        element,
        attribute: kAXChildrenAttribute as CFString
    ) as? [AXUIElement] ?? []
}

private func copyAXElements(
    _ element: AXUIElement,
    attribute: CFString
) -> [AXUIElement] {
    copyAXValue(
        element,
        attribute: attribute
    ) as? [AXUIElement] ?? []
}

private func isAttributeSettable(
    _ element: AXUIElement,
    attribute: CFString
) -> Bool {
    var settable = DarwinBoolean(false)
    return AXUIElementIsAttributeSettable(
        element,
        attribute,
        &settable
    ) == .success && settable.boolValue
}

private func copyAXFrame(
    _ element: AXUIElement
) -> CGRect? {
    guard
        let positionValue =
            copyAXValue(
                element,
                attribute: kAXPositionAttribute as CFString
            ),
        let sizeValue =
            copyAXValue(
                element,
                attribute: kAXSizeAttribute as CFString
            ),
        CFGetTypeID(positionValue) == AXValueGetTypeID(),
        CFGetTypeID(sizeValue) == AXValueGetTypeID()
    else {
        return nil
    }
    var position = CGPoint.zero
    var size = CGSize.zero
    guard
        AXValueGetValue(
            positionValue as! AXValue,
            .cgPoint,
            &position
        ),
        AXValueGetValue(
            sizeValue as! AXValue,
            .cgSize,
            &size
        )
    else {
        return nil
    }
    return CGRect(origin: position, size: size)
}

private func framesMatch(
    _ lhs: CGRect,
    _ rhs: CGRect
) -> Bool {
    abs(lhs.origin.x - rhs.origin.x) <= 2 &&
        abs(lhs.origin.y - rhs.origin.y) <= 2 &&
        abs(lhs.size.width - rhs.size.width) <= 2 &&
        abs(lhs.size.height - rhs.size.height) <= 2
}

private func windowNumber(
    _ window: AXUIElement?,
    processIdentifier: pid_t
) -> String {
    if let window,
       let number =
        copyAXValue(
            window,
            attribute: "AXWindowNumber" as CFString
        ) as? NSNumber {
        return number.stringValue
    }
    guard
        let windowInfo = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements],
            kCGNullWindowID
        ) as? [[String: Any]]
    else {
        return ""
    }
    let candidates = windowInfo.compactMap {
        entry -> (number: NSNumber, frame: CGRect)? in
        guard
            (
                entry[kCGWindowOwnerPID as String] as? NSNumber
            )?.int32Value == processIdentifier,
            (
                entry[kCGWindowLayer as String] as? NSNumber
            )?.intValue == 0,
            let number =
                entry[kCGWindowNumber as String] as? NSNumber,
            let bounds =
                entry[kCGWindowBounds as String]
                    as? [String: Any],
            let frame = CGRect(
                dictionaryRepresentation:
                    bounds as CFDictionary
            )
        else {
            return nil
        }
        return (number: number, frame: frame)
    }
    if let window,
       let axFrame = copyAXFrame(window) {
        let frameMatches = candidates.filter {
            framesMatch(axFrame, $0.frame)
        }
        if frameMatches.count == 1 {
            return frameMatches[0].number.stringValue
        }
    }
    if candidates.count == 1 {
        return candidates[0].number.stringValue
    }
    return ""
}

private func bestOmniboxCandidate(
    applicationElement: AXUIElement
) -> Candidate? {
    var queue = [
        TraversalNode(
            element: applicationElement,
            window: nil,
            insideWebArea: false,
            depth: 0
        )
    ]
    var index = 0
    var candidates: [Candidate] = []

    while index < queue.count && index < 20_000 {
        let node = queue[index]
        index += 1
        let role =
            copyAXString(
                node.element,
                attribute: kAXRoleAttribute as CFString
            ) ?? ""
        let currentWindow =
            role == (kAXWindowRole as String)
            ? node.element
            : node.window
        let insideWebArea =
            node.insideWebArea || role == "AXWebArea"

        if !insideWebArea,
           role == (kAXTextFieldRole as String) ||
            role == "AXSearchField" {
            let subrole =
                copyAXString(
                    node.element,
                    attribute: kAXSubroleAttribute as CFString
                ) ?? ""
            let value =
                copyAXString(
                    node.element,
                    attribute: kAXValueAttribute as CFString
                ) ?? ""
            let description =
                (
                    copyAXString(
                        node.element,
                        attribute:
                            kAXDescriptionAttribute as CFString
                    ) ?? ""
                ).lowercased()
            let addressDescription =
                description.contains("address") ||
                description.contains("adress") ||
                description.contains("suchleiste") ||
                description.contains("search bar")
            var score = addressDescription ? 500 : 0
            if !value.isEmpty {
                score += 120
            }
            if isAttributeSettable(
                node.element,
                attribute:
                    kAXSelectedTextRangeAttribute as CFString
            ) {
                score += 80
            }
            if copyAXBoolean(
                node.element,
                attribute: kAXFocusedAttribute as CFString
            ) == true {
                score += 20
            }
            candidates.append(
                Candidate(
                    element: node.element,
                    window: currentWindow,
                    role: role,
                    subrole: subrole,
                    value: value,
                    score: score
                )
            )
        }

        if node.depth < 40 {
            for child in copyAXChildren(node.element) {
                queue.append(
                    TraversalNode(
                        element: child,
                        window: currentWindow,
                        insideWebArea: insideWebArea,
                        depth: node.depth + 1
                    )
                )
            }
        }
    }

    return candidates.max {
        if $0.score == $1.score {
            return $0.value.count < $1.value.count
        }
        return $0.score < $1.score
    }
}

private func focusAndSelect(
    _ candidate: Candidate,
    application: NSRunningApplication
) throws {
    guard
        isAttributeSettable(
            candidate.element,
            attribute: kAXFocusedAttribute as CFString
        ),
        isAttributeSettable(
            candidate.element,
            attribute:
                kAXSelectedTextRangeAttribute as CFString
        )
    else {
        throw ProbeError.focusRejected
    }

    for _ in 0..<20 {
        _ = application.activate()
        if let window = candidate.window {
            _ = AXUIElementPerformAction(
                window,
                kAXRaiseAction as CFString
            )
        }
        let focusResult = AXUIElementSetAttributeValue(
            candidate.element,
            kAXFocusedAttribute as CFString,
            kCFBooleanTrue
        )
        var selectedRange = CFRange(
            location: 0,
            length: (candidate.value as NSString).length
        )
        guard
            let selectedRangeValue = AXValueCreate(
                .cfRange,
                &selectedRange
            )
        else {
            throw ProbeError.focusRejected
        }
        let selectionResult =
            AXUIElementSetAttributeValue(
                candidate.element,
                kAXSelectedTextRangeAttribute as CFString,
                selectedRangeValue
            )
        Thread.sleep(forTimeInterval: 0.04)

        let systemWide = AXUIElementCreateSystemWide()
        var focusedValue: CFTypeRef?
        let focusedResult =
            AXUIElementCopyAttributeValue(
                systemWide,
                kAXFocusedUIElementAttribute as CFString,
                &focusedValue
            )
        if focusResult == .success,
           selectionResult == .success,
           focusedResult == .success,
           let focusedValue,
           CFGetTypeID(focusedValue) == AXUIElementGetTypeID() {
            let focusedElement = focusedValue as! AXUIElement
            var focusedPID = pid_t()
            if AXUIElementGetPid(
                focusedElement,
                &focusedPID
            ) == .success,
               focusedPID == application.processIdentifier,
               CFEqual(focusedElement, candidate.element),
               let confirmedRange = copyAXRange(
                    candidate.element,
                    attribute:
                        kAXSelectedTextRangeAttribute as CFString
               ),
               confirmedRange.location == selectedRange.location,
               confirmedRange.length == selectedRange.length,
               NSWorkspace.shared.frontmostApplication?
                .processIdentifier ==
                application.processIdentifier {
                return
            }
        }
    }
    throw ProbeError.focusRejected
}

private func focusWindow(
    _ window: AXUIElement,
    application: NSRunningApplication
) throws {
    for _ in 0..<20 {
        _ = application.activate()
        _ = AXUIElementPerformAction(
            window,
            kAXRaiseAction as CFString
        )
        if isAttributeSettable(
            window,
            attribute: kAXMainAttribute as CFString
        ) {
            _ = AXUIElementSetAttributeValue(
                window,
                kAXMainAttribute as CFString,
                kCFBooleanTrue
            )
        }
        if isAttributeSettable(
            window,
            attribute: kAXFocusedAttribute as CFString
        ) {
            _ = AXUIElementSetAttributeValue(
                window,
                kAXFocusedAttribute as CFString,
                kCFBooleanTrue
            )
        }
        Thread.sleep(forTimeInterval: 0.04)
        if NSWorkspace.shared.frontmostApplication?
            .processIdentifier == application.processIdentifier {
            return
        }
    }
    throw ProbeError.focusRejected
}

private func runWindowAction(
    action: String,
    processIdentifier: pid_t,
    bundleIdentifier: String,
    target: String
) throws {
    guard
        let application = NSRunningApplication(
            processIdentifier: processIdentifier
        ),
        !application.isTerminated,
        application.bundleIdentifier == bundleIdentifier
    else {
        throw ProbeError.applicationMissing
    }
    let applicationElement =
        AXUIElementCreateApplication(processIdentifier)
    _ = AXUIElementSetMessagingTimeout(applicationElement, 0.5)
    let windows = copyAXElements(
        applicationElement,
        attribute: kAXWindowsAttribute as CFString
    )

    if action == "window-count" {
        print(windows.count)
        return
    }

    if action == "focus-window" {
        let matches = windows.compactMap {
            window -> (window: AXUIElement, number: String, title: String)? in
            let title =
                copyAXString(
                    window,
                    attribute: kAXTitleAttribute as CFString
                ) ?? ""
            guard title.hasPrefix(target) else {
                return nil
            }
            let number = windowNumber(
                window,
                processIdentifier: processIdentifier
            )
            guard !number.isEmpty else {
                return nil
            }
            return (window: window, number: number, title: title)
        }
        guard matches.count == 1 else {
            throw ProbeError.windowMissing
        }
        try focusWindow(matches[0].window, application: application)
        print("\(matches[0].number)\t\(matches[0].title)")
        return
    }

    if action == "close-window" {
        let matches = windows.filter {
            windowNumber(
                $0,
                processIdentifier: processIdentifier
            ) == target &&
                (
                    copyAXString(
                        $0,
                        attribute: kAXTitleAttribute as CFString
                    ) ?? ""
                ).hasPrefix("ORHOM_WEB:")
        }
        guard
            matches.count == 1,
            let closeButton =
                copyAXElement(
                    matches[0],
                    attribute:
                        kAXCloseButtonAttribute as CFString
                ),
            AXUIElementPerformAction(
                closeButton,
                kAXPressAction as CFString
            ) == .success
        else {
            throw ProbeError.closeRejected
        }
        print("closed")
        return
    }

    guard action == "read-window" else {
        throw ProbeError.invalidArguments
    }
    let matches = windows.compactMap {
        window -> String? in
        guard
            windowNumber(
                window,
                processIdentifier: processIdentifier
            ) == target
        else {
            return nil
        }
        return copyAXString(
            window,
            attribute: kAXTitleAttribute as CFString
        ) ?? ""
    }
    guard matches.count == 1 else {
        throw ProbeError.windowMissing
    }
    print(matches[0])
}

private func run() throws {
    guard
        CommandLine.arguments.count >= 3,
        let processIdentifier =
            pid_t(CommandLine.arguments[2])
    else {
        throw ProbeError.invalidArguments
    }
    let action = CommandLine.arguments[1]
    if action == "window-count" {
        guard CommandLine.arguments.count == 4 else {
            throw ProbeError.invalidArguments
        }
        try runWindowAction(
            action: action,
            processIdentifier: processIdentifier,
            bundleIdentifier: CommandLine.arguments[3],
            target: ""
        )
        return
    }
    if action == "focus-window" ||
        action == "read-window" ||
        action == "close-window" {
        guard CommandLine.arguments.count == 5 else {
            throw ProbeError.invalidArguments
        }
        try runWindowAction(
            action: action,
            processIdentifier: processIdentifier,
            bundleIdentifier: CommandLine.arguments[3],
            target: CommandLine.arguments[4]
        )
        return
    }
    guard
        CommandLine.arguments.count == 3,
        action == "focus-omnibox" ||
        action == "read-omnibox"
    else {
        throw ProbeError.invalidArguments
    }
    guard
        let application = NSRunningApplication(
            processIdentifier: processIdentifier
        ),
        !application.isTerminated,
        application.bundleIdentifier == "com.google.Chrome"
    else {
        throw ProbeError.applicationMissing
    }

    let applicationElement =
        AXUIElementCreateApplication(processIdentifier)
    _ = AXUIElementSetMessagingTimeout(applicationElement, 0.5)
    guard
        let candidate =
            bestOmniboxCandidate(
                applicationElement: applicationElement
            )
    else {
        throw ProbeError.omniboxMissing
    }
    if action == "focus-omnibox" {
        try focusAndSelect(candidate, application: application)
    }

    let currentValue =
        copyAXString(
            candidate.element,
            attribute: kAXValueAttribute as CFString
        ) ?? ""
    print(
        [
            candidate.role,
            candidate.subrole,
            windowNumber(
                candidate.window,
                processIdentifier: processIdentifier
            ),
            currentValue
        ].joined(separator: "|")
    )
}

do {
    try run()
} catch {
    fputs(
        "\(error.localizedDescription)\n",
        stderr
    )
    exit(1)
}
