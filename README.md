<div align="center">
  <img src="assets/agent-halo-windows-preview.png" alt="Agent Halo Windows Codex quota panel" width="594">
</div>

<h1 align="center">Agent Halo</h1>

<p align="center">
  Personalized Codex-only desktop halo for Windows and macOS.
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
> upstream MIT License. This is a personalized version focused on my own Codex
> workflow. Please visit, star, and follow the original project first; this fork
> exists as a narrow set of practical changes on top of that work.

## What Changed In This Version

This fork is not trying to replace upstream Agent Halo. It keeps the local
desktop halo idea, then reshapes the app around a smaller Codex-only workflow:

- Windows and macOS now monitor Codex only.
- Claude Code monitoring, hooks, status-line proxying, and related macOS
  components were removed from this personalized branch.
- The Windows hover panel is simplified for Codex usage, context, model, and
  current-turn token information.
- Official Codex OAuth quota is shown only when that data is available; custom
  providers do not display fabricated quota rows.
- Normal completed-task green stays visible for up to five minutes, then falls
  back to Standby if Codex is still running or Offline if Codex exits.
- New thinking, working, attention, and error states immediately replace an old
  completed state.
- Plan Mode attention remains persistent and does not use the five-minute
  completion timeout.
- Shared state constants are generated from
  [`src/shared/spec/agent-halo.v2.json`](src/shared/spec/agent-halo.v2.json)
  for both C# and Swift.

## Scope

| Platform | Current scope | Notes |
| --- | --- | --- |
| Windows 10/11 | Codex only | Native WPF app and release ZIP. |
| macOS 13+ | Codex only | Native Swift/AppKit development build. |

This repository is tuned for my daily Codex desktop use. If you want the
broader upstream direction, especially historical multi-agent support, use the
original [NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo) project as
the reference point.

## Windows Release

1. Download `AgentHalo-Windows-v*.zip` from this repository's Releases page.
2. Extract the whole ZIP before running the app.
3. Compare `AgentHalo.exe` with the bundled `SHA256.txt`.
4. Run `AgentHalo.exe`.

PowerShell verification:

```powershell
Get-FileHash .\AgentHalo.exe -Algorithm SHA256
```

The Windows build is not signed with a commercial code-signing certificate, so
SmartScreen may warn before first launch.

Only upload `outputs/AgentHalo-Windows-v*.zip` as a release asset. The archive
contains `AgentHalo.exe`, `README.md`, and `SHA256.txt`; the unpacked
`outputs/AgentHalo/` directory is only for local inspection.

## Build And Verify

Windows requirements:

- Windows 10 or Windows 11
- Windows PowerShell
- .NET Framework 4.8

Windows build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Windows self-test:

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

macOS requirements:

- macOS 13 or later
- Swift 6 toolchain

macOS verification:

```bash
bash ./scripts/run-macos.sh --verify
```

Shared contract checks:

```bash
python -m pip install -r scripts/requirements-ci.in
python scripts/validate_schema.py
python scripts/generate_shared.py --check
python scripts/check_shared.py
```

The same platform checks run in
[`ci.yml`](.github/workflows/ci.yml).

## Desktop Behavior

- Drag the halo to move it; it snaps gently to display edges.
- Hover on Windows to inspect Codex state, context, quota, model, and current
  turn token details.
- Click the halo to bring Codex forward.
- Right-click for pause, startup, preview, size, reset-position, and exit
  controls.
- Select `75%`, `100%`, or `125%` from the size menu.
- Use the reset-position command if the halo is off screen.

## State Meanings

| State | Meaning |
| --- | --- |
| Thinking | Codex is reasoning or planning. |
| Working | Codex is running a command, edit, search, or tool call. |
| Completed | A normal task just completed; visible for up to five minutes. |
| Attention | Codex is waiting for approval, confirmation, or input. |
| Error | A blocking failure needs attention. |
| Standby | Codex is running with no active task. |
| Offline | No monitored Codex desktop process is visible. |

More detailed rendering notes live in
[`docs/WINDOWS_VISUAL_BEHAVIOR.md`](docs/WINDOWS_VISUAL_BEHAVIOR.md),
[`docs/MACOS_VISUAL_BEHAVIOR.md`](docs/MACOS_VISUAL_BEHAVIOR.md), and
[`docs/CROSS_PLATFORM_SHARED_CONTRACT.md`](docs/CROSS_PLATFORM_SHARED_CONTRACT.md).

## Privacy

Agent Halo runs locally. It reads Codex lifecycle/session state and structured
diagnostic records needed to render the halo. It does not upload session
content.

When official Codex OAuth usage refresh is available, the app can reuse the
existing Codex OAuth session and call official OpenAI/ChatGPT endpoints for
derived quota data. It does not display API keys, base URLs, relay names, or
OAuth tokens in the details panel. Cached usage data is limited to derived
account identity, percentages, and reset times.

## Attribution

This is a personalized downstream version of
[NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo). The original project
deserves the main attention; this repository documents only my Codex-focused
changes and release builds.

Copyright and license notices from upstream remain applicable. New changes in
this repository are distributed under the same MIT License unless a file states
otherwise.

Agent Halo is an unofficial community project. It is not affiliated with or
endorsed by OpenAI or Quantic Dream, and it does not include their proprietary
assets.

## License

Licensed under the [MIT License](LICENSE).
