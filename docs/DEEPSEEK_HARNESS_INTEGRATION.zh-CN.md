# DeepSeek Harness 桌面端集成

Agent Halo Windows `0.16.5` 默认选择 Codex。首次使用 DSH 时，Agent Halo 会等待 DSH 的 desktop profile 初始化完成，然后从程序包释放观察插件并将其登记到该 profile。首次登记成功后，Agent Halo 自动启用并选择 DSH；之后保留用户在 Agent Halo 中的选择。无需下载额外插件，也无需手动编辑 DSH 的 JSON/YAML 配置。DSH 需要与 Agent Halo 在同一 Windows 用户账户下运行。

观察插件通过 DSH 官方 Cordis 插件入口读取当前 Host 的主会话与显式子代理关系，并把本地状态提供给 Agent Halo。插件不提交模型请求、不调用账户接口，也不收集或保存提示词、消息正文、工具参数和工具结果正文。详情面板显示任务名称和主代理模型。真实模型任务场景尚未完成端到端联调，DSH 窗口激活也未实现；因此当前资料说明的是可配置的实验性集成和验证边界，不代表真实任务已验证可用。

## 工作方式

插件源码位于 `src/integrations/deepseek-harness/`，并随 Windows 程序包内嵌。运行时，Agent Halo 将入口文件释放到 `%LOCALAPPDATA%\CodexHalo\integrations\deepseek-harness\observer\index.mjs`，并通过 DSH 官方 Cordis patch 热加载入口登记它。登记会按插件唯一 ID 幂等执行，迁移本插件旧路径时保留 profile 中的其他配置。观察插件注册一个纯投影，在 `turn/start`、`turn/end`、工具调用/结果、审批以及请求头等会话事件上维护精简状态，并每 2 秒发布一份完整 JSON 快照：

```text
%LOCALAPPDATA%\CodexHalo\integrations\deepseek-harness\runtime\<instanceId>.json
```

插件只用 `ctx.agents.roots()` 和 `ctx.agents.list()` 找当前 Host 的 live Agents，并通过官方 `sessionProjections.snapshot(session)` 读取同一事件序号上的投影基线。它沿着 `subagentCatalog` 明确列出的子会话递归归并；普通 fork 不会因此归到父任务。该投影的 wire 子项名称是 `id`（源事件写作 `childId`）。catalog 可能保留已完成子代理；如果该 `id` 不在当前 Host 的 live Agent 集合中，就作为 inactive 叶节点处理，不读取它的冷历史投影。主任务标题、轮次、实际模型、下一模型和终态只取根会话；工具、审批、未继续的问题和运行状态按根及已列出的 live 子代理累计。`userQuestions.active` 中仅 `state: "open"` 计为阻塞，`continued` 不计。

实际请求模型来自根会话的 `request/header`，并关联当前根轮次；下一次模型选择来自根会话的 `modelSelection`。模型名称目录不读取，`name` 留空，由 AgentHalo 显示 `modelId`。

## 快照 v1

快照包含 Host 身份、快照修订、能力和任务集合。下面是结构示例，标题和模型值均为虚构：

```json
{
  "schemaVersion": 1,
  "bridgeVersion": "1.0.0",
  "dshVersion": "0.2.0-rc.2",
  "profile": "desktop",
  "instanceId": "<uuid>",
  "hostPid": 1234,
  "hostStartUtc": "2026-10-01T02:00:00.000Z",
  "hostExecutable": "D:\\deepseek harness\\DeepSeek Harness.exe",
  "hostParentPid": 1200,
  "hostRuntime": "electron-node",
  "desktopAppId": "com.deepseek.dsh",
  "hostProfileDir": "C:\\Users\\<user>\\.dsh\\profiles\\desktop",
  "hostRuntimeDir": "D:\\deepseek harness\\resources\\app.asar\\dsh",
  "revision": 7,
  "publishedAtUtc": "2026-10-01T02:00:02.000Z",
  "baselineReady": true,
  "capabilities": {
    "lifecycle": true,
    "toolName": true,
    "attention": true,
    "subagents": true,
    "actualModel": true,
    "nextModel": true
  },
  "tasks": [
    {
      "rootSessionId": "<session-id>",
      "title": "示例任务",
      "turnId": "12",
      "running": true,
      "rootRunning": true,
      "relatedRunning": 1,
      "relationsResolved": true,
      "activeToolCount": 1,
      "activeToolNames": ["bash"],
      "pendingApprovalCount": 0,
      "blockingQuestionCount": 0,
      "lastActivityUtc": "2026-10-01T02:00:01.000Z",
      "mainModel": {
        "providerId": "deepseek",
        "modelId": "example-model",
        "name": null,
        "source": "actual-request",
        "turnId": "12",
        "requestId": null,
        "observedAtUtc": "2026-10-01T02:00:01.000Z"
      },
      "nextModel": null,
      "terminal": null
    }
  ]
}
```

文件上限为 2 MiB、任务上限为 512。快照保留当前 Host 的根 Agent（包括待机根任务），并保留近期根终态。达到边界后先去除过期终态和非必要待机会话，尽可能保留最近一个待机根任务；仍超限时写入较小的 `baselineReady: false` 快照，不截断活动任务并谎报完整。计数缺少可靠投影时写 `null`，由 Provider 显示未知。心跳只推进 `revision` 与 `publishedAtUtc`，不更新时间或终态。

