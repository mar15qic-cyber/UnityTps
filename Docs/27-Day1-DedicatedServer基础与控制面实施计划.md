# 27 Day 1：Dedicated Server 基础与控制面实施计划

> 制定日期：2026-09-05（北京时间）  
> 总计划：Docs/26  
> 当前集成基线：`origin/codex/release-56h`，制定时 HEAD=`24772d4f`  
> Day 1 目标：独立服务器进程能够启动并接受两个持有效票据的普通客户端；创建房间的玩家不再启动 Host，也不拥有服务器进程。

## 0. 当日唯一完成定义

Day 1 结束时必须有一条可重复的最小链路：

1. Dedicated Server 以命令行参数启动，直接加载正式 `Arena`，只启动 FishNet Server，不创建本地客户端玩家。
2. 服务器使用服务端密钥向后端注册并持续心跳；后端能够将一个 Ready 实例原子绑定到房间。
3. 创建者与第二名玩家分别获得短期、单次使用的 opaque join ticket，并连接同一服务器。
4. FishNet 在生成 Player 前验证票据；过期、复用、房间/实例不匹配的票据必须拒绝。
5. 创建房间者断开客户端连接不会停止 Dedicated Server。Day 1 不要求完成比赛中退出判胜负，那是 Day 2。
6. 后端目标测试、Unity 编译和 EditMode 新增测试通过；多进程实机联调若需授权，先交付完整命令与预期日志，获得授权后执行。

只做到“Server Build 能打开”不算完成；仍要求玩家按 F1 `StartHost()`、票据未验证或服务器依赖创建者进程，也不算完成。

## 1. 开工前冻结

### 1.1 Git 与工作区

- Codex 在派工时重新 `git fetch`，记录实际集成 HEAD；若不再是 `24772d4f`，以最新远端为准并在交接注明。
- Codely 分支建议：`codely/day1-dedicated-server`。
- zcode 分支建议：`zcode/day1-room-control-plane`。
- 两名执行者必须使用独立 worktree；禁止在原来约 280 项混合改动的工作树中开发或整体搬运文件。
- 禁止强推、禁止 `git add .`，每个提交逐文件暂存。执行者只提交自己的分支，由 Codex 集成。

### 1.2 禁止区域

- `Assets/_Project/Scenes/Arena_LPWTest.unity`
- `Assets/_Project/Prefabs/Weapons/LPW/**`、`LPWTest/**`
- `Assets/_Project/ScriptableObjects/Weapons/LPW/**`、`LPWTest/**`
- `Assets/_Project/Scripts/Debug/LPW*`
- `Assets/FishNet/**` 第三方源码
- `Docs/21*`、`Docs/22*`、`.zcode/`、`out_test/`、`Library/`、`Temp/`、`Logs/`、构建输出和录屏

远端历史中已存在的 `e17f1366` 只视为遗留，不得产生新的 LPW 差异。`CODELY.md` 内仍有 LPW 旧记忆，凡与 Docs/25 冲突均不得采用。

## 2. Codex 冻结的跨端契约

### 2.1 三种身份必须分离

- `roomLeader`：大厅管理身份，可以转移，只负责房间 UI/准备等管理。
- `serverInstance`：独立 Unity Server 进程，有自己的 instanceId 和服务端凭证。
- `matchAuthority`：运行在 serverInstance 内的 FishNet 权威逻辑；玩家永远不是权威服务器。

旧字段 `HostUserId/HostUsername` Day 1 可暂时保留为数据库物理字段以降低迁移风险，但代码注释和新 DTO 中语义必须改为 Leader；旧 `HostAddress/HostPort` 不再接受玩家上报为权威地址。

### 2.2 后端接口

服务端接口使用 `X-Server-Key`，密钥只来自服务器环境变量或命令行，不能写入客户端资源、仓库或日志。

```text
POST /api/server-instances/register
X-Server-Key: <secret>
{ instanceId, address, port, capacity, buildVersion }
-> { instanceId, state, heartbeatIntervalSeconds }

POST /api/server-instances/{instanceId}/heartbeat
X-Server-Key: <secret>
{ roomCode?, currentPlayers, state }
-> 204

POST /api/server-instances/tickets/consume
X-Server-Key: <secret>
{ instanceId, ticket }
-> { valid, roomCode, userId, username, expiresAtUtc, errorCode? }
```

玩家接口继续使用账号 JWT：

