import AppKit
import AgentHaloCore

@MainActor
class DetailsPanel: NSPanel {
    private static let panelWidth: CGFloat = 278
    private static let contextPillWidth: CGFloat = 42
    private static let contextPillHorizontalPadding: CGFloat = 3

    private let stack = NSStackView()
    private let contextValue = NSTextField(labelWithString: L10n.shared["context.empty"])
    private let titleField = NSTextField(labelWithString: "OFFLINE")
    private let detailField = NSTextField(labelWithString: L10n.shared["status.offline_codex"])
    private let primaryQuota = QuotaRowView(title: L10n.shared["quota.5h"])
    private let secondaryQuota = QuotaRowView(title: L10n.shared["quota.weekly"])
    private let codexIcon = NSImageView()
    private let contextPill = NSView()
    private let quotaGroup = NSStackView()
    private let metadataGroup = NSStackView()
    private let sessionTitleRow = MetadataRowView(
        title: L10n.shared["metadata.session_title"]
    )
    private let modelRow = MetadataRowView(
        title: L10n.shared["metadata.model"]
    )
    private let titleModelSeparator = SeparatorView()
    private let modelTokenSeparator = SeparatorView()
    private let tokenRow = MetadataRowView(
        title: L10n.shared["metadata.tokens"],
        valueFont: .systemFont(ofSize: 11.5, weight: .medium)
    )
    private var topRow: NSView?
    var onMouseEntered: (() -> Void)?
    var onMouseExited: (() -> Void)?

    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: Self.panelWidth, height: 192),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        sharingType = .readOnly
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        contentMinSize = NSSize(width: Self.panelWidth, height: 0)
        contentMaxSize = NSSize(width: Self.panelWidth, height: CGFloat.greatestFiniteMagnitude)

        let container = NSVisualEffectView(frame: contentView?.bounds ?? .zero)
        container.material = .popover
        container.state = .active
        container.wantsLayer = true
        container.layer?.cornerRadius = 18
        container.layer?.borderWidth = 1
        container.layer?.borderColor = NSColor(calibratedRed: 0.70, green: 0.78, blue: 0.82, alpha: 0.35).cgColor
        container.layer?.backgroundColor = NSColor(calibratedRed: 0.98, green: 0.99, blue: 1.0, alpha: 0.90).cgColor
        container.translatesAutoresizingMaskIntoConstraints = false

        stack.orientation = .vertical
        stack.spacing = 0
        stack.alignment = .leading
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 17, bottom: 4, right: 17)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let topRow = makeTopRow()
        self.topRow = topRow
        stack.addArrangedSubview(topRow)
        stack.setCustomSpacing(0, after: topRow)

        titleField.font = .systemFont(ofSize: 22, weight: .bold)
        titleField.lineBreakMode = .byTruncatingTail
        titleField.alignment = .left
        detailField.font = .systemFont(ofSize: 12)
        detailField.textColor = NSColor(calibratedRed: 0.38, green: 0.45, blue: 0.50, alpha: 1)
        detailField.lineBreakMode = .byTruncatingTail
        detailField.alignment = .left
        stack.addArrangedSubview(titleField)
        stack.setCustomSpacing(2, after: titleField)
        stack.addArrangedSubview(detailField)
        stack.setCustomSpacing(11, after: detailField)

        quotaGroup.orientation = .vertical
        quotaGroup.spacing = 4
        quotaGroup.alignment = .leading
        quotaGroup.translatesAutoresizingMaskIntoConstraints = false
        quotaGroup.addArrangedSubview(primaryQuota)
        quotaGroup.addArrangedSubview(secondaryQuota)
        stack.addArrangedSubview(quotaGroup)

        metadataGroup.orientation = .vertical
        metadataGroup.spacing = 0
        metadataGroup.alignment = .width
        metadataGroup.translatesAutoresizingMaskIntoConstraints = false
        
        metadataGroup.edgeInsets = NSEdgeInsets(top: 0, left: 0, bottom: 2, right: 0)
        metadataGroup.addArrangedSubview(sessionTitleRow)
        metadataGroup.addArrangedSubview(titleModelSeparator)
        metadataGroup.addArrangedSubview(modelRow)
        metadataGroup.addArrangedSubview(modelTokenSeparator)
        metadataGroup.addArrangedSubview(tokenRow)
        metadataGroup.isHidden = true
        stack.addArrangedSubview(metadataGroup)

        let rootView = TrackingDetailsContentView()
        rootView.owner = self
        contentView = rootView
        contentView?.addSubview(container)
        container.addSubview(stack)
        NSLayoutConstraint.activate([
            contentView!.widthAnchor.constraint(equalToConstant: Self.panelWidth),
            container.leadingAnchor.constraint(equalTo: contentView!.leadingAnchor),
            container.trailingAnchor.constraint(equalTo: contentView!.trailingAnchor),
            container.topAnchor.constraint(equalTo: contentView!.topAnchor),
            container.bottomAnchor.constraint(equalTo: contentView!.bottomAnchor),
            stack.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            stack.topAnchor.constraint(equalTo: container.topAnchor),
            stack.bottomAnchor.constraint(equalTo: container.bottomAnchor),

            topRow.trailingAnchor.constraint(equalTo: stack.trailingAnchor, constant: -17),
            titleField.trailingAnchor.constraint(equalTo: stack.trailingAnchor, constant: -17),
            detailField.trailingAnchor.constraint(equalTo: stack.trailingAnchor, constant: -17),
            quotaGroup.trailingAnchor.constraint(equalTo: stack.trailingAnchor, constant: -17),
            primaryQuota.trailingAnchor.constraint(equalTo: quotaGroup.trailingAnchor),
            secondaryQuota.trailingAnchor.constraint(equalTo: quotaGroup.trailingAnchor),
            metadataGroup.leadingAnchor.constraint(equalTo: stack.leadingAnchor, constant: 17),
            metadataGroup.trailingAnchor.constraint(equalTo: stack.trailingAnchor, constant: -17)
        ])
    }

    func render(aggregate: AggregateSnapshot, model: DetailsPanelViewModel) {
        #if DEBUG
        let startTime = CFAbsoluteTimeGetCurrent()
        defer {
            let duration = (CFAbsoluteTimeGetCurrent() - startTime) * 1000
            if duration > 16.67 {
                NSLog("[Performance] DetailsPanel.render took %.2fms (>1 frame)", duration)
            }
        }
        #endif

        updateStatus(aggregate: aggregate)
        let isOffline = aggregate.state == .idle && aggregate.label == "OFFLINE"
        updateContext(model.contextUsedPercent, isOffline: isOffline)

        quotaGroup.isHidden = true
        metadataGroup.isHidden = true

        switch model.body {
        case .usage(let usage):
            stack.setCustomSpacing(16, after: detailField)
            stack.edgeInsets.bottom = 4
            renderUsage(usage)
            quotaGroup.isHidden = false
        case .session(let session):
            stack.setCustomSpacing(11, after: detailField)
            stack.edgeInsets.bottom = 4
            renderSession(session, isOffline: isOffline)
            metadataGroup.isHidden = false
        }
        resizeToFitContent()
    }

    private func updateContext(_ contextUsedPercent: Double?, isOffline: Bool) {
        contextPill.isHidden = isOffline || contextUsedPercent == nil
        contextValue.stringValue = contextUsedPercent.map(Self.compactContextPercent)
            ?? L10n.shared["context.empty"]
    }

    private func renderUsage(_ usage: UsageDetailsModel) {
        primaryQuota.setTitle(L10n.shared["quota.5h"])
        secondaryQuota.setTitle(L10n.shared["quota.weekly"])
        renderUsageWindow(usage.windows.first { $0.kind == .session }, in: primaryQuota)
        renderUsageWindow(usage.windows.first { $0.kind == .weekly }, in: secondaryQuota)
    }

    private func renderUsageWindow(_ window: UsageWindow?, in row: QuotaRowView) {
        guard let window else {
            row.updateUnavailable()
            return
        }
        row.update(usedPercent: window.usedPercent, resetAt: window.resetsAt)
    }

    private func renderSession(_ session: SessionDetailsSnapshot, isOffline: Bool) {
        sessionTitleRow.setTitle(L10n.shared["metadata.session_title"])
        modelRow.setTitle(L10n.shared["metadata.model"])
        tokenRow.setTitle(L10n.shared["metadata.tokens"])

        if isOffline {
            sessionTitleRow.setValue("--")
            modelRow.setValue("--")
            tokenRow.setValue("--")
            return
        }

        sessionTitleRow.setValue(Self.displayValue(session.sessionTitle), toolTip: session.sessionTitle)
        modelRow.setValue(Self.displayValue(session.modelName), toolTip: session.modelName)
        if session.inputTokens != nil || session.outputTokens != nil {
            tokenRow.attributedStringValue = Self.formatTokenAttributedString(
                input: session.inputTokens,
                output: session.outputTokens
            )
        } else {
            tokenRow.setValue("--")
        }
    }

    private func resizeToFitContent() {
        contentView?.layoutSubtreeIfNeeded()
        let scale = effectiveBackingScale
        let fittingHeight = Self.evenPanelHeight(
            for: stack.fittingSize.height,
            backingScaleFactor: scale
        )
        let topEdge = frame.maxY
        let newFrame = NSRect(
            x: frame.minX,
            y: topEdge - fittingHeight,
            width: Self.panelWidth,
            height: fittingHeight
        )
        applyResizeFrame(newFrame, display: false, animate: false)
    }

    func applyResizeFrame(_ frame: NSRect, display: Bool, animate: Bool) {
        setFrame(frame, display: display, animate: animate)
    }

    private var effectiveBackingScale: CGFloat {
        screen?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 1
    }

    static func evenPanelHeight(for fittingHeight: CGFloat, backingScaleFactor: CGFloat) -> CGFloat {
        let scale = backingScaleFactor > 0 ? backingScaleFactor : 1
        let pixelAlignedHeight = ceil(fittingHeight * scale) / scale
        return ceil(pixelAlignedHeight / 2) * 2
    }

    func updateStatus(aggregate: AggregateSnapshot) {
        titleField.stringValue = aggregate.label
        let rgb = HaloVisualModel.stateColor(aggregate.state)
        titleField.textColor = NSColor(calibratedRed: rgb.red / 255, green: rgb.green / 255, blue: rgb.blue / 255, alpha: 1)
        detailField.stringValue = Self.localizedDetail(for: aggregate)
        let isOffline = aggregate.state == .idle && aggregate.label == "OFFLINE"
        updateContext(aggregate.sessions.first?.contextUsedPercent, isOffline: isOffline)
    }

    static func formatResetTime(_ date: Date?) -> String {
        guard let date else {
            return ""
        }
        let calendar = Calendar.current
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: L10n.shared["date.culture"])
        if calendar.isDateInToday(date) {
            formatter.dateFormat = L10n.shared["date.today_format"]
        } else {
            formatter.dateFormat = L10n.shared["date.other_format"]
        }
        return formatter.string(from: date)
    }

    static func compactTokenCount(_ count: Int64?) -> String {
        guard let count else {
            return "--"
        }
        guard count >= 1_000 else {
            return String(count)
        }
        let thousands = Double(count) / 1_000
        if thousands.rounded() == thousands {
            return "\(Int(thousands))k"
        }
        return String(format: "%.1fk", locale: Locale(identifier: "en_US_POSIX"), thousands)
    }

    static func compactContextPercent(_ value: Double) -> String {
        "\(min(99, max(0, Int(value.rounded()))))%"
    }

    static func formatTokenAttributedString(input: Int64?, output: Int64?) -> NSAttributedString {
        let inputStr = compactTokenCount(input)
        let outputStr = compactTokenCount(output)
        
        let font = NSFont.systemFont(ofSize: 11.5, weight: .medium)
        // 莫兰迪蓝灰色：输入 (In)
        let inColor = NSColor(calibratedRed: 0.25, green: 0.45, blue: 0.65, alpha: 1)
        // 莫兰迪绿灰色：输出 (Out)
        let outColor = NSColor(calibratedRed: 0.25, green: 0.55, blue: 0.45, alpha: 1)
        // 中间分隔点颜色
        let sepColor = NSColor.secondaryLabelColor
        
        let attrStr = NSMutableAttributedString()
        
        attrStr.append(NSAttributedString(string: "↑ \(inputStr)", attributes: [
            .font: font,
            .foregroundColor: inColor
        ]))
        
        attrStr.append(NSAttributedString(string: "  ·  ", attributes: [
            .font: font,
            .foregroundColor: sepColor
        ]))
        
        attrStr.append(NSAttributedString(string: "↓ \(outputStr)", attributes: [
            .font: font,
            .foregroundColor: outColor
        ]))
        
        return attrStr
    }

    private static func displayValue(_ value: String?) -> String {
        guard let value, !value.isEmpty else {
            return "--"
        }
        return value
    }

    static func localizedDetail(for aggregate: AggregateSnapshot) -> String {
        if aggregate.state == .idle {
            if aggregate.label == "PAUSED" {
                return L10n.shared["status.paused"]
            }
            return aggregate.focusedAgent.localizedOfflineDetail
        }
        if aggregate.label == "STANDBY",
           !aggregate.detail.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return aggregate.detail
        }
        let action = aggregate.sessions.first?.action ?? aggregate.detail
        if action.localizedCaseInsensitiveContains("Writing answer") {
            return L10n.shared["status.writing_answer"]
        }
        if action.localizedCaseInsensitiveContains("command") { return L10n.shared["status.running_command"] }
        if action.localizedCaseInsensitiveContains("Editing") { return L10n.shared["status.editing_files"] }
        if action.localizedCaseInsensitiveContains("Search") { return L10n.shared["status.searching"] }
        if action.localizedCaseInsensitiveContains("Compressing context") { return L10n.shared["status.compressing_context"] }
        if action.localizedCaseInsensitiveContains("Context compacted") { return L10n.shared["status.context_compacted"] }
        if action.localizedCaseInsensitiveContains("Awaiting permission") { return L10n.shared["status.awaiting_permission"] }
        if action.localizedCaseInsensitiveContains("Permission denied") { return L10n.shared["status.permission_denied"] }
        if action.localizedCaseInsensitiveContains("Reviewing result") { return L10n.shared["status.reviewing_result"] }
        switch aggregate.state {
        case .thinking: return L10n.shared["status.thinking"]
        case .working: return L10n.shared["status.working"]
        case .done: return L10n.shared["status.done"]
        case .attention: return L10n.shared["status.attention"]
        case .error: return aggregate.detail.isEmpty ? L10n.shared["status.error"] : aggregate.detail
        case .idle: return aggregate.focusedAgent.localizedOfflineDetail
        }
    }

    private func makeTopRow() -> NSView {
        let row = NSView()
        row.translatesAutoresizingMaskIntoConstraints = false

        codexIcon.image = AgentIconAssets.image(named: "codex")
        codexIcon.imageScaling = .scaleProportionallyDown
        codexIcon.translatesAutoresizingMaskIntoConstraints = false
        codexIcon.setAccessibilityLabel("Codex")
        codexIcon.setAccessibilityRole(.image)

        contextPill.wantsLayer = true
        contextPill.layer?.cornerRadius = 9
        contextPill.layer?.backgroundColor = NSColor(calibratedRed: 0.88, green: 0.95, blue: 0.99, alpha: 0.80).cgColor
        contextPill.layer?.borderWidth = 1
        contextPill.layer?.borderColor = NSColor(calibratedRed: 0.62, green: 0.78, blue: 0.88, alpha: 0.42).cgColor
        contextPill.translatesAutoresizingMaskIntoConstraints = false

        contextValue.font = .systemFont(ofSize: 11, weight: .regular)
        contextValue.textColor = NSColor(calibratedRed: 0.22, green: 0.49, blue: 0.57, alpha: 1)
        contextValue.alignment = .center
        contextValue.lineBreakMode = .byTruncatingTail
        contextValue.translatesAutoresizingMaskIntoConstraints = false

        row.addSubview(codexIcon)
        row.addSubview(contextPill)
        contextPill.addSubview(contextValue)
        NSLayoutConstraint.activate([
            row.heightAnchor.constraint(equalToConstant: 24),
            codexIcon.leadingAnchor.constraint(equalTo: row.leadingAnchor),
            codexIcon.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            codexIcon.widthAnchor.constraint(equalToConstant: 32),
            codexIcon.heightAnchor.constraint(equalToConstant: 18),
            contextPill.trailingAnchor.constraint(equalTo: row.trailingAnchor),
            contextPill.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            contextPill.widthAnchor.constraint(equalToConstant: Self.contextPillWidth),
            contextPill.leadingAnchor.constraint(greaterThanOrEqualTo: codexIcon.trailingAnchor, constant: 10),
            contextValue.leadingAnchor.constraint(equalTo: contextPill.leadingAnchor, constant: Self.contextPillHorizontalPadding),
            contextValue.trailingAnchor.constraint(equalTo: contextPill.trailingAnchor, constant: -Self.contextPillHorizontalPadding),
            contextValue.topAnchor.constraint(equalTo: contextPill.topAnchor, constant: 3),
            contextValue.bottomAnchor.constraint(equalTo: contextPill.bottomAnchor, constant: -3)
        ])
        return row
    }

    var focusedAgentForTesting: AgentKind {
        .codex
    }

    var detailTextForTesting: String {
        detailField.stringValue
    }
}

