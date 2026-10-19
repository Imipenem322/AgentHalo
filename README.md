# Agent Halo DSH

`0.16.5` · English | [简体中文](README.zh-CN.md)

A Windows-only DeepSeek Harness desktop plugin with a native activity halo. It shows the current DSH task status, title, and main-agent model while preserving the existing halo animation and controls.

Based on [NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo), under the [MIT License](LICENSE).

## Install from DSH

The GitHub installation address is not available yet. A stable installation commit must include the prebuilt `dist/windows/AgentHalo.Dsh.exe`; a CI build artifact by itself is not part of a GitHub repository install.

After that commit is published, open DSH **Plugins → Add plugin** and enter its immutable commit address:

```text
github:Imipenem322/AgentHalo#<full-release-commit-sha>
```

Then enable **dsh-agenthalo**, the name shown in the plugin manager. This development branch targets DSH Desktop `0.2.1-rc.2`, Windows 10/11, and .NET Framework 4.8. The `0.16.5` Windows build and focused DSH self-test have passed, and the halo is visible during a real task. Approval handling, macOS, CPU impact, and multi-host handover remain unverified; see the [integration guide](docs/DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md).

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
