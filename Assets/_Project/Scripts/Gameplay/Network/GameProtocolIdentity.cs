using System.IO;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 应用级网络协议代际标识（2026-09-15 一枪终局审计 P0-A）：
    /// 覆盖 RPC 签名 / 网络广播 DTO / SyncVar 形状的兼容代际——与 FishNet 框架版本无关
    /// （框架版本相同时，应用层 RPC 签名变更仍会破坏流解析：旧 DS 读新客户端的
    /// ServerFireRequest(uint,uint) 会按 uint 消费错位 → ServerManager 按未知 PacketId 踢连接）。
    /// 刻录规则（纪律，谁改协议谁递增）：
    /// ① 任何 ServerRpc / ObserversRpc / TargetRpc 的参数或频道变更；
    /// ② 任何 IBroadcast struct 的字段增删/换序（追加字段必须放最后）；
    /// ③ 任何 NetworkBehaviour SyncVar 的增删（影响同步流形状）；
    /// 满足任一条 → ProtocolId 末位递增（v2 → v3 …），同批重建 Client+DS。
    /// 双端同源编译（同一份 Game.Gameplay.dll 源码）→ 协议 ID 跨端相同；
    /// 构建清单（build-manifest.json，见 Game.EditorTools 构建脚本）分别记录两端 DLL 哈希。
    /// 消费点：
    /// ① JoinTicketBroadcast.ProtocolId（认证前握手——新服务器按此拒绝旧客户端，错误码 PROTOCOL_MISMATCH）；
    /// ② DS register（ServerInstanceRegisterRequest.protocolId → 后端按房间协议筛选可租实例）；
    /// ③ 启动日志 APP_PROTOCOL（部署门比对"运行进程 vs 目标构建"）。
    /// </summary>
    public static class GameProtocolIdentity
    {
        /// <summary>
        /// 协议代际字符串。v1 = ServerFireRequest(uint) 时代（2026-09-15 之前）；
        /// v2 = ServerFireRequest(uint shotRequestId, uint estimatedServerTick) + 认证协议字段（2026-09-15）；
        /// v3 = MovementSnapshot 增补确定性重放状态（Grounded/GroundSpeed/CoyoteTimer/LandTimer/
        ///      SprintIntent/RecoilDebt；2026-09-16 审计 M3）——AuthoritativeMovementState 载荷形状变更；
        /// v4 = MovementSnapshot 增补 Pitch（基础俯仰）+ AuthoritativeMovementState 增补
        ///      IdleStepsAtSnapshot（服务器无真实输入步数；2026-09-16 审计 §3.2-2/§6.2）；
        /// v5 = NetworkCombatAuthority 新增 SyncVar&lt;uint&gt; _respawnAtTick / _invincibleUntilTick
        ///      （Phase 1 重生调度与出生保护；2026-09-17 四组需求轮）——SyncVar 增删即 wire 形状变更。
        /// </summary>
        public const string ProtocolId = "fps-net-v5";

        /// <summary>协议不匹配的冻结错误码（DS 拒绝广播 + 后端入房筛选共用字面值）。</summary>
        public const string ProtocolMismatchCode = "PROTOCOL_MISMATCH";

        /// <summary>构建清单文件名（构建脚本写入各产物目录根；运行时读取用于启动日志与部署门）。</summary>
        public const string ManifestFileName = "build-manifest.json";

        /// <summary>
        /// 读取本进程部署目录下的构建清单（编辑器内无此文件 → 返回 null）。
        /// 仅用于启动日志/诊断：清单包含 protocolId、Game.Gameplay.dll SHA-256、构建时间与两端产物哈希。
        /// </summary>
        public static DeployedBuildManifest TryReadDeployedManifest()
        {
            try
            {
                var path = Path.Combine(Application.dataPath, "..", ManifestFileName);
                if (!File.Exists(path)) return null;
                var manifest = JsonUtility.FromJson<DeployedBuildManifest>(File.ReadAllText(path));
                return string.IsNullOrEmpty(manifest?.protocolId) ? null : manifest;
            }
            catch
            {
                return null; // 诊断信息：任何读取失败都不影响启动
            }
        }
    }

    /// <summary>部署目录内构建清单（build-manifest.json）的反序列化形状；字段名与 Game.EditorTools 构建脚本逐字对齐。</summary>
    [System.Serializable]
    public sealed class DeployedBuildManifest
    {
        public string buildId;
        public string protocolId;
        public string builtAtUtc;
        public string unityVersion;
        public string subtarget;
        public string gamePlayDllSha256;
        public string gameUiDllSha256;
        public string gameAccountDllSha256;
    }
}