@MainActor
private final class SeparatorView: NSView {
    private let line = NSView()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        translatesAutoresizingMaskIntoConstraints = false
        heightAnchor.constraint(equalToConstant: 1).isActive = true

        line.wantsLayer = true
        line.layer?.backgroundColor = NSColor.textColor.withAlphaComponent(0.06).cgColor
        line.translatesAutoresizingMaskIntoConstraints = false
        addSubview(line)

        NSLayoutConstraint.activate([
            line.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 0),
            line.trailingAnchor.constraint(equalTo: trailingAnchor, constant: 0),
            line.topAnchor.constraint(equalTo: topAnchor),
            line.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }
}

@MainActor
private final class MetadataRowView: NSView {
    private let nameField: NSTextField
    private let valueField = NSTextField(labelWithString: "--")
    private let valueBackground = NSView()

    var value: String {
        get { valueField.stringValue }
        set { setValue(newValue) }
    }

    var attributedStringValue: NSAttributedString {
        get { valueField.attributedStringValue }
        set {
            valueField.attributedStringValue = newValue
            valueField.toolTip = nil
        }
    }

    var valueToolTip: String? { valueField.toolTip }

    func setValue(_ value: String, toolTip: String? = nil) {
        valueField.stringValue = value
        valueField.toolTip = toolTip
    }

