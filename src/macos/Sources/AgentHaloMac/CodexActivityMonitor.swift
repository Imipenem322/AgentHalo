import Foundation
import AgentHaloCore

struct CodexActivitySnapshot: Equatable, Sendable {
    var sessions: [SessionSnapshot]
    var recentFailure: CodexFailure?
    var realtimeActivity: CodexRealtimeActivity?
    var lifecycleGeneration: UInt64 = 0

    static let empty = CodexActivitySnapshot(
        sessions: [],
        recentFailure: nil,
        realtimeActivity: nil,
        lifecycleGeneration: 0
    )
}

final class CodexActivityMonitor: @unchecked Sendable {
    private struct PollingContext: Equatable {
        var focusedAgent: AgentKind = .codex
        var codexRunning = false
        var lifecycleGeneration: UInt64 = 0
    }

    private static let activeIntervalMilliseconds = 300
    // Burst changes (e.g. applyWorkingVisibility flips or realtime token jitter) can
    // wake the main thread many times within a single tick. Coalesce them so the
    // main-thread onChange fires at most once per throttle window, with a trailing
    // delivery guaranteeing the final state always lands. Window matches the tick
    // cadence so UI freshness is indistinguishable from the existing tick refresh.
    private static let dispatchThrottleSeconds: TimeInterval = 0.3

    private let queue = DispatchQueue(label: "com.agenthalo.codex-activity", qos: .utility)
    private let sessionMonitor: CodexSessionMonitor
    private let failureReader: CodexFailureReader
    private let realtimeActivityReader: CodexRealtimeActivityReader
    private var timer: DispatchSourceTimer?
    private var monitoringRequested = false
    private var currentIntervalMilliseconds = CodexActivityMonitor.activeIntervalMilliseconds
    private var latestSnapshot = CodexActivitySnapshot.empty
    private var context = PollingContext()
    private var lastFailurePollAt = Date.distantPast
    private var lastRealtimePollAt = Date.distantPast
    private var realtimeActivityBaseline: Date?
    private var onChange: (@Sendable (CodexActivitySnapshot) -> Void)?
    private var pendingSnapshot: CodexActivitySnapshot?
    private var pendingDispatchWorkItem: DispatchWorkItem?
    private var lastDispatchAt = Date.distantPast

    init(
        sessionMonitor: CodexSessionMonitor = CodexSessionMonitor(),
        failureReader: CodexFailureReader = CodexFailureReader(),
        realtimeActivityReader: CodexRealtimeActivityReader = CodexRealtimeActivityReader()
    ) {
        self.sessionMonitor = sessionMonitor
        self.failureReader = failureReader
        self.realtimeActivityReader = realtimeActivityReader
    }

    func start(onChange: @escaping @Sendable (CodexActivitySnapshot) -> Void) {
        queue.async { [weak self] in
            guard let self else { return }
            self.onChange = onChange
            self.monitoringRequested = true
            guard self.timer == nil else {
                return
            }
            if self.context.codexRunning {
                self.scheduleTimer(intervalMilliseconds: Self.activeIntervalMilliseconds)
            }
        }
    }

    func stop() {
        queue.sync {
            monitoringRequested = false
            timer?.cancel()
            timer = nil
            onChange = nil
            pendingDispatchWorkItem?.cancel()
            pendingDispatchWorkItem = nil
            pendingSnapshot = nil
        }
    }

