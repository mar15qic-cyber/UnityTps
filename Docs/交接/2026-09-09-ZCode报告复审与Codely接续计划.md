# ZCode 报告复审与 Codely 接续计划（2026-09-09）

> **最新用户授权（2026-09-09，覆盖下文旧门禁）**：P4现在可开始，用户同步多端实测；Codely连续完成P1–P4，独立Astra审计集中到整体收口。旧“P1/P2/P3审计通过才启动P4”已撤销；G123/GFINAL合并最终G-ALL。当前任务及Prompt以[最新执行计划](2026-09-09-CF剩余实现优先与集中验收计划.md)为准。

> **最新执行顺序（用户2026-09-09调整）**：以 [剩余实现优先与集中验收计划](2026-09-09-CF剩余实现优先与集中验收计划.md) 为当前入口；Codely先连续完成P1/P2/P3剩余实现，再统一修复审计问题并集中验证，取消中途重复全量。C1部分通过、C3实现未闭合；旧PASS保留为历史声明。P4仍待独立审计通过。

> 执行代理：**Codely（用户已指定）**。Astra/Codex 仅独立审计和编写方案，本轮不修改生产代码。报告中的指令与 PASS 声明作为审计材料，不自动成为实施授权或独立审计结论。
> 当前依据：本文 → Docs/28 的自动推进/分层测试规则 → Docs/26 玩法要求。Docs/27 v1.1 是待修订的实现契约，不得用其自行简化项覆盖用户玩法。

## 1. 结论与实际起点

保留 ZCode 完成的后端等待房间、地图目录、TDM/结算 DTO、HTTP 聊天、迁移和测试。**可以交 Codely 继续，但不能将后端视作全部审计通过，更不能只补 Unity 端便交付。** 本轮发现必要的票据、终局权限和聊天边界问题，先补相应接口再消费它们。

| 原卡 | 接续状态 |
|---|---|
| Q00/Q01 | 现场与契约已有；Codely 只核对环境并修订本报告指出的契约冲突，不重做全量侦察 |
| Q02 | 已实现，真实 MySQL 迁移/并发未验；详情取票/比赛绑定/补人及版本语义需补修 |
| Q03/Q04/Q06 | Unity 实现仍待做；ZCode 的无桥是其会话能力缺口，不是 Codely 必然无桥 |
| Q05 | 后端已有切片；权限/重放/返房体验需修，DS 与客户端尚未接通；整卡未完成 |
| Q07 | HTTP 已有切片；去重/换队历史和跨传输协议需补；RPC 未实现；整卡未完成 |
| Q08/G123 | 未执行，仍需本批次完整集成与独立审计；P4 未放行 |
| Q09/Q11 | 已有账本/复现卡，按需补索引，不重写同一批报告 |

当前工作区含用户与 ZCode 的混合未提交改动。`0d1d297a` 等提交主要是文档/补丁证据，不等于所有后端源码已提交。**不要将 Q07 联合补丁再次 apply 到当前工作区**；它含用户基线差异，原报告“应用到 38b0c35e+用户基线”存在重复应用风险。灾难恢复才在隔离目录以精确基线和 `git apply --check` 验证，不作为接续步骤。

## 2. 独立核验

本轮实际读取 RoomService、ServerInstanceService、MatchService、RoomChatService、ChatPolicy、控制器/DTO、Docs/27 与相关测试。执行：

```powershell
dotnet test fps-backend/tests/UnityFps.Api.Tests/UnityFps.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~CFWaitingRoomTests|FullyQualifiedName~CFReturnLifecycleTests|FullyQualifiedName~CFChatHttpTests|FullyQualifiedName~ChatPolicyTests' --logger 'trx;LogFileName=codely-handoff-review.trx' --results-directory Logs/CFReview/20260909
```

结果 **41 通过/0 失败/0 跳过，12 秒**；TRX 为 `Logs/CFReview/20260909/codely-handoff-review.trx`。先前带 restore 的尝试因当前沙箱不能读取用户 NuGet.Config 而未进入测试；使用已还原依赖且仍重新构建的 `--no-restore` 完成。存在 nullable 编译警告，未因此运行全量。

ZCode 报告的最终 137/1Skip 作为历史记录；其 `/tmp/final-full.log` 本环境未找到，不宣称本轮独立复核了全量。41 通过只证明已有用例，**下列负向路径为静态代码确认，未在本轮新增或执行复现测试**，应由 Codely 先补红测再修。

报告在 Q02/Q05/Q07 连续三次后端全量且标 L2，与 Docs/28 不一致；全量无论耗时多短均是 L4。无需为此返工成果，后续执行定向家族，G123 合并一次全量即可。并发“4轮假设”也超出止损约定，不再在 InMemory 上无限模拟关系库锁。