    func setTitle(_ title: String) {
        nameField.stringValue = title
    }

    init(title: String, isTagStyle: Bool = false, valueFont: NSFont = .systemFont(ofSize: 12, weight: .semibold)) {
        nameField = NSTextField(labelWithString: title)
        super.init(frame: .zero)
        translatesAutoresizingMaskIntoConstraints = false

        nameField.font = .systemFont(ofSize: 11.5)
        nameField.textColor = .secondaryLabelColor
        nameField.setContentCompressionResistancePriority(.required, for: .horizontal)
        nameField.setContentHuggingPriority(.required, for: .horizontal)
        nameField.translatesAutoresizingMaskIntoConstraints = false

        valueField.font = valueFont
        valueField.textColor = .labelColor
        valueField.alignment = .right
        valueField.lineBreakMode = .byTruncatingTail
        valueField.maximumNumberOfLines = 1
        valueField.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        valueField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        valueField.translatesAutoresizingMaskIntoConstraints = false

        valueBackground.translatesAutoresizingMaskIntoConstraints = false
        valueBackground.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        if isTagStyle {
            valueBackground.wantsLayer = true
            valueBackground.layer?.cornerRadius = 5
            valueBackground.layer?.backgroundColor = NSColor.textColor.withAlphaComponent(0.06).cgColor
            valueBackground.layer?.borderWidth = 0.5
            valueBackground.layer?.borderColor = NSColor.textColor.withAlphaComponent(0.08).cgColor
        }

        addSubview(nameField)
        addSubview(valueBackground)
        valueBackground.addSubview(valueField)

        NSLayoutConstraint.activate([
            heightAnchor.constraint(equalToConstant: 24),

            nameField.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 0),
            nameField.centerYAnchor.constraint(equalTo: centerYAnchor),

            valueBackground.trailingAnchor.constraint(equalTo: trailingAnchor, constant: 0),
            valueBackground.centerYAnchor.constraint(equalTo: centerYAnchor),
            valueBackground.leadingAnchor.constraint(greaterThanOrEqualTo: nameField.trailingAnchor, constant: 10),

            valueField.leadingAnchor.constraint(equalTo: valueBackground.leadingAnchor, constant: isTagStyle ? 6 : 0),
            valueField.trailingAnchor.constraint(equalTo: valueBackground.trailingAnchor, constant: isTagStyle ? -6 : 0),
            valueField.topAnchor.constraint(equalTo: valueBackground.topAnchor, constant: isTagStyle ? 2 : 0),
            valueField.bottomAnchor.constraint(equalTo: valueBackground.bottomAnchor, constant: isTagStyle ? -2 : 0),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }
}

@MainActor
private final class TrackingDetailsContentView: NSView {
    weak var owner: DetailsPanel?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeAlways],
            owner: self
        ))
    }

    override func mouseEntered(with event: NSEvent) {
        owner?.onMouseEntered?()
    }

    override func mouseExited(with event: NSEvent) {
        owner?.onMouseExited?()
    }
}

