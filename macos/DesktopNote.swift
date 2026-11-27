import Cocoa
import WebKit

private let kBundleId = "com.evie.to-do-list"
private let kShutdownNotification = Notification.Name("com.evie.to-do-list.shutdown")
private let kScheme = "to-do-list"
private let kLocalPageURL = URL(string: "to-do-list://local/index.html")!

// MARK: - Color Helpers

private func colorFromRGB(_ r: Int, _ g: Int, _ b: Int, alpha: CGFloat = 1.0) -> NSColor {
    return NSColor(srgbRed: CGFloat(r) / 255.0, green: CGFloat(g) / 255.0, blue: CGFloat(b) / 255.0, alpha: alpha)
}

private func parseRGB(_ text: String) -> NSColor? {
    let parts = text.split(separator: ",").compactMap { Int($0.trimmingCharacters(in: .whitespaces)) }
    guard parts.count == 3, parts.allSatisfy({ (0...255).contains($0) }) else { return nil }
    return colorFromRGB(parts[0], parts[1], parts[2])
}

private func parseHex(_ text: String) -> NSColor? {
    let cleaned = text.trimmingCharacters(in: .whitespaces)
    guard cleaned.count == 7, cleaned.hasPrefix("#"),
          let value = Int(cleaned.dropFirst(), radix: 16) else { return nil }
    return colorFromRGB((value >> 16) & 255, (value >> 8) & 255, value & 255)
}

private func blend(_ a: NSColor, _ b: NSColor, weightB: CGFloat) -> NSColor {
    let ca = a.usingColorSpace(.sRGB) ?? a
    let cb = b.usingColorSpace(.sRGB) ?? b
    let w = max(0, min(1, weightB))
    return NSColor(
        srgbRed: ca.redComponent * (1 - w) + cb.redComponent * w,
        green: ca.greenComponent * (1 - w) + cb.greenComponent * w,
        blue: ca.blueComponent * (1 - w) + cb.blueComponent * w,
        alpha: 1.0
    )
}

private struct ThemePalette {
    var paper: NSColor = colorFromRGB(255, 240, 243)
    var border: NSColor = colorFromRGB(223, 178, 190)
    var ink: NSColor = colorFromRGB(82, 58, 66)
    var accent: NSColor = colorFromRGB(184, 107, 130)

    var highlight: NSColor { blend(paper, accent, weightB: 0.2) }
    var muted: NSColor { blend(ink, paper, weightB: 0.5) }
    var inputBg: NSColor { blend(paper, .white, weightB: 0.2) }
}

// MARK: - Local Asset Scheme Handler (Stable Origin for localStorage)

private final class LocalAppSchemeHandler: NSObject, WKURLSchemeHandler {
    private let appDir: URL

    init(appDir: URL) {
        self.appDir = appDir
        super.init()
    }

    func webView(_ webView: WKWebView, start urlSchemeTask: WKURLSchemeTask) {
        guard let url = urlSchemeTask.request.url else {
            urlSchemeTask.didFailWithError(NSError(domain: NSURLErrorDomain, code: NSURLErrorBadURL))
            return
        }
        var path = url.path
        if path.isEmpty || path == "/" { path = "/index.html" }
        let fileName = (path as NSString).lastPathComponent
        let fileURL = appDir.appendingPathComponent(fileName)

        guard let data = try? Data(contentsOf: fileURL) else {
            urlSchemeTask.didFailWithError(NSError(domain: NSURLErrorDomain, code: NSURLErrorFileDoesNotExist))
            return
        }
        let mime = mimeType(for: fileName)
        let response = URLResponse(url: url, mimeType: mime, expectedContentLength: data.count, textEncodingName: mime.hasPrefix("text/") ? "utf-8" : nil)
        urlSchemeTask.didReceive(response)
        urlSchemeTask.didReceive(data)
        urlSchemeTask.didFinish()
    }

    func webView(_ webView: WKWebView, stop urlSchemeTask: WKURLSchemeTask) {}

    private func mimeType(for name: String) -> String {
        switch (name as NSString).pathExtension.lowercased() {
        case "html": return "text/html"
        case "woff2": return "font/woff2"
        case "png": return "image/png"
        case "webmanifest", "json": return "application/manifest+json"
        case "js": return "application/javascript"
        case "txt": return "text/plain"
        default: return "application/octet-stream"
        }
    }
}

