import Foundation
import AgentHaloCore

func expect<T: Equatable>(_ actual: T, _ expected: T, _ message: String) {
    if actual != expected {
        fatalError("\(message): expected \(expected), got \(actual)")
    }
}

func expect(_ condition: Bool, _ message: String) {
    if !condition {
        fatalError(message)
    }
}

func testReducesPlanningWorkingAttentionErrorAndCompleteEvents() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75043.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:00Z","type":"session_meta","payload":{"id":"thread-a","cwd":"/Users/test/Projects/AgentHalo"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:01Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    expect(reducer.snapshot.threadId, "thread-a", "thread id")
    expect(reducer.snapshot.projectName, "AgentHalo", "project name")
    expect(reducer.snapshot.state, .thinking, "task_started state")
    expect(reducer.snapshot.action, "Planning", "task_started action")
    expect(reducer.snapshot.active, "task_started should be active")
    expect(reducer.snapshot.agent, .codex, "Codex reducer should stamp Codex agent")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:02Z","type":"response_item","payload":{"type":"function_call","name":"shell_command"}}"#)
    expect(reducer.snapshot.state, .working, "function_call state")
    expect(reducer.snapshot.action, "Running command", "function_call action")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:03Z","type":"response_item","payload":{"type":"function_call_output"}}"#)
    expect(reducer.snapshot.state, .working, "function_call_output visible state")
    expect(reducer.snapshot.action, "Reviewing result", "function_call_output action")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:04Z","type":"event_msg","payload":{"type":"approval_requested"}}"#)
    expect(reducer.snapshot.state, .attention, "approval state")
    expect(reducer.snapshot.action, "Needs you", "approval action")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:05Z","type":"event_msg","payload":{"type":"turn_failed"}}"#)
    expect(reducer.snapshot.state, .error, "turn_failed state")
    expect(!reducer.snapshot.active, "turn_failed should be inactive")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:06Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T01:00:07Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .done, "task_complete state")
    expect(reducer.snapshot.action, "Complete", "task_complete action")
    expect(!reducer.snapshot.active, "task_complete should be inactive")
    expect(reducer.snapshot.completedAt != nil, "task_complete should set completion time")
}

func testAggregatePrioritizesActionableSessions() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let idle = SessionSnapshot(
        threadId: "idle",
        projectName: "IdleProject",
        workingDirectory: "",
        state: .idle,
        action: "Ready",
        lastEventAt: now,
        completedAt: nil,
        active: false
    )
    let done = SessionSnapshot(
        threadId: "done",
        projectName: "DoneProject",
        workingDirectory: "",
        state: .done,
        action: "Complete",
        lastEventAt: now,
        completedAt: now,
        active: false
    )
    let attention = SessionSnapshot(
        threadId: "attention",
        projectName: "AttentionProject",
        workingDirectory: "",
        state: .attention,
        action: "Needs you",
        lastEventAt: now,
        completedAt: nil,
        active: true
    )

    let aggregate = SessionAggregator.aggregate(
        snapshots: [idle, done, attention],
        settings: HaloSettings(paused: false, installedAt: now.addingTimeInterval(-60), acknowledged: [:]),
        now: now
    )

    expect(aggregate.state, .attention, "aggregate state")
    expect(aggregate.label, "NEEDS YOU", "aggregate label")
    expect(aggregate.detail, "AttentionProject +1", "aggregate detail")
    expect(aggregate.sessions.map(\.threadId), ["attention", "done"], "aggregate sessions")

    let completedBeforeNewState = SessionSnapshot(
        threadId: "completed-before-new-state",
        projectName: "CompletedProject",
        workingDirectory: "",
        state: .done,
        action: "Complete",
        lastEventAt: now.addingTimeInterval(-1),
        completedAt: now.addingTimeInterval(-1),
        active: false
    )
    let replacements: [(HaloState, Bool, String)] = [
        (.thinking, true, "Thinking"),
        (.working, true, "Running command"),
        (.attention, true, "Needs you"),
        (.error, false, "Interrupted"),
    ]
    for (state, active, action) in replacements {
        let newState = SessionSnapshot(
            threadId: "new-\(state.rawValue)",
            projectName: "NewProject",
            workingDirectory: "",
            state: state,
            action: action,
            lastEventAt: now,
            completedAt: nil,
            active: active
        )
        let replacementAggregate = SessionAggregator.aggregate(
            snapshots: [completedBeforeNewState, newState],
            settings: HaloSettings(installedAt: now.addingTimeInterval(-600)),
            codexRunning: true,
            now: now
        )
        expect(replacementAggregate.state, state,
               "\(state.rawValue) should replace a recent completion")
    }
}

func testAggregateRemovesSupersededSessionErrors() {
    let now = ISO8601DateFormatter().date(from: "2026-06-22T04:00:00Z")!
    let oldError = SessionSnapshot(
        threadId: "old-error",
        projectName: "OldProject",
        workingDirectory: "/tmp/old",
        state: .error,
        action: "Interrupted",
        lastEventAt: now.addingTimeInterval(-60),
        completedAt: nil,
        active: false
    )
    let newerWorking = SessionSnapshot(
        threadId: "new-working",
        projectName: "NewProject",
        workingDirectory: "/tmp/new",
        state: .working,
        action: "Running command",
        lastEventAt: now,
        completedAt: nil,
        active: true
    )
    let settings = HaloSettings(installedAt: now.addingTimeInterval(-600))

    let working = SessionAggregator.aggregate(
        snapshots: [oldError, newerWorking],
        settings: settings,
        now: now
    )
    expect(working.state, .working, "newer working session replaces old error")
    expect(working.sessions.map(\.threadId), ["new-working"], "old error removed from display sessions")

    let newerDone = SessionSnapshot(
        threadId: "new-done",
        projectName: "NewProject",
        workingDirectory: "/tmp/new",
        state: .done,
        action: "Complete",
        lastEventAt: now,
        completedAt: now,
        active: false
    )
    let done = SessionAggregator.aggregate(
        snapshots: [oldError, newerDone],
        settings: settings,
        now: now
    )
    expect(done.sessions.map(\.threadId), ["new-done"], "newer completion replaces old error")

    let acknowledged = settings.acknowledgingCompletedSessions([newerDone])
    let ready = SessionAggregator.aggregate(
        snapshots: [oldError, newerDone],
        settings: acknowledged,
        now: now
    )
    expect(ready.state, .idle, "acknowledged newer completion does not resurrect old error")
    expect(ready.sessions.isEmpty, "superseded error remains absent after acknowledgement")

    let newerError = SessionSnapshot(
        threadId: "new-error",
        projectName: "NewProject",
        workingDirectory: "/tmp/new",
        state: .error,
        action: "Interrupted",
        lastEventAt: now,
        completedAt: nil,
        active: false
    )
    let olderWorking = SessionSnapshot(
        threadId: "old-working",
        projectName: "OldProject",
        workingDirectory: "/tmp/old",
        state: .working,
        action: "Running command",
        lastEventAt: now.addingTimeInterval(-60),
        completedAt: nil,
        active: true
    )
    let latestError = SessionAggregator.aggregate(
        snapshots: [olderWorking, newerError],
        settings: settings,
        now: now
    )
    expect(latestError.state, .error, "latest error remains primary")
    expect(
        latestError.sessions.map(\.threadId),
        ["new-error", "old-working"],
        "active sessions remain available behind the latest error"
    )

    let metadataOnly = SessionSnapshot(
        threadId: "metadata-only",
        projectName: "Codex",
        workingDirectory: "/tmp/new",
        state: .idle,
        action: "Ready",
        lastEventAt: now,
        completedAt: nil,
        active: false
    )
    let unchanged = SessionAggregator.aggregate(
        snapshots: [oldError, metadataOnly],
        settings: settings,
        now: now
    )
    expect(unchanged.state, .error, "metadata-only session does not suppress error")
    expect(unchanged.sessions.map(\.threadId), ["old-error"], "metadata-only session stays invisible")
}

func testAcknowledgingCompletedSessionsStoresLatestVisibleCompletionOnly() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let earlier = now.addingTimeInterval(-120)
    let later = now.addingTimeInterval(-60)
    let oldCompletion = SessionSnapshot(
        threadId: "done",
        projectName: "AgentHalo",
        workingDirectory: "",
        state: .done,
        action: "Complete",
        lastEventAt: earlier,
        completedAt: earlier,
        active: false
    )
    let latestCompletion = SessionSnapshot(
        threadId: "done",
        projectName: "AgentHalo",
        workingDirectory: "",
        state: .done,
        action: "Complete",
        lastEventAt: later,
        completedAt: later,
        active: false
    )
    let activeSession = SessionSnapshot(
        threadId: "active",
        projectName: "AgentHalo",
        workingDirectory: "",
        state: .working,
        action: "Running command",
        lastEventAt: later,
        completedAt: nil,
        active: true
    )

    let settings = HaloSettings(installedAt: now.addingTimeInterval(-600))
        .acknowledgingCompletedSessions([oldCompletion, latestCompletion, activeSession])

    expect(settings.acknowledged, ["done": later], "acknowledged completions")
}

