# agent-relay 约定（Qoder 专用，2026-09-29 起）

本工作区（UnityFpsLowPoly）用 agent-relay 做多 agent 协同：审计→拆解→实施→复核的流转与额度感知调度都经它登记。
CLI 调用（终端直接执行）：`node "E:\UnityProject\agent-relay\src\index.ts" <命令>`（下称 relay）。

开工前（会话开始处理任何任务前）：
- `relay status` —— 看额度看板、我的任务、可认领任务、未读提醒
- `relay inbox` —— 收未读信件（自动标记已读）

接手任务：
- `relay task update <T-xxxx> --claim` 认领（已被认领会报错，不要抢；确需强接管用 `--takeover`，仅 fallback 链内允许）

完成任一环节（审计/拆解/实施/复核）后，按顺序：
1. 先把报告落盘 `Docs/交接/YYYY-MM-DD-<主题>-<类型>.md`（零上下文可读，含 commit/测试证据）——正文永远在 md，relay 只存指针
2. `relay report register --type <AUDIT|PLAN|IMPLEMENTATION|REVIEW> --path <相对路径> --author qoder`
3. `relay task update <T-xxxx> --note "<一句话结论>"`（不要改任务状态，阶段状态由调度器推进）
4. `relay send --to user --type report_ready --title "<标题>" --body "<结论摘要>" --ref report:<R-xxxx>`

额度耗尽：立即 `relay quota set qoder exhausted --reason "<已完成/剩余进度>"`，把进度写进任务 note 后停止，不要空转重试。

审计去重：审计/复核开工前先 `relay finding list` —— 已 verified/closed 的旧项不要重复上报（F01–F19 历史底册已入库）。

红线三条：
1. 报告正文永远落盘 md；relay/_bridge/ 只存指针、状态与 sha256。
2. `_bridge/` 状态目录不入 git；relay 生成的状态卡（Docs/交接/*状态卡.md）才是可提交快照。
3. relay 工具故障时降级：直接按 md 文件交接并把情况告知用户，不得阻塞主任务。