@MainActor
private final class QuotaRowView: NSView {
    private let nameField: NSTextField
    private let resetField = NSTextField(labelWithString: "")
    private let valueField = NSTextField(labelWithString: L10n.shared["quota.no_data"])
    private let meter = RoundedMeterView()

    init(title: String) {
        nameField = NSTextField(labelWithString: title)
        super.init(frame: .zero)
        translatesAutoresizingMaskIntoConstraints = false
        setup()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func update(usedPercent: Double, resetAt: Date?) {
        if let resetAt, resetAt <= Date() {
            valueField.stringValue = L10n.shared["quota.waiting_refresh"]
            resetField.stringValue = ""
            resetField.isHidden = true
            meter.value = 0
            return
        }
        let remaining = min(100, max(0, 100 - usedPercent))
        valueField.stringValue = L10n.shared.format("quota.remaining", Int(remaining.rounded()))
        meter.value = remaining
        let resetText = DetailsPanel.formatResetTime(resetAt)
        resetField.stringValue = resetText
        resetField.isHidden = resetText.isEmpty
    }

    func updateUnavailable() {
        valueField.stringValue = L10n.shared["quota.no_data"]
        resetField.stringValue = ""
        resetField.isHidden = true
        meter.value = 0
    }

    func setTitle(_ title: String) {
        nameField.stringValue = title
    }

    private func setup() {
        nameField.font = .systemFont(ofSize: 12, weight: .regular)
        nameField.textColor = NSColor(calibratedRed: 0.37, green: 0.44, blue: 0.48, alpha: 1)
        nameField.lineBreakMode = .byTruncatingTail
        nameField.setContentCompressionResistancePriority(.required, for: .horizontal)
        nameField.setContentHuggingPriority(.required, for: .horizontal)
        resetField.font = .systemFont(ofSize: 11, weight: .regular)
        resetField.textColor = NSColor(calibratedRed: 0.49, green: 0.56, blue: 0.60, alpha: 1)
        resetField.lineBreakMode = .byTruncatingTail
        resetField.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        valueField.font = .systemFont(ofSize: 12, weight: .semibold)
        valueField.textColor = NSColor(calibratedRed: 0.18, green: 0.24, blue: 0.29, alpha: 1)
        valueField.alignment = .right
        valueField.setContentCompressionResistancePriority(.defaultHigh, for: .horizontal)
        valueField.setContentHuggingPriority(.required, for: .horizontal)

        [nameField, resetField, valueField, meter].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            addSubview($0)
        }
        resetField.isHidden = true
        NSLayoutConstraint.activate([
            heightAnchor.constraint(equalToConstant: 33),
            nameField.leadingAnchor.constraint(equalTo: leadingAnchor),
            nameField.topAnchor.constraint(equalTo: topAnchor),
            resetField.leadingAnchor.constraint(equalTo: nameField.trailingAnchor, constant: 7),
            resetField.centerYAnchor.constraint(equalTo: nameField.centerYAnchor),
            resetField.trailingAnchor.constraint(lessThanOrEqualTo: valueField.leadingAnchor, constant: -8),
            valueField.trailingAnchor.constraint(equalTo: trailingAnchor),
            valueField.centerYAnchor.constraint(equalTo: nameField.centerYAnchor),
            meter.leadingAnchor.constraint(equalTo: leadingAnchor),
            meter.trailingAnchor.constraint(equalTo: trailingAnchor),
            meter.topAnchor.constraint(equalTo: nameField.bottomAnchor, constant: 7),
            meter.heightAnchor.constraint(equalToConstant: 4)
        ])
    }
}

