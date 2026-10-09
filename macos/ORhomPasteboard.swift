import AppKit
import Foundation

enum MacPasteboardWriter {
    static func writeText(
        _ text: String,
        to pasteboard: NSPasteboard,
        expectedInitialChangeCount: Int,
        lastOwnedChangeCount: inout Int?,
        delay: (TimeInterval) -> Void = {
            Thread.sleep(forTimeInterval: $0)
        }
    ) -> Int? {
        let retryDelays: [TimeInterval] = [0, 0.020, 0.040, 0.060]
        for retryDelay in retryDelays {
            if retryDelay > 0 {
                delay(retryDelay)
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
}