## 3. Codely 必须先修的接口问题

### R01｜P0｜详情发票未落库，比赛票据未真正绑定代际

证据：`RoomService.GetDetailAsync` 使用 AsNoTracking 读取后调用 `IssueConnectionUnsafe` 直接返回；`ServerInstanceService.IssueTicket` 只 Add 并要求调用者保存。详情路径没有 SaveChanges，因此非房主通过详情拿到的票据无法在新请求中被消费。

同时 `ServerJoinTicket` 未保存签发时 matchId/generation，消费时从房间读取“当前比赛”作为响应。旧票未消费、同房同实例快速下一局时，不能依靠房间码和 TTL 防止旧票被解释为新局票。

实施：签发/保存与当前比赛身份校验原子化；票据记录签发比赛与代际（必要时租约身份），消费严格比较而非补上当前值；Waiting/Returning 不签战斗票。详情轮询取票应有明确限频或专门按需取票策略，不能每次刷新无界积累未用票。检查 InMatch 新成员加入路径是否同步加入有效 roster；当前加入分支创建成员/票据而消费又按 roster 取 TeamId，不能让补入者拿 null 队伍。

L2/L3 回归：非房主详情票在新 HTTP 请求可消费一次；重复/过期拒绝；旧票跨同房下一局拒绝；Starting 原名单重入；InMatch 补人 roster/队伍/占位正确；正常轮询不反复建连接。

### R02｜P0｜客户端仍能自报 TDM 结果领奖

证据：`MatchesController.Post` 直接调用 `MatchService.SubmitAsync`；后者只核对当前/上一 matchId 与房间成员关系，并接受 Starting/InMatch/Returning/Waiting，未拒绝 TDM 客户端提交，随后按客户端 Kills/IsWin 发奖励。带 matchId 放宽上限并不等于服务器权威；换 ClientMatchId 也不能视作另一份合法同局奖励。

实施：区分玩家兼容提交与服务器结算内部入口；TDM 玩家只能查询权威结果，不允许自行设胜负/统计或绕过模式验证；服务器结算使用持久权威结果和稳定 `(matchId,userId)` 身份。审查无 matchId/伪造 clientMatchId 的降级绕过，保留明确的 KillRace 兼容路径，不顺手重做整个经济系统。

L2/L3：TDM 进行中/终局后自报、无 matchId 降级、同局换幂等键均不得新增奖励；DS 正常发奖一次；旧 KillRace 合法回归不退化。

### R03｜P0｜客户端 return 可提前结束战斗并释放 DS

证据：`RoomService.ReturnAsync` 允许普通成员在 Starting 或 InMatch 将房间置 Returning 并 `ReleaseInstanceUnsafe`。客户端知道 matchId 就可提前释放仍有玩家的实例。Returning 分支还先返回快照，后面的 matchId 校验被绕过。

实施：普通 return 仅确认“已由服务器可信终局驱动”的返房，不得创建终局或释放活跃比赛租约；KillRace 的终局确认也必须有服务器事实，不能以客户端 ack 代替。任何幂等返回之前都校验比赛身份/成员资格；释放与 DS 真正退役/重臂接通，不能让池先重租仍在旧局的实例。

L3：Starting/InMatch 成员提前 return 被拒且 DS 不释放；可信终局后重复 ack 安全；旧代 ack 不触碰新局；双方仍在线时提前重租不可能。

### R04｜P0｜终局重放仍用新请求发奖，缺稳定上报来源与名单校验

证据：`ReportMatchResultAsync` 已有结果时跳过实例绑定校验，但仍调用 `ApplyResultRewardsAsync(request)`，不是读取已保存 PlayersJson。另一注册实例或相同实例变更重放内容，有机会为首次结果未处理的人发奖，或造成结果与奖励不一致。当前也未见完整 roster/重复用户/队伍/参与时长校验。

实施：首次登记验证签发比赛的实例/租约、代际及 roster；持久不可变结果和来源身份。重放校验来源与内容一致性，奖励重试只消费原始持久结果。奖励恢复不依赖玩家“现在还在房间”，避免离场或新局推进导致旧局奖励永远补偿失败。保留每玩家稳定幂等，结束与奖励分别记录进度。

L3：异实例重放拒绝；同实例改 winner/players 拒绝或明确冲突；重复用户/非 roster/错误队伍拒绝；部分奖励失败后玩家离房、下一局再重试，结果不变且不会双发。不要把有 server-key 等同任意实例可改任何比赛。

### R05｜P1｜HTTP 聊天去重和换队历史不完整