// MARK: - Hot Corner & Smooth Motion

private final class HotCornerTracker {
    private var enteredAt: Date?
    private var leftAt: Date?

    func shouldShow(cursor: NSPoint, screenFrame: NSRect, edge: CGFloat, now: Date) -> Bool {
        let corner = NSRect(x: screenFrame.minX, y: screenFrame.maxY - edge, width: edge, height: edge)
        if !corner.contains(cursor) {
            enteredAt = nil
            return false
        }
        if enteredAt == nil { enteredAt = now }
        return now.timeIntervalSince(enteredAt!) >= 0.12
    }

    func shouldHide(cursor: NSPoint, panelFrame: NSRect, screenFrame: NSRect, edge: CGFloat, keepOpen: Bool, now: Date) -> Bool {
        let corner = NSRect(x: screenFrame.minX, y: screenFrame.maxY - edge, width: edge, height: edge)
        if keepOpen || panelFrame.contains(cursor) || corner.contains(cursor) {
            leftAt = nil
            return false
        }
        if leftAt == nil { leftAt = now }
        return now.timeIntervalSince(leftAt!) >= 0.26
    }

    func reset() {
        enteredAt = nil
        leftAt = nil
    }
}

private final class NoteMotion {
    private(set) var progress: CGFloat = 0
    private(set) var target: CGFloat = 0
    private var startProgress: CGFloat = 0
    private var startedAt: TimeInterval = 0
    private var duration: TimeInterval = 0.16

    func begin(target: CGFloat, now: TimeInterval) {
        self.target = target
        self.startProgress = progress
        self.startedAt = now
        let base: TimeInterval = target > 0.5 ? 0.16 : 0.18
        self.duration = max(0.016, base * Double(abs(target - progress)))
    }

    func advance(now: TimeInterval) -> Bool {
        let raw = min(1.0, max(0.0, (now - startedAt) / duration))
        let eased = 1.0 - pow(1.0 - raw, 3.0)
        progress = startProgress + (target - startProgress) * CGFloat(eased)
        if raw >= 1.0 {
            progress = target
            return true
        }
        return false
    }

    func snap(to value: CGFloat) {
        progress = value
        target = value
    }
}

// MARK: - Borderless Floating Panel

private final class StickyPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

private final class StickyWebView: WKWebView {
    override func willOpenMenu(_ menu: NSMenu, with event: NSEvent) {
        menu.items.removeAll()
    }
}

// MARK: - 3rd-Level Side Flyout: Pixel Calendar Menu View

private final class PixelCalendarMenuView: NSView {
    static let logicalSize = NSSize(width: 216, height: 196)
    private static let weekdays = ["一", "二", "三", "四", "五", "六", "日"]
    private static let isoFormatter: DateFormatter = {
        let df = DateFormatter()
        df.calendar = Calendar(identifier: .gregorian)
        df.locale = Locale(identifier: "en_US_POSIX")
        df.dateFormat = "yyyy-MM-dd"
        return df
    }()

    private let palette: ThemePalette
    private let selectedDate: Date
    private let pendingDates: Set<String>
    private let onSelect: (String) -> Void
    private var monthStart: Date
    private var hoverPoint = NSPoint(x: -1, y: -1)
    private var trackingArea: NSTrackingArea?
    private let calendar: Calendar = {
        var cal = Calendar(identifier: .gregorian)
        cal.firstWeekday = 2 // Monday
        return cal
    }()

    override var isFlipped: Bool { true }

    init(palette: ThemePalette, selectedISO: String, pendingDates: Set<String>, onSelect: @escaping (String) -> Void) {
        self.palette = palette
        let parsed = Self.isoFormatter.date(from: selectedISO) ?? Date()
        self.selectedDate = calendar.startOfDay(for: parsed)
        self.pendingDates = pendingDates
        self.onSelect = onSelect
        let comps = calendar.dateComponents([.year, .month], from: self.selectedDate)
        self.monthStart = calendar.date(from: comps) ?? self.selectedDate
        super.init(frame: NSRect(origin: .zero, size: Self.logicalSize))
    }