func testSettingsPersistFormalFieldsAndNormalizePaused() {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-settings-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let url = root.appendingPathComponent("settings.json")
    let store = SettingsStore(settingsURL: url)
    let installedAt = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let acknowledgedErrorAt = installedAt.addingTimeInterval(60)
    let settings = HaloSettings(
        hasPosition: true,
        left: 110,
        top: 220,
        haloSize: 144,
        alwaysOnTop: false,
        paused: true,
        installedAt: installedAt,
        acknowledged: ["thread": installedAt],
        acknowledgedErrorAt: acknowledgedErrorAt
    )

    store.save(settings)
    let loaded = store.load(now: installedAt.addingTimeInterval(120))

    expect(loaded.hasPosition, true, "hasPosition should persist")
    expect(loaded.left, 110, "left should persist")
    expect(loaded.top, 220, "top should persist")
    expect(loaded.haloSize, 144, "haloSize should persist")
    expect(loaded.alwaysOnTop, false, "alwaysOnTop should persist")
    expect(loaded.paused, false, "paused should normalize false on load")
    expect(loaded.acknowledged, ["thread": installedAt], "acknowledged should persist")
    expect(loaded.acknowledgedErrorAt, acknowledgedErrorAt, "acknowledgedErrorAt should persist")
}

func testSettingsDefaultsPreferredDisplayPlacementForLegacyFiles() throws {
    let data = Data(#"{"hasPosition":true,"left":1800,"top":600}"#.utf8)
    let settings = try JSONDecoder().decode(HaloSettings.self, from: data)

    expect(settings.preferredDisplayUUID == nil, "legacy settings should not invent a display UUID")
    expect(settings.preferredDisplayOffsetX == nil, "legacy settings should not invent an x offset")
    expect(settings.preferredDisplayOffsetY == nil, "legacy settings should not invent a y offset")
}

func testSettingsPersistPreferredDisplayPlacement() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-display-placement-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let url = root.appendingPathComponent("settings.json")
    let store = SettingsStore(settingsURL: url)
    let settings = HaloSettings(
        hasPosition: true,
        left: 1800,
        top: 600,
        preferredDisplayUUID: "secondary-display",
        preferredDisplayOffsetX: 120,
        preferredDisplayOffsetY: 80
    )

    store.save(settings)
    let loaded = store.load()

    expect(loaded.preferredDisplayUUID, "secondary-display", "preferred display UUID")
    expect(loaded.preferredDisplayOffsetX, 120, "preferred display x offset")
    expect(loaded.preferredDisplayOffsetY, 80, "preferred display y offset")
}

func testSettingsUsesDefaultHaloSizeForLegacyFilesAndClampsInvalidSizes() {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-size-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let legacyURL = root.appendingPathComponent("legacy.json")
    let smallURL = root.appendingPathComponent("small.json")
    let largeURL = root.appendingPathComponent("large.json")
    try! FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    try! """
    {
      "acknowledged" : {},
      "alwaysOnTop" : true,
      "alwaysOnTopBehaviorVersion" : 1,
      "hasPosition" : false,
      "installedAt" : "2026-06-13T12:47:19Z",
      "left" : 0,
      "paused" : false,
      "top" : 0
    }
    """.data(using: .utf8)!.write(to: legacyURL)
    try! """
    {
      "acknowledged" : {},
      "alwaysOnTop" : true,
      "alwaysOnTopBehaviorVersion" : 1,
      "haloSize" : 24,
      "hasPosition" : false,
      "installedAt" : "2026-06-13T12:47:19Z",
      "left" : 0,
      "paused" : false,
      "top" : 0
    }
    """.data(using: .utf8)!.write(to: smallURL)
    try! """
    {
      "acknowledged" : {},
      "alwaysOnTop" : true,
      "alwaysOnTopBehaviorVersion" : 1,
      "haloSize" : 300,
      "hasPosition" : false,
      "installedAt" : "2026-06-13T12:47:19Z",
      "left" : 0,
      "paused" : false,
      "top" : 0
    }
    """.data(using: .utf8)!.write(to: largeURL)

    expect(
        SettingsStore(settingsURL: legacyURL).load().haloSize,
        HaloSettings.defaultHaloSize,
        "legacy settings should use default halo size"
    )
    expect(
        SettingsStore(settingsURL: smallURL).load().haloSize,
        HaloSettings.minimumHaloSize,
        "undersized halo setting should clamp to minimum"
    )
    expect(
        SettingsStore(settingsURL: largeURL).load().haloSize,
        HaloSettings.maximumHaloSize,
        "oversized halo setting should clamp to maximum"
    )
}

func testSettingsMigratesLegacyAlwaysOnTopOffToDefaultOn() {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-legacy-topmost-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let url = root.appendingPathComponent("settings.json")
    try! FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    try! """
    {
      "acknowledged" : {},
      "alwaysOnTop" : false,
      "hasPosition" : true,
      "installedAt" : "2026-06-13T12:47:19Z",
      "left" : 1341,
      "paused" : false,
      "top" : 817
    }
    """.data(using: .utf8)!.write(to: url)

    let loaded = SettingsStore(settingsURL: url).load()

    expect(loaded.alwaysOnTop, true, "legacy settings should migrate alwaysOnTop back to true")
    expect(
        loaded.alwaysOnTopBehaviorVersion,
        HaloSettings.currentAlwaysOnTopBehaviorVersion,
        "legacy settings should record the always-on-top behavior version"
    )
}

func testSettingsPreservesExplicitAlwaysOnTopOffAfterMigrationVersion() {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-current-topmost-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let url = root.appendingPathComponent("settings.json")
    let store = SettingsStore(settingsURL: url)
    let settings = HaloSettings(alwaysOnTop: false)

    store.save(settings)
    let loaded = store.load()

    expect(loaded.alwaysOnTop, false, "current settings should preserve an explicit alwaysOnTop off choice")
    expect(
        loaded.alwaysOnTopBehaviorVersion,
        HaloSettings.currentAlwaysOnTopBehaviorVersion,
        "current settings should persist the always-on-top behavior version"
    )
}

func testSettingsDefaultsFocusedAgentToCodexWhenMissing() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-focus-legacy-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let url = root.appendingPathComponent("settings.json")
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    try """
    {
      "acknowledged" : {},
      "alwaysOnTop" : true,
      "alwaysOnTopBehaviorVersion" : 1,
      "hasPosition" : false,
      "installedAt" : "2026-06-13T02:00:00Z",
      "left" : 0,
      "paused" : false,
      "top" : 0
    }
    """.data(using: .utf8)!.write(to: url)

    let loaded = SettingsStore(settingsURL: url).load()

    expect(loaded.focusedAgent, .codex, "legacy settings should default focus to Codex")
}

func testAcknowledgedErrorVisibilityUsesLatestErrorTime() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let earlier = now.addingTimeInterval(-60)
    let later = now.addingTimeInterval(60)
    let settings = HaloSettings(installedAt: now, acknowledgedErrorAt: earlier)

    expect(settings.shouldShowError(eventAt: now), true, "newer error should show")
    expect(settings.acknowledgingError(at: now).shouldShowError(eventAt: earlier), false, "older error should hide")
    expect(settings.acknowledgingError(at: now).shouldShowError(eventAt: later), true, "future error should show")
}

func testWorkingVisibilityLiveCallOutputAndInitialTail() {
    let formatter = ISO8601DateFormatter()
    let now = formatter.date(from: "2026-06-13T02:00:00Z")!

    var live = SessionReducer(filePath: "/tmp/live.jsonl", now: now, liveTracking: true)
    live.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#, now: now)
    live.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:01Z","type":"response_item","payload":{"type":"function_call","name":"shell_command"}}"#, now: now.addingTimeInterval(1))
    live.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:02Z","type":"response_item","payload":{"type":"function_call_output"}}"#, now: now.addingTimeInterval(2))
    live.applyWorkingVisibility(now: now.addingTimeInterval(3.7))
    expect(live.snapshot.state, .working, "live output should remain working before 1.8s expires")
    live.applyWorkingVisibility(now: now.addingTimeInterval(3.9))
    expect(live.snapshot.state, .thinking, "live output should return thinking after 1.8s expires")

    var initial = SessionReducer(filePath: "/tmp/initial.jsonl", now: now, liveTracking: false)
    initial.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#, now: now)
    initial.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:01Z","type":"response_item","payload":{"type":"function_call","name":"shell_command"}}"#, now: now.addingTimeInterval(1))
    initial.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:02Z","type":"response_item","payload":{"type":"function_call_output"}}"#, now: now.addingTimeInterval(2))
    expect(initial.snapshot.state, .thinking, "initial tail output should not fake working")
}

