import Foundation
import AgentHaloCore

private enum UsageURLProtocolReply: Sendable {
    case http(statusCode: Int, body: Data)
    case nonHTTP
    case transportFailure
}

private final class UsageURLProtocolStub: URLProtocol, @unchecked Sendable {
    static let reply = LockedBox<UsageURLProtocolReply>(.nonHTTP)

    override class func canInit(with request: URLRequest) -> Bool {
        true
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    override func startLoading() {
        guard let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        switch Self.reply.value {
        case .http(let statusCode, let body):
            guard let response = HTTPURLResponse(
                url: url,
                statusCode: statusCode,
                httpVersion: "HTTP/1.1",
                headerFields: nil
            ) else {
                client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
                return
            }
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: body)
            client?.urlProtocolDidFinishLoading(self)
        case .nonHTTP:
            let response = URLResponse(url: url, mimeType: "text/plain", expectedContentLength: 0, textEncodingName: nil)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocolDidFinishLoading(self)
        case .transportFailure:
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
        }
    }

    override func stopLoading() {}
}

private func detailsResolverUpdateTime(_ date: Date, now: Date) -> String {
    let formatter = DateFormatter()
    formatter.locale = Locale(identifier: L10n.shared["date.culture"])
    if Calendar.current.isDate(date, inSameDayAs: now) {
        formatter.dateFormat = L10n.shared["date.today_format"]
    } else {
        formatter.dateFormat = L10n.shared["date.other_format"]
    }
    return formatter.string(from: date)
}

func testFilesystemUsageFilesWritesEmptyAndNonEmptyDataWithMode0600() throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-fs-files-\(UUID().uuidString)", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: root) }
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

    let files = FilesystemUsageFiles()

    // Empty Data must not crash and must create a zero-byte file.
    let emptyURL = root.appendingPathComponent("empty.json")
    try files.writeAtomically(Data(), to: emptyURL.path, preservingModeOf: nil)
    expect(FileManager.default.fileExists(atPath: emptyURL.path), "empty write should create the file")
    let emptyAttrs = try FileManager.default.attributesOfItem(atPath: emptyURL.path)
    expect(
        (emptyAttrs[.posixPermissions] as? NSNumber)?.uint16Value == 0o600,
        "empty write mode should be 0600"
    )
    expect((try Data(contentsOf: emptyURL)).count, 0, "empty write should produce zero bytes")

    // Non-empty Data must persist the exact bytes with the same mode.
    let payload = Data(#"{"hello":"world"}"#.utf8)
    let dataURL = root.appendingPathComponent("data.json")
    try files.writeAtomically(payload, to: dataURL.path, preservingModeOf: nil)
    expect(FileManager.default.fileExists(atPath: dataURL.path), "non-empty write should create the file")
    let dataAttrs = try FileManager.default.attributesOfItem(atPath: dataURL.path)
    expect(
        (dataAttrs[.posixPermissions] as? NSNumber)?.uint16Value == 0o600,
        "non-empty write mode should be 0600"
    )
    expect((try Data(contentsOf: dataURL)), payload, "non-empty write should preserve bytes")
}

func cacheCheckSnapshot(
    key: AccountCacheKey,
    refreshedAt: Date,
    planName: String = "Pro"
) -> UsageSnapshot {
    UsageSnapshot(
        providerID: key.providerID,
        accountKey: key,
        planName: planName,
        windows: [
            UsageWindow(
                kind: .session,
                usedPercent: 12,
                resetsAt: refreshedAt.addingTimeInterval(18_000),
                duration: 18_000
            )
        ],
        refreshedAt: refreshedAt
    )
}

private func coordinatorOAuthAccess(_ key: AccountCacheKey) -> OAuthAccess {
    OAuthAccess(
        providerID: key.providerID,
        accountKey: key,
        source: .file(path: "/tmp/agent-halo-coordinator-\(key.digest).json"),
        sourceVersion: "version-\(key.digest)",
        accessToken: "access-\(key.digest)",
        refreshToken: "refresh-\(key.digest)",
        expiresAt: nil,
        accountID: nil,
        planHint: nil
    )
}

private func coordinatorCache(
    files: any UsageFileAccessing,
    path: String,
    now: LockedBox<Date>
) -> UsageSnapshotCache {
    UsageSnapshotCache(
        cacheURL: URL(fileURLWithPath: path),
        files: files,
        now: { now.value }
    )
}

private func coordinatorSnapshot(
    _ key: AccountCacheKey,
    at date: Date,
    planName: String = "Pro"
) -> UsageSnapshot {
    cacheCheckSnapshot(key: key, refreshedAt: date, planName: planName)
}

private func waitForRefreshCount(
    _ provider: FakeUsageProvider,
    _ expectedCount: Int,
    _ message: String
) async {
    for _ in 0..<10_000 {
        if await provider.refreshCallCount >= expectedCount { return }
        await Task.yield()
    }
    fatalError(message)
}

private actor SequencedCoordinatorProvider: UsageProvider {
    struct ResolvePlan: Sendable {
        let result: ResolvedProviderAccess
        let isGated: Bool
    }

    struct RefreshPlan: Sendable {
        let result: UsageRefreshResult
        let isGated: Bool
    }

    nonisolated let providerID: UsageProviderID

    private var resolvePlans: [ResolvePlan] = []
    private var refreshPlans: [RefreshPlan] = []
    private var resolveContinuations: [Int: CheckedContinuation<Void, Never>] = [:]
    private var refreshContinuations: [Int: CheckedContinuation<Void, Never>] = [:]
    private(set) var resolveCallCount = 0
    private(set) var refreshCallCount = 0
    private(set) var activeResolveCount = 0
    private(set) var activeRefreshCount = 0
    private(set) var resolvedAccountKeys: [AccountCacheKey?] = []

    init(providerID: UsageProviderID) {
        self.providerID = providerID
    }

    func enqueueResolve(_ result: ResolvedProviderAccess, gated: Bool = false) {
        resolvePlans.append(ResolvePlan(result: result, isGated: gated))
    }

    func enqueueRefresh(_ result: UsageRefreshResult, gated: Bool = false) {
        refreshPlans.append(RefreshPlan(result: result, isGated: gated))
    }

    func resumeResolve(call: Int) {
        resolveContinuations.removeValue(forKey: call)?.resume()
    }

    func resumeRefresh(call: Int) {
        refreshContinuations.removeValue(forKey: call)?.resume()
    }

    func resolveAccess(accountKey: AccountCacheKey?) async -> ResolvedProviderAccess {
        resolveCallCount += 1
        activeResolveCount += 1
        defer { activeResolveCount -= 1 }
        resolvedAccountKeys.append(accountKey)
        let call = resolveCallCount
        guard !resolvePlans.isEmpty else {
            fatalError("missing resolve plan for call \(call)")
        }
        let plan = resolvePlans.removeFirst()
        if plan.isGated {
            await withCheckedContinuation { continuation in
                resolveContinuations[call] = continuation
            }
        }
        return plan.result
    }

    func refresh(using access: ResolvedProviderAccess) async -> UsageRefreshResult {
        refreshCallCount += 1
        activeRefreshCount += 1
        defer { activeRefreshCount -= 1 }
        let call = refreshCallCount
        guard !refreshPlans.isEmpty else {
            fatalError("missing refresh plan for call \(call)")
        }
        let plan = refreshPlans.removeFirst()
        if plan.isGated {
            await withCheckedContinuation { continuation in
                refreshContinuations[call] = continuation
            }
        }
        return plan.result
    }
}

private func waitForResolveCount(
    _ provider: SequencedCoordinatorProvider,
    _ expectedCount: Int,
    _ message: String
) async {
    for _ in 0..<10_000 {
        if await provider.resolveCallCount >= expectedCount { return }
        await Task.yield()
    }
    fatalError(message)
}

private func waitForRefreshCount(
    _ provider: SequencedCoordinatorProvider,
    _ expectedCount: Int,
    _ message: String
) async {
    for _ in 0..<10_000 {
        if await provider.refreshCallCount >= expectedCount { return }
        await Task.yield()
    }
    fatalError(message)
}

private final class CoordinatorFailingWriteFiles: UsageFileAccessing, @unchecked Sendable {
    func readDataIfPresent(at path: String) throws -> Data? { nil }
    func ensureDirectory(at path: String, mode: mode_t) throws {}
    func writeAtomically(_ data: Data, to path: String, preservingModeOf existingPath: String?) throws {
        throw FakeUsageFilesError.transientRead
    }
}

private actor BlockingSnapshotCache: UsageSnapshotCaching {
    private var continuation: CheckedContinuation<CachedUsageSnapshot?, Never>?
    private(set) var snapshotCallCount = 0
    private(set) var storeCallCount = 0

    func loadIfNeeded() async throws {}

    func snapshot(for key: AccountCacheKey) async throws -> CachedUsageSnapshot? {
        snapshotCallCount += 1
        return await withCheckedContinuation { continuation = $0 }
    }

    func store(_ snapshot: UsageSnapshot) async throws { storeCallCount += 1 }
    func migrate(from oldKey: AccountCacheKey, to newKey: AccountCacheKey) async throws {}

    func releaseSnapshot() {
        let pending = continuation
        continuation = nil
        pending?.resume(returning: nil)
    }
}

func testCoordinatorAPIKeyModeSkipsRefresh() async {
    let now = LockedBox(Date(timeIntervalSince1970: 2_100_000_000))
    let provider = FakeUsageProvider(providerID: .codex, resolveResult: .apiKey)
    let cache = coordinatorCache(
        files: FakeUsageFiles(),
        path: "/tmp/agent-halo-coordinator-api.json",
        now: now
    )
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })

    let state = await coordinator.ensureFresh(.codex)

    expect(state.accessMode, .apiKey, "API-key access should publish API mode")
    expect(state.status == nil, "API-key access should not publish OAuth usage status")
    expect(state.snapshot == nil, "API-key access should clear OAuth snapshots")
    expect(await provider.refreshCallCount, 0, "API-key access must never refresh usage")
}

func testCoordinatorDiskSnapshotIsExactStaleAndDoesNotSuppressRefresh() async {
    let date = Date(timeIntervalSince1970: 2_100_001_000)
    let now = LockedBox(date)
    let files = FakeUsageFiles()
    let path = "/tmp/agent-halo-coordinator-disk.json"
    let key = AccountCacheKey(providerID: .codex, digest: "disk-account")
    let otherKey = AccountCacheKey(providerID: .codex, digest: "other-account")
    let seed = coordinatorCache(files: files, path: path, now: now)
    try! await seed.store(coordinatorSnapshot(key, at: date.addingTimeInterval(-60), planName: "Exact"))
    try! await seed.store(coordinatorSnapshot(otherKey, at: date, planName: "Other"))

    let provider = FakeUsageProvider(providerID: .codex, resolveResult: .oauth(coordinatorOAuthAccess(key)))
    await provider.setRefreshResult(
        UsageRefreshResult(
            providerID: .codex,
            snapshot: coordinatorSnapshot(key, at: date, planName: "Network"),
            failure: nil
        )
    )
    let cache = coordinatorCache(files: files, path: path, now: now)
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })

    let prepared = await coordinator.prepare(.codex)
    expect(prepared.snapshot?.planName, "Exact", "prepare should load only the resolved account snapshot")
    expect(prepared.status, .stale(updatedAt: date.addingTimeInterval(-60)), "disk snapshot must start stale")
    expect(await provider.refreshCallCount, 0, "prepare must not perform network refresh")

    let refreshed = await coordinator.ensureFresh(.codex)
    expect(await provider.refreshCallCount, 1, "disk snapshot must not suppress first-run refresh")
    expect(refreshed.snapshot?.planName, "Network", "refresh should replace disk snapshot")
    expect(refreshed.status, .fresh(updatedAt: date), "successful refresh should be fresh")
}