    func updatePollingContext(
        focusedAgent: AgentKind,
        codexRunning: Bool,
        lifecycleGeneration: UInt64,
        exitBaseline: Date? = nil,
        preservingNewerActivity: Bool = false
    ) {
        queue.async { [weak self] in
            guard let self else { return }
            let wasRunning = self.context.codexRunning
            self.context = PollingContext(
                focusedAgent: focusedAgent,
                codexRunning: codexRunning,
                lifecycleGeneration: lifecycleGeneration
            )
            if codexRunning {
                if let exitBaseline {
                    self.realtimeActivityBaseline = max(
                        self.realtimeActivityBaseline ?? .distantPast,
                        exitBaseline
                    )
                }
                if !wasRunning {
                    self.lastFailurePollAt = .distantPast
                    self.lastRealtimePollAt = .distantPast
                }
                if self.monitoringRequested,
                   self.timer == nil || self.currentIntervalMilliseconds != Self.activeIntervalMilliseconds {
                    self.scheduleTimer(intervalMilliseconds: Self.activeIntervalMilliseconds)
                }
            } else {
                self.timer?.cancel()
                self.timer = nil
                if wasRunning {
                    let baseline = exitBaseline ?? Date()
                    self.realtimeActivityBaseline = max(
                        self.realtimeActivityBaseline ?? .distantPast,
                        baseline
                    )
                    self.sessionMonitor.agentDidExit(
                        at: baseline,
                        preservingNewerActivity: preservingNewerActivity
                    )
                }
                var nextSnapshot = self.latestSnapshot
                nextSnapshot.lifecycleGeneration = lifecycleGeneration
                nextSnapshot.sessions = self.sessionMonitor.snapshots()
                nextSnapshot.realtimeActivity = nil
                if nextSnapshot != self.latestSnapshot {
                    self.latestSnapshot = nextSnapshot
                    self.scheduleDispatch(of: nextSnapshot, now: Date())
                }
            }
        }
    }

    func snapshot() -> CodexActivitySnapshot {
        queue.sync {
            latestSnapshot
        }
    }

    private func scheduleTimer(intervalMilliseconds: Int) {
        timer?.cancel()
        let next = DispatchSource.makeTimerSource(queue: queue)
        let leeway = max(50, intervalMilliseconds / 3)
        next.schedule(
            deadline: .now(),
            repeating: .milliseconds(intervalMilliseconds),
            leeway: .milliseconds(leeway)
        )
        next.setEventHandler { [weak self] in
            self?.poll()
        }
        timer = next
        currentIntervalMilliseconds = intervalMilliseconds
        next.resume()
    }

    private func poll() {
        guard context.codexRunning else {
            return
        }
        let now = Date()
        _ = sessionMonitor.refresh(now: now)

        var nextSnapshot = latestSnapshot
        nextSnapshot.lifecycleGeneration = context.lifecycleGeneration
        nextSnapshot.sessions = sessionMonitor.snapshots()

        if now.timeIntervalSince(lastFailurePollAt) >= 2 {
            lastFailurePollAt = now
            nextSnapshot.recentFailure = failureReader.readRecent(now: now)
        }

        if context.focusedAgent == .codex, context.codexRunning {
            if now.timeIntervalSince(lastRealtimePollAt) >= 0.3 {
                lastRealtimePollAt = now
                nextSnapshot.realtimeActivity = realtimeActivityReader.readActive(
                    now: now,
                    notBefore: realtimeActivityBaseline
                )
            }
        } else {
            nextSnapshot.realtimeActivity = nil
        }

        guard nextSnapshot != latestSnapshot else {
            return
        }
        latestSnapshot = nextSnapshot
        scheduleDispatch(of: nextSnapshot, now: now)
    }

    private func scheduleDispatch(of snapshot: CodexActivitySnapshot, now: Date) {
        let elapsed = now.timeIntervalSince(lastDispatchAt)
        if elapsed >= Self.dispatchThrottleSeconds {
            lastDispatchAt = now
            pendingSnapshot = nil
            pendingDispatchWorkItem?.cancel()
            pendingDispatchWorkItem = nil
            if let onChange {
                let snapshot = snapshot
                DispatchQueue.main.async {
                    onChange(snapshot)
                }
            }
            return
        }
        pendingSnapshot = snapshot
        guard pendingDispatchWorkItem == nil else { return }
        let remaining = max(0, Self.dispatchThrottleSeconds - elapsed)
        let deadline = DispatchTime.now() + .milliseconds(Int(remaining * 1000))
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.pendingDispatchWorkItem = nil
            guard let pending = self.pendingSnapshot else { return }
            self.pendingSnapshot = nil
            self.lastDispatchAt = Date()
            if let onChange = self.onChange {
                let snapshot = pending
                DispatchQueue.main.async {
                    onChange(snapshot)
                }
            }
        }
        pendingDispatchWorkItem = work
        queue.asyncAfter(deadline: deadline, execute: work)
    }
}
