import Foundation

public final class CodexSessionMonitor {
    private let sessionsRoot: URL
    private var reducers: [URL: SessionReducer] = [:]
    private var offsets: [URL: UInt64] = [:]
    private var pending: [URL: Data] = [:]
    private var lastModified: [URL: Date] = [:]
    private var lastDiscoveryAt = Date.distantPast
    private var sessionTitleReader: CodexSessionTitleReader
    private let fileManager: FileManager

    public init(
        sessionsRoot: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex", isDirectory: true)
            .appendingPathComponent("sessions", isDirectory: true),
        sessionTitleReader: CodexSessionTitleReader = CodexSessionTitleReader(),
        fileManager: FileManager = .default
    ) {
        self.sessionsRoot = sessionsRoot
        self.sessionTitleReader = sessionTitleReader
        self.fileManager = fileManager
    }

    public func refresh(now: Date = Date()) -> Bool {
        var changed = false
        if now.timeIntervalSince(lastDiscoveryAt) >= 2 {
            changed = discover(now: now) || changed
        }
        for url in Array(reducers.keys) {
            changed = readNewLines(from: url, now: now) || changed
            reducers[url]?.applyWorkingVisibility(now: now)
        }
        let titles = sessionTitleReader.read()
        for url in reducers.keys {
            guard let threadID = reducers[url]?.snapshot.threadId,
                  let title = titles[threadID] else {
                continue
            }
            changed = reducers[url]?.setSessionTitle(title) == true || changed
        }
        return changed
    }

    public func snapshots() -> [SessionSnapshot] {
        reducers.values.map(\.snapshot)
    }

    private func discover(now: Date) -> Bool {
        lastDiscoveryAt = now
        let cutoff = now.addingTimeInterval(-172_800)

        // Walk the sessions tree with POSIX opendir/readdir (names + d_type,
        // no per-entry stat during enumeration) and a single stat() per jsonl
        // file, instead of FileManager.enumerator(at:)/subpaths(atPath:), both
        // of which allocate a Foundation object and stat/lstat every entry.
        // Mirrors the Windows FindFirstFile approach.
        let files = FastFileMetadata.discoverJsonlFiles(root: sessionsRoot, cutoff: cutoff, skipSubagents: false)

        let recent = Set(files.sorted { $0.modifiedAt > $1.modifiedAt }.prefix(16).map(\.url))
        var changed = false
        for url in recent where reducers[url] == nil {
            reducers[url] = SessionReducer(filePath: url.path(percentEncoded: false), liveTracking: false)
            let initialReadEnd = readInitialTail(from: url)
            let meta = FastFileMetadata.read(url)
            offsets[url] = initialReadEnd ?? 0
            lastModified[url] = meta?.modifiedAt
            reducers[url]?.setLiveTracking(true)
            changed = true
        }
        for url in reducers.keys where !recent.contains(url) {
            reducers.removeValue(forKey: url)
            offsets.removeValue(forKey: url)
            pending.removeValue(forKey: url)
            lastModified.removeValue(forKey: url)
            changed = true
        }
        return changed
    }

    private func readInitialTail(from url: URL) -> UInt64? {
        guard let handle = try? FileHandle(forReadingFrom: url) else {
            return nil
        }
        defer {
            try? handle.close()
        }
        let size = (try? handle.seekToEnd()) ?? 0
        let start = size > 393_216 ? size - 393_216 : 0
        do {
            try handle.seek(toOffset: start)
            let data = try handle.readToEnd() ?? Data()
            var tail = data
            if start > 0, let firstNewline = tail.firstIndex(of: 0x0A) {
                tail = tail.subdata(in: tail.index(after: firstNewline)..<tail.endIndex)
            }
            _ = processLines(in: tail, from: url)
            return start + UInt64(data.count)
        } catch {
            return 0
        }
    }

    private func readNewLines(from url: URL, now: Date) -> Bool {
        let previous = offsets[url] ?? 0
        let meta = FastFileMetadata.read(url)
        let current = meta?.size ?? 0
        let mtime = meta?.modifiedAt
        let priorMtime = lastModified[url]
        let mtimeChanged = mtime != nil && priorMtime != nil && mtime != priorMtime
        let truncated = current < previous || (mtimeChanged && current <= previous)
        if truncated {
            offsets[url] = 0
            pending[url] = nil
            lastModified[url] = mtime
            reducers[url] = SessionReducer(filePath: url.path(percentEncoded: false), liveTracking: true)
            return false
        }
        guard current > previous, let handle = try? FileHandle(forReadingFrom: url) else {
            return false
        }
        defer {
            try? handle.close()
        }
        do {
            try handle.seek(toOffset: previous)
            let data = try handle.readToEnd() ?? Data()
            let changed = processLines(in: data, from: url, now: now)
            offsets[url] = previous + UInt64(data.count)
            lastModified[url] = mtime
            return changed
        } catch {
            AgentHaloLogger.log("Session refresh failed: \(error)")
            return false
        }
    }

    private func processLines(in data: Data, from url: URL, now: Date? = nil) -> Bool {
        let combined = (pending[url] ?? Data()) + data
        let parts = combined.split(separator: 0x0A, omittingEmptySubsequences: false)
        let completeLines = parts.dropLast()
        if combined.last.map({ $0 == 0x0A }) == true {
            pending[url] = nil
        } else if let trailing = parts.last {
            let bytes = Data(trailing)
            pending[url] = bytes.isEmpty ? nil : bytes
        } else {
            pending[url] = nil
        }

        for bytes in completeLines {
            guard let line = String(data: Data(bytes), encoding: .utf8) else {
                continue
            }
            let trimmed = line.trimmingCharacters(in: CharacterSet(charactersIn: "\r"))
            guard !trimmed.isEmpty else {
                continue
            }
            if let now {
                reducers[url]?.consume(jsonLine: trimmed, now: now)
            } else {
                reducers[url]?.consume(jsonLine: trimmed)
            }
        }
        return !completeLines.isEmpty
    }
}