func testCoordinatorCurrentRunFreshnessAndTenMinuteStaleness() async {
    let date = Date(timeIntervalSince1970: 2_100_002_000)
    let now = LockedBox(date)
    let key = AccountCacheKey(providerID: .codex, digest: "fresh-account")
    let provider = FakeUsageProvider(providerID: .codex, resolveResult: .oauth(coordinatorOAuthAccess(key)))
    await provider.setRefreshResult(
        UsageRefreshResult(
            providerID: .codex,
            snapshot: coordinatorSnapshot(key, at: date),
            failure: nil
        )
    )
    let cache = coordinatorCache(
        files: FakeUsageFiles(),
        path: "/tmp/agent-halo-coordinator-fresh.json",
        now: now
    )
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })

    _ = await coordinator.ensureFresh(.codex)
    _ = await coordinator.ensureFresh(.codex)
    expect(await provider.refreshCallCount, 1, "current-run snapshot younger than five minutes should suppress refresh")

    now.withValue { $0 = date.addingTimeInterval(300) }
    _ = await coordinator.ensureFresh(.codex)
    expect(await provider.refreshCallCount, 1, "snapshot exactly five minutes old should still be fresh")

    now.withValue { $0 = date.addingTimeInterval(300.001) }
    await provider.setRefreshResult(
        UsageRefreshResult(
            providerID: .codex,
            snapshot: coordinatorSnapshot(key, at: now.value),
            failure: nil
        )
    )
    _ = await coordinator.ensureFresh(.codex)
    expect(await provider.refreshCallCount, 2, "five-minute freshness expiry should allow refresh")

    now.withValue { $0 = date.addingTimeInterval(902) }
    let aged = await coordinator.state(for: .codex)
    expect(aged.status, .stale(updatedAt: date.addingTimeInterval(300.001)), "snapshot older than ten minutes should become stale without an error")
}

func testCoordinatorLatestConcurrentPrepareWins() async {
    let date = Date(timeIntervalSince1970: 2_100_002_500)
    let now = LockedBox(date)
    let accountA = AccountCacheKey(providerID: .codex, digest: "prepare-a")
    let accountB = AccountCacheKey(providerID: .codex, digest: "prepare-b")
    let provider = SequencedCoordinatorProvider(providerID: .codex)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(accountA)), gated: true)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(accountB)))
    let coordinator = UsageMonitoringCoordinator(
        providers: [provider],
        cache: coordinatorCache(
            files: FakeUsageFiles(),
            path: "/tmp/agent-halo-coordinator-prepare-generation.json",
            now: now
        ),
        now: { now.value }
    )

    let slowA = Task { await coordinator.prepare(.codex) }
    await waitForResolveCount(provider, 1, "slow account A prepare did not enter access resolution")
    let fastB = Task { await coordinator.prepare(.codex) }
    let fastState = await fastB.value
    expect(fastState.accessMode, .oauth, "newer account B prepare should publish OAuth mode")

    await provider.resumeResolve(call: 1)
    let slowState = await slowA.value
    expect(slowState.accessMode, .oauth, "superseded prepare must return account B's real OAuth context")
    let final = await coordinator.state(for: .codex)
    expect(final.snapshot == nil, "late account A prepare must not restore an account A snapshot")

    await provider.enqueueResolve(.oauthNeedsSignIn(accountKey: accountB))
    let confirmedB = await coordinator.prepare(.codex)
    expect(confirmedB.status, .signInAgain, "late account A prepare must leave account B as the active binding")
    expect(
        (await provider.resolvedAccountKeys)[2],
        accountB,
        "the next prepare should resolve from the latest committed account B binding"
    )
}

func testCoordinatorSupersededFirstEnsureFreshAwaitsCommittedContext() async {
    let date = Date(timeIntervalSince1970: 2_100_002_750)
    let now = LockedBox(date)
    let account = AccountCacheKey(providerID: .codex, digest: "first-concurrent")
    let snapshot = coordinatorSnapshot(account, at: date, planName: "Shared OAuth")
    let provider = SequencedCoordinatorProvider(providerID: .codex)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(account)), gated: true)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(account)), gated: true)
    await provider.enqueueRefresh(
        UsageRefreshResult(providerID: .codex, snapshot: snapshot, failure: nil),
        gated: true
    )
    let coordinator = UsageMonitoringCoordinator(
        providers: [provider],
        cache: coordinatorCache(
            files: FakeUsageFiles(),
            path: "/tmp/agent-halo-coordinator-first-concurrent.json",
            now: now
        ),
        now: { now.value }
    )

    let first = Task { await coordinator.ensureFresh(.codex) }
    await waitForResolveCount(provider, 1, "first ensureFresh did not enter access resolution")
    let second = Task { await coordinator.ensureFresh(.codex) }
    await waitForResolveCount(provider, 2, "second ensureFresh did not enter access resolution")

    // Let the superseded call return from its resolver while the latest call
    // still has no committed context. It must suspend for that context rather
    // than manufacture an API-key fallback.
    await provider.resumeResolve(call: 1)
    for _ in 0..<100 { await Task.yield() }
    await provider.resumeResolve(call: 2)

    await waitForRefreshCount(provider, 1, "latest OAuth context did not start refresh")
    await provider.resumeRefresh(call: 1)
    let firstState = await first.value
    let secondState = await second.value

    expect(firstState.accessMode, .oauth, "superseded first ensureFresh must not return API-key mode")
    expect(secondState.accessMode, .oauth, "latest ensureFresh should retain OAuth mode")
    expect(firstState.snapshot, snapshot, "superseded first ensureFresh should join the shared OAuth refresh")
    expect(secondState.snapshot, snapshot, "latest ensureFresh should return the shared OAuth refresh")
    expect(await provider.refreshCallCount, 1, "first concurrent ensureFresh calls must deduplicate refresh")
}

func testCoordinatorAccountSwitchDoesNotJoinOrCommitOldRefresh() async {
    let date = Date(timeIntervalSince1970: 2_100_003_500)
    let now = LockedBox(date)
    let accountA = AccountCacheKey(providerID: .codex, digest: "switch-a")
    let accountB = AccountCacheKey(providerID: .codex, digest: "switch-b")
    let provider = SequencedCoordinatorProvider(providerID: .codex)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(accountA)))
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(accountB)))
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(accountA)))
    await provider.enqueueRefresh(
        UsageRefreshResult(
            providerID: .codex,
            snapshot: nil,
            failure: .rateLimited(retryAt: date.addingTimeInterval(3_600))
        ),
        gated: true
    )
    let snapshotB = coordinatorSnapshot(accountB, at: date, planName: "Account B")
    await provider.enqueueRefresh(
        UsageRefreshResult(providerID: .codex, snapshot: snapshotB, failure: nil)
    )
    let snapshotA = coordinatorSnapshot(accountA, at: date, planName: "Account A retry")
    await provider.enqueueRefresh(
        UsageRefreshResult(providerID: .codex, snapshot: snapshotA, failure: nil)
    )
    let cache = coordinatorCache(
        files: FakeUsageFiles(),
        path: "/tmp/agent-halo-coordinator-account-switch.json",
        now: now
    )
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })

    let requestA = Task { await coordinator.ensureFresh(.codex) }
    await waitForRefreshCount(provider, 1, "account A refresh did not start")
    let requestB = Task { await coordinator.ensureFresh(.codex) }
    await waitForRefreshCount(provider, 2, "account B must start its own refresh instead of joining account A")
    let stateB = await requestB.value
    expect(stateB.snapshot, snapshotB, "account B refresh should publish account B data")

    await provider.resumeRefresh(call: 1)
    _ = await requestA.value
    expect(
        (await coordinator.state(for: .codex)).snapshot,
        snapshotB,
        "late account A rate-limit result must not overwrite account B state"
    )
    expect(try! await cache.snapshot(for: accountA) == nil, "invalidated account A result must not write cache")

    let retriedA = await coordinator.ensureFresh(.codex)
    expect(await provider.refreshCallCount, 3, "invalidated account A rate-limit must not install cooldown")
    expect(retriedA.snapshot, snapshotA, "account A should refresh normally after switching back")
}

func testCoordinatorInvalidatedMigrationHasNoCacheSideEffects() async {
    let date = Date(timeIntervalSince1970: 2_100_003_700)
    let now = LockedBox(date)
    let oldKey = AccountCacheKey(providerID: .codex, digest: "stale-migration-old")
    let rotatedKey = AccountCacheKey(providerID: .codex, digest: "stale-migration-rotated")
    let accountB = AccountCacheKey(providerID: .codex, digest: "stale-migration-b")
    let files = FakeUsageFiles()
    let path = "/tmp/agent-halo-coordinator-stale-migration.json"
    let seed = coordinatorCache(files: files, path: path, now: now)
    let oldSnapshot = coordinatorSnapshot(oldKey, at: date.addingTimeInterval(-60), planName: "Old")
    try! await seed.store(oldSnapshot)
    let cache = coordinatorCache(files: files, path: path, now: now)

    let provider = SequencedCoordinatorProvider(providerID: .codex)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(oldKey)))
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(accountB)))
    await provider.enqueueRefresh(
        UsageRefreshResult(
            providerID: .codex,
            snapshot: coordinatorSnapshot(rotatedKey, at: date, planName: "Invalidated rotation"),
            failure: nil,
            migrateCacheFrom: oldKey
        ),
        gated: true
    )
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })

    let oldRequest = Task { await coordinator.ensureFresh(.codex) }
    await waitForRefreshCount(provider, 1, "migration refresh did not start")
    let stateB = await coordinator.prepare(.codex)
    expect(stateB.snapshot == nil, "account B prepare must not adopt the old account cache")
    await provider.resumeRefresh(call: 1)
    _ = await oldRequest.value

    expect(
        try! await cache.snapshot(for: oldKey)?.snapshot,
        oldSnapshot,
        "invalidated migration must leave the old cache entry untouched"
    )
    expect(
        try! await cache.snapshot(for: rotatedKey) == nil,
        "invalidated migration must not create the rotated cache entry"
    )
    expect(
        (await coordinator.state(for: .codex)).snapshot == nil,
        "invalidated migration result must not overwrite account B state"
    )
}