    required init?(coder: NSCoder) { fatalError() }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let existing = trackingArea { removeTrackingArea(existing) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    private var prevRect: NSRect { NSRect(x: 4, y: 3, width: 23, height: 22) }
    private var nextRect: NSRect { NSRect(x: bounds.width - 65, y: 3, width: 23, height: 22) }
    private var todayRect: NSRect { NSRect(x: bounds.width - 39, y: 3, width: 35, height: 22) }

    private func gridStart() -> Date {
        let weekday = calendar.component(.weekday, from: monthStart) // 1=Sun, 2=Mon...
        let offset = (weekday + 5) % 7
        return calendar.date(byAdding: .day, value: -offset, to: monthStart) ?? monthStart
    }

    private func cellRect(column: Int, y: CGFloat, height: CGFloat) -> NSRect {
        let width = bounds.width - 8
        let left = 4 + floor(width * CGFloat(column) / 7.0)
        let right = 4 + floor(width * CGFloat(column + 1) / 7.0)
        return NSRect(x: left, y: y, width: right - left, height: height)
    }

    private func dayRect(index: Int) -> NSRect {
        cellRect(column: index % 7, y: 46 + CGFloat(index / 7) * 24, height: 24)
    }

    override func draw(_ dirtyRect: NSRect) {
        palette.paper.setFill()
        bounds.fill()

        drawBox(prevRect, text: "<", fg: palette.ink, active: false, edge: nil)
        drawBox(nextRect, text: ">", fg: palette.ink, active: false, edge: nil)
        drawBox(todayRect, text: "今天", fg: palette.ink, active: false, edge: palette.border)

        let comps = calendar.dateComponents([.year, .month], from: monthStart)
        let title = "\(comps.year ?? 2026)年\(comps.month ?? 1)月"
        let heading = NSRect(x: prevRect.maxX, y: 0, width: nextRect.minX - prevRect.maxX, height: 26)
        drawCentered(title, in: heading, color: palette.ink)

        for col in 0..<7 {
            drawCentered(Self.weekdays[col], in: cellRect(column: col, y: 26, height: 20), color: palette.muted)
        }

        let first = gridStart()
        let todayStart = calendar.startOfDay(for: Date())
        let currentMonth = comps.month

        for idx in 0..<42 {
            guard let date = calendar.date(byAdding: .day, value: idx, to: first) else { continue }
            let area = dayRect(index: idx).insetBy(dx: 1, dy: 1)
            let isSelected = calendar.isDate(date, inSameDayAs: selectedDate)
            let isToday = calendar.isDate(date, inSameDayAs: todayStart)
            let inMonth = calendar.component(.month, from: date) == currentMonth
            let fg = isSelected ? palette.paper : (inMonth ? palette.ink : palette.muted)
            let dayNum = String(calendar.component(.day, from: date))

            drawBox(area, text: dayNum, fg: fg, active: isSelected, edge: isToday ? palette.border : nil)

            let iso = Self.isoFormatter.string(from: date)
            if pendingDates.contains(iso) {
                let dotColor = isSelected ? palette.paper : palette.accent
                dotColor.setFill()
                NSRect(x: floor(area.midX - 1), y: area.maxY - 3, width: 2, height: 2).fill()
            }
        }
    }

    private func drawBox(_ rect: NSRect, text: String, fg: NSColor, active: Bool, edge: NSColor?) {
        if active || rect.contains(hoverPoint) {
            (active ? palette.accent : palette.highlight).setFill()
            rect.fill()
        }
        if let edge = edge {
            edge.setStroke()
            let path = NSBezierPath(rect: rect.insetBy(dx: 0.5, dy: 0.5))
            path.lineWidth = 1.0
            path.stroke()
        }
        drawCentered(text, in: rect, color: fg)
    }

    private func drawCentered(_ text: String, in rect: NSRect, color: NSColor) {
        let font = NSFont.systemFont(ofSize: 11.5)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        let size = (text as NSString).size(withAttributes: attrs)
        let origin = NSPoint(x: rect.minX + floor((rect.width - size.width) / 2), y: rect.minY + floor((rect.height - size.height) / 2))
        (text as NSString).draw(at: origin, withAttributes: attrs)
    }

    override func mouseMoved(with event: NSEvent) {
        hoverPoint = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseExited(with event: NSEvent) {
        hoverPoint = NSPoint(x: -1, y: -1)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        let pt = convert(event.locationInWindow, from: nil)
        if prevRect.contains(pt) {
            if let next = calendar.date(byAdding: .month, value: -1, to: monthStart) {
                monthStart = next
                needsDisplay = true
            }
            return
        }
        if nextRect.contains(pt) {
            if let next = calendar.date(byAdding: .month, value: 1, to: monthStart) {
                monthStart = next
                needsDisplay = true
            }
            return
        }
        if todayRect.contains(pt) {
            onSelect(Self.isoFormatter.string(from: Date()))
            return
        }
        let first = gridStart()
        for idx in 0..<42 {
            if dayRect(index: idx).contains(pt),
               let date = calendar.date(byAdding: .day, value: idx, to: first) {
                onSelect(Self.isoFormatter.string(from: date))
                return
            }
        }
    }
}

// MARK: - 3rd-Level Side Flyout: Pixel Multiline Note Menu View

private final class PixelNoteTextView: NSTextView {
    var onEscape: (() -> Void)?

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // Escape
            onEscape?()
            return
        }
        super.keyDown(with: event)
    }
}