func testSessionReducerCapturesCurrentCodexTurnDetailsAndRateLimitAvailability() {
    var reducer = SessionReducer(filePath: "/tmp/codex-session-details.jsonl")
    reducer.consume(jsonLine: #"{"type":"session_meta","payload":{"id":"codex-details","cwd":"/Users/test/Projects/AgentHalo","title":"  Resolve Usage details  "}}"#)
    reducer.consume(jsonLine: #"{"type":"turn_context","payload":{"model":"gpt-5.5"}}"#)
    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":38000,"output_tokens":1200},"last_token_usage":{"input_tokens":2000,"output_tokens":200},"model_context_window":100000}}}"#)

    expect(reducer.snapshot.projectName, "AgentHalo", "Codex detail project")
    expect(reducer.snapshot.sessionTitle, "Resolve Usage details", "Codex detail session title")
    expect(reducer.snapshot.modelName, "gpt-5.5", "Codex detail model")
    expect(reducer.snapshot.inputTokens, 2_000, "first observed turn should use last input usage")
    expect(reducer.snapshot.outputTokens, 200, "first observed turn should use last output usage")
    expect(reducer.snapshot.hasRateLimits, false, "third-party Codex should have no rate limits")
    expect(reducer.snapshot.contextUsedPercent, 2, "Codex context should come from the current session")

    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":40000,"output_tokens":1500},"last_token_usage":{"input_tokens":4000,"output_tokens":500}},"rate_limits":{"primary":{},"secondary":{}}}}"#)
    expect(reducer.snapshot.inputTokens, 4_000, "current turn input should grow from its inferred baseline")
    expect(reducer.snapshot.outputTokens, 500, "current turn output should grow from its inferred baseline")
    expect(reducer.snapshot.hasRateLimits, true, "subscription Codex should report rate limits")

    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"task_complete"}}"#)
    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"task_started"}}"#)
    expect(reducer.snapshot.inputTokens == nil, "new turn should hide input tokens until usage arrives")
    expect(reducer.snapshot.outputTokens == nil, "new turn should hide output tokens until usage arrives")
    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":40500,"output_tokens":1580},"last_token_usage":{"input_tokens":500,"output_tokens":80}}}}"#)
    expect(reducer.snapshot.inputTokens, 500, "later turn input should subtract the known baseline")
    expect(reducer.snapshot.outputTokens, 80, "later turn output should subtract the known baseline")
}

func testSessionReducerFallsBackToLastTokenUsageWithoutTotals() {
    var reducer = SessionReducer(filePath: "/tmp/codex-last-token-details.jsonl")
    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":90,"output_tokens":12},"model_context_window":1000}}}"#)

    expect(reducer.snapshot.inputTokens, 90, "last input usage should work without cumulative totals")
    expect(reducer.snapshot.outputTokens, 12, "last output usage should work without cumulative totals")
    expect(reducer.snapshot.contextUsedPercent, 9, "last input usage should continue driving context")
}

func testSessionReducerCapturesOnlyExplicitCodexSessionTitles() {
    var legacyTitle = SessionReducer(filePath: "/tmp/codex-legacy-title.jsonl")
    legacyTitle.consume(
        jsonLine: #"{"type":"session_meta","payload":{"id":"legacy","cwd":"/tmp/Project","title":"  ","session_title":"  Legacy title  "}}"#
    )
    expect(legacyTitle.snapshot.sessionTitle, "Legacy title", "session_title should fill blank title")

    var preferredTitle = SessionReducer(filePath: "/tmp/codex-preferred-title.jsonl")
    preferredTitle.consume(
        jsonLine: #"{"type":"session_meta","payload":{"id":"preferred","cwd":"/tmp/Project","title":"Current title","session_title":"Legacy title"}}"#
    )
    expect(preferredTitle.snapshot.sessionTitle, "Current title", "title should precede session_title")

    var missingTitle = SessionReducer(filePath: "/tmp/codex-missing-title.jsonl")
    missingTitle.consume(
        jsonLine: #"{"type":"session_meta","payload":{"id":"thread-must-not-fallback","cwd":"/tmp/Project"}}"#
    )
    expect(missingTitle.snapshot.projectName, "Project", "missing title still preserves project")
    expect(missingTitle.snapshot.sessionTitle == nil, "missing title must not fall back to project or thread")
}

func testCodexSessionTitleReaderUsesLatestValidTitle() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-title-index-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }

    let indexURL = root.appendingPathComponent("session_index.jsonl")
    let records = """
    {"id":"thread-a","thread_name":"  First title  ","updated_at":"2026-07-21T01:00:00Z"}
    not-json
    {"id":"thread-empty","thread_name":"   ","updated_at":"2026-07-21T01:01:00Z"}
    {"id":"thread-a","thread_name":"Renamed title","updated_at":"2026-07-21T01:02:00Z"}

    """
    try Data(records.utf8).write(to: indexURL)

    var reader = CodexSessionTitleReader(indexURL: indexURL)
    let titles = reader.read()

    expect(titles["thread-a"], "Renamed title", "latest valid Codex title should win")
    expect(titles["thread-empty"] == nil, "blank Codex titles should be ignored")
    expect(titles.count, 1, "malformed title records should be ignored independently")
}

func testCodexSessionMonitorPrefersIndexTitleAndKeepsMetadataFallback() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-title-monitor-\(UUID().uuidString)", isDirectory: true)
    let sessionsRoot = root.appendingPathComponent("sessions", isDirectory: true)
    try FileManager.default.createDirectory(at: sessionsRoot, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }

    let indexedID = "019f841a-336f-79e3-8f28-dab1e7c94958"
    let fallbackID = "019f841a-7d2d-7403-a6b9-24f13051c36e"
    let indexedSession = sessionsRoot.appendingPathComponent("rollout-\(indexedID).jsonl")
    let fallbackSession = sessionsRoot.appendingPathComponent("rollout-\(fallbackID).jsonl")
    try Data("{\"type\":\"session_meta\",\"payload\":{\"id\":\"\(indexedID)\",\"title\":\"Old metadata title\"}}\n".utf8)
        .write(to: indexedSession)
    try Data("{\"type\":\"session_meta\",\"payload\":{\"id\":\"\(fallbackID)\",\"title\":\"Metadata fallback\"}}\n".utf8)
        .write(to: fallbackSession)

    let indexURL = root.appendingPathComponent("session_index.jsonl")
    try Data("{\"id\":\"\(indexedID)\",\"thread_name\":\"Codex sidebar title\"}\n".utf8)
        .write(to: indexURL)
    let monitor = CodexSessionMonitor(
        sessionsRoot: sessionsRoot,
        sessionTitleReader: CodexSessionTitleReader(indexURL: indexURL)
    )

    _ = monitor.refresh()
    let sessions = Dictionary(uniqueKeysWithValues: monitor.snapshots().map { ($0.threadId, $0) })
    expect(sessions[indexedID]?.sessionTitle, "Codex sidebar title", "index title should be authoritative")
    expect(sessions[fallbackID]?.sessionTitle, "Metadata fallback", "metadata title should remain the fallback")
}

func testToolFailedDoesNotBecomeFatalError() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    var reducer = SessionReducer(filePath: "/tmp/tool-failed.jsonl", now: now, liveTracking: true)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#, now: now)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-13T02:00:01Z","type":"event_msg","payload":{"type":"tool_failed"}}"#, now: now.addingTimeInterval(1))
    expect(reducer.snapshot.state, .thinking, "tool_failed should keep active thinking state")
    expect(reducer.snapshot.active, true, "tool_failed should not deactivate session")
}

extension FileHandle {
    func withClose(_ body: (FileHandle) throws -> Void) rethrows {
        defer { try? close() }
        try body(self)
    }
}