```text
POST /api/rooms
{ maxPlayers }
-> RoomConnectionDto

POST /api/rooms/{roomCode}/join
-> RoomConnectionDto

GET /api/rooms
-> GameRoomDto[]              // 列表不返回 ticket

POST /api/rooms/leave
-> 204                         // 幂等
```

冻结 DTO：

```text
GameRoomDto {
  roomCode, leaderUsername, joinedPlayers, maxPlayers,
  status, createdAtUtc
}

RoomConnectionDto {
  room: GameRoomDto,
  serverAddress, serverPort,
  joinTicket, ticketExpiresAtUtc
}
```

`status` Day 1 只允许：`WaitingForServer | WaitingForPlayers | InMatch | Closed`。新增状态必须先由 Codex批准。

### 2.3 票据规则

- 票据是 32 字节密码学随机数的 Base64Url 表示，客户端只得到明文一次。
- 数据库只保存 SHA-256 hash，不保存明文。
- TTL 固定 90 秒；绑定 `instanceId + roomCode + userId`。
- `consume` 在事务中检查并写入 `ConsumedAtUtc`；重复消费返回 `valid=false/errorCode=TICKET_REPLAYED`。
- 其他失败码冻结为：`TICKET_INVALID`、`TICKET_EXPIRED`、`TICKET_INSTANCE_MISMATCH`。
- 任何日志不得输出完整 ticket 或 `X-Server-Key`。

### 2.4 房间与实例状态规则

- 创建房间时，后端在事务内选择一个 `Ready` 实例并改为 `Reserved`，同时绑定 roomCode；没有实例返回 409 `NO_SERVER_AVAILABLE`。
- 玩家加入只增加成员并签发个人票据，不修改服务器地址。
- roomLeader 离开而仍有成员时，按 `JoinedAtUtc` 最早、再按 `UserId` 最小选新 leader；房间与 serverInstance 保留。
- 最后一名成员离开且比赛未开始时，关闭房间并释放实例为 `Ready`。
- 房间过期由 serverInstance 心跳决定，不再由玩家 leader 心跳决定。
- Day 1 只支持“一实例一房间/一比赛”；不实现同进程多房间或自动拉起新进程。

### 2.5 Unity 侧接缝

Codely 在 `Game.Gameplay` 提供不依赖 `Game.Account` 的入口：

```text
NetworkLaunchContext.ConfigureClient(address, port, joinTicket)
NetworkLaunchContext.ConfigureDedicatedServer(options)
NetworkLaunchContext.Clear()
```

Day 2 zcode 只调用这个公共入口，不得反向修改网络内部。Codely 不引用 Lobby/AccountSession，也不修改后端 DTO。

## 3. zcode 票据：后端实例控制面（P0 4h，夜班余量用于加固）

### 3.1 允许修改

- `fps-backend/src/UnityFps.Api/Features/Contracts.cs`
- `fps-backend/src/UnityFps.Api/Data/Entities.cs`
- `fps-backend/src/UnityFps.Api/Data/AppDbContext.cs`
- 新增一份前向 EF migration 与 model snapshot
- `fps-backend/src/UnityFps.Api/Services/RoomService.cs`
- 新增 `ServerInstanceService.cs`
- `fps-backend/src/UnityFps.Api/Controllers/RoomsController.cs`
- 新增 `ServerInstancesController.cs`
- `fps-backend/src/UnityFps.Api/Program.cs`、必要的 Options/鉴权小类
- `fps-backend/tests/UnityFps.Api.Tests/RoomApiTests.cs`
- 新增 `ServerInstanceApiTests.cs`
- Unity 镜像契约仅限 `Assets/_Project/Scripts/Account/AccountContracts.cs`、`IApiClient.cs`、`ApiClient.cs`

不得修改 `Gameplay/Network/**`、NetworkManager prefab、Arena 场景或 FishNet 文件。

### 3.2 分段实施

**Z0，0–30 分钟：现场与迁移设计**

- 阅读现有 RoomService 的事务/重试模式和全部 RoomApiTests。
- 输出新增表、索引、外键、状态转换；确认迁移只有新增列/表/索引，没有删表或重命名。

**Z1，30–120 分钟：实例与票据持久化**

- 新增 `ServerInstance` 与 `ServerJoinTicket`。
- 唯一索引：instanceId、ticketHash；查询索引：state+lastHeartbeat、roomCode、expiresAtUtc。
- 使用 `CreateExecutionStrategy().ExecuteAsync` 包裹显式事务，委托开头 Clear ChangeTracker。