private final class PixelNoteMenuView: NSView, NSTextViewDelegate {
    static let logicalSize = NSSize(width: 216, height: 112)
    private let palette: ThemePalette
    private let onSave: (String) -> Void
    private let onDismiss: () -> Void
    private let textView: PixelNoteTextView
    private var lastSaved: String

    init(palette: ThemePalette, note: String, onSave: @escaping (String) -> Void, onDismiss: @escaping () -> Void) {
        self.palette = palette
        self.onSave = onSave
        self.onDismiss = onDismiss
        self.lastSaved = note.replacingOccurrences(of: "\r\n", with: "\n")

        let scroll = NSScrollView(frame: NSRect(x: 11, y: 11, width: Self.logicalSize.width - 22, height: Self.logicalSize.height - 22))
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.drawsBackground = false
        scroll.borderType = .noBorder

        let tv = PixelNoteTextView(frame: NSRect(origin: .zero, size: scroll.contentSize))
        tv.isEditable = true
        tv.isSelectable = true
        tv.isRichText = false
        tv.allowsUndo = true
        tv.font = NSFont.systemFont(ofSize: 11.5)
        tv.textColor = palette.ink
        tv.insertionPointColor = palette.accent
        tv.backgroundColor = palette.inputBg
        tv.drawsBackground = true
        tv.string = self.lastSaved
        tv.textContainerInset = NSSize(width: 2, height: 2)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.textContainer?.widthTracksTextView = true
        scroll.documentView = tv

        self.textView = tv
        super.init(frame: NSRect(origin: .zero, size: Self.logicalSize))
        tv.delegate = self
        tv.onEscape = { [weak self] in
            self?.flush()
            self?.onDismiss()
        }
        addSubview(scroll)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        palette.paper.setFill()
        bounds.fill()

        let frameRect = bounds.insetBy(dx: 8, dy: 8)
        palette.inputBg.setFill()
        frameRect.fill()

        let isFocused = window?.firstResponder === textView
        (isFocused ? palette.accent : palette.border).setStroke()
        let path = NSBezierPath(rect: frameRect.insetBy(dx: 0.5, dy: 0.5))
        path.lineWidth = 1.0
        path.stroke()
    }

    func focusInput() {
        window?.makeFirstResponder(textView)
        let len = (textView.string as NSString).length
        textView.setSelectedRange(NSRange(location: len, length: 0))
        needsDisplay = true
    }

    func textDidChange(_ notification: Notification) {
        if textView.string.count > 500 {
            textView.string = String(textView.string.prefix(500))
        }
        flush()
    }

    func flush() {
        let normalized = textView.string.replacingOccurrences(of: "\r\n", with: "\n")
        guard normalized != lastSaved else { return }
        lastSaved = normalized
        onSave(normalized)
    }
}

// MARK: - Main Desktop Note Controller

private final class DesktopNoteApp: NSObject, NSApplicationDelegate, WKScriptMessageHandler, WKNavigationDelegate, NSMenuDelegate {
    private let appDir: URL
    private let panel: StickyPanel
    private let webView: StickyWebView
    private let hotCorner = HotCornerTracker()
    private let motion = NoteMotion()

