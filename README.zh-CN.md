<a id="readme-top"></a>

<div align="center">
  <img src="assets/agent-halo-windows-preview.png" alt="Agent Halo Windows Codex 额度面板" width="594"/>
</div>

<h1 align="center">Agent Halo</h1>

<div align="center">
  <p>
    <img src="https://img.shields.io/badge/版本-0.15.1-14B8A6?style=flat-square" alt="版本 0.15.1"/>
    <img src="https://img.shields.io/badge/许可证-MIT-2563EB?style=flat-square" alt="MIT 许可证"/>
    <img src="https://img.shields.io/badge/Windows-仅支持%20Codex-0078D4?style=flat-square&logo=windows" alt="Windows 仅支持 Codex"/>
    <img src="https://img.shields.io/badge/macOS-13%2B-000000?style=flat-square&logo=apple" alt="macOS 13+"/>
  </p>
  <p>面向个人 Codex 工作流的桌面常驻状态光环。</p>
  <p><a href="README.md">English</a> | 简体中文</p>
</div>

> [!IMPORTANT]
> 本仓库基于开源的
> [Agent Halo 项目](https://github.com/NePixe1/AgentHalo)修改和改进，继续遵循
> 上游 MIT 许可证。这是一个面向我个人 Codex 使用习惯的个性化版本。请优先访问、
> Star 和关注原项目；本仓库只是建立在原项目基础上的一组实用改动。

## 这个版本改了什么

这个仓库不是用来替代上游 Agent Halo 的。它保留了“桌面状态光环”的核心想法，
但把功能收窄到我自己更常用的 Codex-only 流程：

- Windows 和 macOS 都只监听 Codex。
- 移除了 Claude Code 监听、hooks、status line proxy 以及相关 macOS 组件。
- Windows 悬停面板更聚焦 Codex 状态、上下文、模型和本轮 Token 信息。
- 官方 Codex OAuth 模式会分别显示可用的 5 小时和每周额度；自定义供应商不会伪装出官方额度行。
- 普通任务完成后的绿色状态最多保留 5 分钟；Codex 仍在运行时回到待机，Codex 退出时转为离线。
- 5 分钟窗口内出现新的思考、执行、等待确认或错误状态时，会立即覆盖旧的完成态。
- Plan Mode 的等待确认状态保持持续提示，不套用完成态 5 分钟超时。
- 跨平台状态参数继续由
  [`src/shared/spec/agent-halo.v2.json`](src/shared/spec/agent-halo.v2.json)
  生成到 C# 和 Swift。

## 当前范围

| 平台 | 当前范围 | 说明 |
| --- | --- | --- |
| Windows 10/11 | 仅 Codex | 原生 WPF 应用和 ZIP 发布包。 |
| macOS 13+ | 仅 Codex | 原生 Swift/AppKit 开发版。 |

这个版本按我的个人桌面使用方式取舍。如果你想了解更完整的上游路线，特别是历史上的多 Agent 支持，请以
[NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo) 为主要参考。

## Windows 发布包

1. 从本仓库 Releases 下载 `AgentHalo-Windows-v*.zip`。
2. 解压整个 ZIP，不要直接在压缩包里运行。
3. 用 `SHA256.txt` 校验 `AgentHalo.exe`。
4. 运行 `AgentHalo.exe`。

PowerShell 校验：

```powershell
Get-FileHash .\AgentHalo.exe -Algorithm SHA256
```

这个 Windows 构建没有购买商业代码签名证书，首次运行时 SmartScreen 可能提示风险。

发布时只上传 `outputs/AgentHalo-Windows-v*.zip`。压缩包内包含
`AgentHalo.exe`、`README.md` 和 `SHA256.txt`；`outputs/AgentHalo/` 解压目录只用于本地检查。

## 构建与验证

Windows 需要：

- Windows 10 或 Windows 11
- Windows PowerShell
- .NET Framework 4.8

Windows 构建：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Windows 自检：

```powershell
$report = ".\outputs\agenthalo-selftest.txt"
.\outputs\AgentHalo\AgentHalo.exe --self-test $report
Get-Content $report
Remove-Item -LiteralPath $report
```

期望结果：

```text
PASS
Lifecycle, topmost guard, usage metrics, panel formatting, and animation checks passed.
```

macOS 需要：

- macOS 13 或更高版本
- Swift 6 工具链

macOS 验证：

```bash
bash ./scripts/run-macos.sh --verify
```

共享契约检查：

```bash
python -m pip install -r scripts/requirements-ci.in
python scripts/validate_schema.py
python scripts/generate_shared.py --check
python scripts/check_shared.py
```

同一组平台检查也会在
[`ci.yml`](.github/workflows/ci.yml) 中运行。

## 桌面行为

- 拖动光环可以调整位置，靠近屏幕边缘时会吸附。
- Windows 悬停面板显示 Codex 状态、上下文、独立的 5 小时与每周额度、模型和本轮 Token 信息。
- 单击光环会把 Codex 窗口切到前台。
- 右键菜单提供暂停监听、开机启动、状态预览、光环大小、重置位置和退出。
- 光环大小支持 `75%`、`100%`、`125%`。
- 光环跑到屏幕外时，可以用“脱离卡死”重置到主屏右上角。

## 状态含义

| 状态 | 含义 |
| --- | --- |
| Thinking | Codex 正在思考或规划。 |
| Working | Codex 正在执行命令、编辑、搜索或调用工具。 |
| Completed | 普通任务刚完成，最多保留 5 分钟。 |
| Attention | Codex 正在等待授权、确认或输入。 |
| Error | 有阻止任务继续的故障需要处理。 |
| Standby | Codex 正在运行，但当前没有活动任务。 |
| Offline | 当前没有检测到可见的 Codex 桌面进程。 |

更细的动效规则见
[`docs/WINDOWS_VISUAL_BEHAVIOR.md`](docs/WINDOWS_VISUAL_BEHAVIOR.md)、
[`docs/MACOS_VISUAL_BEHAVIOR.md`](docs/MACOS_VISUAL_BEHAVIOR.md) 和
[`docs/CROSS_PLATFORM_SHARED_CONTRACT.md`](docs/CROSS_PLATFORM_SHARED_CONTRACT.md)。

## 隐私

Agent Halo 在本机运行。它读取 Codex 生命周期、会话状态，以及用于显示光环的结构化诊断记录。
它不会上传会话内容。

当官方 Codex OAuth 额度刷新可用时，程序可以复用 Codex 现有 OAuth 会话，并访问官方
OpenAI/ChatGPT 端点获取派生额度数据。详情面板不会展示 API Key、Base URL、中转服务名称或
OAuth Token。缓存的使用数据仅限账户派生标识、百分比和重置时间。

## 致谢

这是
[NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo) 的个性化下游版本。
原项目应该获得主要关注；本仓库只记录我围绕 Codex 工作流做出的改动和发布包。

上游版权和许可证声明继续适用。除非文件另有说明，本仓库新增修改同样按 MIT 许可证发布。

Agent Halo 是非官方社区项目，与 OpenAI、Quantic Dream 均无隶属或背书关系，也不包含它们的专有素材。

## 许可证

遵循 [MIT License](LICENSE)。