func testMonitorHandlesPendingLinesAndTruncation() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-monitor-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let sessions = root.appendingPathComponent("sessions", isDirectory: true)
    try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
    let file = sessions.appendingPathComponent("session-\(UUID().uuidString).jsonl")
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    try Data(#"{"timestamp":"2026-06-13T02:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#.utf8).write(to: file)

    let monitor = CodexSessionMonitor(sessionsRoot: sessions)
    _ = monitor.refresh(now: now)
    expect(monitor.snapshots().first?.state == .idle, "partial line should wait for newline")

    func append(_ data: Data) throws {
        try FileHandle(forWritingTo: file).withClose {
            try $0.seekToEnd()
            try $0.write(contentsOf: data)
        }
    }

    try append(Data("\n".utf8))
    _ = monitor.refresh(now: now.addingTimeInterval(1))
    expect(monitor.snapshots().first?.state == .thinking, "completed pending line should parse")
    expect(monitor.snapshots().first?.agent, .codex, "Codex monitor snapshots should carry Codex agent")

    let unicodeEvent = #"{"timestamp":"2026-06-13T02:00:02Z","type":"event_msg","payload":{"type":"task_complete","message":"你"}}"#
    let unicodeStart = unicodeEvent.range(of: "你")!.lowerBound
    let unicodeByteOffset = unicodeEvent.utf8.distance(from: unicodeEvent.utf8.startIndex, to: unicodeStart)
    let unicodeBytes = Data(unicodeEvent.utf8)
    try append(Data(unicodeBytes.prefix(unicodeByteOffset + 1)))
    _ = monitor.refresh(now: now.addingTimeInterval(2))
    expect(monitor.snapshots().first?.state == .thinking, "partial UTF-8 character should wait for remaining bytes")

    try append(Data(unicodeBytes.dropFirst(unicodeByteOffset + 1)) + Data([0x0A]))
    _ = monitor.refresh(now: now.addingTimeInterval(3))
    expect(monitor.snapshots().first?.state == .done, "completed UTF-8 event should parse after the remaining bytes arrive")
    expect(monitor.refresh(now: now.addingTimeInterval(3.5)), false, "completed UTF-8 event should not be processed again")

    let nextEvent = Data(#"{"timestamp":"2026-06-13T02:00:04Z","type":"event_msg","payload":{"type":"task_started"}}"#.utf8)
    try append(Data([0xFF, 0x0A]) + nextEvent + Data([0x0A]))
    _ = monitor.refresh(now: now.addingTimeInterval(4))
    expect(monitor.snapshots().first?.state == .thinking, "a malformed UTF-8 line should not block the following event")

    try Data(#"{"timestamp":"2026-06-13T02:00:05Z","type":"event_msg","payload":{"type":"task_complete"}}"#.utf8).write(to: file)
    _ = monitor.refresh(now: now.addingTimeInterval(5))
    expect(monitor.snapshots().first?.state == .idle, "truncation should reset the reducer and pending bytes")
}

func testAggregatorHidesAcknowledgedErrorsAndShowsStandbyInput() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let error = SessionSnapshot(
        threadId: "error",
        projectName: "Codex",
        workingDirectory: "",
        state: .error,
        action: "Interrupted",
        lastEventAt: now,
        completedAt: nil,
        active: false
    )
    let settings = HaloSettings(installedAt: now.addingTimeInterval(-600), acknowledgedErrorAt: now.addingTimeInterval(1))
    let aggregate = SessionAggregator.aggregate(snapshots: [error], settings: settings, now: now)
    expect(aggregate.state, .idle, "acknowledged error should hide")
    expect(aggregate.label, "OFFLINE", "hidden error should return offline")
}

func testFailureClassification() {
    expect(CodexFailureReader.classify("authentication failed for account"), "认证已失效", "auth failure")
    expect(CodexFailureReader.classify("rate_limit_reached"), "额度已用尽", "rate limit")
    expect(CodexFailureReader.classify("server overloaded"), "服务暂时不可用", "service")
    expect(CodexFailureReader.classify("connect timeout"), "连接 Codex 失败", "network")
    expect(CodexFailureReader.classify("plain info") == nil, "non failure")
}

func testRateLimitReaderFindsNewestTailRateLimit() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-rate-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let sessions = root.appendingPathComponent("sessions", isDirectory: true)
    try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
    let file = sessions.appendingPathComponent("a.jsonl")
    let line = #"{"type":"event_msg","payload":{"info":{"rate_limits":{"primary":{"used_percent":25},"secondary":{"used_percent":80}}}}}"#
    try Data((line + "\n").utf8).write(to: file)

    let snapshot = RateLimitReader(roots: [sessions]).read()
    expect(snapshot, RateLimitSnapshot(primaryUsedPercent: 25, secondaryUsedPercent: 80), "rate limit")
}

func testRateLimitReaderFindsContextUsageAndResetTimes() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-usage-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let sessions = root.appendingPathComponent("sessions", isDirectory: true)
    try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
    let file = sessions.appendingPathComponent("usage.jsonl")
    let line = #"{"type":"event_msg","payload":{"info":{"rate_limits":{"primary":{"used_percent":47,"resets_at":1781765880},"secondary":{"used_percent":76,"resets_at":1781938560}},"last_token_usage":{"input_tokens":202600},"model_context_window":258400}}}"#
    try Data((line + "\n").utf8).write(to: file)

    let snapshot = RateLimitReader(roots: [sessions]).read()
    expect(snapshot?.primaryResetAt, Date(timeIntervalSince1970: 1_781_765_880), "primary reset time")
    expect(snapshot?.secondaryResetAt, Date(timeIntervalSince1970: 1_781_938_560), "secondary reset time")
    expectAlmost(snapshot?.contextUsedPercent ?? 0, 78.405, tolerance: 0.01, "context usage")
}

func testRateLimitReaderCombinesSplitQuotaAndContextSnapshots() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-split-rate-\(UUID().uuidString)", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let sessions = root.appendingPathComponent("sessions", isDirectory: true)
    try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
    let file = sessions.appendingPathComponent("split.jsonl")
    let quota = #"{"type":"event_msg","payload":{"info":{"rate_limits":{"primary":{"used_percent":25,"resets_at":1781765880},"secondary":{"used_percent":40,"resets_at":1781938560}}}}}"#
    let context = #"{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":50},"model_context_window":100}}}"#
    try Data("\(quota)\n\(context)\n".utf8).write(to: file)

    let snapshot = RateLimitReader(roots: [sessions]).read()
    expect(snapshot?.primaryUsedPercent, 25, "split snapshot primary quota")
    expect(snapshot?.secondaryUsedPercent, 40, "split snapshot secondary quota")
    expect(snapshot?.contextUsedPercent, 50, "split snapshot context usage")
}

func testRateLimitReaderReadsExplicitMonthlyQuota() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"rate_limits":{"monthly":{"used_percent":37,"resets_at":4102444800}},"last_token_usage":{"input_tokens":25},"model_context_window":100}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, true, "monthly quota should be detected")
    expect(snapshot?.hasPrimary, false, "monthly quota stays separate from Plus primary")
    expect(snapshot?.hasSecondary, false, "monthly quota stays separate from Plus secondary")
    expect(snapshot?.monthlyUsedPercent, 37, "monthly used percent")
    expect(snapshot?.primaryUsedPercent, 0, "Plus primary should be zero when absent")
}

func testRateLimitReaderReadsFreeCreditsRemainingAsMonthlyQuota() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"rate_limits":{"credits":{"remaining_percent":95,"resets_at":1785628800}},"last_token_usage":{"input_tokens":25},"model_context_window":100}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, true, "free credits should be detected as monthly quota")
    expect(snapshot?.monthlyUsedPercent, 5, "credits remaining percent should convert to used percent")
    expect(snapshot?.monthlyResetAt, Date(timeIntervalSince1970: 1_785_628_800), "credits monthly reset")
    expect(snapshot?.hasPrimary, false, "credits monthly quota should not fill Plus primary")
    expect(snapshot?.hasSecondary, false, "credits monthly quota should not fill Plus secondary")
}

func testRateLimitReaderKeepsNewestCompletePlusBucketsOverOlderMonthlyUsage() {
    let reader = RateLimitReader()
    let plus = #"{"payload":{"info":{"last_token_usage":{"input_tokens":25},"model_context_window":100},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":100,"window_minutes":300,"resets_at":4102441200},"secondary":{"used_percent":60,"window_minutes":10080,"resets_at":4102444800},"credits":{"balance":"0","has_credits":false,"unlimited":false},"individual_limit":null,"plan_type":"plus","rate_limit_reached_type":null}}}"#
    let monthly = #"{"payload":{"info":{"rate_limits":{"credits":{"remaining_percent":82,"resets_at":1785628800}}}}}"#
    let snapshot = reader.parseForTest(lines: [plus, monthly])
    expect(snapshot?.hasMonthly ?? false, false, "older monthly usage should not override a complete newest Plus snapshot")
    expect(snapshot?.monthlyUsedPercent, nil, "Plus compatibility should require explicit monthly data in the same snapshot")
    expect(snapshot?.hasMonthlyPlan ?? false, false, "Plus compatibility should not force monthly layout")
    expect(snapshot?.hasPrimary, true, "newest Plus primary should render as 5-hour quota")
    expect(snapshot?.primaryUsedPercent, 100, "newest Plus primary used percent")
    expect(snapshot?.hasSecondary, true, "newest Plus secondary should render as weekly quota")
    expect(snapshot?.secondaryUsedPercent, 60, "newest Plus secondary used percent")
}