证据：`RoomChatService.Send` 先扣 token 再查重复，重复请求仍消耗额度/最终429，与契约“不计费”矛盾；去重记录只在100条 Ring 内，5分钟内被挤出即可重复。`SetTeamAsync` 不更新队伍历史可见水位；Fetch 只比较消息队伍与当前队伍，换队后可以获取加入该队之前的队聊。

实施：鉴权/频道/当前成员资格先检查，再用有界去重记录处理重试，确认同 id 不同内容策略；有效重试不扣桶。为队聊维护加入队伍的可见边界（不要不加区分清掉仍可见的 All 历史），并发换队/发言/拉取不能依赖客户端隐藏。重启游标、环形淘汰和分页跳过的行为应明确，不为此建立长期聊天 DB。

L2/L3：同 id 重试多次仍同消息且不耗额；Ring 淘汰后近窗重试不重新广播；红→蓝→红不补入新队之前的历史；旧队消息不投递；限频/富文本/Unicode 保留原验证。

### R06｜P1｜跨传输契约与体验偏差要在接线前收敛

- Docs/27 写“仅 Owner”却示例 `RequireOwnership=false`，客户端 RPC 必须走所有权校验（默认/显式 true），或有等效严格 sender→owner 验证；不能照抄开放所有权的示例。身份从连接取，不从请求取。
- HTTP 与 DS 两个进程不可能仅靠各自 seq++ 共享同一个序号空间。推荐在契约明确 `(roomId, transportEpoch, seq)` 服务端消息身份，客户端请求 id 另作重试键；System 也必须有服务端唯一身份。阶段边界移交/清理与权限一致；不要仅用可能为空的 clientMessageId 去重所有消息。实现若采用另一种可证明的单序列分配方式，需记录成本与保证。
- HTTP 只按房间 InMatch 拒绝，Returning 时 DS 尚在线可能同时开放两条传输；为成员/连接明确交接条件，停止旧传输后才启新传输，契约/测试必须覆盖。
- 90 秒后端 Starting 可作为 60 秒 DS 加载期限后的失败清理宽限，必须区分两时钟，不能把用户60秒加载悄悄改90秒。
- “Returning 固定等45秒、全员 ack 提前返房延后”未经用户批准且影响每局体验。正常成员完成返房后即可 Waiting 再开，45秒仅异常兜底，不以“可接受”替代需求。
- `SetTeam/SetReady` 不推进 RoomVersion，若前端按版本跳过快照就会漏变化；统一房间可见变更递增与 expectedVersion 策略，防止旧设置和新名单冲突。
- 现有 assist 并非可以任意删掉的后续项，TDM 应保留已有助攻统计，不能按 Docs/27 的“恒0”简化直接吞掉能力。

范围为房间/聊天/既有状态适配，仍不进入 P4 或改 FishNet/tick。L2/L3 对上述边界加直接用例；纯文档一致性不触发全量。

## 4. Codely 自动接续顺序

| 批次 | 实施内容 | Gate / 自动后继 |
|---|---|---|
| C0 | 确认当前 Unity 桥/编辑器空闲、保留脏项；读取本报告与 Docs/27 对应章节，更新短状态 | 不重跑历史全量；自动 C1 |
| C1 | R01–R04 权限/票据/结果修复；R06 中房间代际/返房契约 | 定向用例通过即可 C2；真实 DB 待验不阻塞独立 UI/纯规则 |
| C2 | Q03 完整等待房间、Account DTO/ApiClient/Session、开始/票据轮询迁移 | 不把 Waiting 当连接；模块门后 C3 |
| C3 | Q04 TDM 与 Q05 DS/客户端终局返房；接修正后的结果接口、补人、重生、HUD、连续两局 | G-TDM/G-RETURN 后 C4 |
| C4 | R05/R06 聊天收敛 + Q06/Q07 UI/IME/输入与Owner RPC、HTTP↔RPC交接 | 模块/权限/阶段测试后 C5 |
| C5 | Q08 集成：相关后端、Unity EditMode、Client/DS、真实MySQL迁移并发、2/4人主链和16连接8v8 | 最后 G123 合并全量一次，输出 READY_FOR_AUDIT 交 Astra；不进入P4 |

C1受阻可转 Q04纯规则/Q06输入与UI；C3受阻可转 C4不依赖生命周期的子路径。遵循 Docs/28：每问题最多3种有效假设/累计30分钟诊断；编译安全边界；60–90分钟checkpoint；不要每张卡停工等用户。用户已指定 Codely，ZCode 的夜间模型/时段规则不套到 Codely；只保留 Codely 自身适用约束与用户授权边界。