    private var palette = ThemePalette()
    private var preferredHeight: CGFloat = 72
    private var targetOpacity: CGFloat = 0.88
    private var ready = false
    private var keepOpen = false
    private var pinned = false
    private var secondaryPanel = false
    private var sheetOpen = false
    private var menuVisible = false
    private var menuGraceUntil = Date.distantPast

    private var calendarViewDate = ""
    private var calendarTaskDate = ""
    private var calendarDots = Set<String>()
    private var noteTaskKey = ""
    private var noteText = ""

    private var pollTimer: Timer?
    private var animTimer: Timer?
    private var fileWatcher: DispatchSourceFileSystemObject?
    private var reloadWorkItem: DispatchWorkItem?
    private var activeMenu: NSMenu?
    private weak var activeNoteView: PixelNoteMenuView?

    override init() {
        let bundleApp = Bundle.main.resourceURL?.appendingPathComponent("app")
        let fallbackApp = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("app")
        if let bundleApp = bundleApp, FileManager.default.fileExists(atPath: bundleApp.appendingPathComponent("index.html").path) {
            self.appDir = bundleApp
        } else {
            self.appDir = fallbackApp
        }

        let panelRect = NSRect(x: 0, y: 0, width: 268, height: 72)
        self.panel = StickyPanel(
            contentRect: panelRect,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.alphaValue = 0

        let config = WKWebViewConfiguration()
        let contentController = WKUserContentController()
        let bridgeScript = """
        (function () {
          const listeners = new Set();
          window.desktopNote = {
            postMessage(msg) {
              window.webkit.messageHandlers.desktopNote.postMessage(String(msg));
            },
            addEventListener(type, fn) {
              if (type === "message" && typeof fn === "function") listeners.add(fn);
            },
            removeEventListener(type, fn) {
              if (type === "message") listeners.delete(fn);
            },
            _dispatch(data) {
              for (const fn of listeners) {
                try { fn({ data }); } catch (e) {}
              }
            }
          };
        })();
        """
        contentController.addUserScript(WKUserScript(source: bridgeScript, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        config.userContentController = contentController
        config.setURLSchemeHandler(LocalAppSchemeHandler(appDir: appDir), forURLScheme: kScheme)
        config.preferences.setValue(true, forKey: "developerExtrasEnabled")

        self.webView = StickyWebView(frame: panel.contentView!.bounds, configuration: config)
        webView.autoresizingMask = [.width, .height]
        webView.setValue(false, forKey: "drawsBackground")

        super.init()

        contentController.add(self, name: "desktopNote")
        webView.navigationDelegate = self
        panel.contentView?.addSubview(webView)
        panel.contentView?.wantsLayer = true
        panel.contentView?.layer?.backgroundColor = palette.paper.cgColor
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        DistributedNotificationCenter.default().addObserver(
            self,
            selector: #selector(handleShutdown),
            name: kShutdownNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(panelDidResignKey),
            name: NSWindow.didResignKeyNotification,
            object: panel
        )

        applyPanelBounds()
        webView.load(URLRequest(url: kLocalPageURL))
        startPageWatcher()
    }

    @objc private func handleShutdown() {
        NSApp.terminate(nil)
    }

    @objc private func panelDidResignKey() {
        keepOpen = false
        if ready && sheetOpen && !menuVisible {
            sendToWeb("close-sheet")
        }
    }

    // MARK: - WKNavigationDelegate

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        ready = true
        if pollTimer == nil {
            pollTimer = Timer.scheduledTimer(withTimeInterval: 0.06, repeats: true) { [weak self] _ in
                self?.trackCursor()
            }
        }
        if pinned { showNote() }
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if navigationAction.request.url == kLocalPageURL {
            decisionHandler(.allow)
        } else {
            decisionHandler(.cancel)
        }
    }

    // MARK: - Bridge Messaging

    private func sendToWeb(_ message: String) {
        guard let jsonData = try? JSONSerialization.data(withJSONObject: [message]),
              let jsonStr = String(data: jsonData, encoding: .utf8) else { return }
        let js = "window.desktopNote && window.desktopNote._dispatch((\(jsonStr))[0]);"
        webView.evaluateJavaScript(js, completionHandler: nil)
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let msg = message.body as? String else { return }

        if msg.hasPrefix("paper:") {
            let parts = msg.split(separator: ":", omittingEmptySubsequences: false).map(String.init)
            if parts.count == 5,
               let paper = parseRGB(parts[1]),
               let border = parseHex(parts[2]),
               let ink = parseHex(parts[3]),
               let accent = parseHex(parts[4]) {
                palette = ThemePalette(paper: paper, border: border, ink: ink, accent: accent)
                panel.contentView?.layer?.backgroundColor = paper.cgColor
            }
            return
        }
        if msg.hasPrefix("opacity:") {
            let val = String(msg.dropFirst(8))
            if ["0.80", "0.88", "0.95"].contains(val), let dbl = Double(val) {
                targetOpacity = CGFloat(dbl)
                if animTimer != nil {
                    panel.alphaValue = targetOpacity * motion.progress
                } else if panel.isVisible {
                    panel.alphaValue = targetOpacity
                }
            }
            return
        }
        if msg == "pin:1" || msg == "pin:0" {
            pinned = (msg == "pin:1")
            hotCorner.reset()
            if pinned && ready && !panel.isVisible { showNote() }
            return
        }
        if msg == "panel:1" || msg == "panel:0" {
            secondaryPanel = (msg == "panel:1")
            hotCorner.reset()
            return
        }
        if msg == "sheet:1" || msg == "sheet:0" {
            sheetOpen = (msg == "sheet:1")
            hotCorner.reset()
            return
        }
        if msg.hasPrefix("notemeta:") {
            let payload = String(msg.dropFirst(9))
            let fields = payload.split(separator: "|", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
            noteTaskKey = ""
            noteText = ""
            if fields.count == 2, !fields[0].isEmpty,
               let data = Data(base64Encoded: fields[1]),
               let decoded = String(data: data, encoding: .utf8) {
                noteTaskKey = fields[0]
                noteText = decoded
            }
            return
        }
        if msg.hasPrefix("calmeta:") {
            let payload = String(msg.dropFirst(8))
            let fields = payload.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            if fields.count == 3 {
                calendarViewDate = fields[0]
                calendarTaskDate = fields[1]
                calendarDots = Set(fields[2].split(separator: ",").map(String.init).filter { !$0.isEmpty })
            }
            return
        }
        if msg.hasPrefix("menu:") {
            showNoteMenu(msg)
            return
        }
        if msg == "exit" {
            NSApp.terminate(nil)
            return
        }
        if msg == "focus" {
            if panel.isVisible {
                keepOpen = true
                NSApp.activate(ignoringOtherApps: true)
                panel.makeKeyAndOrderFront(nil)
                panel.makeFirstResponder(webView)
                sendToWeb("focus-ready")
            }
            return
        }
        if msg == "hold" {
            keepOpen = true
            return
        }
        if msg == "release" {
            keepOpen = false
            return
        }
        if msg.hasPrefix("height:"), let h = Int(msg.dropFirst(7)) {
            preferredHeight = CGFloat(max(72, min(620, h)))
            if panel.isVisible && animTimer == nil && !menuVisible {
                applyPanelBounds()
            }
        }
    }

    // MARK: - Window Positioning & 60fps Motion

    private func anchorScreen() -> NSScreen {
        return NSScreen.screens.first ?? NSScreen.main!
    }

    private func targetFrame() -> NSRect {
        let screen = anchorScreen()
        let vis = screen.visibleFrame
        let w = min(CGFloat(268), vis.width)
        let h = min(preferredHeight, vis.height)
        return NSRect(x: vis.minX, y: vis.maxY - h, width: w, height: h)
    }

    private func applyPanelBounds() {
        let rect = targetFrame()
        if panel.frame != rect {
            panel.setFrame(rect, display: true)
        }
    }

    private func trackCursor() {
        guard ready else { return }
        let screen = anchorScreen()
        let cursor = NSEvent.mouseLocation
        let now = Date()
        let edge: CGFloat = 14

        if !panel.isVisible {
            if hotCorner.shouldShow(cursor: cursor, screenFrame: screen.frame, edge: edge, now: now) {
                showNote()
            }
            return
        }

        let hold = pinned || secondaryPanel || sheetOpen || menuVisible || (keepOpen && panel.isKeyWindow) || now < menuGraceUntil
        if motion.target < 0.5 && (hold || panel.frame.contains(cursor)) {
            showNote()
            return
        }
        if motion.target < 0.5 { return }

        if hotCorner.shouldHide(cursor: cursor, panelFrame: panel.frame, screenFrame: screen.frame, edge: edge, keepOpen: hold, now: now) {
            hideNote()
        }
    }

    private func showNote() {
        if !panel.isVisible {
            motion.snap(to: 0)
            let target = targetFrame()
            var start = target
            start.origin.y += 8
            panel.setFrame(start, display: false)
            panel.alphaValue = 0
            panel.orderFrontRegardless()
            sendToWeb("shown")
        }
        hotCorner.reset()
        startAnimation(to: 1.0)
    }

    private func hideNote() {
        hotCorner.reset()
        startAnimation(to: 0.0)
    }

    private func startAnimation(to target: CGFloat) {
        motion.begin(target: target, now: ProcessInfo.processInfo.systemUptime)
        animTimer?.invalidate()
        animTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in
            self?.stepAnimation()
        }
    }

    private func stepAnimation() {
        let done = motion.advance(now: ProcessInfo.processInfo.systemUptime)
        let base = targetFrame()
        let offset = (1.0 - motion.progress) * 8.0
        var frame = base
        frame.origin.y += offset
        panel.setFrameOrigin(frame.origin)
        panel.alphaValue = targetOpacity * motion.progress

        if done {
            animTimer?.invalidate()
            animTimer = nil
            if motion.target > 0.5 {
                applyPanelBounds()
                panel.alphaValue = targetOpacity
            } else {
                panel.orderOut(nil)
                keepOpen = false
                sendToWeb("hidden")
                applyPanelBounds()
                hotCorner.reset()
            }
        }
    }

    // MARK: - Native Right-Click Menu with 3rd-Level Side Flyouts

    private func showNoteMenu(_ message: String) {
        let parts = message.split(separator: ":", omittingEmptySubsequences: false).map(String.init)
        guard panel.isVisible, parts.count == 8, let x = Double(parts[1]), let y = Double(parts[2]) else { return }

        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false

        if parts[3] == "1" {
            menu.addItem(makeNoteFlyoutItem())
            let taskSel = calendarTaskDate.isEmpty ? calendarViewDate : calendarTaskDate
            menu.addItem(makeCalendarFlyoutItem(command: "taskDay", title: "切换时间", selectedISO: taskSel))
            menu.addItem(makeActionItem("延期一天", command: "delay"))
            menu.addItem(makeActionItem("删除任务", command: "delete"))
            menu.addItem(.separator())
        }
        if parts[7] == "1" {
            menu.addItem(makeActionItem("返回待办", command: "tasks"))
        }
        menu.addItem(makeActionItem("新增任务", command: "new"))
        menu.addItem(makeActionItem("清理已完成", command: "clearDone"))
        menu.addItem(makeSubmenu(key: "theme", title: "便签颜色", selected: parts[4], values: [
            ("rose", "奶油藕粉"), ("butter", "黄油奶黄"), ("sage", "抹茶鼠尾草"),
            ("lilac", "芋泥淡紫"), ("oat", "海盐燕麦"), ("sky", "冰川奶蓝")
        ]))
        menu.addItem(makeSubmenu(key: "opacity", title: "透明度", selected: parts[5], values: [
            ("0.80", "轻透 (80%)"), ("0.88", "柔和 (88%)"), ("0.95", "清晰 (95%)")
        ]))
        menu.addItem(makeSubmenu(key: "filter", title: "筛选任务", selected: parts[6], values: [
            ("today", "当日"), ("all", "全部"), ("postponed", "延期")
        ]))
        menu.addItem(makeCalendarFlyoutItem(command: "calendar", title: "切换日期", selectedISO: calendarViewDate))
        menu.addItem(makeActionItem("固定在左上角", command: "pin", checked: pinned))
        menu.addItem(.separator())
        menu.addItem(makeActionItem("新增选项（本周每天）", command: "add"))
        menu.addItem(makeActionItem("剪贴板", command: "clipboard"))
        menu.addItem(makeSubmenu(key: "tool", title: "工具", selected: nil, values: [
            ("add30", "划线价"), ("usd", "换美金")
        ]))
        menu.addItem(.separator())
        menu.addItem(makeActionItem("退出桌面便签", command: "exit"))

        activeMenu = menu
        menuVisible = true

        // Position menu to the right of the sticky note so Level-3 flyouts never cover the note.
        let flippedY = max(0, min(panel.frame.height, panel.frame.height - CGFloat(y)))
        let targetPoint = NSPoint(x: panel.frame.width + 2, y: flippedY)

        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        menu.popUp(positioning: nil, at: targetPoint, in: panel.contentView)
    }

    private func makeActionItem(_ title: String, command: String, checked: Bool = false) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: #selector(handleMenuCommand(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = command
        item.state = checked ? .on : .off
        return item
    }

    private func makeSubmenu(key: String, title: String, selected: String?, values: [(String, String)]) -> NSMenuItem {
        let root = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let sub = NSMenu(title: title)
        sub.autoenablesItems = false
        for (val, label) in values {
            sub.addItem(makeActionItem(label, command: "\(key)/\(val)", checked: selected == val))
        }
        root.submenu = sub
        return root
    }

    private func makeCalendarFlyoutItem(command: String, title: String, selectedISO: String) -> NSMenuItem {
        let root = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let sub = NSMenu(title: title)
        let host = NSMenuItem()
        let calView = PixelCalendarMenuView(palette: palette, selectedISO: selectedISO, pendingDates: calendarDots) { [weak self] iso in
            guard let self = self else { return }
            self.menuGraceUntil = Date().addingTimeInterval(0.5)
            self.activeMenu?.cancelTracking()
            self.sendToWeb("command:\(command)/\(iso)")
        }
        host.view = calView
        sub.addItem(host)
        root.submenu = sub
        return root
    }

    private func makeNoteFlyoutItem() -> NSMenuItem {
        let taskKey = noteTaskKey
        let root = NSMenuItem(title: "备注", action: nil, keyEquivalent: "")
        let sub = NSMenu(title: "备注")
        sub.delegate = self
        let host = NSMenuItem()
        let noteView = PixelNoteMenuView(palette: palette, note: noteText, onSave: { [weak self] text in
            guard let self = self, !taskKey.isEmpty else { return }
            let encoded = Data(text.utf8).base64EncodedString()
            self.sendToWeb("notesave:\(taskKey)|\(encoded)")
        }, onDismiss: { [weak self] in
            self?.activeMenu?.cancelTracking()
        })
        activeNoteView = noteView
        host.view = noteView
        sub.addItem(host)
        root.submenu = sub
        return root
    }

    @objc private func handleMenuCommand(_ sender: NSMenuItem) {
        guard let cmd = sender.representedObject as? String else { return }
        if cmd != "pin" && cmd != "exit" {
            menuGraceUntil = Date().addingTimeInterval(0.4)
        }
        sendToWeb("command:\(cmd)")
    }

    func menuWillOpen(_ menu: NSMenu) {
        if menu.title == "备注" {
            DispatchQueue.main.async { [weak self] in
                self?.activeNoteView?.focusInput()
            }
        }
    }

    func menuDidClose(_ menu: NSMenu) {
        if menu.title == "备注" {
            activeNoteView?.flush()
            menuGraceUntil = Date().addingTimeInterval(0.5)
        }
        if menu === activeMenu {
            activeNoteView?.flush()
            menuVisible = false
            activeMenu = nil
            activeNoteView = nil
            menuGraceUntil = Date().addingTimeInterval(0.4)
            applyPanelBounds()
        }
    }

    // MARK: - Live HTML Hot-Reload

    private func startPageWatcher() {
        let indexPath = appDir.appendingPathComponent("index.html").path
        let fd = open(indexPath, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .extend], queue: .main)
        source.setEventHandler { [weak self] in
            guard let self = self else { return }
            self.reloadWorkItem?.cancel()
            let item = DispatchWorkItem { [weak self] in
                self?.webView.reload()
            }
            self.reloadWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: item)
        }
        source.setCancelHandler { close(fd) }
        source.resume()
        fileWatcher = source
    }
}

// MARK: - Entry Point

if CommandLine.arguments.contains("--shutdown") {
    DistributedNotificationCenter.default().post(name: kShutdownNotification, object: nil)
    exit(0)
}

let existing = NSRunningApplication.runningApplications(withBundleIdentifier: kBundleId)
if existing.contains(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) {
    exit(0)
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = DesktopNoteApp()
app.delegate = delegate
app.run()