func testRateLimitReaderLeavesResetOnlyMonthlyQuotaPending() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"rate_limits":{"monthly":{"resets_at":1785628800}},"last_token_usage":{"input_tokens":25},"model_context_window":100}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, false, "reset-only monthly bucket should not fabricate usage")
    expect(snapshot?.monthlyUsedPercent, nil, "reset-only monthly bucket should wait for usage data")
    expect(snapshot?.hasMonthlyPlan ?? false, true, "reset-only monthly bucket should still mark monthly layout")
    expect(snapshot?.monthlyResetAt, Date(timeIntervalSince1970: 1_785_628_800), "reset-only monthly bucket should keep reset time")
}

func testRateLimitReaderReadsLongWindowPrimaryAsMonthly() {
    let reader = RateLimitReader()
    // A solo primary with a 30-day window is the free-plan shape — the reader
    // should reclassify it as monthly so the panel shows "月额度" not "5 小时额度".
    let line = #"{"payload":{"info":{"rate_limits":{"primary":{"used_percent":41,"window_minutes":43200,"resets_at":4102444800}}}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, true, "long-window primary becomes monthly")
    expect(snapshot?.monthlyUsedPercent, 41, "long-window primary used as monthly")
    expect(snapshot?.hasPrimary, false, "long-window primary should not also fill Plus primary")
}

func testRateLimitReaderDoesNotTreatSecondaryBucketAsMonthly() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"last_token_usage":{"input_tokens":25},"model_context_window":100},"rate_limits":{"primary":{"used_percent":2,"window_minutes":300,"resets_at":4102441200},"secondary":{"used_percent":45,"window_minutes":10080,"resets_at":4102444800},"credits":null,"plan_type":"free"}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, false, "secondary bucket should not become monthly quota")
    expect(snapshot?.monthlyUsedPercent, nil, "monthly used percent should require an explicit monthly bucket")
    expect(snapshot?.hasMonthlyPlan ?? false, true, "free-plan marker should keep the UI on single monthly quota")
    expect(snapshot?.hasPrimary, false, "free-plan primary bucket should not render as 5-hour quota")
    expect(snapshot?.hasSecondary, false, "free-plan secondary bucket should not render as weekly quota")
}

func testRateLimitReaderTreatsNullCreditsCodexCompatibilityAsPlus() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"last_token_usage":{"input_tokens":25},"model_context_window":100},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":2,"window_minutes":300,"resets_at":4102441200},"secondary":{"used_percent":45,"window_minutes":10080,"resets_at":4102444800},"credits":null,"individual_limit":null,"plan_type":"plus","rate_limit_reached_type":null}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, false, "null credits compatibility should not become monthly")
    expect(snapshot?.monthlyUsedPercent, nil, "monthly used percent should require explicit monthly data")
    expect(snapshot?.hasMonthlyPlan ?? false, false, "null credits compatibility should keep the Plus two-row quota")
    expect(snapshot?.hasPrimary, true, "null credits primary should render as 5-hour quota")
    expect(snapshot?.hasSecondary, true, "null credits secondary should render as weekly quota")
}

func testRateLimitReaderTreatsEmptyCodexCreditsCompatibilityAsPlus() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"last_token_usage":{"input_tokens":25},"model_context_window":100},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":100,"window_minutes":300,"resets_at":4102441200},"secondary":{"used_percent":60,"window_minutes":10080,"resets_at":4102444800},"credits":{"balance":"0","has_credits":false,"unlimited":false},"individual_limit":null,"plan_type":"plus","rate_limit_reached_type":null}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, false, "empty Codex credits should not fabricate monthly usage")
    expect(snapshot?.monthlyUsedPercent, nil, "empty Codex credits should require explicit monthly data")
    expect(snapshot?.hasMonthlyPlan ?? false, false, "empty Codex credits compatibility should keep the Plus two-row quota")
    expect(snapshot?.hasPrimary, true, "empty Codex credits primary should render as 5-hour quota")
    expect(snapshot?.hasSecondary, true, "empty Codex credits secondary should render as weekly quota")
}

func testRateLimitReaderDoesNotTreatEmptyLegacyCreditsSecondaryAsMonthly() {
    let reader = RateLimitReader()
    let line = #"{"payload":{"info":{"last_token_usage":{"input_tokens":25},"model_context_window":100},"rate_limits":{"primary":{"used_percent":7,"window_minutes":300,"resets_at":4102441200},"secondary":{"used_percent":55,"window_minutes":10080,"resets_at":4102444800},"credits":{"has_credits":false,"unlimited":false,"balance":null},"plan_type":null}}}"#
    let snapshot = reader.parseForTest(lines: [line])
    expect(snapshot?.hasMonthly ?? false, false, "empty legacy credits should not make secondary monthly")
    expect(snapshot?.monthlyUsedPercent, nil, "monthly used percent should require explicit monthly data")
    expect(snapshot?.hasPrimary, true, "primary bucket should stay available")
    expect(snapshot?.hasSecondary, true, "secondary bucket should stay available")
}

func testRateLimitReaderDoesNotReturnEarlyOnContextOnlySnapshot() {
    let reader = RateLimitReader()
    // Newest-first: a context-only snapshot followed by the real rate-limit
    // snapshot. The reader must keep scanning past the context-only line
    // instead of bailing with nil quota.
    let contextOnly = #"{"payload":{"info":{"last_token_usage":{"input_tokens":50},"model_context_window":100}}}"#
    let quota = #"{"payload":{"info":{"rate_limits":{"primary":{"used_percent":25,"resets_at":1781765880},"secondary":{"used_percent":40,"resets_at":1781938560}}}}}"#
    let snapshot = reader.parseForTest(lines: [contextOnly, quota])
    expect(snapshot?.hasPrimary, true, "Plus primary should be found after context-only snapshot")
    expect(snapshot?.primaryUsedPercent, 25, "Plus primary used percent")
    expect(snapshot?.secondaryUsedPercent, 40, "Plus secondary used percent")
    expect(snapshot?.contextUsedPercent, 50, "context usage carried over from earlier snapshot")
}

func testCodexRealtimeActivityReaderDetectsAnswerStreaming() {
    let reader = CodexRealtimeActivityReader()
    let delta = #"SSE event: {"type":"response.output_text.delta","delta":"hello"}"#
    let activity = reader.findActive(in: [delta])

    expect(activity?.state, .working, "answer text delta state")
    expect(activity?.action, "Writing answer", "answer text delta action")
    // Streaming text used to flip the ring into the green "done" presentation
    // mid-answer (via `answerStreaming = true`). PR #10 keeps it blue working
    // so users can't confuse mid-stream with completion.
    expect(activity?.answerStreaming, false, "answer text delta should stay blue working, not flip to done")
}

func testCodexRealtimeActivityReaderDetectsContextCompactionStream() {
    let reader = CodexRealtimeActivityReader()
    let delta = #"SSE event: {"type":"response.output_text.delta","delta":"Compressing context"}"#
    let activity = reader.findActive(in: [delta])

    expect(activity?.state, .working, "context compaction state")
    expect(activity?.action, "Compressing context", "context compaction action")
    expect(activity?.answerStreaming, false, "compaction stream should not mark answer streaming")
}

func testCodexRealtimeActivityReaderDetectsArgumentStream() {
    let reader = CodexRealtimeActivityReader()
    let argsDelta = #"SSE event: {"type":"response.function_call_arguments.delta","item_id":"fc-1","delta":"{\"cmd\":\"git"}"#
    let activity = reader.findActive(in: [argsDelta])

    expect(activity?.state, .working, "argument stream keeps Codex active")
    expect(activity?.action, "Preparing command", "argument stream action")
}

func testCodexRealtimeActivityReaderEscalatedArgumentsAttention() {
    let reader = CodexRealtimeActivityReader()
    let escalated = #"SSE event: {"type":"response.function_call_arguments.delta","item_id":"fc-2","delta":"require_escalated sandbox_permissions justification"}"#
    let activity = reader.findActive(in: [escalated])

    expect(activity?.state, .attention, "escalated argument stream state")
    expect(activity?.action, "Needs you", "escalated argument stream action")
}

func testCodexRealtimeActivityReaderDetectsRequestUserInput() {
    let reader = CodexRealtimeActivityReader()
    let request = #"SSE event: {"type":"response.output_item.added","item":{"id":"approval-1","type":"custom_tool_call","name":"request_user_input"}}"#
    let activity = reader.findActive(in: [request])

    expect(activity?.state, .attention, "request_user_input state")
    expect(activity?.action, "Needs you", "request_user_input action")
    expect(activity?.answerStreaming, false, "request_user_input should not mark answer streaming")
}

