import AppKit
import AgentHaloCore

@MainActor
func runHaloInteractionChecks() {
    check(
        AppDelegate.haloWindowLevel(alwaysOnTop: true) == .floating,
        "always-on-top should use the floating window level"
    )
    check(
        AppDelegate.haloWindowLevel(alwaysOnTop: false) == .normal,
        "disabled always-on-top should use the normal window level"
    )
    check(
        AppDelegate.haloCollectionBehavior.contains(.canJoinAllSpaces),
        "halo should remain available across Spaces"
    )
    check(
        AppDelegate.haloCollectionBehavior.contains(.fullScreenAuxiliary),
        "halo should remain a full-screen auxiliary window"
    )
    check(
        CodexAppDetector.isCodexDesktopIdentity(
            activationPolicy: .regular,
            bundleIdentifier: "com.openai.codex",
            executableName: "Codex",
            localizedName: "Codex",
            allowLocalizedName: false
        ),
        "regular Codex desktop application should be recognized"
    )
    check(
        !CodexAppDetector.isCodexDesktopIdentity(
            activationPolicy: .prohibited,
            bundleIdentifier: nil,
            executableName: "codex-code-mode-host",
            localizedName: nil,
            allowLocalizedName: false
        ),
        "background Codex helper should not keep desktop presence online"
    )

    let offlineAggregate = AggregateSnapshot(
        state: .idle,
        label: "OFFLINE",
        detail: "Codex is not running",
        sessions: [],
        focusedAgent: .codex
    )
    let standbyAggregate = AppDelegate.standbyAggregate(
        aggregate: offlineAggregate,
        hasLiveSession: true
    )
    check(
        standbyAggregate.state == .done &&
            standbyAggregate.label == "STANDBY" &&
            standbyAggregate.sessions.isEmpty,
        "running Codex with no visible session should become STANDBY"
    )
    check(
        AppDelegate.standbyAggregate(
            aggregate: offlineAggregate,
            hasLiveSession: false
        ).label == "OFFLINE",
        "stopped Codex should remain OFFLINE instead of becoming standby"
    )
    check(
        HaloView.shouldUseSteadyDone(
            state: standbyAggregate.state,
            sessions: standbyAggregate.sessions
        ) && !HaloView.shouldUseSteadyDone(
            state: offlineAggregate.state,
            sessions: offlineAggregate.sessions
        ),
        "standby uses the steady green visual while offline does not"
    )

    let panel = DetailsPanel()
    defer { panel.close() }
    let model = DetailsPanelViewModel(
        providerName: "Codex",
        planName: nil,
        usageWarning: nil,
        contextUsedPercent: nil,
        body: .session(SessionDetailsSnapshot())
    )
    panel.render(aggregate: offlineAggregate, model: model)
    check(panel.focusedAgentForTesting == .codex, "details panel should be Codex-only")
    check(panel.detailTextForTesting == L10n.shared["status.offline_codex"],
          "offline detail should use Codex localization")
}

private func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    guard condition() else {
        fatalError(message)
    }
}
