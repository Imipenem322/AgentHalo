import { access, cp, mkdir, readFile, rename, rm, stat } from "node:fs/promises";
import { spawn } from "node:child_process";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";

const PACKAGE_NAME = "dsh-agenthalo";
const EXECUTABLE_NAME = "AgentHalo.Dsh.exe";
const MANAGED_ARGUMENT = "--dsh-managed";
const HELPER_CLOSE_TIMEOUT_MS = 10_000;

function isFullyQualifiedWindowsPath(value) {
	if (typeof value !== "string" || !path.win32.isAbsolute(value)) return false;
	return /^[A-Za-z]:[\\/]/.test(value) ||
		/^(?:\\\\|\/\/)[^\\/]+[\\/][^\\/]+(?:[\\/]|$)/.test(value);
}

async function isFile(filePath) {
	try {
		return (await stat(filePath)).isFile();
	} catch (error) {
		if (error?.code === "ENOENT") return false;
		throw error;
	}
}

export function resolveAgentHaloHome(env = process.env) {
	const configuredHome = env.AGENTHALO_DSH_HOME;
	if (configuredHome !== undefined) {
		if (!isFullyQualifiedWindowsPath(configuredHome)) {
			throw new Error("AGENTHALO_DSH_HOME must be a fully qualified Windows drive or UNC path.");
		}
		return path.win32.resolve(configuredHome);
	}

	const localAppData = env.LOCALAPPDATA || path.win32.join(os.homedir(), "AppData", "Local");
	if (!isFullyQualifiedWindowsPath(localAppData)) {
		throw new Error("LOCALAPPDATA must be a fully qualified Windows drive or UNC path.");
	}
	return path.win32.join(path.win32.resolve(localAppData), "AgentHaloDSH");
}

async function readPackageVersion() {
	const manifestPath = fileURLToPath(new URL("../../../package.json", import.meta.url));
	const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
	if (manifest.name !== PACKAGE_NAME ||
		typeof manifest.version !== "string" ||
		!/^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/.test(manifest.version)) {
		throw new Error("The dsh-agenthalo package manifest has an invalid name or version.");
	}
	return manifest.version;
}

async function prepareVersionedApplication(home) {
	const packageNativeDirectory = fileURLToPath(
		new URL("../../../dist/windows/", import.meta.url));
	const sourceExecutable = path.join(packageNativeDirectory, EXECUTABLE_NAME);
	if (!await isFile(sourceExecutable)) {
		throw new Error(`The packaged Windows helper is missing: ${sourceExecutable}`);
	}

	const version = await readPackageVersion();
	const applicationRoot = path.join(home, "app");
	const versionDirectory = path.join(applicationRoot, version);
	const cachedExecutable = path.join(versionDirectory, EXECUTABLE_NAME);
	if (await isFile(cachedExecutable)) return cachedExecutable;

	await mkdir(applicationRoot, { recursive: true });
	try {
		await access(versionDirectory);
		throw new Error(`The cached AgentHalo ${version} directory is incomplete.`);
	} catch (error) {
		if (error?.code !== "ENOENT") throw error;
	}

	const stagingDirectory = path.join(applicationRoot,
		`.agenthalo-dsh-${version}-${randomUUID()}.staging`);
	try {
		await cp(packageNativeDirectory, stagingDirectory, {
			recursive: true,
			errorOnExist: true,
			force: false
		});
		if (!await isFile(path.join(stagingDirectory, EXECUTABLE_NAME))) {
			throw new Error("The staged AgentHalo Windows helper is missing.");
		}
		try {
			await rename(stagingDirectory, versionDirectory);
		} catch (error) {
			if (!await isFile(cachedExecutable)) throw error;
			await rm(stagingDirectory, { recursive: true, force: true });
		}
		if (!await isFile(cachedExecutable)) {
			throw new Error(`The cached AgentHalo ${version} helper was not installed.`);
		}
		return cachedExecutable;
	} catch (error) {
		await rm(stagingDirectory, { recursive: true, force: true }).catch(() => {});
		throw error;
	}
}

export function createManagedHelper(home) {
	let stopping = false;
	let child;
	let childClosed = false;
	let closePromise;
	let startPromise;

	function log(message) {
		try {
			process.stderr.write(`[AgentHalo DSH] ${message}\n`);
		} catch {
		}
	}

	function start() {
		if (startPromise === undefined) startPromise = startOnce();
		return startPromise;
	}

	async function startOnce() {
		if (stopping) return;
		if (process.platform !== "win32") {
			throw new Error("The AgentHalo DSH helper is supported on Windows only.");
		}
		const executable = await prepareVersionedApplication(home);
		if (stopping) return;

		const processHandle = spawn(executable, [MANAGED_ARGUMENT], {
			cwd: path.dirname(executable),
			env: { ...process.env, AGENTHALO_DSH_HOME: home },
			stdio: ["pipe", "pipe", "pipe"],
			windowsHide: false
		});
		child = processHandle;
		processHandle.stdin?.on("error", error => {
			if (error?.code !== "EPIPE") {
				log(`The helper stdin pipe reported an error: ${error.message}`);
			}
		});

		for (const [stream, label] of [
			[processHandle.stdout, "stdout"],
			[processHandle.stderr, "stderr"]
		]) {
			stream?.setEncoding("utf8");
			stream?.on("data", chunk => log(`${label}: ${String(chunk).trimEnd()}`));
		}

		let spawned = false;
		closePromise = new Promise(resolve => {
			processHandle.once("close", (code, signal) => {
				childClosed = true;
				if (spawned && !stopping) {
					if (code === 10) {
						log("The helper exited with code 10 because another AgentHalo DSH instance already owns the window.");
					} else if (code !== 0) {
						log(`The helper exited with code ${String(code)}${signal ? ` (${signal})` : ""}; it will not be restarted automatically.`);
					} else {
						log("The helper window closed; it will not be restarted automatically.");
					}
				}
				resolve({ code, signal });
			});
		});

		await new Promise((resolve, reject) => {
			processHandle.once("spawn", () => {
				spawned = true;
				resolve();
			});
			processHandle.on("error", error => {
				if (!spawned) reject(error);
				else log(`The helper process reported an error: ${error.message}`);
			});
		});
	}

	function requestStop() {
		stopping = true;
	}

	async function dispose() {
		stopping = true;
		if (startPromise !== undefined) await startPromise.catch(() => {});
		if (child === undefined || childClosed) return;
		try {
			if (child.stdin && !child.stdin.destroyed && !child.stdin.writableEnded) {
				child.stdin.end();
			}
		} catch {
		}
		if (closePromise !== undefined) {
			let timeout;
			const closed = await Promise.race([
				closePromise.then(() => true),
				new Promise(resolve => {
					timeout = setTimeout(() => resolve(false), HELPER_CLOSE_TIMEOUT_MS);
				})
			]);
			if (timeout !== undefined) clearTimeout(timeout);
			if (!closed) {
				log(`The helper did not exit within ${HELPER_CLOSE_TIMEOUT_MS} ms after stdin EOF; leaving the process untouched.`);
			}
		}
	}

	return { start, requestStop, dispose };
}