func testCodexRealtimeActivityReaderClearsAnswerStreamingWhenDone() {
    let reader = CodexRealtimeActivityReader()
    let delta = #"SSE event: {"type":"response.output_text.delta","delta":"hello"}"#
    let textDone = #"SSE event: {"type":"response.output_text.done"}"#
    let completed = #"SSE event: {"type":"response.completed","response":{"id":"resp-test"}}"#

    expect(reader.findActive(in: [textDone, delta]) == nil, "text done should clear realtime working")
    expect(reader.findActive(in: [completed, delta]) == nil, "response completed should clear realtime working")
}

func testSessionReducerMapsCustomToolRequestUserInputToAttention() {
    var reducer = SessionReducer(filePath: "/tmp/custom-tool-request-user-input.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:01Z","type":"response_item","payload":{"type":"custom_tool_call","name":"request_user_input"}}"#)

    expect(reducer.snapshot.state, .attention, "custom_tool_call request_user_input state")
    expect(reducer.snapshot.action, "Needs you", "custom_tool_call request_user_input action")
    expect(reducer.snapshot.active, "custom_tool_call request_user_input should keep session active")
}

func testSessionReducerMapsEscalatedExecCommandToAttention() {
    var reducer = SessionReducer(filePath: "/tmp/escalated-exec-command.jsonl")
    let arguments = #"{"cmd":"swift build","sandbox_permissions":"require_escalated","justification":"Allow build?"}"#

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:01Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"\#(arguments.replacingOccurrences(of: "\"", with: "\\\""))"}}"#)

    expect(reducer.snapshot.state, .attention, "escalated exec_command state")
    expect(reducer.snapshot.action, "Needs you", "escalated exec_command action")
    expect(reducer.snapshot.active, "escalated exec_command should keep session active")
}

func testSessionReducerMapsApprovalNamedToolToAttention() {
    var reducer = SessionReducer(filePath: "/tmp/approval-tool.jsonl")
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    // PR #10: tool names containing approval/permission/request_user/needs_input
    // are attention signals even without a sandbox_permissions escalation.
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:01Z","type":"response_item","payload":{"type":"function_call","name":"request_permission","arguments":"{}"}}"#)

    expect(reducer.snapshot.state, .attention, "approval-named tool state")
    expect(reducer.snapshot.action, "Needs you", "approval-named tool action")
    expect(reducer.snapshot.active, "approval-named tool should keep session active")
}

func testSessionReducerMapsEscalatedArgumentsStringToAttention() {
    var reducer = SessionReducer(filePath: "/tmp/escalated-args.jsonl")
    let arguments = #"{"sandbox_permissions":"require_escalated","justification":"Allow build?"}"#
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    // An unrecognized tool name whose arguments carry the escalation markers
    // should still surface as attention via the argument-string fallback.
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-19T01:00:01Z","type":"response_item","payload":{"type":"function_call","name":"custom_shell","arguments":"\#(arguments.replacingOccurrences(of: "\"", with: "\\\""))"}}"#)

    expect(reducer.snapshot.state, .attention, "escalated arguments state")
    expect(reducer.snapshot.action, "Needs you", "escalated arguments action")
}

func testAggregatorInjectsUnacknowledgedCodexFailureWhenIdle() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let failure = CodexFailure(detail: "认证已失效", eventAt: now)
    let aggregate = SessionAggregator.aggregate(
        snapshots: [],
        settings: HaloSettings(installedAt: now.addingTimeInterval(-600)),
        recentFailure: failure,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )

    expect(aggregate.state, .error, "recent failure should surface as error")
    expect(aggregate.label, "INTERRUPTED", "recent failure label")
    expect(aggregate.detail, "认证已失效", "recent failure detail")
    expect(aggregate.sessions.map(\.threadId), ["codex-app"], "synthetic failure session")

    let acknowledged = SessionAggregator.aggregate(
        snapshots: [],
        settings: HaloSettings(installedAt: now.addingTimeInterval(-600), acknowledgedErrorAt: now),
        recentFailure: failure,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )
    expect(acknowledged.state, .idle, "acknowledged failure should hide")
}

func testAggregatorLimitsCodexCompletionToFiveMinutesAndRequiresRunningApp() {
    let now = ISO8601DateFormatter().date(from: "2026-06-13T02:00:00Z")!
    let completion = SessionSnapshot(
        threadId: "codex-done",
        projectName: "CodexProject",
        workingDirectory: "",
        state: .done,
        action: "Complete",
        lastEventAt: now,
        completedAt: now,
        active: false,
        agent: .codex
    )

    let settings = HaloSettings(installedAt: now.addingTimeInterval(-600))
    let visible = SessionAggregator.aggregate(
        snapshots: [completion],
        settings: settings,
        codexRunning: true,
        focusedAgent: .codex,
        now: now.addingTimeInterval(299)
    )

    expect(visible.state, .done, "Codex completion should remain visible before five minutes")
    expect(visible.sessions.map(\.threadId), ["codex-done"], "visible completion should stay in sessions")

    let expired = SessionAggregator.aggregate(
        snapshots: [completion],
        settings: settings,
        codexRunning: true,
        focusedAgent: .codex,
        now: now.addingTimeInterval(300)
    )
    expect(expired.state, .idle, "Codex completion should expire exactly at five minutes")
    expect(expired.sessions.isEmpty, "expired completion should be removed from sessions")

    let stopped = SessionAggregator.aggregate(
        snapshots: [completion],
        settings: settings,
        codexRunning: false,
        focusedAgent: .codex,
        now: now.addingTimeInterval(299)
    )
    expect(stopped.state, .idle, "completed session should hide immediately when Codex exits")
    expect(stopped.sessions.isEmpty, "stopped Codex should not retain completion sessions")

    let replacements: [SessionSnapshot] = [
        SessionSnapshot(
            threadId: "thinking-after-done",
            projectName: "CodexProject",
            workingDirectory: "",
            state: .thinking,
            action: "Planning",
            lastEventAt: now,
            completedAt: nil,
            active: true,
            agent: .codex
        ),
        SessionSnapshot(
            threadId: "working-after-done",
            projectName: "CodexProject",
            workingDirectory: "",
            state: .working,
            action: "Running command",
            lastEventAt: now,
            completedAt: nil,
            active: true,
            agent: .codex
        ),
        SessionSnapshot(
            threadId: "attention-after-done",
            projectName: "CodexProject",
            workingDirectory: "",
            state: .attention,
            action: "Waiting for your choice",
            lastEventAt: now,
            completedAt: nil,
            active: true,
            agent: .codex
        ),
        SessionSnapshot(
            threadId: "error-after-done",
            projectName: "CodexProject",
            workingDirectory: "",
            state: .error,
            action: "Interrupted",
            lastEventAt: now,
            completedAt: nil,
            active: false,
            agent: .codex
        )
    ]
    for replacement in replacements {
        let replaced = SessionAggregator.aggregate(
            snapshots: [completion, replacement],
            settings: settings,
            codexRunning: true,
            focusedAgent: .codex,
            now: now
        )
        expect(replaced.state, replacement.state,
               "new \(replacement.state) state should replace a recent completion")
    }

    let planAttention = SessionSnapshot(
        threadId: "plan-attention",
        projectName: "CodexPlan",
        workingDirectory: "",
        state: .attention,
        action: "Waiting for your choice",
        lastEventAt: now.addingTimeInterval(-301),
        completedAt: now.addingTimeInterval(-301),
        active: true,
        agent: .codex
    )
    let planAggregate = SessionAggregator.aggregate(
        snapshots: [planAttention],
        settings: settings,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )
    expect(planAggregate.state, .attention,
           "Plan Mode attention should not expire with normal completion")
}

func testStartupExecutablePathUsesAppBundleRoot() {
    let bundleURL = URL(fileURLWithPath: "/tmp/AgentHalo.app")
    let path = StartupLaunchAgent.executablePath(appBundleURL: bundleURL)
    expect(path, "/tmp/AgentHalo.app/Contents/MacOS/AgentHaloMac", "startup executable path")
}

func testPlanModePlainFinalAnswerDoesNotHoldAttentionAtTaskComplete() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75044.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T01:00:00Z","type":"event_msg","payload":{"type":"task_started","collaboration_mode_kind":"plan"}}"#)
    expect(reducer.snapshot.state, .thinking, "plan task_started state")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T01:00:01Z","type":"event_msg","payload":{"type":"agent_message","phase":"final_answer","content":[{"type":"output_text","text":"plain answer"}]}}"#)
    expect(reducer.snapshot.active, "plan agent_message keeps active")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T01:00:02Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .done, "plain plan final_answer -> done")
    expect(reducer.snapshot.action, "Complete", "plain plan final_answer action")
    expect(!reducer.snapshot.active, "plain plan final_answer should deactivate")
}