@MainActor
enum QuotaMeterPalette {
    private struct Stop {
        let percent: Double
        let red: Double
        let green: Double
        let blue: Double
    }

    private static let stops = [
        Stop(percent: 0, red: 202, green: 217, blue: 224),
        Stop(percent: 25, red: 168, green: 191, blue: 202),
        Stop(percent: 50, red: 112, green: 148, blue: 169),
        Stop(percent: 75, red: 82, green: 121, blue: 146),
        Stop(percent: 100, red: 64, green: 105, blue: 132),
    ]

    static func fillColor(for remainingPercent: Double) -> NSColor {
        let clamped = min(100, max(0, remainingPercent))
        guard let upperIndex = stops.firstIndex(where: { clamped <= $0.percent }) else {
            return color(for: stops[stops.count - 1])
        }
        guard upperIndex > 0 else {
            return color(for: stops[0])
        }

        let lower = stops[upperIndex - 1]
        let upper = stops[upperIndex]
        let progress = (clamped - lower.percent) / (upper.percent - lower.percent)
        return NSColor(
            deviceRed: component(from: lower.red, to: upper.red, progress: progress),
            green: component(from: lower.green, to: upper.green, progress: progress),
            blue: component(from: lower.blue, to: upper.blue, progress: progress),
            alpha: 1
        )
    }

