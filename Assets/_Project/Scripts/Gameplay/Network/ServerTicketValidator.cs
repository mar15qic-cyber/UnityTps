using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.Gameplay.Network
{
    // ============================================================================
    // 冻结契约 DTO（Docs/27 Day1 §2.2/§2.3）——字段名与后端逐字小驼峰对齐，
    // 由 zcode 在后端侧实现同名端点；本文件只消费契约，不依赖任何数据库实现。
    // ============================================================================

    [Serializable]
    public sealed class ServerInstanceRegisterRequest
    {
        public string instanceId;
        public string address;
        public int port;
        public int capacity;
        public string buildVersion;
        /// <summary>应用协议代际（GameProtocolIdentity.ProtocolId；空 = 旧 DS 二进制）。
        /// 后端存储并按房间协议期望筛选可租实例（2026-09-15 P0-A 实例筛选）。</summary>
        public string protocolId;
        /// <summary>Phase 8：本实例绑定的地图（Docs/27 §4 目录键；空 = 旧 DS 二进制，
        /// 后端按 arena 处理）。控制面 JSON——不属于 FishNet wire，协议不递增，但需 DS/后端同批。</summary>
        public string mapId;
    }

    [Serializable]
    public sealed class ServerInstanceRegisterResponse
    {
        public string instanceId;
        public string state;
        public int heartbeatIntervalSeconds;
    }

    [Serializable]
    public sealed class ServerInstanceHeartbeatRequest
    {
        public string roomCode;
        public int currentPlayers;
        public string state;
        /// <summary>Phase 8：本实例地图（随心跳同步，后端重注册/租用匹配消费；空 = 旧 DS）。</summary>
        public string mapId;
    }

    [Serializable]
    public sealed class ServerJoinTicketConsumeRequest
    {
        public string instanceId;
        public string ticket;
    }

    /// <summary>
    /// 账号权威配装快照（Gameplay 侧镜像契约，2026-09-08 追加 P0 §6 二.1/二.2）：字段名与后端
    /// LoadoutDto JSON 逐字小驼峰对齐——Game.Gameplay 程序集不引用 Game.Account（asmdef 边界），
    /// 故在冻结契约文件内本地镜像；JsonUtility 反序列化同名映射。附件数组含影响枪模/属性的条目。
    /// </summary>
    [Serializable]
    public sealed class TicketLoadoutSnapshot
    {
        public string primaryWeaponId;
        public string secondaryWeaponId;
        public string throwableId; public string[] throwableIds;
        public long version;
        public TicketLoadoutAttachment[] attachments;
        /// <summary>背包下标（CF 三背包 2026-09-30）：0/1/2 = 背包 1/2/3；旧后端无字段时 JsonUtility 得 0，语义兼容。</summary>
        public int backpackIndex;
    }

    [Serializable]
    public sealed class TicketLoadoutAttachment
    {
        public string weaponSlot;
        public string attachmentSlot;
        public string attachmentItemId;
    }

    [Serializable]
    public sealed class ServerJoinTicketConsumeResponse
    {
        public bool valid;
        public string roomCode;
        public string userId;
        public string username;
        public string expiresAtUtc;
        public long sessionId;
        public string errorCode;
        /// <summary>账号权威配装快照：服务器据此把网络玩家 Arsenal 严格配置为账号实际两槽；
        /// 失败响应恒 null（后端防泄露契约）。</summary>
        public TicketLoadoutSnapshot loadout;
        // ---- CF 比赛身份与规则快照（C3/Q04）：MatchId 空 = 无比赛（旧语义）；
        // 规则字段 0/null = 旧票据语义（DS 按 KillRace 默认值兜底）。JsonUtility 不支持 nullable，
        // 用空串/0 作缺省哨兵。 ----
        public string matchId;
        public int matchGeneration;
        public string teamId;
        public string matchMode;
        public int killTarget;
        public int timeLimitMinutes;
        public int maxPlayers;
        // ---- CF 三背包（2026-09-30 Phase A/B）：三背包全集 + 活动下标；旧后端无字段时
        // backpacks=null/activeBackpackIndex=0——DS 回退单配装语义（loadout 镜像字段仍携带）。
        public TicketLoadoutSnapshot[] backpacks;
        public int activeBackpackIndex;
    }

    // ============================================================================
    // 掉线端点契约（Day2 三缺口 2026-09-07 + P0 实例租约闭环 2026-09-08）——
    // POST /api/server-instances/{instanceId}/players/disconnect（X-Server-Key）
    // body { userId, roomCode, sessionId } → 200 { roomCode, remainingPlayers, instanceState }
    //（真实移除与幂等 no-op 一律返回处置后权威事实）；
    // 404 = 房间不存在（已释放/已解散 → 上报方视为已完成）；409 = 房间绑定与实例不符（权威拒绝）。
    // DS 只认 instanceState=Ready 且 remainingPlayers=0 为"实例已释放"信号——404/409 不得猜 Ready。
    // ============================================================================

    [Serializable]
    public sealed class ServerPlayerDisconnectRequest
    {
        /// <summary>后端权威 userId（TicketConsumeResult.UserId 的数字形态；后端实体为 long）。</summary>
        public long userId;
        public string roomCode;
        /// <summary>本连接成功消费的票据行 id；后端据此拒绝旧连接迟到上报。</summary>
        public long sessionId;
    }

    [Serializable]
    public sealed class ServerPlayerDisconnectResponse
    {
        public string roomCode;
        public int remainingPlayers;
        /// <summary>处置后的实例状态（"Ready" = 已随最后成员释放；"Reserved"/"InMatch" = 房间仍在）。</summary>
        public string instanceState;
    }

    /// <summary>掉线上报结果：2xx=接受（含幂等命中，携带处置后权威事实）；404=房间已不存在（终态，停止重试）；
    /// 409=后端权威状态矛盾（终态，停止重试并告警）；其余=传输层失败（进入有界重试）。</summary>
    public enum PlayerDisconnectOutcome
    {
        Accepted,
        RoomGone,
        StateConflict,
        TransportError,
    }

    /// <summary>
    /// 掉线上报结果（P0 租约闭环 2026-09-08）：Outcome 分类 + 后端处置后的权威事实。
    /// Accepted 必须携带事实（roomCode/remainingPlayers/instanceState）——调用方只认
    /// instanceState=Ready 且 remainingPlayers=0 为释放信号；RoomGone/StateConflict/TransportError
    /// 不携带事实（404/409 绝不猜 Ready）；旧版后端 204 空响应 → Accepted 但事实缺失（remainingPlayers&lt;0），
    /// 调用方 fail closed 保留绑定，交由心跳 409→重注册权威纠正。
    /// </summary>
    public sealed class PlayerDisconnectReport
    {
        public PlayerDisconnectOutcome Outcome;
        public string RoomCode = string.Empty;
        public int RemainingPlayers = -1;
        public string InstanceState = string.Empty;

        public static PlayerDisconnectReport AcceptedFromBackend(ServerPlayerDisconnectResponse response) => new()
        {
            Outcome = PlayerDisconnectOutcome.Accepted,
            RoomCode = response != null ? response.roomCode ?? string.Empty : string.Empty,
            RemainingPlayers = response != null ? response.remainingPlayers : -1,
            InstanceState = response != null ? response.instanceState ?? string.Empty : string.Empty,
        };

        public static PlayerDisconnectReport Failed(PlayerDisconnectOutcome outcome) => new()
        {
            Outcome = outcome,
        };
    }

    // ============================================================================
    // DS 权威终局上报契约（C3/Q05，Docs/27 §7.2）——
    // POST /api/server-instances/{instanceId}/match-result（X-Server-Key）
    // body { matchId, durationSeconds, winnerTeam, players[{userId, teamId, kills, deaths,
    //        assists, participationSeconds, rewardEligible, leftAtSeconds}] }
    // → 200 { matchId, status, replayed, rewardsApplied }（按 matchId 幂等）
    // 404 = 实例未注册；409 = 比赛/来源/内容一致性冲突（终态，不重试）。
    // ============================================================================

    [Serializable]
    public sealed class ServerMatchResultPlayerRow
    {
        public long userId;
        public string teamId;
        public int kills;
        public int deaths;
        public int assists;
        public int participationSeconds;
        public bool rewardEligible;
        public int leftAtSeconds;
        /// <summary>逐玩家胜负（R2 审计修复）：KillRace 个人胜者（winnerTeam=null）由该字段承载，
        /// 后端发奖/结果卡据此判胜；旧后端缺字段时回退胜队推导。</summary>
        public bool isWin;
    }

    [Serializable]
    public sealed class ServerMatchResultReportRequest
    {
        public string matchId;
        public int durationSeconds;
        /// <summary>胜队（Red/Blue）；null = 平局/无胜者（KillRace/双方同时归零）——A01（V0）后端契约。</summary>
        public string winnerTeam;
        public ServerMatchResultPlayerRow[] players;
    }

    [Serializable]
    public sealed class ServerMatchResultReportResponse
    {
        public string matchId;
        public string status;
        public bool replayed;
        public bool rewardsApplied;
    }

    /// <summary>终局上报结果：Accepted（登记+奖励均完成，含幂等重放）/ RewardsPending（R3 审计修复：
    /// 已登记但 rewardsApplied=false——奖励有缺，必须重试补发）/ 冲突终态 / 传输失败（可重试）。</summary>
    public enum MatchResultReportOutcome { Accepted, RewardsPending, StateConflict, TransportError }

    /// <summary>
    /// 终局上报能力接口（C3/Q05）：独立于 IServerControlPlaneClient——既有测试假实现不必跟随扩展；
    /// 生产 UnityWebServerControlPlaneClient 同时实现两者，Bootstrap 以 as 转换探测能力。
    /// </summary>
    public interface IServerMatchResultReporter
    {
        Task<MatchResultReportOutcome> ReportMatchResultAsync(ServerMatchResultReportRequest request);
    }

    // ============================================================================
    // 票据消费结果
    // ============================================================================

    /// <summary>
    /// 后端 consume 结论。后端冻结错误码：TICKET_INVALID / TICKET_EXPIRED /
    /// TICKET_REPLAYED / TICKET_INSTANCE_MISMATCH；本进程内的 fail-closed 原因
    /// 使用 AUTH_ 前缀（AUTH_BACKEND_UNREACHABLE / AUTH_BACKEND_TIMEOUT），
    /// 不与后端 TICKET_* 语义混淆（交接文档已注明，Codex 复核点）。
    /// </summary>
    public sealed class TicketConsumeResult
    {
        public bool Accepted;
        public string RoomCode = string.Empty;
        public string UserId = string.Empty;
        public string Username = string.Empty;
        public long SessionId;
        public string ErrorCode = string.Empty;
        /// <summary>账号权威配装快照（2026-09-08 追加 P0 §6 二.2）：服务器在 PlayerSpawner 生成
        /// 之前从 AcceptedUsers 按本连接取档，把网络玩家 Arsenal 严格配置为账号实际两槽。
        /// null = 无快照（旧版后端/异常态）——服务器侧 fail closed 拒绝生成，绝不回退调试 Arsenal。</summary>
        public TicketLoadoutSnapshot Loadout;
        /// <summary>CF 三背包全集（2026-09-30 Phase A/B）：恒长 3（后端懒默认合成）；null = 旧版后端
        /// → 对局内换背包不可用（DS 按单配装语义运行，Loadout 字段仍 = 活动背包）。</summary>
        public TicketLoadoutSnapshot[] Backpacks;
        /// <summary>活动背包下标（0/1/2；本轮后端恒 0）。</summary>
        public int ActiveBackpackIndex;
        /// <summary>true = 经 -allowUnsafeLocalDebugAuth 的本地调试放行（仅 Editor/Development 服务器）。</summary>
        public bool DebugBypass;
        // ---- CF 比赛身份与规则快照（C3/Q04；空/0 = 旧票据语义）----
        public string MatchId = string.Empty;
        public int MatchGeneration;
        public string TeamId = string.Empty;
        public string MatchMode = string.Empty;
        public int KillTarget;
        public int TimeLimitMinutes;
        public int MaxPlayers;

        public static TicketConsumeResult AcceptedFromBackend(string roomCode, string userId, string username,
            long sessionId = 0, TicketLoadoutSnapshot loadout = null,
            string matchId = "", int matchGeneration = 0, string teamId = "",
            string matchMode = "", int killTarget = 0, int timeLimitMinutes = 0, int maxPlayers = 0,
            TicketLoadoutSnapshot[] backpacks = null, int activeBackpackIndex = 0) => new()
        {
            Accepted = true,
            RoomCode = roomCode ?? string.Empty,
            UserId = userId ?? string.Empty,
            Username = username ?? string.Empty,
            SessionId = sessionId,
            Loadout = loadout,
            Backpacks = backpacks,
            ActiveBackpackIndex = activeBackpackIndex,
            MatchId = matchId ?? string.Empty,
            MatchGeneration = matchGeneration,
            TeamId = string.IsNullOrEmpty(teamId) ? MatchRules.TeamNone : teamId,
            MatchMode = matchMode ?? string.Empty,
            KillTarget = killTarget,
            TimeLimitMinutes = timeLimitMinutes,
            MaxPlayers = maxPlayers,
        };

        public static TicketConsumeResult AcceptedDebugBypass() => new()
        {
            Accepted = true,
            DebugBypass = true,
            ErrorCode = "UNSAFE_DEBUG_AUTH",
        };

        public static TicketConsumeResult Rejected(string errorCode) => new()
        {
            Accepted = false,
            // 空码兜底为 TICKET_INVALID：任何拒绝都不得把空错误码泄露进结果广播
            ErrorCode = string.IsNullOrEmpty(errorCode) ? "TICKET_INVALID" : errorCode,
        };
    }

    // ============================================================================
    // 控制面接口（服务器进程 → 后端，X-Server-Key）
    // ============================================================================

    /// <summary>服务器控制面客户端（register / heartbeat / tickets/consume / players/disconnect）。测试注入假实现。</summary>
    public interface IServerControlPlaneClient
    {
        Task<ServerInstanceRegisterResponse> RegisterAsync(ServerInstanceRegisterRequest request);

        Task<HeartbeatOutcome> HeartbeatAsync(ServerInstanceHeartbeatRequest request);

        Task<TicketConsumeResult> ConsumeTicketAsync(string ticket);

        /// <summary>已认证玩家掉线上报（Day2：后端房间成员清理 + leader 转移/最后成员释放）。
        /// P0 租约闭环（2026-09-08）：返回带 Outcome + 权威事实的结果，调用方只认 Ready+0 为释放信号。</summary>
        Task<PlayerDisconnectReport> DisconnectPlayerAsync(ServerPlayerDisconnectRequest request);
    }

    /// <summary>心跳结果：200/204=接受；409=后端判定实例状态矛盾（须重注册同步，不得自行改状态）；其余=传输层失败。</summary>
    public enum HeartbeatOutcome
    {
        Accepted,
        StateConflict,
        TransportError,
    }

    /// <summary>控制面请求失败（网络/HTTP 非 2xx）。异常文本不含密钥与票据明文。</summary>
    public sealed class ControlPlaneRequestException : Exception
    {
        /// <summary>HTTP 状态码（0 = 传输层失败无响应）。409 = 实例状态矛盾（zcode 心跳契约）。</summary>
        public long ResponseCode { get; }

        public ControlPlaneRequestException(string path, long responseCode, string transportError)
            : base($"control-plane request failed: {path} http={responseCode} error={transportError}")
        {
            ResponseCode = responseCode;
        }
    }

    /// <summary>
    /// 控制面 HTTP 客户端（服务器进程内使用；HttpClient 直连、显式禁用系统代理——
    /// Day2 实证 UnityWebRequest 在系统代理环境下会把控制面请求转发到远端节点返回 502，见 ctor 注释）。
    /// 安全红线：X-Server-Key 只存本类内存字段，绝不写入日志/资源/仓库；
    /// 任何日志不得输出完整 ticket（只允许 TicketHashPrefix 的 8 位摘要）。
    /// </summary>
    public sealed class UnityWebServerControlPlaneClient : IServerControlPlaneClient, IServerMatchResultReporter
    {
        private readonly string _baseUrl;
        private readonly string _instanceId;
        private readonly string _serverKey;
        private readonly int _timeoutSeconds;
        private readonly HttpClient _httpClient;

        public UnityWebServerControlPlaneClient(string backendUrl, string instanceId, string serverKey, int timeoutSeconds = 10)
        {
            _baseUrl = (backendUrl ?? string.Empty).TrimEnd('/');
            _instanceId = instanceId ?? string.Empty;
            _serverKey = serverKey ?? string.Empty;
            _timeoutSeconds = timeoutSeconds;
            // Day2 实证：Unity 6 的 UnityWebRequest 强制经系统代理（无视 ProxyOverride 例外表），
            // 本机代理（Clash 类）会把控制面请求转发到远端节点 → 回环/内网后端 502，且 X-Server-Key
            // 会流经代理进程。控制面是服务器基础设施内网端点——必须直连，显式禁用代理。
            _httpClient = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
            })
            {
                Timeout = System.TimeSpan.FromSeconds(_timeoutSeconds),
            };
        }

        public async Task<ServerInstanceRegisterResponse> RegisterAsync(ServerInstanceRegisterRequest request)
        {
            string body = await PostJsonAsync("/api/server-instances/register", request);
            if (string.IsNullOrEmpty(body))
                throw new ControlPlaneRequestException("/api/server-instances/register", 204, "empty register response");
            return JsonUtility.FromJson<ServerInstanceRegisterResponse>(body);
        }

        public async Task<HeartbeatOutcome> HeartbeatAsync(ServerInstanceHeartbeatRequest request)
        {
            try
            {
                await PostJsonAsync($"/api/server-instances/{_instanceId}/heartbeat", request);
                return HeartbeatOutcome.Accepted;
            }
            catch (ControlPlaneRequestException exception)
            {
                // 409 = 后端判定实例状态矛盾（zcode 心跳契约）：交由调用方重注册同步，
                // 本客户端不做任何状态自决（绝不把实例自行恢复 Ready）
                if (exception.ResponseCode == 409)
                    return HeartbeatOutcome.StateConflict;
                return HeartbeatOutcome.TransportError;
            }
        }

        public async Task<TicketConsumeResult> ConsumeTicketAsync(string ticket)
        {
            try
            {
                var request = new ServerJoinTicketConsumeRequest
                {
                    instanceId = _instanceId,
                    ticket = ticket ?? string.Empty,
                };
                string body = await PostJsonAsync("/api/server-instances/tickets/consume", request);
                var response = JsonUtility.FromJson<ServerJoinTicketConsumeResponse>(body);
                return response.valid
                    ? TicketConsumeResult.AcceptedFromBackend(response.roomCode, response.userId, response.username,
                        response.sessionId, response.loadout,
                        response.matchId, response.matchGeneration, response.teamId,
                        response.matchMode, response.killTarget, response.timeLimitMinutes, response.maxPlayers,
                        response.backpacks, response.activeBackpackIndex)
                    : TicketConsumeResult.Rejected(string.IsNullOrEmpty(response.errorCode) ? "TICKET_INVALID" : response.errorCode);
            }
            catch (ControlPlaneRequestException)
            {
                // HTTP 非 2xx / 网络错误 / 超时：fail closed——按票据纪律拒绝，绝不放行
                return TicketConsumeResult.Rejected("AUTH_BACKEND_UNREACHABLE");
            }
            catch (Exception)
            {
                return TicketConsumeResult.Rejected("AUTH_BACKEND_UNREACHABLE");
            }
        }

        public async Task<PlayerDisconnectReport> DisconnectPlayerAsync(ServerPlayerDisconnectRequest request)
        {
            try
            {
                // 后端契约（P0 租约闭环）：成功一律 200 + { roomCode, remainingPlayers, instanceState }
                //（真实移除与幂等 no-op 同形）；404 = 房间已不存在（终态）；409 = 绑定矛盾（权威拒绝，终态）
                string body = await PostJsonAsync($"/api/server-instances/{_instanceId}/players/disconnect", request);
                // 旧版后端 204 空 body（2xx 范围内）→ Accepted 但事实缺失：调用方 fail closed 保留绑定
                var response = string.IsNullOrEmpty(body)
                    ? null
                    : JsonUtility.FromJson<ServerPlayerDisconnectResponse>(body);
                return PlayerDisconnectReport.AcceptedFromBackend(response);
            }
            catch (ControlPlaneRequestException exception)
            {
                if (exception.ResponseCode == 404)
                    return PlayerDisconnectReport.Failed(PlayerDisconnectOutcome.RoomGone);
                if (exception.ResponseCode == 409)
                    return PlayerDisconnectReport.Failed(PlayerDisconnectOutcome.StateConflict);
                return PlayerDisconnectReport.Failed(PlayerDisconnectOutcome.TransportError);
            }
            catch (Exception)
            {
                return PlayerDisconnectReport.Failed(PlayerDisconnectOutcome.TransportError);
            }
        }

        /// <summary>DS 权威终局上报（C3/Q05 + R3 审计修复）：2xx 解析处置结果——rewardsApplied=false
        /// 返回 RewardsPending（奖励有缺，调用方须重试补发，不能当成功收尾）；409 = 权威冲突（终态）；
        /// 其余 = 传输失败（可重试）。空响应体（旧版后端）按 Accepted 处理（保持旧兼容语义）。</summary>
        public async Task<MatchResultReportOutcome> ReportMatchResultAsync(ServerMatchResultReportRequest request)
        {
            try
            {
                string body = await PostJsonAsync($"/api/server-instances/{_instanceId}/match-result", request);
                if (string.IsNullOrEmpty(body))
                    return MatchResultReportOutcome.Accepted;
                var response = JsonUtility.FromJson<ServerMatchResultReportResponse>(body);
                if (response == null || string.IsNullOrEmpty(response.status))
                    return MatchResultReportOutcome.Accepted; // 无法解析的 2xx：不猜测奖励缺失，按旧语义接受
                return response.rewardsApplied
                    ? MatchResultReportOutcome.Accepted
                    : MatchResultReportOutcome.RewardsPending;
            }
            catch (ControlPlaneRequestException exception)
            {
                // 409 = 后端权威拒绝（来源/内容/名单一致性冲突）——终态，重试无意义
                if (exception.ResponseCode == 404 || exception.ResponseCode == 409)
                    return MatchResultReportOutcome.StateConflict;
                return MatchResultReportOutcome.TransportError;
            }
            catch (Exception)
            {
                return MatchResultReportOutcome.TransportError;
            }
        }

        private async Task<string> PostJsonAsync(string path, object payload)
        {
            // 传输错误/超时统一折算为 ControlPlaneRequestException（code=0）——与旧 UnityWebRequest
            // 路径的 result!=Success 语义一致，调用方的 fail-closed 分类（UNREACHABLE/TransportError）不变
            try
            {
                using var content = new StringContent(JsonUtility.ToJson(payload), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, _baseUrl + path)
                {
                    Content = content,
                };
                request.Headers.TryAddWithoutValidation("X-Server-Key", _serverKey);

                using var response = await _httpClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                int statusCode = (int)response.StatusCode;
                if (statusCode < 200 || statusCode >= 299)
                    throw new ControlPlaneRequestException(path, statusCode, response.ReasonPhrase ?? string.Empty);
                return body;
            }
            catch (ControlPlaneRequestException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ControlPlaneRequestException(path, 0, exception.Message);
            }
        }
    }

    // ============================================================================
    // 认证决策核心（纯逻辑，EditMode 全覆盖）
    // ============================================================================

    /// <summary>
    /// 服务器侧票据认证决策核心（Docs/27 Day1 §4.1 C2）：
    /// 同步前置判定 + 异步后端消费 + fail-closed 超时。MonoBehaviour 胶水见 JoinTicketAuthenticator。
    /// </summary>
    public sealed class JoinTicketAuthService
    {
        /// <summary>收到客户端认证 broadcast 的同步判定结果。</summary>
        public enum IncomingDecision
        {
            /// <summary>开始后端 consume 验证。</summary>
            BeginValidation,
            /// <summary>同一连接验证在途，重复 broadcast 直接忽略（不得重复认证）。</summary>
            IgnoreDuplicate,
            /// <summary>已认证连接再发认证 broadcast = 攻击，断开（Demo 同款处置）。</summary>
            DisconnectAttacker,
            /// <summary>空票据/未获授权的调试请求：fail closed 拒绝。</summary>
            Reject,
            /// <summary>应用协议代际不匹配（含旧客户端空字段）：零成本拒绝，不消费后端票据（P0-A）。</summary>
            RejectProtocolMismatch,
        }

        private readonly IServerControlPlaneClient _controlPlane;
        private readonly bool _allowUnsafeDebugAuth;
        private readonly double _consumeTimeoutSeconds;
        private readonly string _expectedProtocolId;
        private readonly Func<double, Task> _delayFactory;

        /// <param name="delayFactory">超时等待工厂（入参=秒）。测试注入同步完成的假实现以零定时器复现超时路径。</param>
        /// <param name="expectedProtocolId">本进程期望的应用协议代际（GameProtocolIdentity.ProtocolId）。
        /// null/空 = 门关闭（旧测试/调试 Host 兼容：不做协议判定）。生产接线由 JoinTicketAuthenticator.RebuildService 恒传。</param>
        public JoinTicketAuthService(
            IServerControlPlaneClient controlPlane,
            bool allowUnsafeDebugAuth,
            double consumeTimeoutSeconds = 10.0,
            Func<double, Task> delayFactory = null,
            string expectedProtocolId = null)
        {
            _controlPlane = controlPlane;
            _allowUnsafeDebugAuth = allowUnsafeDebugAuth;
            _consumeTimeoutSeconds = consumeTimeoutSeconds;
            _expectedProtocolId = expectedProtocolId;
            _delayFactory = delayFactory ?? (seconds => Task.Delay(TimeSpan.FromSeconds(seconds)));
        }

        /// <summary>同步前置判定（纯函数式，不触碰网络）。协议门在票据判定之前：
        /// 期望代际已配置且客户端申报值不匹配（含旧客户端空字段）→ RejectProtocolMismatch——
        /// 拒绝发生在后端 consume 之前（票据零消耗，玩家可重新取票）。</summary>
        public IncomingDecision EvaluateIncoming(bool connectionAuthenticated, bool validationInFlight, JoinTicketBroadcast message)
        {
            if (connectionAuthenticated)
                return IncomingDecision.DisconnectAttacker;
            if (validationInFlight)
                return IncomingDecision.IgnoreDuplicate;
            if (!string.IsNullOrEmpty(_expectedProtocolId)
                && !string.Equals(message.ProtocolId, _expectedProtocolId, StringComparison.Ordinal))
                return IncomingDecision.RejectProtocolMismatch;
            if (string.IsNullOrWhiteSpace(message.Ticket))
            {
                // 无票据 + 服务器显式开启本地调试通道（仅 Editor/Development）→ 放行走本地直通
                if (message.UnsafeDebugRequest && _allowUnsafeDebugAuth)
                    return IncomingDecision.BeginValidation;
                return IncomingDecision.Reject;
            }
            return IncomingDecision.BeginValidation;
        }

        /// <summary>
        /// 异步消费验证：后端 consume + 时钟兜底总超时；任何异常/超时/不可达一律 fail closed 拒绝。
        /// </summary>
        public async Task<TicketConsumeResult> ValidateAsync(JoinTicketBroadcast message)
        {
            if (string.IsNullOrWhiteSpace(message.Ticket))
            {
                return message.UnsafeDebugRequest && _allowUnsafeDebugAuth
                    ? TicketConsumeResult.AcceptedDebugBypass()
                    : TicketConsumeResult.Rejected("TICKET_INVALID");
            }

            if (_controlPlane == null)
            {
                // F1 调试 Host 等无控制面场景：真实票据一律 fail closed（不存在无后端放行）
                return TicketConsumeResult.Rejected("AUTH_BACKEND_UNREACHABLE");
            }

            Task<TicketConsumeResult> consumeTask;
            try
            {
                consumeTask = _controlPlane.ConsumeTicketAsync(message.Ticket);
            }
            catch (Exception)
            {
                // 同步抛出的实现同样 fail closed（不信任任何控制面实现形态）
                return TicketConsumeResult.Rejected("AUTH_BACKEND_UNREACHABLE");
            }

            var completed = await Task.WhenAny(consumeTask, _delayFactory(_consumeTimeoutSeconds));
            if (completed != consumeTask)
                return TicketConsumeResult.Rejected("AUTH_BACKEND_TIMEOUT");

            try
            {
                return await consumeTask;
            }
            catch (Exception)
            {
                return TicketConsumeResult.Rejected("AUTH_BACKEND_UNREACHABLE");
            }
        }

        /// <summary>票据日志摘要：SHA-256 前 8 位（空票据 → "none"）。日志中绝不出现完整票据。</summary>
        public static string TicketHashPrefix(string ticket)
        {
            if (string.IsNullOrEmpty(ticket))
                return "none";
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(ticket));
            var builder = new StringBuilder(8);
            for (int i = 0; i < 4; i++)
                builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }
    }

    /// <summary>每连接认证截止时间追踪（服务器在 10s 内未完成认证即强制断开，fail closed）。</summary>
    public sealed class AuthDeadlineTracker
    {
        private readonly double _timeoutSeconds;
        private readonly Dictionary<int, double> _deadlines = new();

        public AuthDeadlineTracker(double timeoutSeconds)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        public int Count => _deadlines.Count;

        /// <summary>该连接是否仍在等待认证结果（未被断开清理/超时清场移除）。</summary>
        public bool Contains(int connectionId) => _deadlines.ContainsKey(connectionId);

        public void Mark(int connectionId, double nowSeconds)
        {
            _deadlines[connectionId] = nowSeconds + _timeoutSeconds;
        }

        public void Remove(int connectionId)
        {
            _deadlines.Remove(connectionId);
        }

        /// <summary>取出并移除所有已过期的连接 id（调用方负责断开）。</summary>
        public List<int> CollectExpired(double nowSeconds)
        {
            var expired = new List<int>();
            foreach (var pair in _deadlines)
            {
                if (pair.Value <= nowSeconds)
                    expired.Add(pair.Key);
            }
            for (int i = 0; i < expired.Count; i++)
                _deadlines.Remove(expired[i]);
            return expired;
        }
    }

    /// <summary>
    /// 客户端侧一次性票据会话（Codex 审计 P1 修复）：
    /// 明文票据在「构建认证 broadcast」的瞬间即被清除——Broadcast() 只需要消息副本，
    /// 不需要源票据；调用方发送完成后再无任何可重复发送的残留。
    /// 同一会话绝不重复发送（一次性消费）；断线清理只作兜底。
    /// 日志纪律：TryBuild 返回的是 TicketHashPrefix 摘要，绝不返回/记录明文。
    /// </summary>
    public sealed class JoinTicketClientSession
    {
        public enum SendDecision
        {
            /// <summary>已构建消息且会话明文已清——调用方应立即 Broadcast。</summary>
            Send,
            /// <summary>无票据且未开 unsafe debug 通道：fail closed，调用方应断开连接。</summary>
            FailClosedNoTicket,
            /// <summary>本会话已消费过：不得再发送（旧票据不复用）。</summary>
            AlreadyConsumed,
        }

        private string _ticket;
        private bool _consumed;

        public JoinTicketClientSession(string joinTicket)
        {
            _ticket = joinTicket;
        }

        /// <summary>会话当前是否仍持有明文票据（发送前为 true；发送/失败/清理后为 false）。</summary>
        public bool HasPlaintextTicket => !string.IsNullOrEmpty(_ticket);

        /// <summary>
        /// 一次性构建认证消息。Send 决策返回时会话明文已清除（消息对象是唯一持有者）；
        /// FailClosed 决策同样终结核会话——一次失败后不得重试旧状态。
        /// </summary>
        public SendDecision TryBuildAuthBroadcast(
            bool allowUnsafeDebugAuth, out JoinTicketBroadcast message, out string ticketHashPrefix, out string reason)
        {
            message = default;
            reason = string.Empty;

            if (_consumed)
            {
                ticketHashPrefix = "none";
                reason = "auth broadcast already consumed for this session";
                return SendDecision.AlreadyConsumed;
            }

            bool hasTicket = !string.IsNullOrEmpty(_ticket);
            if (!hasTicket && !allowUnsafeDebugAuth)
            {
                ticketHashPrefix = "none";
                reason = "no ticket and -allowUnsafeLocalDebugAuth not enabled";
                _consumed = true;
                ClearPlaintext();
                return SendDecision.FailClosedNoTicket;
            }

            ticketHashPrefix = JoinTicketAuthService.TicketHashPrefix(_ticket);
            message = new JoinTicketBroadcast
            {
                Ticket = _ticket ?? string.Empty,
                UnsafeDebugRequest = !hasTicket && allowUnsafeDebugAuth,
                // P0-A 认证前协议握手：新服务器据此拒绝旧客户端（旧服务器读不到该字段，
                // 认证阶段即流错位断开——绝不带协议差异进对局）
                ProtocolId = GameProtocolIdentity.ProtocolId,
                MapContentHash = MapContentIdentity.ClientHash,
            };
            _consumed = true;
            // 明文在消息构建完成后立即清除：此后会话内不存在可再次发送的旧票据
            ClearPlaintext();
            return SendDecision.Send;
        }

        /// <summary>断线/放弃连接时的兜底清理（幂等；不影响一次性消费语义）。</summary>
        public void ClearPlaintext()
        {
            _ticket = null;
        }
    }

    /// <summary>
    /// 本地无票据调试通道门（-allowUnsafeLocalDebugAuth）：
    /// 供 Editor F1 调试 Host / F2 调试客户端使用（Dedicated Server 用 DedicatedServerOptions 的同名校验）。
    /// 纯函数 Evaluate 供 EditMode 锁定：Release 编译一律拒绝。
    /// </summary>
    public static class JoinTicketDebugAuthGuard
    {
        public static bool Evaluate(bool flagRequested, bool isReleaseBuild)
        {
            return flagRequested && !isReleaseBuild;
        }

        /// <summary>本进程命令行是否显式请求了 unsafe debug 通道。</summary>
        public static bool IsRequestedInCurrentProcess()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], DedicatedServerOptions.ArgAllowUnsafeLocalDebugAuth, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>本进程是否允许 unsafe debug（请求 + 非 Release 编译）。</summary>
        public static bool IsAllowedInCurrentProcess()
        {
            return Evaluate(IsRequestedInCurrentProcess(), !Debug.isDebugBuild);
        }
    }

    /// <summary>
    /// 拒绝投递窗口追踪（Codex 终审 F1 修复，2026-09-06，OnPostTick 冲刷信号方案）。
    /// 投递机制查证 + 真实构建 5 轮判别实验结论：
    /// ① Broadcast → 连接级 PacketBundle → TimeManager tick 循环（OnPostTick → TryIterateData(false)）
    ///   → ServerSocket._outgoing → NetPeer.Send → LiteNetLib 通道队列 → 后台线程合并发送——
    ///   全链【无发送完成回调】；TimeManager.OnPostTick 与 TryIterateData(false) 同 tick 相邻，
    ///   是唯一可靠的"冲刷即将发生"信号；
    /// ② 仅靠 timer 窗口（67ms，任意相位）无法保证送达——单发小包在 LiteNetLib 合并缓冲区滞留
    ///   后被断开截断（真实构建 3/3 丢失；接受路径靠后续场景同步流量推挤通道而 100% 送达）；
    /// ③ conn.Disconnect → NetPeer.Shutdown 同步直发断开包——收口必须在冲刷之后。
    /// 收口二元条件（先到先收，调用方 Update 每帧驱动）：
    ///   ① 冲刷已发生——登记时快照的 OnPostTick 计数被推进（钩子在 TryIterateData(false) 之前触发，
    ///     计数递增的下一帧该广播必已过冲刷点）；② 250ms 上限到——fail closed（断开优先于投递）。
    /// 待定窗口语义：Mark 登记二元条件（重复 Mark 保持最早）；CollectDue 取出并移除（一次性收口——
    /// 同一项绝不出两次，调用方以此保证 OnAuthenticationResult 恰一次）；
    /// Cancel 客户端主动断开时作废待定（连接已死，绝不触发结果回调）。
    /// </summary>
    public sealed class PendingRejectionTracker
    {
        private sealed class PendingEntry
        {
            public double DeadlineSeconds;
            public ulong FlushCountAtMark;
        }

        private readonly Dictionary<int, PendingEntry> _pending = new();

        public int Count => _pending.Count;

        public bool Contains(int connectionId) => _pending.ContainsKey(connectionId);

        /// <summary>登记拒绝待定：deadlineSeconds=250ms 上限；flushCountAtMark=登记时 OnPostTick 计数快照。重复登记保持最早（更快 fail closed）。</summary>
        public void Mark(int connectionId, double deadlineSeconds, ulong flushCountAtMark)
        {
            if (!_pending.TryGetValue(connectionId, out PendingEntry existing) || deadlineSeconds < existing.DeadlineSeconds)
                _pending[connectionId] = new PendingEntry { DeadlineSeconds = deadlineSeconds, FlushCountAtMark = flushCountAtMark };
        }

        /// <summary>客户端断开：作废待定（幂等）。</summary>
        public void Cancel(int connectionId) => _pending.Remove(connectionId);

        /// <summary>
        /// 收口二元条件（先到先收）：冲刷计数较登记快照推进（广播已过冲刷点）或 250ms 上限到。
        /// 取出并移除——一次性收口：同一连接不会出现在两次 Collect 中（调用方保证 OnAuthenticationResult 恰一次）。
        /// </summary>
        public List<int> CollectDue(ulong currentFlushCount, double nowSeconds)
        {
            var due = new List<int>();
            foreach (var pair in _pending)
            {
                bool flushed = currentFlushCount > pair.Value.FlushCountAtMark;
                bool capped = pair.Value.DeadlineSeconds <= nowSeconds;
                if (flushed || capped)
                    due.Add(pair.Key);
            }
            for (int i = 0; i < due.Count; i++)
                _pending.Remove(due[i]);
            return due;
        }
    }
}
