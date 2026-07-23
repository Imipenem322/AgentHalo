<a id="readme-top"></a>

<div align="center">
  <img src="assets/agent-halo-windows-preview.png" alt="Agent Halo Windows Codex 额度面板" width="594"/>
</div>

<h1 align="center">Agent Halo</h1>

<div align="center">
  <p>
    <img src="https://img.shields.io/badge/版本-0.15.0-14B8A6?style=flat-square" alt="版本 0.15.0"/>
    <img src="https://img.shields.io/badge/许可证-MIT-2563EB?style=flat-square" alt="MIT 许可证"/>
    <img src="https://img.shields.io/badge/Windows-仅支持%20Codex-0078D4?style=flat-square&logo=windows" alt="Windows 仅支持 Codex"/>
  </p>
  <p>Agent 的本地常驻状态光环。在桌面上原生呈现各种 Agent 的执行与规划状态。</p>
  <p><a href="README.md">English</a> | 简体中文</p>
</div>


---

> [!IMPORTANT]
> 本仓库基于开源的
> [Agent Halo 项目](https://github.com/NePixe1/AgentHalo)修改和改进，继续遵循
> 上游 MIT 许可证。本版本的独立改动主要集中在更精简的 Windows Codex-only
> 构建，以及简化后的 Windows 额度详情面板。

跨平台行为以
[`src/shared/spec/agent-halo.v2.json`](src/shared/spec/agent-halo.v2.json)
为唯一参数来源，并生成 C# 与 Swift 常量；Windows 和 macOS 继续使用各自的原生渲染。
详见 [共享契约说明](src/shared/README.md) 与
[跨平台架构说明](docs/CROSS_PLATFORM_SHARED_CONTRACT.md)。

## 系统要求

- Windows 10/11 或 macOS 13+
- 已安装并使用 Codex 桌面端
- Windows 需要 .NET Framework 4.8（目前的 Windows 10/11 通常已自带）

## macOS 开发版

运行并验证：

```bash
bash ./scripts/run-macos.sh --verify
```

应用是菜单栏辅助应用，不显示 Dock 图标。可以从菜单栏 Agent Halo 图标退出，也可以执行：

```bash
pkill -x AgentHaloMac
```

诊断命令：

```bash
cd src/macos
swift run AgentHaloDiagnostics --self-test /tmp/agent-halo-self-test.txt
swift run AgentHaloDiagnostics --render-states /tmp/agent-halo-states
swift run AgentHaloDiagnostics --transition-strip /tmp/agent-halo-transitions
```

## 安装与运行

1. 从 GitHub Releases 下载最新的 `AgentHalo-Windows-v*.zip`。
2. 解压整个 ZIP 压缩包，不要直接在压缩包内运行。
3. 双击 `AgentHalo.exe`，光环会出现在主显示器右上方附近。

程序没有安装器，也不需要 OpenAI API Key。为独立刷新额度，Agent Halo
会复用 Codex 已有的 OAuth 登录凭据；OAuth Token 轮换时会原子更新 Codex
原有的 `auth.json`。

## 操作

- 拖动光环：调整位置，靠近屏幕边缘时会自动吸附。
- 鼠标悬停：查看当前状态；官方 Codex OAuth 显示当前可用额度、剩余百分比和重置时间。
- 使用 CCSwitch、自定义模型提供商或 API Key 时，Codex 面板会自动改为显示项目、模型和本轮输入/输出 Token，不展示 API Key、Base URL 或中转工具名称。
- Windows 和 macOS 版本都只监听 Codex。
- 上下文 pill 显示 Codex 的上下文占用。
- Codex 官方额度行只在 OAuth 模式显示；自定义 API 模式使用相同高度的项目、模型和 Token 信息行，不混入虚假的官方额度。
- 普通任务完成后绿色会缓慢呼吸，最多持续 5 分钟；期间有新状态会立即替换它。Codex 桌面进程仍在运行时，到期后显示不发光的稳定绿色待机；该进程退出则显示离线。
- 右键单击：打开状态预览、暂停监听、开机启动和退出菜单。
- 右键”光环大小”：选择 `75% / 100% / 125%`，重启后保持设置。
- macOS 会记住光环所属显示器及相对位置；该显示器断开时临时移到主屏右上角，重新连接后恢复原位置。临时回退期间如果手动拖动光环，新位置会成为首选位置，不再返回原显示器。
- Windows 保持原有离屏恢复行为：启动或显示器变化后，如果光环完全离开所有屏幕，会自动移回主屏右上角。
- 两个平台都可从右键菜单选择“脱离卡死”，明确重置到主屏右上角。
- 单击光环：将 Codex 窗口切到前台。

## 状态含义

- 黄色长亮短暗：Agent 正在思考或规划。
- 蓝色长亮短暗：Agent 正在执行命令、搜索、编辑文件或调用工具。
- 绿色双闪：Agent 已完成；高亮两次后缓慢呼吸，最多持续 5 分钟（仅在 Codex 桌面进程仍运行时）。
- 珊瑚橙双脉冲：Agent 正在等待 Yes、授权、确认或输入。
- 红色：仅表示阻止任务继续的故障；未查看时爆闪，打开 Codex 后常亮，离开后变为暗红。
- 稳定绿色：被监听的 Agent 已运行且当前没有活动任务。
- 暗白色：当前没有可见的 Agent 活动。

详细动效规则按平台拆分：

- [Windows 视觉行为说明](docs/WINDOWS_VISUAL_BEHAVIOR.md)
- [macOS 视觉行为说明](docs/MACOS_VISUAL_BEHAVIOR.md)
- 共享状态机契约见 [CROSS_PLATFORM_SHARED_CONTRACT.md](docs/CROSS_PLATFORM_SHARED_CONTRACT.md)。

## 隐私

Agent Halo 只在本机读取 Codex 会话目录中的生命周期事件，并只读查询
`logs_2.sqlite` 中结构化的 Codex 连接和服务故障记录。程序不会配置
Claude Code hooks、status line，也不会读取 Claude Code 会话或凭据。

为了独立刷新 Codex 额度，程序会读取现有 OAuth 登录凭据，并仅向
`auth.openai.com` 与 `chatgpt.com` 的官方接口发起 HTTPS 请求。OAuth Token
不会写入 Agent Halo 缓存；Token 轮换时只会原子写回 Codex 原有凭据文件。
Agent Halo 的额度缓存仅保存账户哈希、使用百分比和重置时间，不上传会话内容，
也不读取或保存 OpenAI API Key。

## Windows 安全提示

这是一个未购买商业代码签名证书的自制程序，因此 Windows SmartScreen 可能提示
“Windows 已保护你的电脑”。请只在确认压缩包来自可信发送者、并核对
`SHA256.txt` 后运行。确认无误时，可选择“更多信息”查看程序名称。

可以在解压后的文件夹中打开 PowerShell，并执行：

```powershell
Get-FileHash .\AgentHalo.exe -Algorithm SHA256
```

输出的哈希值应与 `SHA256.txt` 中的值完全一致。

---

## 项目声明

本仓库是开源
[Agent Halo 项目](https://github.com/NePixe1/AgentHalo)的独立修改版本，保留并遵循
上游 MIT 许可证。除非文件另有说明，本仓库新增修改同样以 MIT 许可证发布。

Agent Halo 是非官方社区项目，与 OpenAI、Quantic Dream 均无隶属或背书关系。
项目不包含其专有素材、Logo 或照搬的指示灯几何造型。