## 首次使用

普通安装只需把 Windows ZIP 完整解压，然后在同一个 Windows 用户账户下运行 DSH 和 `AgentHalo.exe`。如果 DSH 已经创建过 desktop profile，Agent Halo 检测就绪后会自动安装观察插件；如果 Agent Halo 先启动，它每 5 秒检查一次并等待 DSH 首次创建 profile。profile 就绪后，Agent Halo 将插件释放到本机 `%LOCALAPPDATA%\CodexHalo\integrations\deepseek-harness\observer\index.mjs`，并通过 DSH 官方 Cordis patch 入口登记。此过程会保留 DSH profile 的其他插件和配置。

DSH 默认 profile 位于 `%USERPROFILE%\.dsh\profiles\desktop`。如设置了 `DSH_HOME`，请将它设为**绝对路径**，或以 `~/` 开头的路径；Agent Halo 会展开 `~/`，profile 位于该目录下的 `profiles\desktop`。不要使用相对路径，以免 Halo 与 DSH 的工作目录差异导致它们指向不同 profile。

首次自动登记成功后，Agent Halo 会自动启用并选择 `deepseek-harness`，同时记录首次设置已完成。此前已手动启用 DSH 的用户保留原来的选择。后续启动会保留你在 Agent Halo 中的选择；如果之后切回 Codex，Agent Halo 不会再自动切走。官方 DSH Host 会热加载 patch，正常情况下无需手动重启。若 15 秒内仍未连上，光环会提示启动或重启 DSH。DSH 仍未安装或尚未初始化 profile 时，完成这两步后保持 Agent Halo 开启并等待自动接入即可。

连接成功后，DSH 详情面板会显示任务标题和主代理模型 ID。可选用随程序提供的只读诊断命令检查连接；它只输出健康状态、能力、任务数量及标题/模型是否存在，不输出标题、模型字符串或会话正文：

```powershell
.\AgentHalo.exe --deepseek-snapshot "$env:TEMP\deepseek-snapshot-check.json"
```

健康时退出码为 0，其他健康状态为 2。诊断文件可在检查后删除。此步骤仅检查观察链路，不会提交模型请求。

## 撤销

若只想暂时回到 Codex，可在 Agent Halo 中选择 Codex，后续启动会保留该选择。若要彻底移除 DSH 观察插件，先退出 Agent Halo，再编辑 `%LOCALAPPDATA%\CodexHalo\settings.json`：从 `EnabledAgents` 移除 `deepseek-harness`，并在需要时把 `FocusedAgent` 改为 `codex`；保留 `DeepSeekHarnessSetupComplete: true`，这样下次启动不会再次自动登记。随后从 DSH desktop profile 的 `cordis.patch.yml` 删除 `id: agenthalo-deepseek-harness-observer` 对应的整段插入项，保留其他内容。若 DSH 没有热卸载插件，等当前任务结束后重启 DSH。不要删除 `.dsh` 目录、profile 文件或其他插件注册项。

## 已核实接口与验证边界

- 官方固定提交 [`639ed015397290b3745d163aafe02ffee4aa3f84`](https://github.com/deepseek-ai/deepseek-harness/tree/639ed015397290b3745d163aafe02ffee4aa3f84) 的桌面 Host 插件使用 ESM 命名导出 `name`、`apply` 和可选 `inject`；`cordis.patch.yml` 支持 `insert` 及绝对本地插件路径。
- `sessionProjections.snapshot(session)` 同步返回同一会话事件位置上的投影值和 `asOfSeq`；`agents.roots()/list()` 提供当前 Host 的 live Agent。插件不调用 `sessionController.projections()` 扫描历史会话。
- `userQuestions` 服务在官方实现中向 `sessionProjections` 注册同名 projection，wire 结构含 `active`、`settled`。若服务或 live 会话基线中缺少该 projection，`attention` 能力或对应计数不会伪报为可用/零。
- 官方子代理目录的 wire 项以 `id` 表示会话 ID；插件仅按该明确关系聚合，不把通用父会话字段当作子代理关系。

## 实现状态与验证边界

Agent Halo 自 `0.16.0` 起已包含自动释放与登记观察插件的逻辑，因此新电脑首次运行时不需要从其他电脑复制插件或配置。此前使用隔离的全新 DSH profile 和 Agent Halo 配置，验证了 EXE 首次启动自动登记、启用与选中 DSH；也验证了先启动 DSH 桌面 Host 后再打开 Agent Halo，插件无需重启即可热加载，原生读取器得到 `Healthy / STANDBY`。这些检查没有提交模型请求，证明的是首次接入与空闲状态读取链路。

真实模型回复、工具调用与审批、问题继续、子代理/fork、运行中切换模型、桌面退出或休眠恢复、Host 重启等场景尚未端到端验证。模型目录名称未接入，详情显示模型 ID；DSH 窗口激活尚未实现。不得将空闲快照或离线断言当作真实任务验收。
