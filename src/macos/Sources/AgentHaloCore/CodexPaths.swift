import Foundation

public enum CodexPaths {
    public static let dataRoot = resolveDataRoot(
        codexHome: ProcessInfo.processInfo.environment["CODEX_HOME"],
        homeDirectory: FileManager.default.homeDirectoryForCurrentUser
    )

    public static func authFilePaths(
        codexHome: String?,
        homeDirectory: String
    ) -> [String] {
        if let codexHome = normalizedValue(codexHome) {
            return [resolveDataRoot(
                codexHome: codexHome,
                homeDirectory: URL(fileURLWithPath: homeDirectory, isDirectory: true)
            ).appendingPathComponent("auth.json").path(percentEncoded: false)]
        }

        let homePath = normalizedValue(homeDirectory)
            ?? FileManager.default.homeDirectoryForCurrentUser.path
        let home = URL(fileURLWithPath: homePath, isDirectory: true)
        let compatibilityPath = home
            .appendingPathComponent(".config", isDirectory: true)
            .appendingPathComponent("codex", isDirectory: true)
            .appendingPathComponent("auth.json")
        let defaultPath = resolveDataRoot(
            codexHome: nil,
            homeDirectory: home
        ).appendingPathComponent("auth.json")
        return [
            compatibilityPath.path(percentEncoded: false),
            defaultPath.path(percentEncoded: false)
        ]
    }

    private static func resolveDataRoot(
        codexHome: String?,
        homeDirectory: URL
    ) -> URL {
        if let codexHome = normalizedValue(codexHome) {
            return URL(fileURLWithPath: codexHome, isDirectory: true)
                .standardizedFileURL
        }
        return homeDirectory
            .appendingPathComponent(".codex", isDirectory: true)
            .standardizedFileURL
    }

    private static func normalizedValue(_ value: String?) -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}