**Z2，120–195 分钟：API 与房间重构**

- 完成 register/heartbeat/consume。
- CreateRoom 不再信任玩家提交的地址，原子租用 Ready 实例并返回 RoomConnectionDto。
- JoinRoom 签发绑定同一实例的新票据。
- Leave 不再因 leader 离开删除多人房间；按冻结规则转移 leader。
- 保留旧客户端字段的解析兼容可以，但必须忽略玩家上报的 hostAddress/hostPort。

**Z3，195–240 分钟：测试、提交与交接**

- 完成目标测试并修复本轮失败。
- 同步 Unity Account 契约镜像，保证字段逐字小驼峰一致。
- 分 2–3 个语义提交，写零上下文交接文档。

**夜班剩余容量（非扩范围）**：运行后端全量测试、真实 MySQL migration smoke、复核日志不泄密、补并发租用与票据消费竞态。若 P0 超时，剩余容量优先完成 P0，不做 UI。

### 3.3 zcode 必须新增的测试

- 无 `X-Server-Key` 或错误密钥注册/心跳/消费均 401/403。
- 两个并发 CreateRoom 不能租到同一 Ready 实例。
- CreateRoom 无 Ready 实例返回 409 `NO_SERVER_AVAILABLE`。
- Create/Join 返回不同 ticket，均绑定正确 user/room/instance。
- 有效票据第一次消费成功，第二次为 `TICKET_REPLAYED`。
- 过期、随机和实例不匹配票据全部拒绝。
- 三人房 leader 离开后房间仍存在、人数为 2、leader 确定性转移。
- 普通成员/重复离开保持幂等；最后成员离开释放实例。
- 房间列表绝不序列化 joinTicket/tokenHash/serverKey。
- 旧 `HostLeaveDissolvesRoom` 测试必须按 Dedicated 语义改写，不能删除覆盖。

建议命令：

```powershell
dotnet test fps-backend/UnityFps.Backend.sln --filter "FullyQualifiedName~RoomApiTests|FullyQualifiedName~ServerInstanceApiTests"
dotnet test fps-backend/UnityFps.Backend.sln
```

## 4. Codely 票据：Dedicated Server 与 FishNet 票据认证（P0 8h）

### 4.1 允许修改

- `Assets/_Project/Scripts/Gameplay/Network/NetworkHud.cs`
- 新增 `DedicatedServerOptions.cs`
- 新增 `DedicatedServerBootstrap.cs`
- 新增 `NetworkLaunchContext.cs`
- 新增 `JoinTicketAuthenticator.cs` 与认证 broadcast DTO
- 必要时新增 `ServerTicketValidator.cs`
- 新增 `.meta`
- 新增 `Assets/_Project/Editor/DedicatedServerBuild.cs` 与 `.meta`
- `Assets/_Project/Tests/EditMode/**DedicatedServer*Tests.cs`、`*JoinTicket*Tests.cs`
- 若 asmdef 确有缺口，只能修改 `Game.Gameplay.asmdef` 与测试 asmdef，并在交接解释

原则上不得修改 `Arena.unity`。如果运行时无法取得 NetworkManager，停下报告证据，由 Codex决定是否批准单人修改场景。不得修改后端、Lobby、AccountSession、设置、武器或第三方 FishNet 源码。

### 4.2 分段实施

**C0，0–30 分钟：资源优先审计**

- 确认 `Arena` 中 NetworkManager、Tugboat、PlayerSpawner、NetworkHud 的实际组件关系。
- 读取本项目 FishNet 4.7 `Authenticator` 与 Demo broadcast 用法，只借鉴 API，不复制 Demo 密码方案。
- 输出需新增的运行时组件依赖图，未经确认不得编辑场景 YAML。

**C1，30–120 分钟：Server 启动骨架**

- `DedicatedServerOptions` 解析：`-dedicatedServer -instanceId -port -publicAddress -backendUrl -serverKey -buildVersion`。
- `UNITY_SERVER`、`Application.isBatchMode` 或显式 `-dedicatedServer` 进入服务器路径；普通客户端绝不误启动 Server。
- 自动加载正式 `Arena`，取得 NetworkManager/Tugboat，只调用 `ServerManager.StartConnection()`。
- Server 路径不得调用 `ClientManager.StartConnection()`，不得生成本地 Owner Player。
- F1 Host 只可作为 Editor/Development 调试入口，并打印 deprecated 警告；正式入口不依赖 F1。

