# Agent Halo DSH

`0.16.5` · English | [简体中文](README.zh-CN.md)

A Windows-only DeepSeek Harness desktop plugin with a native activity halo. It shows the current DSH task status, title, and main-agent model while preserving the existing halo animation and controls.

Based on [NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo), under the [MIT License](LICENSE).

## Install from DSH

The plugin includes its prebuilt Windows executable and locales. No installation-time build is required. The following fixed commit has been installed and enabled through the DSH plugin manager.

Open DSH **Plugins → Add plugin** and enter:

```text
github:Imipenem322/AgentHalo#2d539465a804659c06449b9a3e0047d682ef3249
```

Then enable **dsh-agenthalo**. This version supports DSH Desktop `0.2.1-rc.2`, Windows 10/11, and .NET Framework 4.8. The visible halo, details panel, real task states, input waiting, subagents, and plugin lifecycle have been verified. Approval handling, macOS, CPU impact, and multi-host handover remain unverified; see the [integration guide](docs/DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md).

If you previously installed a linked local directory, uninstall it and restart DSH before installing from GitHub. This clears the running Host's cached module paths.

## What it does

- Shows DSH task status, title, and main-agent model ID.
- Preserves dragging, edge snapping, always-on-top, pause, sizing, and the existing animation behavior.
- Keeps its settings and runtime files under `%LOCALAPPDATA%\AgentHaloDSH`, separate from the full AgentHalo app.
- Starts and stops its own native halo with the DSH plugin lifecycle.

The observer does not persist prompt or message bodies, tool arguments, or tool result bodies. It does not submit model requests or contact account endpoints. The full AgentHalo observer has a different plugin ID and is not removed or migrated by this package; see the integration guide if that older observer is already registered in your DSH profile.

## Build a local package

Run from the repository root in PowerShell on Windows:

```powershell
$stageRoot = Join-Path $env:TEMP 'agenthalo-dsh-stage'
.\scripts\build-windows.ps1 -OutputRoot $stageRoot
```

The installable package is staged at `$stageRoot\dsh-agenthalo`, with the executable and locales under `dist\windows`. The command does not create a ZIP or checksum by default. Add `-CreateArchive` only when a ZIP is specifically needed. To run the DSH-specific self-test and read its result after the executable exits:

```powershell
$exe = Join-Path $stageRoot 'dsh-agenthalo\dist\windows\AgentHalo.Dsh.exe'
$selfTestOutput = Join-Path $env:TEMP 'agenthalo-dsh-self-test.txt'
$selfTest = Start-Process -FilePath $exe `
  -ArgumentList @('--dsh-self-test', "`"$selfTestOutput`"") `
  -WindowStyle Hidden -Wait -PassThru
if ($selfTest.ExitCode -ne 0) {
  throw "DSH self-test failed with exit code $($selfTest.ExitCode)"
}
if (-not (Test-Path -LiteralPath $selfTestOutput -PathType Leaf)) {
  throw 'DSH self-test did not write its output file.'
}
Get-Content -LiteralPath $selfTestOutput
```

CI checks the plugin JavaScript syntax, builds the Windows package, and runs that focused DSH self-test. Shared contract checks run when shared specs, locales, or their check scripts change. The workflow does not run the full Codex suite or macOS build on this branch.

## Data access

The plugin reads DSH's local live-agent projections and writes a bounded status snapshot for its native process. It does not save conversation text or tool payloads. No session content is uploaded.
