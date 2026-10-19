# DeepSeek Harness 桌面端插件

Agent Halo DSH 是一个 Windows 专用的 DSH 桌面插件。插件负责启动和关闭原生状态光环；光环只读取 DSH 快照，不连接 Codex 会话或账户接口。

当前开发分支面向 DSH Desktop `0.2.1-rc.2`。Windows `0.16.5` 原生程序构建和 DSH 专项自检已通过，实际 DSH 窗口中也确认了光环可见。真实任务状态、子代理、用户问题等待以及插件管理器停用/重新启用/卸载已完成端到端验收；审批、macOS、CPU 影响及多 Host 接管仍未验收。**目前尚无可供用户安装的 GitHub 发布提交**：正式提交必须包含预编译的 `dist/windows/AgentHalo.Dsh.exe` 及其所需文件。CI 产生的构建结果不会自动进入 GitHub 仓库安装内容。

## 安装

发布提交准备好后：

1. 在 DSH 中打开 **Plugins → Add plugin**。
2. 输入固定到完整发布提交 SHA 的 GitHub 安装规格：

   ```text
   github:Imipenem322/AgentHalo#<完整发布提交 SHA>
   ```

3. 安装并启用插件管理器中显示的 **dsh-agenthalo**。

使用包含 EXE 的发布提交。开发分支或不含 `dist/windows/AgentHalo.Dsh.exe` 的提交不能视为安装即用。

启用后，插件会启动包内的 `AgentHalo.Dsh.exe`。正常释放时会清理本次快照，独立设置和按版本缓存的程序会保留。已通过 DSH 插件管理器实测停用、重新启用和启用状态下卸载：停用后开关关闭、旧 helper 消失且 Host 快照数归零；重新启用后新 helper 启动并生成 `baselineReady=true` 快照；卸载后 profile 中的依赖和 bundle 均移除，helper 消失且 Host 快照数归零。卸载会保留本地独立设置和版本缓存。本插件不会改写 DSH profile 来自动登记旧式 observer。

## 运行文件与设置

GitHub 插件包中的入口和 Cordis patch 位于 `src/integrations/deepseek-harness/`；原生程序与语言文件位于 `dist/windows/`。语言文件 `locales/zh.json` 和 `locales/en.json` 必须与 EXE 一起保留。

默认数据目录为：

```text
%LOCALAPPDATA%\AgentHaloDSH\
```

其中设置、运行快照和按应用版本缓存的程序各自位于此目录的子目录。启动器按包版本缓存原生程序，并复用同一版本已有的 EXE。发布内容中的 EXE 有变化时必须提升包版本，避免用户继续复用旧缓存。开发期间如果在同一版本替换了 EXE，应先退出 DSH 和光环进程，再只清除隔离测试数据目录中的 `app\<版本号>` 缓存后重新启用；不要清理用户默认数据目录。需要使用其他数据位置时，可在启动 DSH 前将 `AGENTHALO_DSH_HOME` 设置为绝对路径；相对路径不受支持。插件会把相同路径传给原生程序。

独立设置目录与应用实例标识允许 DSH 专用版和完整版 AgentHalo 共存。DSH 专用版只提供 DSH 状态，不显示 Codex 选择、会话、额度或 SQLite 数据。它保留现有光环的动画、拖动、缩放、置顶、暂停和语言设置功能。

## 旧版 observer 共存

旧版完整版 AgentHalo 曾将 `agenthalo-deepseek-harness-observer` 写入 DSH 的 Cordis patch。新插件 ID 为 `agenthalo-dsh`；它不会覆盖、移除或迁移旧 ID，也不会读取旧 observer 写入的快照。

如果同一个 DSH profile 仍登记旧 observer，两者会同时加载。要移除旧 observer，请先按旧版接入说明停用完整版 AgentHalo，再从 profile 的 `cordis.patch.yml` 中删除仅属于 `agenthalo-deepseek-harness-observer` 的插入项；保留同一文件中的其他插件配置。不要删除整个 DSH profile 或整份 `cordis.patch.yml`。

## 状态与隐私

插件从 DSH 当前 Host 的活动 Agent 和会话投影读取任务状态、标题和主代理模型，并通过本地有界快照交给光环。它不提交模型请求，不保存提示词、消息正文、工具参数或工具结果正文，也不会上传会话内容。快照只用于本机状态显示。

## 验收状态

已验收的 Windows 场景包括：`0.16.5` 原生程序构建和 DSH 专项自检；实际 DSH 窗口中的光环显示；真实根任务从 `Healthy / EXECUTING` 到 `COMPLETE` 的状态变化；子代理 `relatedRunning` 从 `0` 到 `1` 再回到 `0`；根任务标题和实际主模型 `deepseek-flash` 保持正确。另已复验旧式 `ask_user_question` 能显示为 `Healthy / NEEDS YOU` 且 `blockingQuestionCount=1`。强制结束隔离的 Desktop 后，Host 和光环进程均退出，重新启动后状态恢复。

审批流程、macOS、CPU 影响测量和多 Host 所有权接管尚未验收。GitHub 安装也要等包含原生程序的发布提交完成后再验证。编译、自检或空闲快照不能替代这些项目的验收。

## 本地构建与专项检查

在 Windows PowerShell 的仓库根目录执行：

```powershell
$stageRoot = Join-Path $env:TEMP 'agenthalo-dsh-stage'
.\scripts\build-windows.ps1 -OutputRoot $stageRoot
```

完整暂存包位于 `$stageRoot\dsh-agenthalo`。默认构建不会生成 ZIP 或 SHA-256 文件；需要 ZIP 时显式添加 `-CreateArchive`。DSH 专项自检命令为：

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

只读快照诊断可将健康摘要写入指定文件，不输出任务标题、模型字符串或会话正文：

```powershell
$snapshotOutput = Join-Path $env:TEMP 'agenthalo-dsh-snapshot.json'
$snapshot = Start-Process -FilePath $exe `
  -ArgumentList @('--deepseek-snapshot', "`"$snapshotOutput`"") `
  -WindowStyle Hidden -Wait -PassThru
if ($snapshot.ExitCode -ne 0) {
  throw "DSH snapshot diagnostic failed with exit code $($snapshot.ExitCode)"
}
if (-not (Test-Path -LiteralPath $snapshotOutput -PathType Leaf)) {
  throw 'DSH 快照诊断没有写入结果文件。'
}
Get-Content -LiteralPath $snapshotOutput
```

GitHub 安装提交必须在仓库根目录跟踪对应版本的 `dist/windows/AgentHalo.Dsh.exe` 和 `dist/windows/locales/`。本地暂存包或 CI artifact 不会自动替代这一步。CI 对插件入口和启动器执行一次 Node 语法检查，构建 Windows 包并运行 DSH 专项自检；只有共享规范、语言文件或相应检查脚本改变时才运行共享契约检查。它不例行运行完整 Codex 自测或 macOS 构建。

普通 README、许可证和说明文档变更不会触发此 Windows 构建流程；CI 的路径过滤保留给插件代码、原生程序、构建脚本和共享契约相关文件。
