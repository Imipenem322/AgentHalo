import AppKit

@MainActor
enum CodexAppDetector {
    private static var runningCacheValue: Bool?

    static func isCodexRunning() -> Bool {
        // Do not return a stale "running" cache here. A forcible quit can
        // miss a workspace termination notification, while AppDelegate polls
        // this method every 0.3 seconds for the live halo state.
        let value = NSWorkspace.shared.runningApplications.contains { app in
            isCodexApp(app, allowLocalizedName: false)
        }
        runningCacheValue = value
        return value
    }

    @discardableResult
    static func noteApplicationDidLaunch(_ app: NSRunningApplication?) -> Bool {
        guard let app, isCodexApp(app, allowLocalizedName: false) else { return false }
        let changed = runningCacheValue != true
        runningCacheValue = true
        return changed
    }

    @discardableResult
    static func noteApplicationDidTerminate(_ app: NSRunningApplication?) -> Bool {
        guard let app, isCodexApp(app, allowLocalizedName: false),
              runningCacheValue == true else { return false }
        runningCacheValue = nil
        return true
    }

    static func isCodexForeground(_ app: NSRunningApplication?) -> Bool {
        guard let app else { return false }
        return isCodexApp(app, allowLocalizedName: true)
    }

    static func activateCodex() {
        let candidates = NSWorkspace.shared.runningApplications.filter { app in
            app.processIdentifier != ProcessInfo.processInfo.processIdentifier &&
            app.activationPolicy == .regular &&
            isCodexApp(app, allowLocalizedName: true)
        }
        candidates.first?.activate(options: [.activateIgnoringOtherApps])
    }

    private static func isCodexApp(
        _ app: NSRunningApplication,
        allowLocalizedName: Bool
    ) -> Bool {
        return isCodexDesktopIdentity(
            activationPolicy: app.activationPolicy,
            bundleIdentifier: app.bundleIdentifier,
            executableName: app.executableURL?.lastPathComponent,
            localizedName: app.localizedName,
            allowLocalizedName: allowLocalizedName
        )
    }

    static func isCodexDesktopIdentity(
        activationPolicy: NSApplication.ActivationPolicy,
        bundleIdentifier: String?,
        executableName: String?,
        localizedName: String?,
        allowLocalizedName: Bool
    ) -> Bool {
        guard activationPolicy == .regular else {
            return false
        }
        let bundle = bundleIdentifier?.lowercased() ?? ""
        let executable = executableName?.lowercased() ?? ""
        if bundle.contains("codex") || executable == "codex" {
            return true
        }
        guard allowLocalizedName else {
            return false
        }
        let name = localizedName?.lowercased() ?? ""
        return name.contains("codex")
    }
}
