# Agent Halo DSH

`0.16.5` · [English](README.md) | 简体中文

一个仅支持 Windows 的 DeepSeek Harness 桌面插件，配有原生状态光环。它显示当前 DSH 任务的状态、名称和主代理模型，同时保留现有光环动画与控制功能。

基于 [NePixe1/AgentHalo](https://github.com/NePixe1/AgentHalo) 修改，采用 [MIT 许可证](LICENSE)。

## 从 DSH 安装

插件包已包含预编译 EXE 和语言文件，无需在安装时编译。以下固定提交已通过 DSH 插件管理器安装并启用。

在 DSH 中打开 **插件 → 添加插件**，输入：

```text
github:Imipenem322/AgentHalo#2d539465a804659c06449b9a3e0047d682ef3249
```

然后启用 **dsh-agenthalo**。当前版本支持 DSH Desktop `0.2.1-rc.2`、Windows 10/11 和 .NET Framework 4.8。已确认光环可见、详情显示正常，并完成真实任务、等待输入、子代理及插件启停验收。审批流程、macOS、CPU 影响和多 Host 接管尚未验收，详见[集成说明](docs/DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md)。

如果之前安装的是本地目录链接，卸载后请先重启 DSH，再安装 GitHub 版本，避免当前 Host 继续使用旧路径缓存。

## 功能

- 显示 DSH 任务状态、任务名称和主代理模型 ID。
- 保留拖动、边缘吸附、置顶、暂停、尺寸调整和现有动画表现。
- 设置与运行文件保存在 `%LOCALAPPDATA%\AgentHaloDSH`，与完整版 AgentHalo 分开。
- 随 DSH 插件启停，只管理本插件启动的原生光环。

观察插件不会保存提示词或消息正文、工具参数和工具结果正文；也不会提交模型请求或访问账户接口。完整版 AgentHalo 的观察插件使用不同 ID，本包不会自动移除或迁移它。如果你的 DSH profile 已登记旧观察插件，请先阅读[集成说明](docs/DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md)中的共存说明。

## 本地构建

在 Windows PowerShell 的仓库根目录执行：

```powershell
$stageRoot = Join-Path $env:TEMP 'agenthalo-dsh-stage'
.\scripts\build-windows.ps1 -OutputRoot $stageRoot
```

可安装目录位于 `$stageRoot\dsh-agenthalo`，EXE 和语言文件位于其中的 `dist\windows`。默认不生成 ZIP 或校验值；确实需要 ZIP 时再加 `-CreateArchive`。可用下面的命令运行 DSH 专项自检；它会等待 EXE 退出、检查实际退出码，再读取结果文件：

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
  throw 'DSH 自检没有写入结果文件。'
}
Get-Content -LiteralPath $selfTestOutput
```

CI 会检查插件 JavaScript 语法、构建 Windows 包，并运行这项 DSH 专项自检。只有共享规范、语言文件或对应检查脚本发生变化时，才运行共享契约检查。该分支不会例行运行完整 Codex 测试或 macOS 构建。

## 数据访问

插件读取 DSH 本地的活动代理投影，并向原生进程提供有限的状态快照。它不保存会话正文或工具载荷，也不会上传会话内容。