func testPlanModeProposedPlanFromTaskStartedHoldsAttentionAtTaskComplete() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75045.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T01:10:00Z","type":"event_msg","payload":{"type":"task_started","collaboration_mode_kind":"plan"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T01:10:01Z","type":"event_msg","payload":{"type":"agent_message","phase":"final_answer","content":[{"type":"output_text","text":"<proposed_plan>"}]}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T01:10:02Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .attention, "proposed plan task_complete -> attention")
    expect(reducer.snapshot.action, "Waiting for your choice", "proposed plan task_complete action")
    expect(reducer.snapshot.active, "proposed plan task_complete keeps active")
}

func testPlanModeFromTurnContextHoldsAttentionAtTaskComplete() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75045.jsonl")

    // turn_context 在 task_started 之前到达。
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:00:00Z","type":"turn_context","payload":{"collaboration_mode":{"mode":"plan"}}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:00:01Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:00:02Z","type":"response_item","payload":{"type":"message","phase":"final_answer","content":[{"type":"output_text","text":"<proposed_plan>"}]}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:00:03Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .attention, "turn_context plan -> attention at task_complete")
    expect(reducer.snapshot.action, "Waiting for your choice", "turn_context plan action")
}

func testPlanModeCompletedPlanItemHoldsAttentionAtTaskComplete() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75046.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:10:00Z","type":"event_msg","payload":{"type":"task_started","collaboration_mode_kind":"plan"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:10:01Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"Plan","text":"Plan body"}}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T02:10:02Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .attention, "completed plan item -> attention at task_complete")
    expect(reducer.snapshot.action, "Waiting for your choice", "completed plan item action")
}

func testNonPlanTaskCompleteStillTurnsGreen() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75046.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T03:00:00Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T03:00:01Z","type":"event_msg","payload":{"type":"agent_message","phase":"final_answer"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T03:00:02Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .done, "non-plan task_complete stays done")
    expect(reducer.snapshot.action, "Complete", "non-plan task_complete action")
    expect(!reducer.snapshot.active, "non-plan task_complete inactive")
}

func testPlanModeWithoutFinalAnswerStillTurnsGreen() {
    // Plan 模式但本轮没有产出 final_answer(被打断或仅做工具调用),
    // 视为普通完成,仍走 .done 绿色,避免假阳性等待。
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75047.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T04:00:00Z","type":"event_msg","payload":{"type":"task_started","collaboration_mode_kind":"plan"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T04:00:01Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .done, "plan w/o final_answer -> done")
}

func testPlanModeFlagResetsAcrossTurns() {
    // 第 1 轮 plan + proposed plan -> attention;
    // 第 2 轮普通 task -> 必须回到 .done,不应残留 plan 标志。
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75048.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T05:00:00Z","type":"event_msg","payload":{"type":"task_started","collaboration_mode_kind":"plan"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T05:00:01Z","type":"event_msg","payload":{"type":"agent_message","phase":"final_answer","content":[{"type":"output_text","text":"<proposed_plan>"}]}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T05:00:02Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .attention, "round 1 attention")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T05:00:10Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T05:00:11Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .done, "round 2 falls back to done")
}

func testPlanModeFlagResetsAfterFatalTurn() {
    var reducer = SessionReducer(filePath: "/tmp/session-019c6e27-e55b-73d1-87d8-4e01f1f75049.jsonl")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T06:00:00Z","type":"event_msg","payload":{"type":"task_started","collaboration_mode_kind":"plan"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T06:00:01Z","type":"event_msg","payload":{"type":"turn_failed"}}"#)
    expect(reducer.snapshot.state, .error, "plan fatal turn becomes error")

    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T06:00:10Z","type":"event_msg","payload":{"type":"task_started"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T06:00:11Z","type":"response_item","payload":{"type":"message","phase":"final_answer"}}"#)
    reducer.consume(jsonLine: #"{"timestamp":"2026-06-18T06:00:12Z","type":"event_msg","payload":{"type":"task_complete"}}"#)
    expect(reducer.snapshot.state, .done, "normal turn after plan fatal should not inherit plan mode")
    expect(!reducer.snapshot.active, "normal turn after plan fatal should deactivate")
}

func testDiagnosticsCreatesParentDirectoryForOutput() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-diagnostics-\(UUID().uuidString)", isDirectory: true)
    defer {
        try? FileManager.default.removeItem(at: root)
    }
    let output = root.appendingPathComponent("self-test.txt")
    try DiagnosticsOutput.write("PASS\n", to: output.path(percentEncoded: false))
    expect(FileManager.default.fileExists(atPath: output.path(percentEncoded: false)), "diagnostics output should create parent directory")
}

func testHaloMathMatchesProgramConstants() {
    expect(GeneratedHaloSpec.contractVersion, 2, "generated shared contract version")
    expect(GeneratedHaloSpec.releaseVersion, "0.16.5", "generated shared release version")
    expect(GeneratedHaloSpec.state(.attention).label, "NEEDS YOU", "generated state labels")
    expect(GeneratedHaloSpec.friendlyAction("apply_patch"), "Editing files", "generated action rules")
    expect(GeneratedHaloSpec.classifyFailure("server overloaded"), "failure.service_unavailable", "generated failure rules")
    expectAlmost(HaloMath.stateBreath(.thinking, time: 1.0), 1.0, tolerance: 0.08, "thinking bright plateau")
    expect(HaloMath.targetPowered(.done, time: 8.0) < 0.20, "done powered should dip close to dark")
    expect(HaloMath.transitionLight(from: 0.9, to: 0.0, progress: 0.99) < 0.01, "steady green transition should finish dark")
    expect(HaloMath.diagnosticBrightDuration(.thinking) < HaloMath.diagnosticBrightDuration(.working), "thinking bright duration shorter than working")
    expectAlmost(HaloMath.diagnosticGapSeparation(0), 40, tolerance: 0.001, "gap repulsion start")
    expectAlmost(HaloMath.diagnosticGapSeparation(1), 150, tolerance: 0.001, "gap repulsion end")
    expect(HaloMath.repulsionDurationFromOrbit(28) > HaloMath.repulsionDurationFromOrbit(80), "slow orbit uses longer repulsion")
}

func testLinearSRGBMixAvoidsGammaLerp() {
    let mixed = HaloMath.mixColor(
        HaloRGB(red: 226, green: 170, blue: 31),
        HaloRGB(red: 52, green: 158, blue: 199),
        amount: 0.5
    )
    expect(mixed.red > 150, "linear red midpoint should be brighter than gamma midpoint")
    expect(mixed.blue > 145, "linear blue midpoint should be brighter than gamma midpoint")
}

func testWindowsStyleVisualTransitionAndMaterial() {
    let from = HaloVisualModel.targetVisual(
        state: .thinking,
        time: 1.0,
        errorPresentation: .flashing,
        steadyDone: false
    )
    let to = HaloVisualModel.targetVisual(
        state: .working,
        time: 0.8,
        errorPresentation: .flashing,
        steadyDone: false
    )
    let dimmed = HaloVisualModel.transitionVisual(from: from, to: to, progress: 0.48)
    expect(dimmed.powered < 0.12, "transition should dim before power-up")
    expect(dimmed.coreWhite > min(from.coreWhite, to.coreWhite) && dimmed.coreWhite < max(from.coreWhite, to.coreWhite), "core white should transition as a scalar")

    let material = HaloVisualModel.materialSnapshot(color: to.color, visual: to, intensity: 1.0)
    expect(material.poweredCore.red > to.color.red, "powered core should move toward white")
    expect(material.glowAlphas[1] > material.glowAlphas[0], "middle glow should be brighter than outer glow")
    expect(material.whiteSparkAlpha > 180, "powered visual should retain a white center spark")
}

func testCompletionDoubleFlashMatchesWindowsCadence() {
    expect(HaloVisualModel.completionDoubleFlash(sinceState: 0.28) > 0.95, "first completion flash should peak early")
    expect(HaloVisualModel.completionDoubleFlash(sinceState: 0.92) > 0.80, "second completion flash should peak later")
    expect(HaloVisualModel.completionDoubleFlash(sinceState: 1.45) < 0.02, "completion flash should fade out")
}