func testCoordinatorKeepsStableCacheWhenCodexRotationCannotPersist() async {
    let now = Date(timeIntervalSince1970: 2_100_006_100)
    let authFiles = CodexCheckFailingFiles()
    let expiring = codexCheckJWT(exp: now.addingTimeInterval(60).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: expiring, now: now, files: authFiles)
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(#"{"access_token":"memory-access","refresh_token":"memory-refresh"}"#))
    await http.enqueue(response: codexUsageResponse(#"{"plan_type":"plus"}"#))
    let provider = CodexUsageProvider(
        authStore: fixture.0,
        usageClient: CodexUsageClient(http: http),
        now: { now }
    )
    let cacheFiles = FakeUsageFiles()
    let cache = coordinatorCache(
        files: cacheFiles,
        path: "/tmp/agent-halo-unpersisted-rotation.json",
        now: LockedBox(now)
    )
    try! await cache.store(coordinatorSnapshot(fixture.1.accountKey, at: now.addingTimeInterval(-600)))
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now })

    let refreshed = await coordinator.ensureFresh(.codex)
    expect(refreshed.snapshot?.accountKey, fixture.1.accountKey, "failed writeback keeps Coordinator on the stable key")
    let preparedAgain = await coordinator.prepare(.codex)
    expect(preparedAgain.snapshot?.accountKey, fixture.1.accountKey, "next prepare from the old source finds the old-key cache")
    expect(try! await cache.snapshot(for: fixture.1.accountKey) != nil, "old stable cache entry remains present")
}

func testCoordinatorRecoversRealCodexProviderAfterExternalLogin() async {
    let date = Date(timeIntervalSince1970: 2_100_006_500)
    let oldToken = codexCheckJWT(exp: date.addingTimeInterval(3_600).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: oldToken, now: date)
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [
            .response(codexUsageResponse(#"{"plan_type":"pro"}"#)),
            .response(codexUsageResponse(#"{"plan_type":"plus"}"#)),
        ],
        gatedPath: "/backend-api/wham/usage"
    )
    let provider = CodexUsageProvider(
        authStore: fixture.0,
        usageClient: CodexUsageClient(http: http),
        now: { date }
    )
    let now = LockedBox(date)
    let cache = coordinatorCache(
        files: FakeUsageFiles(),
        path: "/tmp/agent-halo-coordinator-real-codex-external.json",
        now: now
    )
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })

    let request = Task { await coordinator.ensureFresh(.codex) }
    await waitForHTTPRequestCount(http, 1, "real Codex Provider did not enter gated Usage")
    let externalToken = codexCheckJWT(exp: date.addingTimeInterval(7_200).timeIntervalSince1970)
    try? files.writeAtomically(
        codexExternalCredential(accessToken: externalToken, refreshToken: "new-codex-refresh"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    guard case .oauth(let externalAccess) = fixture.0.resolveAccess() else {
        fatalError("external Codex fixture should resolve")
    }
    await http.resume()
    let recovered = await request.value

    expect(recovered.snapshot?.accountKey, externalAccess.accountKey, "Coordinator should bind the external Codex account")
    expect(recovered.snapshot?.planName, "Plus", "Coordinator should publish only the external Codex response")
    expect(recovered.lastFailure == nil, "external Codex recovery should not publish an old-request failure")
    expect(try! await cache.snapshot(for: fixture.1.accountKey) == nil, "old Codex request must have zero cache side effects")
    expect(try! await cache.snapshot(for: externalAccess.accountKey)?.snapshot.planName, "Plus", "new Codex account should be cached independently")
    let requests = await http.capturedRequests
    expect(requests.count, 2, "Coordinator should re-prepare and refresh Codex in the same ensureFresh")
    expect(requests[0].headers["authorization"], "Bearer \(oldToken)", "first Codex request uses old access")
    expect(requests[1].headers["authorization"], "Bearer \(externalToken)", "recovery Codex request uses external access")
}

func testCoordinatorCancelAllAwaitsRefreshQuiescence() async {
    let date = Date(timeIntervalSince1970: 2_100_006_700)
    let now = LockedBox(date)
    let key = AccountCacheKey(providerID: .codex, digest: "quiescent-refresh")
    let provider = SequencedCoordinatorProvider(providerID: .codex)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(key)))
    await provider.enqueueRefresh(
        UsageRefreshResult(
            providerID: .codex,
            snapshot: coordinatorSnapshot(key, at: date),
            failure: nil
        ),
        gated: true
    )
    let cache = coordinatorCache(
        files: FakeUsageFiles(),
        path: "/tmp/agent-halo-coordinator-quiescent-refresh.json",
        now: now
    )
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache, now: { now.value })
    let request = Task { await coordinator.ensureFresh(.codex) }
    await waitForRefreshCount(provider, 1, "quiescent refresh did not enter Provider")
    expect(await provider.activeRefreshCount, 1, "gated refresh should be active before cancellation")

    let didReturn = LockedBox(false)
    let cancellation = Task {
        await coordinator.cancelAll()
        didReturn.withValue { $0 = true }
    }
    for _ in 0..<100 { await Task.yield() }
    expect(!didReturn.value, "cancelAll must not return while tracked refresh work is still active")

    await provider.resumeRefresh(call: 1)
    await cancellation.value
    _ = await request.value
    expect(await provider.activeRefreshCount, 0, "cancelAll must return only after refresh exits")
    expect(try! await cache.snapshot(for: key) == nil, "cancelled refresh must have zero cache side effects")
}

func testCoordinatorCancelAllTracksBlockedCacheSnapshotThroughCommitBoundary() async {
    let key = AccountCacheKey(providerID: .codex, digest: "blocked-cache-prepare")
    let provider = SequencedCoordinatorProvider(providerID: .codex)
    await provider.enqueueResolve(.oauth(coordinatorOAuthAccess(key)))
    let cache = BlockingSnapshotCache()
    let coordinator = UsageMonitoringCoordinator(providers: [provider], cache: cache)
    let request = Task { await coordinator.ensureFresh(.codex) }
    for _ in 0..<10_000 {
        if await cache.snapshotCallCount == 1 { break }
        await Task.yield()
    }
    expect(await cache.snapshotCallCount, 1, "prepare must reach the blocking cache snapshot")

    let didReturn = LockedBox(false)
    let cancellation = Task {
        await coordinator.cancelAll()
        didReturn.withValue { $0 = true }
    }
    for _ in 0..<100 { await Task.yield() }
    expect(!didReturn.value, "cancelAll must await a prepare blocked in cache.snapshot")

    await cache.releaseSnapshot()
    await cancellation.value
    _ = await request.value
    expect(await provider.refreshCallCount, 0, "late cancelled prepare must not start refresh")
    expect(await cache.storeCallCount, 0, "late cancelled prepare must not write cache")
    expect((await coordinator.state(for: .codex)).snapshot == nil, "late cancelled prepare must not commit")
    await cache.releaseSnapshot()
}

func usageSnapshotPayloadPrivacyViolations(in payload: [String: Any]) -> [String] {
    var violations: [String] = []
    let forbiddenKeyNames: Set<String> = [
        "accesstoken",
        "refreshtoken",
        "authorization",
        "accountid",
        "project",
        "projecttitle",
        "sessionid",
        "sessiontitle",
        "rawresponse",
        "response",
        "credential",
        "credentials",
        "apikey",
    ]

    func normalizedKey(_ key: String) -> String {
        key.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    func scanForbiddenKeys(_ value: Any, path: String) {
        if let dictionary = value as? [String: Any] {
            for (key, nestedValue) in dictionary {
                if forbiddenKeyNames.contains(normalizedKey(key)) {
                    violations.append("\(path).\(key): forbidden key")
                }
                scanForbiddenKeys(nestedValue, path: "\(path).\(key)")
            }
        } else if let array = value as? [Any] {
            for (index, nestedValue) in array.enumerated() {
                scanForbiddenKeys(nestedValue, path: "\(path)[\(index)]")
            }
        }
    }

    func validateKeys(
        _ dictionary: [String: Any],
        required: Set<String>,
        allowed: Set<String>,
        path: String
    ) {
        let actual = Set(dictionary.keys)
        let missing = required.subtracting(actual).sorted()
        let unexpected = actual.subtracting(allowed).sorted()
        if !missing.isEmpty {
            violations.append("\(path): missing keys \(missing)")
        }
        if !unexpected.isEmpty {
            violations.append("\(path): unexpected keys \(unexpected)")
        }
    }

    scanForbiddenKeys(payload, path: "payload")
    validateKeys(
        payload,
        required: ["version", "entries"],
        allowed: ["version", "entries"],
        path: "payload"
    )
    if (payload["version"] as? NSNumber)?.intValue != 1 {
        violations.append("payload.version: expected schema version 1")
    }

    guard let entries = payload["entries"] as? [Any] else {
        violations.append("payload.entries: expected array")
        return violations
    }
    for (entryIndex, rawEntry) in entries.enumerated() {
        let entryPath = "payload.entries[\(entryIndex)]"
        guard let entry = rawEntry as? [String: Any] else {
            violations.append("\(entryPath): expected object")
            continue
        }
        validateKeys(
            entry,
            required: ["snapshot", "lastAccessedAt"],
            allowed: ["snapshot", "lastAccessedAt"],
            path: entryPath
        )
        if !(entry["lastAccessedAt"] is NSNumber) {
            violations.append("\(entryPath).lastAccessedAt: expected encoded date")
        }

        guard let snapshot = entry["snapshot"] as? [String: Any] else {
            violations.append("\(entryPath).snapshot: expected object")
            continue
        }
        let snapshotPath = "\(entryPath).snapshot"
        validateKeys(
            snapshot,
            required: ["providerID", "accountKey", "windows", "refreshedAt"],
            allowed: ["providerID", "accountKey", "planName", "windows", "refreshedAt"],
            path: snapshotPath
        )
        if !(snapshot["providerID"] is String) {
            violations.append("\(snapshotPath).providerID: expected string")
        }
        if let planName = snapshot["planName"], !(planName is String) {
            violations.append("\(snapshotPath).planName: expected string")
        }
        if !(snapshot["refreshedAt"] is NSNumber) {
            violations.append("\(snapshotPath).refreshedAt: expected encoded date")
        }

        if let accountKey = snapshot["accountKey"] as? [String: Any] {
            let accountKeyPath = "\(snapshotPath).accountKey"
            validateKeys(
                accountKey,
                required: ["providerID", "digest"],
                allowed: ["providerID", "digest"],
                path: accountKeyPath
            )
            if !(accountKey["providerID"] is String) {
                violations.append("\(accountKeyPath).providerID: expected string")
            }
            if !(accountKey["digest"] is String) {
                violations.append("\(accountKeyPath).digest: expected string")
            }
        } else {
            violations.append("\(snapshotPath).accountKey: expected object")
        }

        guard let windows = snapshot["windows"] as? [Any] else {
            violations.append("\(snapshotPath).windows: expected array")
            continue
        }
        for (windowIndex, rawWindow) in windows.enumerated() {
            let windowPath = "\(snapshotPath).windows[\(windowIndex)]"
            guard let window = rawWindow as? [String: Any] else {
                violations.append("\(windowPath): expected object")
                continue
            }
            validateKeys(
                window,
                required: ["kind", "usedPercent", "duration"],
                allowed: ["kind", "usedPercent", "resetsAt", "duration"],
                path: windowPath
            )
            if !(window["kind"] is String) {
                violations.append("\(windowPath).kind: expected string")
            }
            if !(window["usedPercent"] is NSNumber) {
                violations.append("\(windowPath).usedPercent: expected number")
            }
            if !(window["duration"] is NSNumber) {
                violations.append("\(windowPath).duration: expected number")
            }
            if let resetsAt = window["resetsAt"], !(resetsAt is NSNumber) {
                violations.append("\(windowPath).resetsAt: expected encoded date")
            }
        }
    }
    return violations
}

func testUsageSnapshotCacheRemovesEntriesOlderThanThirtyDays() async throws {
    let cacheURL = URL(fileURLWithPath: "/tmp/agent-halo-cache-expiry.json")
    let files = FakeUsageFiles()
    let clock = LockedBox(Date(timeIntervalSince1970: 40_000))
    let oldKey = AccountCacheKey(providerID: .codex, digest: "old")
    let cache = UsageSnapshotCache(cacheURL: cacheURL, files: files, now: { clock.value })
    try await cache.store(cacheCheckSnapshot(key: oldKey, refreshedAt: clock.value))

    clock.withValue { $0 = $0.addingTimeInterval(30 * 24 * 60 * 60 + 1) }
    let reloaded = UsageSnapshotCache(cacheURL: cacheURL, files: files, now: { clock.value })
    try await reloaded.loadIfNeeded()
    expect(try await reloaded.snapshot(for: oldKey) == nil, "entries older than 30 days should be removed")
    expect(files.capturedWrites().count, 2, "expiry pruning should persist the cleaned payload")
}

func testUsageSnapshotCacheIgnoresCorruptAndUnknownPayloads() async throws {
    let now = Date(timeIntervalSince1970: 50_000)
    let key = AccountCacheKey(providerID: .codex, digest: "replacement")

    let corruptURL = URL(fileURLWithPath: "/tmp/agent-halo-cache-corrupt.json")
    let corruptFiles = FakeUsageFiles(contents: [corruptURL.path: Data("not-json".utf8)])
    let corruptCache = UsageSnapshotCache(cacheURL: corruptURL, files: corruptFiles, now: { now })
    try await corruptCache.loadIfNeeded()
    expect(try await corruptCache.snapshot(for: key) == nil, "corrupt payload should be ignored safely")
    try await corruptCache.store(cacheCheckSnapshot(key: key, refreshedAt: now))
    expect(try await corruptCache.snapshot(for: key) != nil, "cache should recover after corrupt payload")

    let unknownURL = URL(fileURLWithPath: "/tmp/agent-halo-cache-unknown.json")
    let unknownPayload = try JSONSerialization.data(withJSONObject: ["version": 2, "entries": []])
    let unknownFiles = FakeUsageFiles(contents: [unknownURL.path: unknownPayload])
    let unknownCache = UsageSnapshotCache(cacheURL: unknownURL, files: unknownFiles, now: { now })
    try await unknownCache.loadIfNeeded()
    expect(try await unknownCache.snapshot(for: key) == nil, "unknown schema version should be ignored safely")
}

func testUsageSnapshotCacheCreatesPrivateParentAndFile() async throws {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("agent-halo-snapshot-cache-\(UUID().uuidString)", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let cacheDirectory = root.appendingPathComponent(".agent-halo", isDirectory: true)
    let cacheURL = cacheDirectory.appendingPathComponent("usage-snapshots-v1.json")
    let now = Date(timeIntervalSince1970: 60_000)
    let key = AccountCacheKey(providerID: .codex, digest: "mode-check")
    let cache = UsageSnapshotCache(cacheURL: cacheURL, files: FilesystemUsageFiles(), now: { now })

    try await cache.store(cacheCheckSnapshot(key: key, refreshedAt: now))

    let directoryAttributes = try FileManager.default.attributesOfItem(atPath: cacheDirectory.path)
    expect(
        (directoryAttributes[.posixPermissions] as? NSNumber)?.uint16Value == 0o700,
        "new snapshot cache directory should use mode 0700"
    )
    let attributes = try FileManager.default.attributesOfItem(atPath: cacheURL.path)
    expect(
        (attributes[.posixPermissions] as? NSNumber)?.uint16Value == 0o600,
        "new snapshot cache file should use mode 0600"
    )
}

func codexCheckJWT(exp: TimeInterval) -> String {
    let header = #"{"alg":"HS256","typ":"JWT"}"#
    let payload = #"{"exp":\#(exp)}"#
    func base64URLEncode(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
    let headerB64 = base64URLEncode(Data(header.utf8))
    let payloadB64 = base64URLEncode(Data(payload.utf8))
    return "\(headerB64).\(payloadB64).sig"
}

func codexCheckJSON(_ object: [String: Any]) -> Data {
    try! JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
}

func codexCheckAuthPath(home: String) -> String {
    "\(home)/.codex/auth.json"
}

func codexCheckConfigPath(home: String) -> String {
    "\(home)/.config/codex/auth.json"
}

func testCodexHomeWinsOverDefaultPaths() {
    let codexHome = "/tmp/agent-halo-codex-home-\(UUID().uuidString)"
    let home = "/tmp/agent-halo-fake-home-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["CODEX_HOME": codexHome, "HOME": home])
    let files = FakeUsageFiles(contents: [
        "\(codexHome)/auth.json": codexCheckJSON([
            "tokens": ["access_token": "codex-home-token", "refresh_token": "rt-home"]
        ]),
        codexCheckConfigPath(home: home): codexCheckJSON([
            "tokens": ["access_token": "config-token"]
        ]),
        codexCheckAuthPath(home: home): codexCheckJSON([
            "tokens": ["access_token": "codex-token"]
        ]),
    ])
    let store = CodexAuthStore(environment: env, files: files, keychain: FakeUsageKeychain())

    guard case .oauth(let access) = store.resolveAccess() else {
        fatalError("CODEX_HOME should resolve to OAuth")
    }
    expect(access.accessToken, "codex-home-token", "CODEX_HOME auth.json should win over default paths")
    if case .file(let path) = access.source {
        expect(path, "\(codexHome)/auth.json", "source should point to CODEX_HOME/auth.json")
    } else {
        fatalError("CODEX_HOME source should be a file")
    }
}

func testCodexDiscoveryOrderWithoutCodexHome() {
    let home = "/tmp/agent-halo-order-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["HOME": home])

    // .config/codex wins over .codex.
    let filesConfig = FakeUsageFiles(contents: [
        codexCheckConfigPath(home: home): codexCheckJSON(["tokens": ["access_token": "config-token"]]),
        codexCheckAuthPath(home: home): codexCheckJSON(["tokens": ["access_token": "codex-token"]]),
    ])
    let storeConfig = CodexAuthStore(environment: env, files: filesConfig, keychain: FakeUsageKeychain())
    guard case .oauth(let configAccess) = storeConfig.resolveAccess() else {
        fatalError(".config/codex should resolve to OAuth")
    }
    expect(configAccess.accessToken, "config-token", ".config/codex/auth.json should win over .codex")

    // Only .codex/auth.json exists.
    let filesCodex = FakeUsageFiles(contents: [
        codexCheckAuthPath(home: home): codexCheckJSON(["tokens": ["access_token": "codex-only-token"]]),
    ])
    let storeCodex = CodexAuthStore(environment: env, files: filesCodex, keychain: FakeUsageKeychain())
    guard case .oauth(let codexAccess) = storeCodex.resolveAccess() else {
        fatalError(".codex should resolve to OAuth")
    }
    expect(codexAccess.accessToken, "codex-only-token", ".codex/auth.json should be found when .config absent")

    // Keychain fallback.
    let keychain = FakeUsageKeychain()
    try? keychain.write(
        service: CodexAuthStore.keychainService,
        account: "codex-account",
        value: String(data: codexCheckJSON(["tokens": ["access_token": "key-token"]]), encoding: .utf8)!
    )
    let storeKey = CodexAuthStore(environment: env, files: FakeUsageFiles(), keychain: keychain)
    guard case .oauth(let keyAccess) = storeKey.resolveAccess() else {
        fatalError("keychain should resolve to OAuth")
    }
    expect(keyAccess.accessToken, "key-token", "keychain should be the last candidate")
    if case .keychain(let service, let account) = keyAccess.source {
        expect(service, CodexAuthStore.keychainService, "keychain service is Codex Auth")
        expect(account, "codex-account", "keychain source keeps the discovered exact account")
    } else {
        fatalError("keychain source should be a keychain")
    }
}

func testCodexOAuthWinsOverAPIKey() {
    let home = "/tmp/agent-halo-oauth-priority-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["HOME": home])

    // Same file has both OPENAI_API_KEY and tokens.access_token.
    let filesSame = FakeUsageFiles(contents: [
        codexCheckAuthPath(home: home): codexCheckJSON([
            "OPENAI_API_KEY": "sk-keep",
            "tokens": ["access_token": "oauth-token", "refresh_token": "rt"],
        ]),
    ])
    let storeSame = CodexAuthStore(environment: env, files: filesSame, keychain: FakeUsageKeychain())
    guard case .oauth(let access) = storeSame.resolveAccess() else {
        fatalError("OAuth should win when both OAuth and API key are present")
    }
    expect(access.accessToken, "oauth-token", "OAuth token wins over OPENAI_API_KEY")

    // Earlier file has only OPENAI_API_KEY; later file has OAuth tokens. The
    // later OAuth login must not be shadowed by the earlier API-key-only file.
    let filesShadow = FakeUsageFiles(contents: [
        codexCheckConfigPath(home: home): codexCheckJSON(["OPENAI_API_KEY": "sk-shadow"]),
        codexCheckAuthPath(home: home): codexCheckJSON(["tokens": ["access_token": "later-oauth"]]),
    ])
    let storeShadow = CodexAuthStore(environment: env, files: filesShadow, keychain: FakeUsageKeychain())
    guard case .oauth(let shadowAccess) = storeShadow.resolveAccess() else {
        fatalError("later OAuth login should not be shadowed by earlier API-key-only file")
    }
    expect(shadowAccess.accessToken, "later-oauth", "later OAuth file should win over earlier API-key-only file")
}

func testCodexAPIKeyOnlyAndNoCredentialReturnAPIKey() {
    let home = "/tmp/agent-halo-apikey-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["HOME": home])

    // API-key-only file.
    let filesAPIKey = FakeUsageFiles(contents: [
        codexCheckAuthPath(home: home): codexCheckJSON(["OPENAI_API_KEY": "sk-only"]),
    ])
    let storeAPIKey = CodexAuthStore(environment: env, files: filesAPIKey, keychain: FakeUsageKeychain())
    guard case .apiKey = storeAPIKey.resolveAccess() else {
        fatalError("API-key-only file should resolve to API key mode")
    }

    // No recognized credential at all.
    let storeNone = CodexAuthStore(environment: env, files: FakeUsageFiles(), keychain: FakeUsageKeychain())
    guard case .apiKey = storeNone.resolveAccess() else {
        fatalError("no credential should safely degrade to API key mode")
    }
}

func testCodexNeedsRefresh() {
    let now = Date(timeIntervalSince1970: 1_000_000)
    let home = "/tmp/agent-halo-refresh-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["HOME": home])
    let store = CodexAuthStore(environment: env, files: FakeUsageFiles(), keychain: FakeUsageKeychain(), now: { now })

    // JWT exp within 5 minutes → needs refresh. Also verify resolveAccess
    // parsed exp into OAuthAccess.expiresAt.
    let soonExp = now.addingTimeInterval(3 * 60).timeIntervalSince1970
    let soonJWT = codexCheckJWT(exp: soonExp)
    let filesSoon = FakeUsageFiles(contents: [
        codexCheckAuthPath(home: home): codexCheckJSON(["tokens": ["access_token": soonJWT]]),
    ])
    let storeSoon = CodexAuthStore(environment: env, files: filesSoon, keychain: FakeUsageKeychain(), now: { now })
    guard case .oauth(let soonAccess) = storeSoon.resolveAccess() else {
        fatalError("soon-exp JWT should resolve to OAuth")
    }
    expect(soonAccess.expiresAt != nil, true, "resolveAccess should parse JWT exp into expiresAt")
    expect(
        soonAccess.expiresAt?.timeIntervalSince1970 ?? 0,
        soonExp,
        "expiresAt should match JWT exp"
    )
    expect(store.needsRefresh(soonAccess, lastRefresh: nil), true, "exp within 5 minutes needs refresh")

    // JWT exp beyond 5 minutes → no refresh.
    let farExp = now.addingTimeInterval(3600).timeIntervalSince1970
    let farJWT = codexCheckJWT(exp: farExp)
    let filesFar = FakeUsageFiles(contents: [
        codexCheckAuthPath(home: home): codexCheckJSON(["tokens": ["access_token": farJWT]]),
    ])
    let storeFar = CodexAuthStore(environment: env, files: filesFar, keychain: FakeUsageKeychain(), now: { now })
    guard case .oauth(let farAccess) = storeFar.resolveAccess() else {
        fatalError("far-exp JWT should resolve to OAuth")
    }
    expect(store.needsRefresh(farAccess, lastRefresh: nil), false, "exp beyond 5 minutes does not refresh")

    // Unreadable JWT → expiresAt is nil. Falls back to last_refresh.
    let filesBad = FakeUsageFiles(contents: [
        codexCheckAuthPath(home: home): codexCheckJSON(["tokens": ["access_token": "not-a-jwt"]]),
    ])
    let storeBad = CodexAuthStore(environment: env, files: filesBad, keychain: FakeUsageKeychain(), now: { now })
    guard case .oauth(let badAccess) = storeBad.resolveAccess() else {
        fatalError("unreadable JWT should still resolve to OAuth")
    }
    expect(badAccess.expiresAt == nil, true, "unreadable JWT should not produce expiresAt")

    let eightDays: TimeInterval = 8 * 24 * 60 * 60
    expect(
        store.needsRefresh(badAccess, lastRefresh: now.addingTimeInterval(-eightDays - 60)),
        true,
        "unreadable JWT with last_refresh older than 8 days needs refresh"
    )
    expect(
        store.needsRefresh(badAccess, lastRefresh: now.addingTimeInterval(-eightDays + 60)),
        false,
        "unreadable JWT with last_refresh newer than 8 days does not refresh"
    )
    // A new login with no exp and no last_refresh does not refresh.
    expect(
        store.needsRefresh(badAccess, lastRefresh: nil),
        false,
        "new login without exp or last_refresh does not refresh"
    )
}

func testCodexNeedsRefreshUsesStoredLastRefresh() {
    let now = Date(timeIntervalSince1970: 2_000_000)
    let formatter = ISO8601DateFormatter()
    formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]

    func storedResult(lastRefresh: Date?) -> Bool {
        let home = "/tmp/agent-halo-stored-refresh-\(UUID().uuidString)"
        let path = codexCheckAuthPath(home: home)
        var object: [String: Any] = ["tokens": ["access_token": "not-a-jwt"]]
        if let lastRefresh {
            object["last_refresh"] = formatter.string(from: lastRefresh)
        }
        let store = CodexAuthStore(
            environment: FakeUsageEnvironment(["HOME": home]),
            files: FakeUsageFiles(contents: [path: codexCheckJSON(object)]),
            keychain: FakeUsageKeychain(),
            now: { now }
        )
        guard case .oauth(let access) = store.resolveAccess() else {
            fatalError("stored last_refresh case should resolve to OAuth")
        }
        return store.needsRefresh(access)
    }

    let eightDays: TimeInterval = 8 * 24 * 60 * 60
    expect(
        storedResult(lastRefresh: now.addingTimeInterval(-eightDays - 60)),
        true,
        "stored last_refresh older than 8 days requires refresh"
    )
    expect(
        storedResult(lastRefresh: now.addingTimeInterval(-eightDays + 60)),
        false,
        "stored last_refresh newer than 8 days does not require refresh"
    )
    expect(
        storedResult(lastRefresh: nil),
        false,
        "stored login without exp or last_refresh does not require refresh"
    )
}

func testCodexFileRotationPreservesCustomKeysAndMode() {
    let home = "/tmp/agent-halo-rotate-file-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["HOME": home])
    let path = codexCheckAuthPath(home: home)
    let files = FakeUsageFiles(
        contents: [
            path: codexCheckJSON([
                "OPENAI_API_KEY": "sk-keep",
                "custom_top": "keep",
                "tokens": [
                    "access_token": "old-token",
                    "refresh_token": "old-rt",
                    "custom_nested": "keep",
                ],
            ])
        ],
        modes: [path: 0o644]
    )
    let store = CodexAuthStore(environment: env, files: files, keychain: FakeUsageKeychain())
    guard case .oauth(let access) = store.resolveAccess() else {
        fatalError("file rotation should start from OAuth")
    }
    expect(access.accessToken, "old-token", "initial token before rotation")

    let refreshedAt = Date(timeIntervalSince1970: 2_000_000)
    let rotation = CodexTokenRotation(
        accessToken: "new-token",
        refreshToken: "new-rt",
        idToken: nil,
        refreshedAt: refreshedAt
    )
    let result: OAuthAccess?
    do {
        result = try store.persist(rotation: rotation, replacing: access)
    } catch {
        fatalError("persist should not throw: \(error)")
    }
    guard let rotated = result else {
        fatalError("persist should return a rotated OAuthAccess")
    }
    expect(rotated.accessToken, "new-token", "rotated access token")
    expect(rotated.refreshToken, "new-rt", "rotated refresh token")
    expect(rotated.sourceVersion != access.sourceVersion, true, "rotated source version should change")

    let writes = files.capturedWrites()
    expect(writes.count, 1, "exactly one file write")
    expect(writes[0].path, path, "write path")
    expect(writes[0].preservingModeOf, path, "write should preserve mode of original path")
    expect(files.storedMode(for: path), 0o644, "original mode preserved")

    guard let written = try? JSONSerialization.jsonObject(with: writes[0].data) as? [String: Any] else {
        fatalError("written data should be a JSON object")
    }
    expect(written["custom_top"] as? String, "keep", "custom top-level key preserved")
    expect(written["OPENAI_API_KEY"] as? String, "sk-keep", "OPENAI_API_KEY preserved")
    let tokens = written["tokens"] as? [String: Any]
    expect(tokens?["access_token"] as? String, "new-token", "access token rotated")
    expect(tokens?["refresh_token"] as? String, "new-rt", "refresh token rotated")
    expect(tokens?["custom_nested"] as? String, "keep", "custom nested token key preserved")
    let lastRefresh = written["last_refresh"] as? String
    expect(lastRefresh != nil, "last_refresh should be set")
    // last_refresh should parse back to the refreshedAt date.
    let parser = ISO8601DateFormatter()
    parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    expect(parser.date(from: lastRefresh ?? ""), refreshedAt, "last_refresh should round-trip to refreshedAt")
}

func testCodexKeychainRotationWritesToCodexAuth() {
    let env = FakeUsageEnvironment()
    let keychain = FakeUsageKeychain()
    let kcJSON = String(data: codexCheckJSON([
        "tokens": ["access_token": "old-token", "account_id": "acct-1", "refresh_token": "old-rt"],
    ]), encoding: .utf8)!
    try? keychain.write(service: CodexAuthStore.keychainService, account: "codex-account", value: kcJSON)

    let store = CodexAuthStore(environment: env, files: FakeUsageFiles(), keychain: keychain)
    guard case .oauth(let access) = store.resolveAccess() else {
        fatalError("keychain should resolve to OAuth")
    }
    expect(access.accessToken, "old-token", "initial keychain token")
    if case .keychain(let service, _) = access.source {
        expect(service, CodexAuthStore.keychainService, "source service is Codex Auth")
    } else {
        fatalError("keychain source should be a keychain")
    }

    let refreshedAt = Date(timeIntervalSince1970: 2_000_000)
    let rotation = CodexTokenRotation(
        accessToken: "new-token",
        refreshToken: "new-rt",
        idToken: "new-id",
        refreshedAt: refreshedAt
    )
    let result: OAuthAccess?
    do {
        result = try store.persist(rotation: rotation, replacing: access)
    } catch {
        fatalError("keychain persist should not throw: \(error)")
    }
    guard let rotated = result else {
        fatalError("keychain persist should return a rotated OAuthAccess")
    }
    expect(rotated.accessToken, "new-token", "rotated keychain access token")
    expect(rotated.refreshToken, "new-rt", "rotated keychain refresh token")
    expect(rotated.accountID, "acct-1", "account_id preserved")

    // The keychain entry should be updated at the same service/account.
    expect(keychain.contains(service: CodexAuthStore.keychainService, account: "codex-account"), "keychain entry present")
    let written = try? keychain.read(service: CodexAuthStore.keychainService, account: "codex-account")
    guard let writtenObj = try? JSONSerialization.jsonObject(
        with: Data((written ?? "").utf8)
    ) as? [String: Any] else {
        fatalError("written keychain value should be a JSON object")
    }
    let tokens = writtenObj["tokens"] as? [String: Any]
    expect(tokens?["access_token"] as? String, "new-token", "keychain access token rotated")
    expect(tokens?["refresh_token"] as? String, "new-rt", "keychain refresh token rotated")
    expect(tokens?["id_token"] as? String, "new-id", "keychain id token rotated")
    expect(tokens?["account_id"] as? String, "acct-1", "keychain account_id preserved")
}

func testCodexPersistRefusesOnVersionMismatch() {
    let home = "/tmp/agent-halo-version-\(UUID().uuidString)"
    let env = FakeUsageEnvironment(["HOME": home])
    let path = codexCheckAuthPath(home: home)
    let files = FakeUsageFiles(contents: [
        path: codexCheckJSON(["tokens": ["access_token": "old-token", "refresh_token": "old-rt"]]),
    ])
    let store = CodexAuthStore(environment: env, files: files, keychain: FakeUsageKeychain())
    guard case .oauth(let access) = store.resolveAccess() else {
        fatalError("version-mismatch check should start from OAuth")
    }
    // Simulate an external change: rewrite the file with different content
    // through the same file accessor (updates the "disk" and the version).
    try? files.writeAtomically(
        codexCheckJSON(["tokens": ["access_token": "changed-externally", "refresh_token": "old-rt"]]),
        to: path,
        preservingModeOf: nil
    )

    let rotation = CodexTokenRotation(
        accessToken: "new-token",
        refreshToken: "new-rt",
        idToken: nil,
        refreshedAt: Date(timeIntervalSince1970: 2_000_000)
    )
    let result: OAuthAccess?
    do {
        result = try store.persist(rotation: rotation, replacing: access)
    } catch {
        fatalError("persist should not throw on version mismatch: \(error)")
    }
    expect(result == nil, "persist should refuse to write when the source version changed")
    // No additional write beyond the external one.
    expect(files.capturedWrites().count, 1, "persist must not write on version mismatch")
}

func testUsageDependencyFactoryDisablesProductionKeychainForPackagedVerification() {
    let productionInstantiations = LockedBox(0)
    let verification = UsageMonitoringDependencyFactory.keychain(for: .packagedVerification) {
        productionInstantiations.withValue { $0 += 1 }
        return FakeUsageSecurityItems()
    }
    expect(verification.backend, .disabled, "packaged verification must assemble the disabled backend")
    expect(productionInstantiations.value, 0, "packaged verification must not instantiate Security.framework")
    expect(
        try? verification.keychain.readFirstMatching(service: CodexAuthStore.keychainService),
        nil,
        "disabled Keychain must always report not found"
    )

    let production = UsageMonitoringDependencyFactory.keychain(for: .production) {
        productionInstantiations.withValue { $0 += 1 }
        return FakeUsageSecurityItems()
    }
    expect(production.backend, .securityFramework, "normal startup must assemble SecurityUsageKeychain")
    expect(productionInstantiations.value, 1, "production factory should instantiate its Security backend once")
}

func testSecurityUsageKeychainWritesSecretBytesInProcess() {
    let service = "Codex Auth"
    let account = "exact-account"
    let secret = "synthetic-credential-json"

    let updateItems = FakeUsageSecurityItems()
    updateItems.enqueueUpdate(status: UsageSecurityStatus.success)
    let updating = SecurityUsageKeychain(items: updateItems)
    try? updating.write(service: service, account: account, value: secret)
    let updateState = updateItems.snapshot()
    expect(updateState.updates.count, 1, "existing keychain item should use SecItemUpdate seam")
    expect(updateState.updates.first?.0.account, account, "update keeps exact source account")
    expect(updateState.updates.first?.1, Data(secret.utf8), "update passes secret bytes in process")
    expect(updateState.additions.count, 0, "successful update should not add a duplicate")

    let addItems = FakeUsageSecurityItems()
    addItems.enqueueUpdate(status: UsageSecurityStatus.itemNotFound)
    addItems.enqueueAdd(status: UsageSecurityStatus.success)
    let adding = SecurityUsageKeychain(items: addItems)
    try? adding.write(service: service, account: account, value: secret)
    let addState = addItems.snapshot()
    expect(addState.updates.count, 1, "write should first attempt exact update")
    expect(addState.additions.count, 1, "missing keychain item should use SecItemAdd seam")
    expect(addState.additions.first?.service, service, "add keeps exact service")
    expect(addState.additions.first?.account, account, "add keeps exact account")
    expect(addState.additions.first?.value, Data(secret.utf8), "add passes secret bytes in process")
}

func testSecurityUsageKeychainRefusesServiceOnlyWriteAndUpdatesOneExactAccount() {
    let service = CodexAuthStore.keychainService
    let items = StatefulUsageSecurityItems([
        (service, "account-a", "value-a"),
        (service, "account-b", "value-b"),
    ])
    let keychain = SecurityUsageKeychain(items: items)
    let discovered = try? keychain.readFirstMatching(service: service)
    expect(discovered?.account, "account-a", "legacy metadata ordering should discover the first exact account")

    do {
        try keychain.write(service: service, account: nil, value: "bulk-write")
        fatalError("service-only Keychain writes must be rejected")
    } catch let error as UsageKeychainError {
        expect(error, .missingExactAccount, "ambiguous Keychain identity must fail safely")
    } catch {
        fatalError("unexpected Keychain error type")
    }
    try? keychain.write(service: service, account: discovered?.account, value: "rotated-a")
    expect(items.value(service: service, account: "account-a"), "rotated-a", "target account rotates")
    expect(items.value(service: service, account: "account-b"), "value-b", "other same-service account stays untouched")
}

func testSecurityUsageKeychainMapsNotFoundAndFrameworkErrors() {
    let missingItems = FakeUsageSecurityItems()
    missingItems.enqueueCopy(
        SecurityUsageKeychainResult(
            status: UsageSecurityStatus.itemNotFound,
            account: nil,
            data: nil
        )
    )
    let missing = SecurityUsageKeychain(items: missingItems)
    expect(try? missing.read(service: "missing", account: "exact"), nil, "SecItem not-found should remain nil")

    let failureStatus: Int32 = -50
    let failingItems = FakeUsageSecurityItems()
    failingItems.enqueueCopy(
        SecurityUsageKeychainResult(status: failureStatus, account: nil, data: nil)
    )
    let failing = SecurityUsageKeychain(items: failingItems)
    do {
        _ = try failing.read(service: "broken", account: "exact")
        fatalError("unexpected Security.framework status should throw")
    } catch let error as UsageKeychainError {
        expect(error, .unexpectedExitCode(Int(failureStatus)), "framework error mapping stays classified")
    } catch {
        fatalError("unexpected keychain error type")
    }
}

func codexUsageResponse(
    statusCode: Int = 200,
    headers: [String: String] = [:],
    _ body: String
) -> UsageHTTPResponse {
    UsageHTTPResponse(statusCode: statusCode, headers: headers, body: Data(body.utf8))
}

func expectCodexFailure(
    _ expected: UsageProviderFailure,
    _ message: String,
    operation: () async throws -> Void
) async {
    do {
        try await operation()
        fatalError("\(message): expected \(expected), got success")
    } catch let failure as UsageProviderFailure {
        expect(failure, expected, message)
    } catch {
        fatalError("\(message): unexpected error type")
    }
}

func expectExternalAccessChange(_ result: UsageRefreshResult, _ message: String) {
    guard case .externalAccessChanged = result.outcome else {
        fatalError(message)
    }
    expect(result.snapshot == nil, "external access change must not carry a snapshot")
    expect(result.failure == nil, "external access change must not masquerade as a failure")
    expect(result.migrateCacheFrom == nil, "external access change must not masquerade as cache migration")
}

func waitForHTTPRequestCount(
    _ client: GatedUsageHTTPClient,
    _ expectedCount: Int,
    _ message: String
) async {
    for _ in 0..<10_000 {
        if await client.capturedRequests.count >= expectedCount { return }
        await Task.yield()
    }
    fatalError(message)
}

func testCodexUsageClientBuildsOnlyOfficialRequests() async {
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(#"{"plan_type":"plus"}"#))
    await http.enqueue(response: codexUsageResponse(#"{"access_token":"new-access","refresh_token":"new-refresh","id_token":"new-id"}"#))
    let client = CodexUsageClient(http: http, now: { Date(timeIntervalSince1970: 2_000_000_000) })

    _ = try? await client.fetchUsage(accessToken: "access token", accountID: "account-1")
    let refreshed = try? await client.refreshToken("refresh token+/=")
    expect(refreshed?.accessToken, "new-access", "refresh response access token")

    let requests = await http.capturedRequests
    expect(requests.count, 2, "client should issue exactly usage and refresh requests")
    expect(requests[0].method, "GET", "usage method")
    expect(requests[0].host, "chatgpt.com", "usage official host")
    expect(requests[0].path, "/backend-api/wham/usage", "usage official path")
    expect(requests[0].timeout, 10, "usage timeout")
    expect(requests[0].headers["authorization"], "Bearer access token", "usage bearer header")
    expect(requests[0].headers["chatgpt-account-id"], "account-1", "usage account header")

    expect(requests[1].method, "POST", "refresh method")
    expect(requests[1].host, "auth.openai.com", "refresh official host")
    expect(requests[1].path, "/oauth/token", "refresh official path")
    expect(requests[1].timeout, 15, "refresh timeout")
    expect(requests[1].headers["content-type"], "application/x-www-form-urlencoded", "refresh content type")
    let form = String(data: requests[1].body ?? Data(), encoding: .utf8) ?? ""
    expect(form.contains("grant_type=refresh_token"), "refresh form grant type")
    expect(form.contains("client_id=app_EMoamEEZ73f0CkXaXp7hrann"), "refresh form client id")
    expect(form.contains("refresh_token=refresh%20token%2B%2F%3D"), "refresh token must be form encoded")
    expect(
        requests.allSatisfy {
            !$0.path.contains("reset-credit") && !$0.path.contains("balance") &&
                !$0.path.contains("spend") && ["chatgpt.com", "auth.openai.com"].contains($0.host)
        },
        "Codex client must call only the two approved official endpoints"
    )

    let noAccountHTTP = RecordingUsageHTTPClient()
    await noAccountHTTP.enqueue(response: codexUsageResponse(#"{"plan_type":"free"}"#))
    _ = try? await CodexUsageClient(http: noAccountHTTP).fetchUsage(accessToken: "a", accountID: nil)
    let noAccountRequests = await noAccountHTTP.capturedRequests
    expect(noAccountRequests.first?.headers["chatgpt-account-id"] == nil, "account header must be optional")
}

func testCodexUsageClientClassifiesFailures() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)

    let transport = RecordingUsageHTTPClient()
    await transport.enqueue(error: .network)
    await expectCodexFailure(.network, "transport failure") {
        _ = try await CodexUsageClient(http: transport, now: { now }).fetchUsage(accessToken: "a", accountID: nil)
    }

    let unavailable = RecordingUsageHTTPClient()
    await unavailable.enqueue(response: codexUsageResponse(statusCode: 503, "{}"))
    await expectCodexFailure(.serviceUnavailable, "5xx failure") {
        _ = try await CodexUsageClient(http: unavailable, now: { now }).fetchUsage(accessToken: "a", accountID: nil)
    }

    let limited = RecordingUsageHTTPClient()
    await limited.enqueue(response: codexUsageResponse(statusCode: 429, headers: ["Retry-After": "120"], "{}"))
    await expectCodexFailure(.rateLimited(retryAt: now.addingTimeInterval(120)), "Retry-After seconds") {
        _ = try await CodexUsageClient(http: limited, now: { now }).fetchUsage(accessToken: "a", accountID: nil)
    }

    let dateLimited = RecordingUsageHTTPClient()
    await dateLimited.enqueue(response: codexUsageResponse(
        statusCode: 429,
        headers: ["Retry-After": "Wed, 21 Oct 2015 07:28:00 GMT"],
        "{}"
    ))
    let retryDate = Date(timeIntervalSince1970: 1_445_412_480)
    await expectCodexFailure(.rateLimited(retryAt: retryDate), "Retry-After HTTP date") {
        _ = try await CodexUsageClient(http: dateLimited, now: { now }).fetchUsage(accessToken: "a", accountID: nil)
    }

    let malformed = RecordingUsageHTTPClient()
    await malformed.enqueue(response: codexUsageResponse("not-json"))
    await expectCodexFailure(.invalidResponse, "malformed successful usage body") {
        _ = try await CodexUsageClient(http: malformed).fetchUsage(accessToken: "a", accountID: nil)
    }
}

func testURLSessionUsageHTTPClientClassifiesResponses() async {
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [UsageURLProtocolStub.self]
    configuration.urlCache = nil
    let session = URLSession(configuration: configuration)
    defer { session.invalidateAndCancel() }

    let client = URLSessionUsageHTTPClient(session: session, fixedHost: "usage.invalid")
    func request(_ path: String) -> UsageHTTPRequest {
        UsageHTTPRequest(method: "GET", host: "usage.invalid", path: path, headers: [:], body: nil)
    }

    UsageURLProtocolStub.reply.withValue { $0 = .nonHTTP }
    do {
        _ = try await client.send(request("/non-http"))
        fatalError("a non-HTTP response should be classified as invalidResponse")
    } catch let failure as UsageProviderFailure {
        expect(failure, .invalidResponse, "non-HTTP response classification")
    } catch {
        fatalError("unexpected non-HTTP response error: \(error)")
    }

    UsageURLProtocolStub.reply.withValue { $0 = .transportFailure }
    do {
        _ = try await client.send(request("/transport-failure"))
        fatalError("a transport error should fail")
    } catch let failure as UsageProviderFailure {
        expect(failure, .network, "transport error classification")
    } catch {
        fatalError("unexpected transport error: \(error)")
    }

    let body = Data("ok".utf8)
    UsageURLProtocolStub.reply.withValue { $0 = .http(statusCode: 200, body: body) }
    do {
        let response = try await client.send(request("/success"))
        expect(response.statusCode, 200, "HTTP response status")
        expect(response.body, body, "HTTP response body")
    } catch {
        fatalError("valid HTTP response should succeed: \(error)")
    }
}

func testCodexUsageMapperPlansWindowsAndRestrictedFields() {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let accountKey = AccountCacheKey(providerID: .codex, digest: "mapper")
    let planCases: [(String, String?)] = [
        ("prolite", "Pro 5x"), ("pro", "Pro 20x"), ("free", "Free"),
        ("plus", "Plus"), ("", nil), ("team_plan", "Team Plan"),
    ]
    for (raw, expected) in planCases {
        let mapped = try? CodexUsageMapper.map(
            response: codexUsageResponse(
                #"{"plan_type":"\#(raw)","rate_limit":{"primary_window":{"used_percent":1}}}"#
            ),
            accountKey: accountKey,
            now: now
        )
        expect(mapped?.planName, expected, "Codex plan mapping for \(raw)")
    }

    let response = codexUsageResponse("""
    {
      "plan_type": "pro",
      "rate_limit": {
        "primary_window": {
          "used_percent": -5,
          "limit_window_seconds": 18000,
          "reset_after_seconds": 60
        },
        "secondary_window": {
          "used_percent": 140,
          "limit_window_seconds": 604800,
          "reset_at": "2033-05-18T03:35:00Z"
        }
      },
      "additional_rate_limits": [{"rate_limit":{"primary_window":{"used_percent":55}}}],
      "credits": {"balance": 100},
      "rate_limit_reset_credits": {"available_count": 9},
      "balance": 100,
      "spend": 50
    }
    """)
    guard let mapped = try? CodexUsageMapper.map(response: response, accountKey: accountKey, now: now) else {
        fatalError("valid Codex usage should map")
    }
    expect(mapped.windows.count, 2, "mapper must expose exactly the two supported windows")
    expect(mapped.windows[0].kind, .session, "primary 5-hour window kind")
    expect(mapped.windows[0].usedPercent, 0, "used percent lower clamp")
    expect(mapped.windows[0].duration, 18_000, "session duration")
    expect(mapped.windows[0].resetsAt, now.addingTimeInterval(60), "reset-after seconds")
    expect(mapped.windows[1].kind, .weekly, "secondary weekly window kind")
    expect(mapped.windows[1].usedPercent, 100, "used percent upper clamp")
    expect(mapped.windows[1].duration, 604_800, "weekly duration")
    expect(mapped.windows[1].resetsAt, ISO8601DateFormatter().date(from: "2033-05-18T03:35:00Z"), "ISO reset time")

    let weeklyOnly = try? CodexUsageMapper.map(
        response: codexUsageResponse("""
        {"rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":604800}}}
        """),
        accountKey: accountKey,
        now: now
    )
    expect(weeklyOnly?.windows.count, 1, "missing secondary must stay absent")
    expect(weeklyOnly?.windows.first?.kind, .weekly, "sole 7-day primary must reclassify as weekly")
}

func testCodexUsageMapperClassifiesInvalidResponses() {
    let key = AccountCacheKey(providerID: .codex, digest: "invalid")
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let cases: [(UsageHTTPResponse, UsageProviderFailure)] = [
        (codexUsageResponse("{}"), .invalidResponse),
        (codexUsageResponse(#"{"credits":{"balance":100}}"#), .invalidResponse),
        (codexUsageResponse(statusCode: 401, "{}"), .signInAgain),
        (codexUsageResponse(statusCode: 503, "{}"), .serviceUnavailable),
    ]
    for (response, expected) in cases {
        do {
            _ = try CodexUsageMapper.map(response: response, accountKey: key, now: now)
            fatalError("invalid Codex response should fail")
        } catch let failure as UsageProviderFailure {
            expect(failure, expected, "Codex mapper failure classification")
        } catch {
            fatalError("unexpected Codex mapper error")
        }
    }
}

func makeCodexProviderFixture(
    token: String,
    refreshToken: String = "refresh-old",
    accountID: String? = nil,
    now: Date,
    files: (any UsageFileAccessing)? = nil
) -> (CodexAuthStore, OAuthAccess, any UsageFileAccessing, String) {
    let home = "/tmp/agent-halo-provider-\(UUID().uuidString)"
    let path = codexCheckAuthPath(home: home)
    var tokens: [String: Any] = ["access_token": token, "refresh_token": refreshToken]
    if let accountID { tokens["account_id"] = accountID }
    let initial = codexCheckJSON(["tokens": tokens, "last_refresh": "2030-01-01T00:00:00Z"])
    let resolvedFiles: any UsageFileAccessing = files ?? FakeUsageFiles(contents: [path: initial])
    if let custom = resolvedFiles as? CodexCheckFailingFiles {
        custom.setData(initial, at: path)
    }
    let store = CodexAuthStore(
        environment: FakeUsageEnvironment(["HOME": home]),
        files: resolvedFiles,
        keychain: FakeUsageKeychain(),
        now: { now }
    )
    guard case .oauth(let access) = store.resolveAccess() else {
        fatalError("provider fixture should resolve OAuth")
    }
    return (store, access, resolvedFiles, path)
}

func testCodexProviderAdoptsExternalSourceWithoutMigration() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let oldJWT = codexCheckJWT(exp: now.addingTimeInterval(60).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: oldJWT, now: now)
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let externalJWT = codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970)
    try? files.writeAtomically(
        codexCheckJSON(["tokens": ["access_token": externalJWT, "refresh_token": "external-refresh"]]),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(#"{"plan_type":"plus"}"#))
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let result = await provider.refresh(using: .oauth(fixture.1))
    let requests = await http.capturedRequests
    expectExternalAccessChange(result, "external source adoption must be a typed Coordinator outcome")
    expect(requests.count, 0, "Provider must not continue under an externally replaced source")
}

func codexExternalCredential(
    accessToken: String,
    refreshToken: String = "external-refresh"
) -> Data {
    codexCheckJSON([
        "tokens": [
            "access_token": accessToken,
            "refresh_token": refreshToken,
        ],
    ])
}

func testCodexProviderDetectsExternalSourceDuringRefresh() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(60).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [.response(codexUsageResponse(#"{"access_token":"stale-rotation"}"#))],
        gatedPath: "/oauth/token"
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 1, "Codex refresh request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-during-refresh"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login during Codex refresh must discard the old refresh result"
    )
}

func testCodexProviderDetectsExternalSourceAfterRefreshFailure() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(60).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [.failure(.network)],
        gatedPath: "/oauth/token"
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 1, "failing Codex refresh request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-after-refresh-failure"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login during Codex refresh failure must supersede the old error"
    )
}

func testCodexProviderDetectsExternalSourceDuringUsageSuccess() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [.response(codexUsageResponse(#"{"plan_type":"pro"}"#))],
        gatedPath: "/backend-api/wham/usage"
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 1, "Codex Usage request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-during-usage"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login during Codex Usage success must discard the old snapshot"
    )
}

func testCodexProviderDetectsExternalSourceDuringUsageFailure() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [.failure(.network)],
        gatedPath: "/backend-api/wham/usage"
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 1, "Codex failing Usage request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-after-network"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login during Codex transport failure must supersede the old error"
    )
}

func testCodexProviderDetectsExternalSourceOnFirstUnauthorized() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [.response(codexUsageResponse(statusCode: 401, "{}"))],
        gatedPath: "/backend-api/wham/usage"
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 1, "Codex first 401 request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-on-first-401"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login at first Codex 401 must prevent old-token refresh"
    )
    expect(await http.capturedRequests.count, 1, "first-401 access change must stop before refresh")
}

func testCodexProviderDetectsExternalSourceDuringUnauthorizedRetry() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [
            .response(codexUsageResponse(statusCode: 401, "{}")),
            .response(codexUsageResponse(#"{"access_token":"internal-retry"}"#)),
            .response(codexUsageResponse(statusCode: 401, "{}")),
        ],
        gatedPath: "/backend-api/wham/usage",
        gatedMatchNumber: 2
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 3, "Codex retry Usage request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-during-retry"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login during Codex 401 retry must supersede sign-in failure"
    )
}

func testCodexProviderDetectsExternalSourceDuringUnauthorizedRetryFailure() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fixture = makeCodexProviderFixture(
        token: codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970),
        now: now
    )
    guard let files = fixture.2 as? FakeUsageFiles else { fatalError("expected fake files") }
    let http = GatedUsageHTTPClient(
        outcomes: [
            .response(codexUsageResponse(statusCode: 401, "{}")),
            .response(codexUsageResponse(#"{"access_token":"internal-retry"}"#)),
            .failure(.network),
        ],
        gatedPath: "/backend-api/wham/usage",
        gatedMatchNumber: 2
    )
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let request = Task { await provider.refresh(using: .oauth(fixture.1)) }
    await waitForHTTPRequestCount(http, 3, "failing Codex retry request did not enter gate")
    try? files.writeAtomically(
        codexExternalCredential(accessToken: "external-during-retry-failure"),
        to: fixture.3,
        preservingModeOf: fixture.3
    )
    await http.resume()
    expectExternalAccessChange(
        await request.value,
        "external login during Codex retry failure must supersede the old error"
    )
}

func testCodexProviderRefreshesProactively() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let expiring = codexCheckJWT(exp: now.addingTimeInterval(60).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: expiring, accountID: "stable-account", now: now)
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(#"{"access_token":"proactive-access","refresh_token":"proactive-refresh"}"#))
    await http.enqueue(response: codexUsageResponse(#"{"plan_type":"free"}"#))
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let result = await provider.refresh(using: .oauth(fixture.1))
    let requests = await http.capturedRequests
    expect(result.failure == nil, "proactive refresh should succeed")
    expect(requests.map(\.path), ["/oauth/token", "/backend-api/wham/usage"], "proactive refresh order")
    expect(requests.last?.headers["authorization"], "Bearer proactive-access", "proactive token must serve usage")
    expect(result.migrateCacheFrom == nil, "stable account id should not require cache migration")
}

func testCodexProviderRetriesOneUnauthorizedAndMigratesCache() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fresh = codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: fresh, now: now)
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(statusCode: 401, "{}"))
    await http.enqueue(response: codexUsageResponse(#"{"access_token":"retry-access","refresh_token":"retry-refresh"}"#))
    await http.enqueue(response: codexUsageResponse(#"{"plan_type":"pro"}"#))
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let result = await provider.refresh(using: .oauth(fixture.1))
    let requests = await http.capturedRequests
    expect(result.failure == nil, "first 401 should refresh and retry")
    expect(
        requests.map(\.path),
        ["/backend-api/wham/usage", "/oauth/token", "/backend-api/wham/usage"],
        "401 retry request order"
    )
    expect(requests.last?.headers["authorization"], "Bearer retry-access", "retry must use rotated token")
    expect(result.migrateCacheFrom, fixture.1.accountKey, "internal rotation should return old cache key")
}

func testCodexProviderStopsAfterSecondUnauthorized() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let fresh = codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: fresh, now: now)
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(statusCode: 401, "{}"))
    await http.enqueue(response: codexUsageResponse(#"{"access_token":"retry-access"}"#))
    await http.enqueue(response: codexUsageResponse(statusCode: 401, "{}"))
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let result = await provider.refresh(using: .oauth(fixture.1))
    let requests = await http.capturedRequests
    expect(result.failure, .signInAgain, "second 401 should require sign in")
    expect(requests.filter { $0.path == "/backend-api/wham/usage" }.count, 2, "usage must retry at most once")
}

func testCodexProviderRefreshCodesRequireSignIn() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    for code in ["refresh_token_expired", "refresh_token_reused", "refresh_token_invalidated"] {
        let fresh = codexCheckJWT(exp: now.addingTimeInterval(3_600).timeIntervalSince1970)
        let fixture = makeCodexProviderFixture(token: fresh, now: now)
        let http = RecordingUsageHTTPClient()
        await http.enqueue(response: codexUsageResponse(statusCode: 401, "{}"))
        await http.enqueue(response: codexUsageResponse(statusCode: 400, #"{"error":{"code":"\#(code)"}}"#))
        let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })
        let result = await provider.refresh(using: .oauth(fixture.1))
        expect(result.failure, .signInAgain, "refresh code \(code) should require sign in")
    }
}

final class CodexCheckFailingFiles: UsageFileAccessing, @unchecked Sendable {
    private let data = LockedBox<[String: Data]>([:])

    func setData(_ value: Data, at path: String) {
        data.withValue { $0[path] = value }
    }

    func readDataIfPresent(at path: String) throws -> Data? {
        data.value[path]
    }

    func ensureDirectory(at path: String, mode: mode_t) throws {}

    func writeAtomically(_ data: Data, to path: String, preservingModeOf existingPath: String?) throws {
        throw UsageProviderFailure.network
    }
}

func testCodexProviderUsesRotatedTokenWhenWritebackFails() async {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let files = CodexCheckFailingFiles()
    let expiring = codexCheckJWT(exp: now.addingTimeInterval(60).timeIntervalSince1970)
    let fixture = makeCodexProviderFixture(token: expiring, now: now, files: files)
    let http = RecordingUsageHTTPClient()
    await http.enqueue(response: codexUsageResponse(#"{"access_token":"memory-access","refresh_token":"memory-refresh"}"#))
    await http.enqueue(response: codexUsageResponse(#"{"plan_type":"plus"}"#))
    let provider = CodexUsageProvider(authStore: fixture.0, usageClient: CodexUsageClient(http: http), now: { now })

    let result = await provider.refresh(using: .oauth(fixture.1))
    let requests = await http.capturedRequests
    expect(result.failure == nil, "writeback failure must not fail current usage request")
    expect(requests.last?.headers["authorization"], "Bearer memory-access", "in-memory token must serve current request")
    expect(result.snapshot?.accountKey, fixture.1.accountKey, "unpersisted Codex rotation keeps the stable account key")
    expect(result.migrateCacheFrom == nil, "unpersisted Codex rotation must not migrate cache")
}

func testCodexDetailsContentResolverModesAndWarnings() {
    let now = Date(timeIntervalSince1970: 2_100_000_000)
    let updatedAt = now.addingTimeInterval(-900)
    let key = AccountCacheKey(providerID: .codex, digest: "codex-details")
    let window = UsageWindow(
        kind: .weekly,
        usedPercent: 23,
        resetsAt: now.addingTimeInterval(1_800),
        duration: 604_800
    )
    let snapshot = UsageSnapshot(
        providerID: .codex,
        accountKey: key,
        planName: "Plus",
        windows: [window],
        refreshedAt: updatedAt
    )
    let session = SessionDetailsSnapshot(
        projectName: "AgentHalo",
        sessionTitle: "Codex-only checks",
        modelName: "gpt-5",
        inputTokens: 1_200,
        outputTokens: 240
    )

    let oauth = DetailsContentResolver.resolve(
        providerID: .codex,
        monitorState: UsageMonitorState(
            providerID: .codex,
            accessMode: .oauth,
            snapshot: snapshot,
            status: .fresh(updatedAt: now)
        ),
        isOffline: false,
        sessionDetails: session,
        contextUsedPercent: 42,
        now: now
    )
    expect(oauth.providerName, "Codex", "Codex OAuth provider name")
    expect(oauth.planName, "Plus", "Codex OAuth plan")
    expect(oauth.contextUsedPercent, 42, "Codex OAuth context")
    expect(
        oauth.body,
        .usage(UsageDetailsModel(windows: [window], status: .fresh(updatedAt: now))),
        "Codex OAuth should render usage"
    )

    let api = DetailsContentResolver.resolve(
        providerID: .codex,
        monitorState: UsageMonitorState(providerID: .codex, accessMode: .apiKey),
        isOffline: false,
        sessionDetails: session,
        contextUsedPercent: 37,
        now: now
    )
    expect(api.planName == nil, "API mode must not expose an OAuth plan")
    expect(api.usageWarning == nil, "API mode must not expose a usage warning")
    expect(api.contextUsedPercent, 37, "API mode keeps exact context")
    expect(api.body, .session(session), "API mode keeps session details")

    let offline = DetailsContentResolver.resolve(
        providerID: .codex,
        monitorState: UsageMonitorState(providerID: .codex, accessMode: .apiKey),
        isOffline: true,
        sessionDetails: session,
        contextUsedPercent: 71,
        now: now
    )
    expect(offline.contextUsedPercent == nil, "offline mode clears context")
    expect(
        offline.body,
        .session(SessionDetailsSnapshot()),
        "offline API mode clears session details"
    )

    let signIn = DetailsContentResolver.resolve(
        providerID: .codex,
        monitorState: UsageMonitorState(
            providerID: .codex,
            accessMode: .oauth,
            snapshot: snapshot,
            status: .signInAgain,
            lastFailure: .signInAgain
        ),
        isOffline: false,
        sessionDetails: session,
        contextUsedPercent: nil,
        now: now
    )
    expect(
        signIn.usageWarning,
        L10n.shared["usage.warning.sign_in_codex"],
        "Codex sign-in warning"
    )

    let stale = DetailsContentResolver.resolve(
        providerID: .codex,
        monitorState: UsageMonitorState(
            providerID: .codex,
            accessMode: .oauth,
            snapshot: snapshot,
            status: .stale(updatedAt: updatedAt),
            lastFailure: .network
        ),
        isOffline: false,
        sessionDetails: session,
        contextUsedPercent: nil,
        now: now
    )
    expect(
        stale.usageWarning,
        L10n.shared.format("usage.warning.stale", detailsResolverUpdateTime(updatedAt, now: now)),
        "stale warning should precede a network warning"
    )
}

func testCodexUsageMonitoringLocalization() {
    let originalLanguage = L10n.shared.currentLanguage
    defer { L10n.shared.setLanguage(originalLanguage) }

    let translations: [(String, [(String, String)])] = [
        ("en", [
            ("quota.5h", "5-Hour"),
            ("quota.weekly", "Weekly"),
            ("quota.current_available", "Available Quota"),
            ("quota.none_available", "No Available Quota"),
            ("usage.warning.sign_in_codex", "Sign in to Codex again to refresh usage."),
        ]),
        ("zh", [
            ("quota.5h", "五小时"),
            ("quota.weekly", "每周"),
            ("quota.current_available", "当前可用额度"),
            ("quota.none_available", "暂无可用额度"),
            ("usage.warning.sign_in_codex", "请重新登录 Codex 以刷新使用情况。"),
        ]),
    ]

    for (language, expectedTranslations) in translations {
        L10n.shared.setLanguage(language)
        for (key, expectedValue) in expectedTranslations {
            expect(L10n.shared[key], expectedValue, "\(language) localization for \(key)")
        }
    }
}

func runUsageModelChecks() async throws {
    try testFilesystemUsageFilesWritesEmptyAndNonEmptyDataWithMode0600()
    await testCoordinatorAPIKeyModeSkipsRefresh()
    await testCoordinatorDiskSnapshotIsExactStaleAndDoesNotSuppressRefresh()
    await testCoordinatorCurrentRunFreshnessAndTenMinuteStaleness()
    await testCoordinatorLatestConcurrentPrepareWins()
    await testCoordinatorSupersededFirstEnsureFreshAwaitsCommittedContext()
    await testCoordinatorAccountSwitchDoesNotJoinOrCommitOldRefresh()
    await testCoordinatorInvalidatedMigrationHasNoCacheSideEffects()
    await testCoordinatorKeepsStableCacheWhenCodexRotationCannotPersist()
    await testCoordinatorRecoversRealCodexProviderAfterExternalLogin()
    await testCoordinatorCancelAllAwaitsRefreshQuiescence()
    await testCoordinatorCancelAllTracksBlockedCacheSnapshotThroughCommitBoundary()
    try await testUsageSnapshotCacheRemovesEntriesOlderThanThirtyDays()
    try await testUsageSnapshotCacheIgnoresCorruptAndUnknownPayloads()
    try await testUsageSnapshotCacheCreatesPrivateParentAndFile()
    testCodexHomeWinsOverDefaultPaths()
    testCodexDiscoveryOrderWithoutCodexHome()
    testCodexOAuthWinsOverAPIKey()
    testCodexAPIKeyOnlyAndNoCredentialReturnAPIKey()
    testCodexNeedsRefresh()
    testCodexNeedsRefreshUsesStoredLastRefresh()
    testCodexFileRotationPreservesCustomKeysAndMode()
    testCodexKeychainRotationWritesToCodexAuth()
    testCodexPersistRefusesOnVersionMismatch()
    testUsageDependencyFactoryDisablesProductionKeychainForPackagedVerification()
    testSecurityUsageKeychainWritesSecretBytesInProcess()
    testSecurityUsageKeychainRefusesServiceOnlyWriteAndUpdatesOneExactAccount()
    testSecurityUsageKeychainMapsNotFoundAndFrameworkErrors()
    await testCodexUsageClientBuildsOnlyOfficialRequests()
    await testCodexUsageClientClassifiesFailures()
    await testURLSessionUsageHTTPClientClassifiesResponses()
    testCodexUsageMapperPlansWindowsAndRestrictedFields()
    testCodexUsageMapperClassifiesInvalidResponses()
    await testCodexProviderAdoptsExternalSourceWithoutMigration()
    await testCodexProviderDetectsExternalSourceDuringRefresh()
    await testCodexProviderDetectsExternalSourceAfterRefreshFailure()
    await testCodexProviderDetectsExternalSourceDuringUsageSuccess()
    await testCodexProviderDetectsExternalSourceDuringUsageFailure()
    await testCodexProviderDetectsExternalSourceOnFirstUnauthorized()
    await testCodexProviderDetectsExternalSourceDuringUnauthorizedRetry()
    await testCodexProviderDetectsExternalSourceDuringUnauthorizedRetryFailure()
    await testCodexProviderRefreshesProactively()
    await testCodexProviderRetriesOneUnauthorizedAndMigratesCache()
    await testCodexProviderStopsAfterSecondUnauthorized()
    await testCodexProviderRefreshCodesRequireSignIn()
    await testCodexProviderUsesRotatedTokenWhenWritebackFails()
    testCodexDetailsContentResolverModesAndWarnings()
    testCodexUsageMonitoringLocalization()
}