**C2，120–270 分钟：Join ticket Authenticator**

- 基于 FishNet `Authenticator`/broadcast 建立认证请求与响应。
- 客户端连接建立后发送 ticket；服务端用冻结 API 调后端 consume。
- 在验证成功前不得触发正常 PlayerSpawner；成功后只调用一次 `OnAuthenticationResult(conn,true)`。
- 空票据、HTTP 超时、后端不可达、错误响应一律 fail closed 并断开；为认证增加 10 秒超时与连接清理。
- 服务端密钥只存在 DedicatedServerOptions 内存中；日志仅显示 ticket hash 前 8 位或完全不显示。

**C3，270–345 分钟：客户端连接入口**

- `NetworkLaunchContext.ConfigureClient` 保存一次性 endpoint+ticket；NetworkHud/Arena 启动时自动配置 Tugboat 并连接。
- 消费 LaunchContext 后清除明文 ticket；断线也清理。
- 保留 F2 仅作 Editor/Development 的 localhost 无票据调试时，Authenticator 必须明确拒绝，除非显式 `-allowUnsafeLocalDebugAuth` 且非 Release Build。

**C4，345–405 分钟：Server Build 工具**

- 增加可重复的 Dedicated Server Build 菜单/脚本，场景只含正式 Boot/Lobby/Arena 中实际所需项；服务器启动最终进入 Arena。
- 构建产物写入已忽略的 `Builds/Server/`，不得提交二进制。
- 不新增 Unity 包，不修改 `Assets/FishNet`。

**C5，405–480 分钟：验证与交接**

- Unity 编译 0 新错误，运行新增 EditMode 测试。
- 若已获多实例授权：启动一个 Server、两个 Client，用两个有效票据连接；随后关闭创建者客户端，确认 Server 与第二客户端仍在线。
- 分 2–3 个语义提交，输出启动命令、日志标记、已知限制和零上下文交接。

### 4.3 Codely 必须新增的测试

- 命令行参数缺 instanceId/port/backend/serverKey 时明确失败且不启动网络。
- server 模式只启动 Server；client 模式只启动 Client。
- `NetworkLaunchContext` 配置、单次消费、Clear 不残留 ticket。
- Authenticator：valid→一次成功；invalid/expired/replayed/backend timeout→失败。
- 同一连接重复发送 auth broadcast 不得重复认证。
- 非 server 进程无法读取或设置 serverKey。
- Release 编译路径不能使用 unsafe debug bypass。
- 现有离线模式不启动网络，既有 `FishNetOfflineSafetyTests` 不回归。

Unity 验证顺序：脚本修改后等待编译结束，再检查 Console error；随后运行相关 EditMode，最后才申请多实例 PlayMode/构建验收。不得在 domain reload 期间连续发工具命令。

## 5. Codex 当日审查与集成（2h）

### K0：开工门，30 分钟

- fetch 最新远端、记录基线和两条执行分支。
- 对照本文件回答执行者的契约问题；任何字段变更统一写入本文件后才实施。
- 检查两个 worktree 均无用户脏改动、没有 LPW 新差异。

### K1：中间门，30 分钟

- zcode 完成 migration 草案后，复核实体关系、租用竞态、密钥和票据存储。
- Codely 完成启动骨架后，复核 Dedicated 路径确实没有 StartClient/Host。
- 发现跨文件需求时调整白名单，禁止双方私下改同一文件。

### K2：集成门，60 分钟

按“后端控制面 → Unity 网络运行时”的顺序审查和集成；若双方提交共同依赖冻结契约但无文件重叠，可独立 cherry-pick。

必须检查：

1. `git diff --check`；新增差异搜索 `LPW|Arena_LPWTest|weapon.lpw`。
2. 新迁移可前向应用，无删表/丢数据；后端全量测试无新增失败。
3. Release 客户端不存在 StartHost 正式路径；Dedicated Server 不启动本地 Client。
4. 票据验证发生在 Player spawn 前，失败默认拒绝，明文 ticket 不落盘不进日志。
5. leader 离开不停止服务器、不删除仍有成员的房间。
6. Unity 编译与相关 EditMode 无新增失败。
7. 只有 Codex 更新集成分支；禁止 force push。

## 6. Day 1 人工验收脚本

由执行者交付实际可用命令，参数名必须与实现一致。期望流程：

