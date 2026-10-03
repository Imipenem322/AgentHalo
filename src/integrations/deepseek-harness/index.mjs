import { mkdir, rename, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { randomUUID } from "node:crypto";

const PLUGIN_NAME = "agenthalo-deepseek-harness-observer";
const PROJECTION_KEY = "agentHaloDeepSeekHarnessObserver";
const BRIDGE_VERSION = "1.0.0";
const DSH_VERSION = "0.2.0-rc.2";
const DESKTOP_APP_ID = "com.deepseek.dsh";
const HEARTBEAT_MS = 2000;
const REFRESH_DEBOUNCE_MS = 100;
const MAX_TASKS = 512;
const MAX_SNAPSHOT_BYTES = 2 * 1024 * 1024;
const COMPLETED_RETENTION_MS = 5 * 60 * 1000;
const ERROR_RETENTION_MS = 12 * 60 * 60 * 1000;

function isRecord(value) {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseObservation(value) {
	if (!isRecord(value) || typeof value.running !== "boolean" ||
		!Array.isArray(value.activeTools) || !Array.isArray(value.pendingApprovals)) {
		throw new TypeError("invalid AgentHalo observation projection");
	}
	return value;
}

function observationView(state) {
	return {
		running: state.running,
		turnId: state.turnId,
		lastActivityMs: state.lastActivityMs,
		activeTools: state.activeTools,
		pendingApprovals: state.pendingApprovals,
		actualModel: state.actualModel,
		terminal: state.terminal
	};
}

function applyObservation(state, event) {
	switch (event.type) {
		case "turn/start":
			return {
				...state,
				running: true,
				turnId: event.data.turn,
				activeTools: [],
				pendingApprovals: [],
				terminal: null,
				lastActivityMs: event.time
			};
		case "turn/end":
			return {
				...state,
				running: false,
				activeTools: [],
				pendingApprovals: [],
				terminal: {
					turnId: event.data.turn,
					kind: event.data.reason?.kind || "unknown",
					endedAtMs: event.time
				},
				lastActivityMs: event.time
			};
		case "tool/call": {
			const call = { callId: event.data.callId, name: event.data.name };
			return {
				...state,
				activeTools: [...state.activeTools.filter(item => item.callId !== call.callId), call],
				lastActivityMs: event.time
			};
		}
		case "tool/result": {
			const message = event.data.message;
			const callId = message.toolCallId || message.source?.callId;
			return {
				...state,
				activeTools: state.activeTools.filter(item => item.callId !== callId),
				lastActivityMs: event.time
			};
		}
		case "approval/asked": {
			const request = { id: event.data.id, turnId: state.turnId };
			return {
				...state,
				pendingApprovals: [...state.pendingApprovals.filter(item => item.id !== request.id), request],
				lastActivityMs: event.time
			};
		}
		case "approval/decided":
			return {
				...state,
				pendingApprovals: state.pendingApprovals.filter(item => item.id !== event.data.id),
				lastActivityMs: event.time
			};
		case "request/header": {
			const config = event.data.header.config;
			return {
				...state,
				actualModel: {
					providerId: config.provider,
					modelId: config.model,
					turnId: state.turnId,
					observedAtMs: event.time
				},
				lastActivityMs: event.time
			};
		}
		default:
			if (["user/message", "step/start"].includes(event.type)) {
				return { ...state, lastActivityMs: event.time };
			}
			return state;
	}
}

const observationSchema = { parse: parseObservation };
const observationProjection = {
	key: PROJECTION_KEY,
	stateVersion: 1,
	stateSchema: observationSchema,
	init: () => ({
		running: false,
		turnId: null,
		lastActivityMs: null,
		activeTools: [],
		pendingApprovals: [],
		actualModel: null,
		terminal: null
	}),
	apply: applyObservation,
	wire: {
		viewSchema: observationSchema,
		view: observationView
	}
};

export const name = PLUGIN_NAME;
export const inject = ["sessionProjections", "agents", "userQuestions", "subagents"];

export function apply(ctx) {
	const instanceId = randomUUID();
	const profileDir = path.resolve(process.argv[3]);
	const runtimeDir = path.resolve(process.argv[2]);
	const runtimeRoot = path.join(
		process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"),
		"CodexHalo", "integrations", "deepseek-harness", "runtime"
	);
	const snapshotPath = path.join(runtimeRoot, `${instanceId}.json`);
	const hostStartUtc = new Date(Date.now() - process.uptime() * 1000).toISOString();
	const questionServiceAvailable = ctx.userQuestions !== undefined;
	const subagentServiceAvailable = ctx.subagents !== undefined;
	let revision = 0;
	let heartbeat;
	let refreshTimer;
	let refreshRunning = false;
	let refreshAgain = false;
	let disposed = false;
	let inflight = Promise.resolve();
	let lastTasks = [];
	let lastBaselineReady = false;
	let lastCapabilities = unknownCapabilities();

	ctx.sessionProjections.register(observationProjection);

	function scheduleRefresh() {
		if (disposed) return;
		refreshAgain = true;
		if (refreshTimer !== undefined) return;
		refreshTimer = setTimeout(() => {
			refreshTimer = undefined;
			startRefresh();
		}, REFRESH_DEBOUNCE_MS);
	}

	function startRefresh() {
		if (disposed || refreshRunning) return;
		refreshRunning = true;
		inflight = (async () => {
			do {
				refreshAgain = false;
				try {
					collect();
				} catch {
					lastBaselineReady = false;
					lastTasks = [];
					lastCapabilities = unknownCapabilities();
				}
				await publish();
			} while (refreshAgain && !disposed);
		})().catch(() => {
			lastBaselineReady = false;
			lastTasks = [];
			lastCapabilities = unknownCapabilities();
		});
		void inflight.then(() => {
			refreshRunning = false;
			if (refreshAgain && !disposed) scheduleRefresh();
		});
	}

	function unknownCapabilities() {
		return {
			lifecycle: false,
			toolName: false,
			attention: false,
			subagents: false,
			actualModel: false,
			nextModel: false
		};
	}

	function collect() {
		const allAgents = ctx.agents.list();
		const agentsBySession = new Map();
		const agentsById = new Map();
		for (const agent of allAgents) {
			agentsBySession.set(agent.session.id, agent);
			agentsById.set(agent.id, agent);
		}

		const capabilities = {
			lifecycle: true,
			toolName: true,
			attention: questionServiceAvailable,
			subagents: subagentServiceAvailable,
			actualModel: true,
			nextModel: true
		};
		const currentTasks = [];
		const closures = [];

		for (const rootAgent of ctx.agents.roots()) {
			const closure = collectRoot(rootAgent, agentsBySession, agentsById, capabilities);
			currentTasks.push(closure.task);
			closures.push(closure);
		}

		const rootForMember = new Map();
		for (const closure of closures) {
			for (const memberId of closure.memberIds) {
				const owner = rootForMember.get(memberId);
				if (owner !== undefined && owner !== closure.task.rootSessionId) {
					closure.task.relationsResolved = false;
					const other = closures.find(item => item.task.rootSessionId === owner);
					if (other !== undefined) other.task.relationsResolved = false;
				}
				rootForMember.set(memberId, closure.task.rootSessionId);
			}
		}

		let baselineReady = currentTasks.every(task => task.relationsResolved === true);
		const retainedTasks = retainTerminals(lastTasks, currentTasks, Date.now());
		retainedTasks.sort((left, right) =>
			Date.parse(right.lastActivityUtc || "1970-01-01T00:00:00.000Z") -
			Date.parse(left.lastActivityUtc || "1970-01-01T00:00:00.000Z"));
		lastTasks = retainedTasks.length > MAX_TASKS
			? pruneIdleTasks(retainedTasks) : retainedTasks;
		if (lastTasks === null) {
			baselineReady = false;
			lastTasks = [];
		}
		lastBaselineReady = baselineReady;
		lastCapabilities = capabilities;
	}

	function collectRoot(rootAgent, agentsBySession, agentsById, capabilities) {
		const queue = [rootAgent];
		const visited = new Set();
		const members = [];
		let relationsResolved = true;
		let attentionResolved = questionServiceAvailable;
		let nextModelResolved = true;
		let observationResolved = true;

		while (queue.length > 0) {
			const agent = queue.shift();
			const sessionId = agent.session.id;
			if (visited.has(sessionId)) {
				relationsResolved = false;
				continue;
			}
			visited.add(sessionId);
			const snapshot = ctx.sessionProjections.snapshot(agent.session);
			const values = snapshot.values;
			const observation = values[PROJECTION_KEY];
			if (!isRecord(observation) || !Array.isArray(observation.activeTools) ||
				!Array.isArray(observation.pendingApprovals)) {
				observationResolved = false;
			}
			if (!Array.isArray(values.userQuestions?.active)) attentionResolved = false;
			if (values.modelSelection === undefined) nextModelResolved = false;
			const catalog = values.subagentCatalog;
			if (!Array.isArray(catalog)) {
				relationsResolved = false;
			} else {
				for (const child of catalog) {
					if (typeof child.id !== "string" || child.id.length === 0) {
						relationsResolved = false;
						continue;
					}
					const childAgent = agentsBySession.get(child.id) || agentsById.get(child.id);
					if (childAgent !== undefined) queue.push(childAgent);
				}
			}
			members.push({ agent, sessionId, values, observation });
		}

		const root = members[0];
		const rootObservation = root.observation;
		const activeTools = [];
		let activeToolCount = 0;
		let pendingApprovalCount = 0;
		let blockingQuestionCount = 0;
		let relatedRunning = 0;
		let lastActivityMs = 0;

		for (const member of members) {
			const observation = member.observation;
			if (!isRecord(observation)) continue;
			activeToolCount += observation.activeTools.length;
			activeTools.push(...observation.activeTools.map(item => item.name));
			pendingApprovalCount += observation.pendingApprovals.length;
			const questions = member.values.userQuestions?.active;
			if (Array.isArray(questions)) {
				blockingQuestionCount += questions.filter(item => item.state === "open").length;
			}
			if (member !== root && member.agent.status === "running") relatedRunning++;
			lastActivityMs = Math.max(lastActivityMs, Number(observation.lastActivityMs) || 0);
		}

		const rootRunning = root.agent.status === "running";
		const modelSelection = root.values.modelSelection;
		const selectedModel = modelSelection?.next || modelSelection?.lastUsed || null;
		const mainModel = rootObservation?.actualModel == null ? null : {
			providerId: rootObservation.actualModel.providerId,
			modelId: rootObservation.actualModel.modelId,
			name: null,
			source: "actual-request",
			turnId: rootObservation.actualModel.turnId == null
				? null : String(rootObservation.actualModel.turnId),
			requestId: null,
			observedAtUtc: new Date(rootObservation.actualModel.observedAtMs).toISOString()
		};
		const nextModel = selectedModel == null ? null : {
			providerId: selectedModel.provider,
			modelId: selectedModel.model,
			name: null,
			source: "next-selection",
			turnId: null,
			requestId: null,
			observedAtUtc: new Date().toISOString()
		};

		let terminal = null;
		if (rootObservation?.terminal != null) {
			const terminalAge = Date.now() - rootObservation.terminal.endedAtMs;
			const retention = rootObservation.terminal.kind === "completed"
				? COMPLETED_RETENTION_MS : ERROR_RETENTION_MS;
			if (terminalAge <= retention) {
				terminal = {
					turnId: String(rootObservation.terminal.turnId),
					kind: rootObservation.terminal.kind,
					endedAtUtc: new Date(rootObservation.terminal.endedAtMs).toISOString()
				};
			}
		}

		const relationshipsReady = relationsResolved && observationResolved;
		if (!attentionResolved) capabilities.attention = false;
		if (!relationsResolved) capabilities.subagents = false;
		if (!observationResolved) {
			capabilities.lifecycle = false;
			capabilities.toolName = false;
			capabilities.actualModel = false;
		}
		if (!nextModelResolved) capabilities.nextModel = false;

		return {
			task: {
				rootSessionId: root.sessionId,
				title: typeof root.values.title === "string" ? root.values.title : null,
				turnId: rootObservation?.turnId == null ? null : String(rootObservation.turnId),
				running: rootRunning || relatedRunning > 0,
				rootRunning,
				relatedRunning,
				relationsResolved: relationshipsReady,
				activeToolCount: relationshipsReady ? activeToolCount : null,
				activeToolNames: relationshipsReady ? activeTools : null,
				pendingApprovalCount: relationshipsReady ? pendingApprovalCount : null,
				blockingQuestionCount: relationshipsReady && attentionResolved
					? blockingQuestionCount : null,
				lastActivityUtc: lastActivityMs > 0 ? new Date(lastActivityMs).toISOString() : null,
				mainModel,
				nextModel,
				terminal
			},
			memberIds: [...visited]
		};
	}

	function retainTerminals(previous, current, now) {
		const currentById = new Map(current.map(task => [task.rootSessionId, task]));
		for (const task of previous) {
			if (currentById.has(task.rootSessionId) || task.terminal === null) continue;
			const endedAt = Date.parse(task.terminal.endedAtUtc);
			const retention = task.terminal.kind === "completed"
				? COMPLETED_RETENTION_MS : ERROR_RETENTION_MS;
			if (now - endedAt <= retention) currentById.set(task.rootSessionId, task);
		}
		return [...currentById.values()];
	}

	function isEssentialTask(task) {
		return task.running || task.activeToolCount > 0 || task.pendingApprovalCount > 0 ||
			task.blockingQuestionCount > 0 || task.terminal !== null;
	}

	function pruneIdleTasks(tasks, idleLimit = MAX_TASKS) {
		const essential = tasks.filter(isEssentialTask);
		if (essential.length > MAX_TASKS) return null;
		const idles = tasks.filter(task => !isEssentialTask(task));
		const selected = [...essential, ...idles.slice(0,
			Math.min(idleLimit, Math.max(0, MAX_TASKS - essential.length)))];
		selected.sort((left, right) =>
			Date.parse(right.lastActivityUtc || "1970-01-01T00:00:00.000Z") -
			Date.parse(left.lastActivityUtc || "1970-01-01T00:00:00.000Z"));
		return selected;
	}

	async function publish() {
		if (disposed) return;
		revision++;
		const document = {
			schemaVersion: 1,
			bridgeVersion: BRIDGE_VERSION,
			dshVersion: DSH_VERSION,
			profile: "desktop",
			instanceId,
			hostPid: process.pid,
			hostStartUtc,
			hostExecutable: process.execPath,
			hostParentPid: process.ppid,
			hostRuntime: "electron-node",
			desktopAppId: DESKTOP_APP_ID,
			hostProfileDir: profileDir,
			hostRuntimeDir: runtimeDir,
			revision,
			publishedAtUtc: new Date().toISOString(),
			baselineReady: lastBaselineReady,
			capabilities: lastCapabilities,
			tasks: lastTasks
		};
		let contents = JSON.stringify(document);
		if (Buffer.byteLength(contents, "utf8") > MAX_SNAPSHOT_BYTES) {
			const pruned = pruneIdleTasks(lastTasks, 1);
			if (pruned !== null) {
				document.tasks = pruned;
				contents = JSON.stringify(document);
				lastTasks = pruned;
			}
		}
		if (document.tasks.length > MAX_TASKS ||
			Buffer.byteLength(contents, "utf8") > MAX_SNAPSHOT_BYTES) {
			document.baselineReady = false;
			document.tasks = [];
			contents = JSON.stringify(document);
			lastBaselineReady = false;
			lastTasks = [];
		}

		const tempPath = `${snapshotPath}.${revision}.${randomUUID()}.tmp`;
		try {
			await mkdir(runtimeRoot, { recursive: true });
			await writeFile(tempPath, contents, "utf8");
			await rename(tempPath, snapshotPath);
		} catch {
			lastBaselineReady = false;
			lastTasks = [];
			lastCapabilities = unknownCapabilities();
		} finally {
			await rm(tempPath, { force: true }).catch(() => {});
		}
	}

	ctx.on("session/event", scheduleRefresh);
	ctx.on("session/created", scheduleRefresh);
	ctx.on("session/disposed", scheduleRefresh);
	ctx.on("agent/status", scheduleRefresh);

	scheduleRefresh();
	heartbeat = setInterval(scheduleRefresh, HEARTBEAT_MS);
	ctx.effect(() => () => {
		disposed = true;
		clearInterval(heartbeat);
		if (refreshTimer !== undefined) clearTimeout(refreshTimer);
		void inflight.then(() => rm(snapshotPath, { force: true })).catch(() => {});
	});
}