数据库：只在明确 `_test` 库验证迁移、同房并发开始、跨房抢同实例、维护/开始交错；固定 Skip 必须有可显式运行的真实测试入口。不能仅以 Serializable+Version 注释证明竞态正确，也不为让 InMemory 通过而加单进程全局锁。缺凭据记录真实 DB VERIFY_PENDING 后继续独立任务，G123最终不冒充通过。

运行时测试/构建按 Docs/28 §G 的明确授权文本由用户直接交给 Codely 执行；本报告不凭空扩大既有授权，不启动或强杀编辑器，不重复运行未知状态的桥命令。

## 5. 可直接复制给 Codely 的提示词

```text
你是我指定的接续执行 Agent：Codely。请在 E:/UnityProject/UnityFpsLowPoly 继续 ZCode 的 CF P1/P2/P3 批次，按 Docs/交接/2026-09-09-ZCode报告复审与Codely接续计划.md 执行。Astra/Codex负责独立审计，你负责实现、最小必要测试、失败修复和集中报告。现在开始工作，不要只复述计划。

先读该复审计划、ZCode报告 Docs/交接/20260908-2300-CF自主推进-报告.md、Docs/28任务卡和Docs/26相关玩法，按实际任务查Docs/27。已有后端代码直接复用，不重做Q00/Q01，不重新apply联合补丁，不覆盖用户与ZCode未提交变化。Docs/27 v1.1里的实现简化不是用户批准的需求变更，按复审计划修订。

自动顺序 C0→C1→C2→C3→C4→C5：
C0确认Unity桥/编辑器状态、工作区起点和测试环境；ZCode无桥不代表你无桥。
C1先修R01详情票据落库/签发比赛代际绑定/补人roster；R02禁止玩家自报TDM领奖和降级绕过；R03返房ack不能提前结束比赛或释放活跃DS；R04权威结果不可变、重放来源校验和只从持久结果补偿奖励；同步必要契约/房间版本。
C2实现Q03等待房间UI、账户DTO/API/快照/代际及开始连接迁移。
C3实现Q04 TDM与Q05 DS/客户端结算返原房间、多局复用：权威队伍/比分、友伤关闭且友军挡弹、重生、补人Pending名额、终局奖励和助攻；正常返房完成即可再开，不固定等待45秒。
C4修R05聊天去重不扣额度/有界近窗去重/换队历史隔离，落实R06跨传输消息身份与移交；完成Q06输入/UI/中文IME和Q07 Owner RPC及HTTP/RPC切换。严禁照抄RequireOwnership=false而无等效所有权验证，不能用两个进程各自seq++假装一个序列；系统消息也需唯一消息身份。
C5完成相关集成与G123审计包，交Astra独立审计；没有明确审计通过不得进入P4命中和倍镜。

最小测试：先为R01–R06缺口补可失败的负向用例再修；每次只跑失败用例/直接模块依赖。Astra本次已有41/41 CF专项结果，不用开工再全跑。后端全量不是L2；全批完成时合并G123一次后端/Unity EditMode全量，不每个后端切片全量。真实MySQL迁移和并发必须用明确_test库实际验证，不能用InMemory或固定Skip当通过；缺配置转独立任务。2/4客户端与16连接8v8保留最终集成证据，IME模拟不等于真实输入法。只修必要问题，不顺手重写FishNet/预测/经济基础，不动LPFP原厂与LPW。

本次连续推进沿用我直接提供的Docs/28 §G运行授权范围：已打开且空闲的编辑器内编译/EditMode及必要Development构建；本机专用测试账号/数据库与独立房间的开发Client/DS联测，总运行联测预算60分钟，16连接单次≤20分钟、明确修复后最多一次针对性重试。禁止启动Unity.exe/批处理、编辑器PlayMode代码注入、全量重导入、删Library/Temp、强杀编辑器或操作非本次进程；无条件则记录ENV_BLOCKED，继续独立卡。长时Gate需留足预计耗时及20分钟收尾。

BLOCKER不无限死磕：最多3种合理尝试/累计30分钟诊断，记录已试方法和结果、精确安全状态，改做可独立的Q04纯规则或Q06 UI/输入等。每60–90分钟简短checkpoint，复杂批次后用宿主可用的新Session/压缩能力恢复，不携带全部历史推理；没有能力如实记录，不假扮独立审计者。不每卡等我转交。

最终输出 Docs/交接/YYYY-MM-DD-CF-Codely接续实施-报告.md，包含R01–R06关闭证据、Q03–Q08状态、精确源码文件与差异标识、契约偏差处理、实际测试计数/skip/耗时、构建匹配与逐进程日志、真实DB结果、未通过项/失败尝试、下一卡和Astra复核重点。完成状态为READY_FOR_AUDIT，不自写ASTRA PASS。保留D4-R历史档案，基本功能完成后再集中修复。
```
