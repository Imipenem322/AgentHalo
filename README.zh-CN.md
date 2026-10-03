# Agent Halo

`v0.16.0` · [English](README.md) | 简体中文

<img src="assets/agent-halo-windows-preview.png" alt="Agent Halo Windows 详情面板" width="594">

显示编程代理运行状态的桌面光环。Windows 支持 Codex 和 DeepSeek Harness（DSH）；macOS 开发版支持 Codex。

基于 [NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo) 修改，采用 [MIT 许可证](LICENSE)。

## 功能

- Codex：显示任务状态、上下文、模型和本轮 Token；官方 OAuth 模式额外显示可用的 5 小时、每周额度。
- DSH：显示任务状态、任务名称和主代理模型。
- 完成提醒最多保留 5 分钟，新任务会立即覆盖；等待用户操作的提醒不受此限。
- 支持拖动吸附、置顶、暂停、开机启动，以及 75%、100%、125% 三档尺寸。

## 使用（Windows）

需要 Windows 10/11 和 .NET Framework 4.8。解压 `outputs/AgentHalo-Windows-v0.16.0.zip`，保持文件完整，运行 `AgentHalo.exe`。悬停查看详情，右键切换代理或调整设置。

默认监测 Codex。首次发现 DSH desktop 配置就绪时，会自动登记内置观察插件并切换到 DSH；之后保留你的选择。接入和撤销方法见 [DSH 接入说明](docs/DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md)。DSH 的真实任务全流程联调尚未完成。

发布包未签名，首次运行可能出现 SmartScreen 提示。EXE 校验值见包内 `SHA256.txt`。

## 构建与检查

在项目根目录执行。Windows 使用 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
.\outputs\AgentHalo\AgentHalo.exe --self-test .\outputs\self-test.txt
Get-Content .\outputs\self-test.txt
```

构建结果位于 `outputs/AgentHalo/` 和同目录的 ZIP，自检应输出 `PASS`。

macOS 需要 macOS 13+ 和 Swift 6：

```bash
bash ./scripts/run-macos.sh --verify
```

共享检查见 [CI 配置](.github/workflows/ci.yml)。状态与动画说明见 [Windows](docs/WINDOWS_VISUAL_BEHAVIOR.md) 和 [macOS](docs/MACOS_VISUAL_BEHAVIOR.md)。

## 数据访问

状态监测读取本地日志或 DSH 插件快照，不上传会话内容。DSH 插件不提交模型请求，也不采集对话正文。官方 Codex 额度刷新会复用现有 OAuth 登录并访问官方接口。
