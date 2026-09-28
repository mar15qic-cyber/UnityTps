# 使用说明：agent-relay 多 agent 编排工具（2026-09-29）

> 读者：用户及任意 agent，零上下文可读。基线链：无（本工具为独立基础设施，不涉产品代码）。
> 状态：已交付并完成种子数据灌入；单测已编写但按用户指示未执行；端到端真实调度待用户授权试运行。

## 0. 30 秒速览

工具位置：`E:\UnityProject\agent-relay\`（独立 git 仓库，零依赖 Node/TypeScript，node v24 直接跑）。
状态目录：`E:\UnityProject\UnityFpsLowPoly\Docs\交接\_bridge\`（gitignore，不入库）。
它解决什么：Codex 审计拆解 → 人工复制粘贴给实施方 → 实施报告 → 人工喂回复核方——这条链上的人工传话环节，全部替换为**共享状态板（任务/发现/报告/信箱）+ 额度感知的阶段链自动调度**。
你的代号：`user`；四家 agent：`codex / codely / zcode / qoder`（agents.json 可随时加新 agent）。

## 1. 日常只需要三句话

1. **开工**：对任一 agent 说"查收 relay"（它会跑 `relay status` + `relay inbox`）；
2. **推进**：`agent-relay run T-0001`（走一个阶段，停下等你审）→ 审完 `agent-relay next T-0001` 放行；
3. **断粮**：谁没额度了 `agent-relay quota set <agent> exhausted`（agent 自己也会报），恢复后 `quota reset <agent>`；活动期免费就 `priority set <阶段> <agent,...>` 把它调到链首。

## 2. 调度模型

每个任务走阶段机 `audit（可选）→ breakdown（可选）→ implement → review`，每阶段一条独立优先级链（`_bridge/agents.json` 的 `chains`，可 `priority set` 随时改序）：

- audit/breakdown：codex → codely → zcode → qoder
- implement：zcode → codely → qoder → codex
- review：codex → codely → zcode → qoder

**额度降级语义**（按你的要求）：codex 断粮 → codely 接审计拆解 → codely 也断 → zcode 接替 → 只剩最后一家时**自审计自执行**（任务打 `self_audit` 标记，快照高亮）。调度失败输出命中该 agent 的 `quota_error_patterns`（usage limit / 429 / 额度…）即自动冷却（默认 4h/agent 可调）并顺延链上下一家；全部断粮则停止 + Windows toast 通知你。

**模型与思考强度**：`agents.json` 每 agent 有 `models.default / default_effort / per_stage` 覆盖；`run --model X --effort high` 随时覆盖。zcode 默认按现行纪律写死 GLM5.3flash / max。

**审批门**：默认每阶段完成即停（审计授权 ≠ 实施授权，沿用项目既有文化）；`--auto` 连跑；`watch` 守护只自动推进显式 `task update <id> --auto on` 的任务。

## 3. 当前四家接入状态（spike 实测 2026-09-29）

| agent | 无头调度 | 说明 |
|---|---|---|
| codely | ✅ 全自动 | `-p` 无头 / `-m` 模型 / `--reasoning_effort`（minimal..max）/ `--approval-mode auto_edit`（实测可用） |
| codex | ⏳ 待装 CLI | 本机只有 Codex 桌面版；`npm i -g @openai/codex` 装好后 doctor 通过即全自动（exec 参数已预写在 agents.json） |
| zcode | 📦 人工投递包 | 桌面版（Electron）未发现 CLI 入口；调度器生成投递包 + toast 提醒你粘贴进 zcode 会话。拿到无头入口后把 agents.json 的 launch.mode 改为 `cli` 即全自动 |
| qoder | 📦 人工投递包 | IDE 无 CLI，固定投递包模式；Qoder 侧 agent 会用终端跑 relay CLI 上报 |

投递包落盘 `_bridge/sessions/<任务>/<阶段>-<agent>-<时间>.prompt.md`，无头会话的 stdout/stderr 同目录 `.log` 留痕。

## 4. agent 侧约定（已写入四家各自的指令文件，互不可见）

- Codex：`C:\Users\陈琪\.codex\AGENTS.md`（全局，仅 Codex 读）
- Codely：工作区 `CODELY.md` 末尾"静态约定"节
- Qoder：工作区 `.qoder/rules/agent-bridge.md`
- zcode：`.zcode/config.json` 的 SessionStart hook（有未读/可认领时自动注入提醒，无动静零噪音）——**未动 AGENTS.md 一字**

约定核心：开工先 status/inbox；接手用 claim/takeover；完成环节先落盘 md 再 `report register` + `task update --note` + `send`；额度耗尽立即自报 `quota set`；审计前先 `finding list` 查重。

## 5. 已灌入的真实数据（种子）

- 报告注册表 R-0001~R-0005：9/19 四日审计三件套 + 修复执行报告 + 9/20 复审记录（均带 sha256 钉死，`report list --verify` 可检测事后篡改/补写）。
- 发现底册 FD-0001~FD-0019：对应 F01–F19（17 verified / 2 fixed_pending_verify——F06 实机演练、F11 蒙皮实测、F13 v7 wire 残余），供下轮审计查重。
- 额度初始值：codex/codely/zcode=available，qoder=unknown。

## 6. 常用命令速查

```
relay status / inbox / events                     总览 / 收信 / 事件留痕
relay task create --title .. --goal .. [--stages audit,implement,review]
relay task list [--claimable] [--stale]           可认领 / 卡死任务
relay task update <id> --claim|--release|--takeover|--note|--auto on
relay finding register|list|update                发现登记/查重/状态推进
relay report register --type AUDIT --path .. | report list --verify
relay quota show | set <agent> exhausted | reset <agent>
relay priority show | set <阶段> <agent,agent,...>
relay run <T-xxxx> [--from] [--agent] [--model] [--effort] [--auto]
relay next <T-xxxx>                               审批门放行
relay doctor                                      一键体检（CLI 可达性/配置/模板）
relay snapshot                                    状态卡落盘 Docs/交接/（唯一可入 git 的快照）
```

`relay` = `node "E:\UnityProject\agent-relay\src\index.ts"`（或将 `E:\UnityProject\agent-relay` 加入 PATH 后直接用 `agent-relay.cmd`）。

## 7. 边界与残余

1. **单测未执行**（用户指示"完成后不做测试"）：`cd E:\UnityProject\agent-relay && npm test`（纯逻辑单测，不启动任何 agent、不耗额度）。跑通后再做下一条。
2. **端到端真实试运行未做**（需你授权，会消耗额度）：建议玩具任务走 `run T-xxxx --from audit`（gate 模式）验证 codely 无头调度与报告闭环；zcode/qoder 走投递包链路各验一次。
3. codex CLI 安装后需重跑 `relay doctor` 确认；codely 无头首跑建议先小任务观察 `--approval-mode auto_edit` 是否合意（要全自动改 `yolo`）。
4. `_bridge/` 不入 git；跨机器迁移目前靠 `snapshot` 状态卡 + 重新 seed。
5. MCP 前端未做（按你的要求以本地 CLI 工具交付）；状态层已就绪，日后要结构化工具可半天包一层。

## 8. 提交索引

- agent-relay 仓库（`E:\UnityProject\agent-relay`，独立 git）：`f1670b8` 状态层 → `ede71fa` CLI → `1a3406e` 调度器 → `cf71e8a` 快照/doctor/单测 → `bf7dfa7` 种子脚本。
- 本仓库（UnityFpsLowPoly，当前分支 codex/day4-release）：`.gitignore`（+`/Docs/交接/_bridge/`）、`CODELY.md`（+静态约定节）、本说明，逐文件点名 add 提交。
