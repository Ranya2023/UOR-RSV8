// uorrc-helper — posts mouse and keyboard events for UOR-RC (reads one JSON command per line).
// Uses only CoreGraphics / ApplicationServices, so it builds with plain `swiftc`.
import Foundation
import CoreGraphics
import ApplicationServices

// Ask for the Accessibility permission once (macOS shows its own dialog).
let promptKey = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
_ = AXIsProcessTrustedWithOptions([promptKey: true] as CFDictionary)

let src = CGEventSource(stateID: .hidSystemState)
var leftDown = false
var rightDown = false

func location() -> CGPoint { CGEvent(source: nil)?.location ?? .zero }

func desktopBounds() -> CGRect {
    var count: UInt32 = 0
    CGGetActiveDisplayList(0, nil, &count)
    var ids = [CGDirectDisplayID](repeating: 0, count: Int(max(count, 1)))
    CGGetActiveDisplayList(count, &ids, &count)
    var r = CGRect.null
    for i in 0..<Int(count) { r = r.union(CGDisplayBounds(ids[i])) }
    return r.isNull ? CGDisplayBounds(CGMainDisplayID()) : r
}

func postMouse(_ type: CGEventType, _ p: CGPoint, _ button: CGMouseButton, clicks: Int64 = 1) {
    guard let e = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: button) else { return }
    e.setIntegerValueField(.mouseEventClickState, value: clicks)
    e.post(tap: .cghidEventTap)
}

func moveTo(_ p: CGPoint) {
    let b = desktopBounds()
    let q = CGPoint(x: min(max(p.x, b.minX), b.maxX - 1), y: min(max(p.y, b.minY), b.maxY - 1))
    let type: CGEventType = leftDown ? .leftMouseDragged : (rightDown ? .rightMouseDragged : .mouseMoved)
    postMouse(type, q, leftDown ? .left : (rightDown ? .right : .left))
}

func button(_ b: String, _ a: String) {
    let p = location()
    let right = b == "right"
    let down: CGEventType = right ? .rightMouseDown : .leftMouseDown
    let up: CGEventType = right ? .rightMouseUp : .leftMouseUp
    let btn: CGMouseButton = right ? .right : .left
    switch a {
    case "down": postMouse(down, p, btn); if right { rightDown = true } else { leftDown = true }
    case "up": postMouse(up, p, btn); if right { rightDown = false } else { leftDown = false }
    case "dblclick":
        postMouse(down, p, btn, clicks: 1); postMouse(up, p, btn, clicks: 1)
        postMouse(down, p, btn, clicks: 2); postMouse(up, p, btn, clicks: 2)
    default: postMouse(down, p, btn); postMouse(up, p, btn)   // click
    }
}

let keyCodes: [String: CGKeyCode] = [
    "enter": 36, "backspace": 51, "tab": 48, "esc": 53, "space": 49, "delete": 117,
    "left": 123, "right": 124, "down": 125, "up": 126, "home": 115, "end": 119,
    "pageup": 116, "pagedown": 121, "f5": 96, "plus": 69, "minus": 78,
    "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9, "b": 11, "q": 12,
    "w": 13, "e": 14, "r": 15, "y": 16, "t": 17, "o": 31, "u": 32, "i": 34, "p": 35, "l": 37,
    "j": 38, "k": 40, "n": 45, "m": 46
]

func key(_ name: String, _ o: [String: Any]) {
    guard let code = keyCodes[name.lowercased()] else { return }
    var flags = CGEventFlags()
    // "ctrl" from the phone means the Mac's ⌘ (copy / paste / undo / select all)
    if (o["ctrl"] as? Bool) == true { flags.insert(.maskCommand) }
    if (o["shift"] as? Bool) == true { flags.insert(.maskShift) }
    if (o["alt"] as? Bool) == true { flags.insert(.maskAlternate) }
    if (o["cmd"] as? Bool) == true || (o["win"] as? Bool) == true { flags.insert(.maskCommand) }
    let n = max(1, min(200, (o["n"] as? Int) ?? 1))
    for _ in 0..<n {
        let d = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: true)
        d?.flags = flags; d?.post(tap: .cghidEventTap)
        let u = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: false)
        u?.flags = flags; u?.post(tap: .cghidEventTap)
    }
}

/// Types any text (Kurdish, Arabic, English…) directly as Unicode.
func typeText(_ s: String) {
    for line in s.components(separatedBy: "\n").enumerated() {
        if line.offset > 0 { key("enter", [:]) }
        var units = Array(line.element.utf16)
        while !units.isEmpty {
            var chunk = Array(units.prefix(16))
            units.removeFirst(chunk.count)
            let d = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: true)
            d?.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: &chunk)
            d?.post(tap: .cghidEventTap)
            let u = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: false)
            u?.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: &chunk)
            u?.post(tap: .cghidEventTap)
        }
    }
}

func num(_ v: Any?) -> Double { (v as? NSNumber)?.doubleValue ?? 0 }

while let line = readLine(strippingNewline: true) {
    guard let data = line.data(using: .utf8),
          let o = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
          let t = o["t"] as? String else { continue }
    switch t {
    case "move": moveTo(CGPoint(x: num(o["x"]), y: num(o["y"])))
    case "rel":
        let p = location()
        moveTo(CGPoint(x: p.x + num(o["dx"]), y: p.y + num(o["dy"])))
    case "btn": button((o["b"] as? String) ?? "left", (o["a"] as? String) ?? "click")
    case "scroll":
        let d = Int32(num(o["d"]))
        CGEvent(scrollWheelEvent2Source: src, units: .line, wheelCount: 1, wheel1: d, wheel2: 0, wheel3: 0)?.post(tap: .cghidEventTap)
    case "key": key((o["k"] as? String) ?? "", o)
    case "type": typeText((o["s"] as? String) ?? "")
    default: break
    }
}