    private static func color(for stop: Stop) -> NSColor {
        NSColor(
            deviceRed: CGFloat(stop.red) / 255,
            green: CGFloat(stop.green) / 255,
            blue: CGFloat(stop.blue) / 255,
            alpha: 1
        )
    }

    private static func component(from start: Double, to end: Double, progress: Double) -> CGFloat {
        CGFloat(start + (end - start) * progress) / 255
    }
}

@MainActor
final class RoundedMeterView: NSView {
    var value: Double = 0 {
        didSet {
            needsDisplay = true
        }
    }

    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let bounds = NSRect(x: 0, y: 0, width: bounds.width, height: bounds.height)
        let radius = bounds.height / 2
        NSColor(calibratedRed: 0.72, green: 0.79, blue: 0.84, alpha: 0.30).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: radius, yRadius: radius).fill()

        let rawFillWidth = bounds.width * min(100, max(0, value)) / 100
        guard rawFillWidth > 0 else {
            return
        }
        let fillWidth = max(bounds.height, rawFillWidth)
        QuotaMeterPalette.fillColor(for: value).setFill()
        NSBezierPath(
            roundedRect: NSRect(x: 0, y: 0, width: fillWidth, height: bounds.height),
            xRadius: radius,
            yRadius: radius
        ).fill()
    }
}

private enum AgentIconAssets {
    static func image(named name: String) -> NSImage? {
        guard let url = url(named: name),
              let data = try? Data(contentsOf: url) else {
            return nil
        }
        return NSImage(data: data)
    }

    private static func url(named name: String) -> URL? {
        if let bundled = Bundle.main.url(
            forResource: name,
            withExtension: "svg",
            subdirectory: "agent-switch"
        ) {
            return bundled
        }

        let srcRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let sourceAsset = srcRoot
            .appendingPathComponent("shared/assets/agent-switch", isDirectory: true)
            .appendingPathComponent("\(name).svg")
        return FileManager.default.fileExists(atPath: sourceAsset.path) ? sourceAsset : nil
    }
}