1. 启动后端并准备 `X-Server-Key`。
2. 启动 Dedicated Server：instanceId=`arena-01`、端口 7770，看到 `DS_READY`，后端显示 Ready。
3. 玩家 A 创建房间，实例变 Reserved，获得 ticket A。
4. 玩家 B 加入，获得 ticket B；A/B 均通过认证并生成网络玩家。
5. 重放 ticket A 的第三个客户端被拒绝，服务器和原连接不受影响。
6. 关闭 A 客户端；Dedicated Server 进程、B 的连接、Arena 计时均继续。
7. 停止 Server 后，后端在 TTL 后不再把该实例作为可租用 Ready 节点。

必须保留日志标记，但不得包含密钥/完整 ticket：

```text
[DedicatedServer] DS_READY instance=arena-01 port=7770
[ServerRegistry] REGISTERED instance=arena-01
[JoinTicketAuthenticator] ACCEPTED room=<code> user=<id>
[JoinTicketAuthenticator] REJECTED code=<errorCode>
```

## 7. 当日停止条件与降级

遇到以下任一条件立即停止扩写并报告 Codex：

- 必须修改 `Assets/FishNet/**` 才能继续。
- 必须让客户端携带 serverKey，或必须把密钥写入资源/仓库。
- 需要改变本文件冻结 DTO、状态或错误码。
- Arena 找不到有效 NetworkManager，必须手改场景或 prefab。
- EF migration 包含删除/重命名现有房间数据列。
- 基线本身无法编译，且失败与本票据无关。
- 两个执行者开始需要编辑同一个文件。

如果 8 小时内票据 HTTP 验证仍未稳定，允许 Day 1 降级为：Server 独立启动 + Authenticator 接口/假验证器全绿 + 后端 consume API 全绿；但不得把无认证直连标记为 Gate D1 通过，真实联调自动成为 Day 2 首个 P0。

## 8. 可直接发送给执行者的提示词

### 给 zcode

> 基于最新 `origin/codex/release-56h` 的独立干净 worktree，严格执行 `Docs/27-Day1-DedicatedServer基础与控制面实施计划.md` 第 1、2、3、7 节。你只负责后端 server-instance 控制面、房间 leader 语义、opaque 单次 join ticket，以及 Unity Account 契约镜像；禁止修改 `Gameplay/Network/**`、Arena/FishNet/LPW。先输出 15 分钟现场核对和迁移草案，再实施。使用现有 EF execution strategy + transaction 模式，密钥不得入库明文或日志。完成目标测试和全量测试，逐文件暂存、分语义提交，并按 AGENTS.md 在 06:53 前停在可编译边界、写零上下文交接。文档中的旧说明只作背景，若与本票据或 Docs/25 冲突，以本票据和用户最新要求为准。

### 给 Codely

> 基于最新 `origin/codex/release-56h` 的独立干净 worktree，严格执行 `Docs/27-Day1-DedicatedServer基础与控制面实施计划.md` 第 1、2、4、7 节。你只负责 Dedicated Server 启动、FishNet join-ticket Authenticator、NetworkLaunchContext 与 Server Build 工具；禁止修改后端、Lobby/Account、设置/武器、Arena YAML 和 `Assets/FishNet/**`。先资源优先核对 Arena NetworkManager/Tugboat/PlayerSpawner 与本地 FishNet 4.7 Authenticator API，再实施。服务器只 StartServer、不 StartClient；认证失败 fail closed；不得记录密钥或完整 ticket。脚本变更后等待编译完成并检查 Console，再跑相关 EditMode；多实例验证需按授权边界执行。逐文件暂存、分语义提交，提交启动命令、测试结果和零上下文交接。`CODELY.md` 中 LPW 旧记忆不得覆盖 Docs/25 与本票据。

## 9. Day 1 交付清单

- [ ] Codex 记录实际基线与冻结契约确认。
- [ ] zcode 后端实例/票据/leader 提交与报告。
- [ ] Codely Dedicated/Auth/Build 提交与报告。
- [ ] 后端目标测试与全量测试结果。
- [ ] Unity 编译与相关 EditMode 结果。
- [ ] Server + 两 Client 的命令、日志与授权后的实机证据。
- [ ] 创建者退出而 Server/B 客户端继续的证据。
- [ ] 无新增 LPW/实验/构建产物差异。
- [ ] Codex 集成提交与 Day 2 blocker 清单。
