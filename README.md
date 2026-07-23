<div align="center">
  <img src="assets/agent-halo-windows-preview.png" alt="Agent Halo Windows Codex quota panel" width="594">
</div>

<h1 align="center">Agent Halo</h1>

<p align="center">
  A native desktop status halo for Codex on Windows and supported agents on macOS.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.15.0-14B8A6?style=flat-square" alt="Version 0.15.0">
  <img src="https://img.shields.io/badge/license-MIT-2563EB?style=flat-square" alt="MIT License">
  <img src="https://img.shields.io/badge/Windows-Codex%20only-0078D4?style=flat-square&logo=windows" alt="Windows Codex only">
  <img src="https://img.shields.io/badge/macOS-13%2B-000000?style=flat-square&logo=apple" alt="macOS 13 or later">
</p>

<p align="center">
  English · <a href="README.zh-CN.md">简体中文</a>
</p>

> [!IMPORTANT]
> This repository is a modified derivative of the open-source
> [Agent Halo project](https://github.com/NePixe1/AgentHalo). It retains the
> upstream MIT License. This version contains independent changes focused on a
> smaller, Codex-only Windows build and a simplified Windows quota panel.

Agent Halo displays the current agent state as a lightweight, always-on-top
halo. It turns local Codex activity into a small set of readable visual states
without requiring a dashboard or browser window.

## Highlights

- Native Windows WPF and macOS AppKit implementations.
- Local status monitoring with thinking, working, completed, attention, error,
  standby, and offline states.
- A compact Windows hover panel with the Codex state and context usage aligned
  on one row.
- One visible quota row on Windows:
  - weekly quota when available;
  - otherwise the current available monthly/credit quota;
  - never a renamed, combined, or visible five-hour quota.
- Custom Codex providers show project, model, and current-turn token details
  instead of fabricated official quota data.
- Drag positioning, edge snapping, off-screen recovery, startup control, pause,
  state previews, and configurable halo size.
- A generated cross-platform state contract with platform-native rendering.

## Platform scope

| Platform | Monitored agents | Notes |
| --- | --- | --- |
| Windows 10/11 | Codex only | Native WPF build. |
| macOS 13+ | Codex only | Native Swift/AppKit build. |

## Install on Windows

1. Download `AgentHalo-Windows-v*.zip` from the repository's Releases page.
2. Extract the complete archive.
3. Verify `AgentHalo.exe` against `SHA256.txt`.
4. Run `AgentHalo.exe` from the extracted directory.

The application has no installer. Because personal builds are not signed with a
commercial certificate, Windows SmartScreen may show a warning.

Verify the executable in PowerShell:

```powershell
Get-FileHash .\AgentHalo.exe -Algorithm SHA256
```

## Build from source

### Windows

Requirements:

- Windows 10 or Windows 11
- Windows PowerShell
- .NET Framework 4.8

Build the executable and release archive:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Run the local self-test, then remove its temporary report after confirming the
result:

```powershell
$report = ".\outputs\agenthalo-selftest.txt"
.\outputs\AgentHalo\AgentHalo.exe --self-test $report
Get-Content $report
Remove-Item -LiteralPath $report
```

Expected result:

```text
PASS
Lifecycle, topmost guard, usage metrics, panel formatting, and animation checks passed.
```

### Windows release asset

Upload only `outputs/AgentHalo-Windows-v*.zip`. The archive contains
`AgentHalo.exe`, this README, and `SHA256.txt`; use the unpacked
`outputs/AgentHalo/` directory only for local inspection. Generated outputs
and self-test reports are excluded from source control.

### macOS

Requirements:

- macOS 13 or later
- Swift 6 toolchain

Build and verify:

```bash
bash ./scripts/run-macos.sh --verify
```

Core diagnostics can also be run directly:

```bash
cd src/macos
swift run AgentHaloDiagnostics --self-test /tmp/agent-halo-self-test.txt
```

### Shared contract checks

Install the schema validator and run all shared checks:

```bash
python -m pip install -r scripts/requirements-ci.in
python scripts/validate_schema.py
python scripts/generate_shared.py --check
python scripts/check_shared.py
```

The same checks and native builds run in
[GitHub Actions](.github/workflows/ci.yml).

## Usage

- Drag the halo to move it; it snaps gently to display edges.
- Hover over the halo to view status, quota, reset time, and context usage.
- Click the halo to bring Codex to the foreground.
- Right-click for pause, startup, preview, size, reset-position, and exit
  controls.
- Select `75%`, `100%`, or `125%` from the halo-size submenu.

The Windows panel shows weekly quota first. If weekly data is unavailable, it
uses the generic current-available quota supplied by monthly or credit data. If
neither exists, the quota row contains no stale percentage or reset time.
Five-hour data remains parseable internally for compatibility but is not bound
to visible Windows UI.

## Visual states

| State | Appearance |
| --- | --- |
| Thinking | Amber asymmetric breathing |
| Working | Blue asymmetric breathing |
| Completed | Green double flash followed by slow breathing for up to five minutes |
| Attention | Coral double pulse |
| Error | Red unseen/seen/acknowledged sequence |
| Standby | Stable green |
| Offline | Dim white |

A completed state remains visible for at most five minutes while the Codex
desktop process is running. Any new state replaces it immediately; when that
process exits, Agent Halo switches to Offline.

The shared lifecycle contract is documented in
[`src/shared/README.md`](src/shared/README.md). Platform rendering behavior is
documented in
[`docs/WINDOWS_VISUAL_BEHAVIOR.md`](docs/WINDOWS_VISUAL_BEHAVIOR.md) and
[`docs/MACOS_VISUAL_BEHAVIOR.md`](docs/MACOS_VISUAL_BEHAVIOR.md).

## Privacy and credentials

Agent Halo is designed to run locally. It reads supported local agent state and
diagnostic data required to render the halo. Session content is not uploaded by
this project.

For official Codex quota refresh, the application can reuse the existing Codex
OAuth session and communicate with the official OpenAI authentication and
ChatGPT endpoints. It does not display API keys, base URLs, relay names, or
OAuth tokens in the details panel. Cached usage data contains only derived
account identity, percentages, and reset times.

Review the source and build it yourself if your environment has strict
credential-handling requirements.

## Project structure

```text
AgentHalo/
├── .github/workflows/       GitHub Actions checks
├── assets/                  Public project artwork
├── docs/                    Maintained engineering documentation
├── scripts/                 Build and validation scripts
├── src/shared/              Shared contract, fixtures, locales, and assets
├── src/windows/             Windows Codex-only application
└── src/macos/               macOS application and Swift package
```

Generated output is written to `outputs/` and is excluded from source control.

## Attribution and project status

This repository is an independently modified version of the open-source
[Agent Halo project](https://github.com/NePixe1/AgentHalo). Copyright and
license notices from the upstream project remain applicable. New modifications
in this repository are distributed under the same MIT License unless a file
states otherwise.

Agent Halo is an unofficial community project. It is not affiliated with or
endorsed by OpenAI or Quantic Dream, and it does not include their proprietary
assets.

## License

Licensed under the [MIT License](LICENSE).