func testAggregateFiltersInactiveAndTimedOutSessions() {
    let now = Date()
    let activeSnap = SessionSnapshot(
        threadId: "active-codex",
        projectName: "CodexActive",
        workingDirectory: "",
        state: .thinking,
        action: "Thinking",
        lastEventAt: now,
        completedAt: nil,
        active: true,
        agent: .codex
    )
    
    // 1. 测试正常状态下 activeSnap 在 10 分钟内应判定为活跃
    let freshAgg = SessionAggregator.aggregate(
        snapshots: [activeSnap],
        settings: HaloSettings(paused: false),
        recentFailure: nil,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )
    expect(freshAgg.state, .thinking, "fresh active session should show thinking")
    expect(freshAgg.sessions.count, 1, "should contain 1 session")

    // 2. 测试 10 分钟（600秒）超时过滤
    let timedOutSnap = SessionSnapshot(
        threadId: "timedout-codex",
        projectName: "CodexTimedOut",
        workingDirectory: "",
        state: .thinking,
        action: "Thinking",
        lastEventAt: now.addingTimeInterval(-601),
        completedAt: nil,
        active: true,
        agent: .codex
    )
    let timedOutAgg = SessionAggregator.aggregate(
        snapshots: [timedOutSnap],
        settings: HaloSettings(paused: false),
        recentFailure: nil,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )
    expect(timedOutAgg.state, .idle, "timed out active session should filter out to idle")
    expect(timedOutAgg.sessions.count, 0, "should filter out timed out session")

    // 2b. 测试 attention 状态（等待授权等）即使超过 10 分钟也不应该被超时过滤
    let timedOutAttentionSnap = SessionSnapshot(
        threadId: "timedout-attention-codex",
        projectName: "CodexTimedOutAttention",
        workingDirectory: "",
        state: .attention,
        action: "Needs you",
        lastEventAt: now.addingTimeInterval(-601),
        completedAt: nil,
        active: true,
        agent: .codex
    )
    let timedOutAttentionAgg = SessionAggregator.aggregate(
        snapshots: [timedOutAttentionSnap],
        settings: HaloSettings(paused: false),
        recentFailure: nil,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )
    expect(timedOutAttentionAgg.state, .attention, "timed out attention session should NOT filter out")
    expect(timedOutAttentionAgg.sessions.count, 1, "should preserve timed out attention session")

    // 3. 测试 codexRunning == false 时的过滤
    let notRunningAgg = SessionAggregator.aggregate(
        snapshots: [activeSnap],
        settings: HaloSettings(paused: false),
        recentFailure: nil,
        codexRunning: false,
        focusedAgent: .codex,
        now: now
    )
    expect(notRunningAgg.state, .idle, "not running codex should filter out active session to idle")
    expect(notRunningAgg.sessions.count, 0, "should filter out when codex is not running")

    // 4. 测试当活跃会话过滤掉时，能够正确触发 recentFailure
    let failure = CodexFailure(detail: "额度已用尽", eventAt: now.addingTimeInterval(-10))
    let failureAgg = SessionAggregator.aggregate(
        snapshots: [timedOutSnap],
        settings: HaloSettings(paused: false, installedAt: now.addingTimeInterval(-600)),
        recentFailure: failure,
        codexRunning: true,
        focusedAgent: .codex,
        now: now
    )
    expect(failureAgg.state, .error, "should surface synthetic error when active session is filtered out")
    expect(failureAgg.detail, "额度已用尽", "should show correct failure detail")
}

func expectAlmost(_ actual: Double, _ expected: Double, tolerance: Double, _ message: String) {
    if abs(actual - expected) > tolerance {
        fatalError("\(message): expected \(expected) +/- \(tolerance), got \(actual)")
    }
}

func testCodexOnlyAgentModelAndLegacyFocusMigration() throws {
    expect(AgentKind.allCases, [.codex], "only Codex should be available")
    let legacy = Data(
        #"{"focusedAgent":"claudeCode","alwaysOnTop":true,"installedAt":"2026-01-01T00:00:00Z"}"#.utf8
    )
    let decoder = JSONDecoder()
    decoder.dateDecodingStrategy = .iso8601
    let settings = try decoder.decode(HaloSettings.self, from: legacy)
    expect(settings.focusedAgent, .codex, "legacy focus should migrate to Codex")
}

func runCoreChecks() async throws {
    testReducesPlanningWorkingAttentionErrorAndCompleteEvents()
    testAggregatePrioritizesActionableSessions()
    testAggregateRemovesSupersededSessionErrors()
    testAcknowledgingCompletedSessionsStoresLatestVisibleCompletionOnly()
    testSettingsPersistFormalFieldsAndNormalizePaused()
    try testSettingsDefaultsPreferredDisplayPlacementForLegacyFiles()
    try testSettingsPersistPreferredDisplayPlacement()
    testSettingsUsesDefaultHaloSizeForLegacyFilesAndClampsInvalidSizes()
    testSettingsMigratesLegacyAlwaysOnTopOffToDefaultOn()
    testSettingsPreservesExplicitAlwaysOnTopOffAfterMigrationVersion()
    try testSettingsDefaultsFocusedAgentToCodexWhenMissing()
    testAcknowledgedErrorVisibilityUsesLatestErrorTime()
    testWorkingVisibilityLiveCallOutputAndInitialTail()
    testSessionReducerCapturesCurrentCodexTurnDetailsAndRateLimitAvailability()
    testSessionReducerFallsBackToLastTokenUsageWithoutTotals()
    testSessionReducerCapturesOnlyExplicitCodexSessionTitles()
    try testCodexSessionTitleReaderUsesLatestValidTitle()
    try testCodexSessionMonitorPrefersIndexTitleAndKeepsMetadataFallback()
    testToolFailedDoesNotBecomeFatalError()
    try testMonitorHandlesPendingLinesAndTruncation()
    testAggregatorHidesAcknowledgedErrorsAndShowsStandbyInput()
    testFailureClassification()
    try testRateLimitReaderFindsNewestTailRateLimit()
    try testRateLimitReaderFindsContextUsageAndResetTimes()
    try testRateLimitReaderCombinesSplitQuotaAndContextSnapshots()
    testRateLimitReaderReadsExplicitMonthlyQuota()
    testRateLimitReaderReadsFreeCreditsRemainingAsMonthlyQuota()
    testRateLimitReaderKeepsNewestCompletePlusBucketsOverOlderMonthlyUsage()
    testRateLimitReaderLeavesResetOnlyMonthlyQuotaPending()
    testRateLimitReaderReadsLongWindowPrimaryAsMonthly()
    testRateLimitReaderDoesNotTreatSecondaryBucketAsMonthly()
    testRateLimitReaderTreatsNullCreditsCodexCompatibilityAsPlus()
    testRateLimitReaderTreatsEmptyCodexCreditsCompatibilityAsPlus()
    testRateLimitReaderDoesNotTreatEmptyLegacyCreditsSecondaryAsMonthly()
    testRateLimitReaderDoesNotReturnEarlyOnContextOnlySnapshot()
    testCodexRealtimeActivityReaderDetectsAnswerStreaming()
    testCodexRealtimeActivityReaderDetectsContextCompactionStream()
    testCodexRealtimeActivityReaderDetectsArgumentStream()
    testCodexRealtimeActivityReaderEscalatedArgumentsAttention()
    testCodexRealtimeActivityReaderDetectsRequestUserInput()
    testCodexRealtimeActivityReaderClearsAnswerStreamingWhenDone()
    testSessionReducerMapsCustomToolRequestUserInputToAttention()
    testSessionReducerMapsEscalatedExecCommandToAttention()
    testSessionReducerMapsApprovalNamedToolToAttention()
    testSessionReducerMapsEscalatedArgumentsStringToAttention()
    testAggregatorInjectsUnacknowledgedCodexFailureWhenIdle()
    testAggregatorLimitsCodexCompletionToFiveMinutesAndRequiresRunningApp()
    testStartupExecutablePathUsesAppBundleRoot()
    testPlanModePlainFinalAnswerDoesNotHoldAttentionAtTaskComplete()
    testPlanModeProposedPlanFromTaskStartedHoldsAttentionAtTaskComplete()
    testPlanModeFromTurnContextHoldsAttentionAtTaskComplete()
    testPlanModeCompletedPlanItemHoldsAttentionAtTaskComplete()
    testNonPlanTaskCompleteStillTurnsGreen()
    testPlanModeWithoutFinalAnswerStillTurnsGreen()
    testPlanModeFlagResetsAcrossTurns()
    testPlanModeFlagResetsAfterFatalTurn()
    try testDiagnosticsCreatesParentDirectoryForOutput()
    testHaloMathMatchesProgramConstants()
    testLinearSRGBMixAvoidsGammaLerp()
    testWindowsStyleVisualTransitionAndMaterial()
    testCompletionDoubleFlashMatchesWindowsCadence()
    testAggregateFiltersInactiveAndTimedOutSessions()
    try testCodexOnlyAgentModelAndLegacyFocusMigration()
}

do {
    try await runCoreChecks()
    try await runUsageModelChecks()
    print("PASS AgentHaloCore Codex-only checks")
} catch {
    fatalError("AgentHaloCore checks failed: \(error)")
}
