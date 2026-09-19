using System.Collections.Generic;
using FishNet;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 稳定玩家身份解析（Day2 掉线生命周期：不以临时 FishNet ClientId 作为玩家永久身份）。
    /// 服务器：Owner ClientId → JoinTicketAuthenticator.AcceptedUsers 里的后端权威 userId
    ///（重连/换连接产生新 ClientId，但 userId 不变——后续重连与幂等结算以它为键）。
    /// 客户端：本地玩家自身的 userId 来自认证接受结果广播（JoinTicketResultBroadcast.UserId）。
    /// 回退链（保证离线/调试旁路不破裂）：
    ///   后端 userId → "debug-{ClientId}"（unsafe debug 旁路，不经过后端）→ "unknown"（无 Owner/离线作者玩家）。
    /// 回退值两端同构：客户端与服务器对同一连接推导出相同字符串（ClientId 双端一致）。
    /// Day2 三缺口（2026-09-07）增补：服务器侧 accepted 身份墓碑——认证接受【瞬间】记录
    /// ClientId→userId（RecordServerAccepted，由 JoinTicketAuthenticator 在写入 AcceptedUsers
    /// 同点调用）。连接断开时 AcceptedUsers 会被清理（事件订阅顺序决定先于/后于其他订阅者的
    /// Stopped 处理），而墓碑不随连接清理——MatchLifecycle 对离开者稳定 userId 的读取由此
    /// 与 OnRemoteConnectionState 的订阅顺序完全无关（旧实现对订阅顺序敏感：认证器清理若先
    /// 于 MatchLifecycle 的离开处理执行，离开者会退化为 debug-{ClientId}）。
    /// </summary>
    public static class MatchPlayerIdentity
    {
        public const string UnknownId = "unknown";
        public const string DebugPrefix = "debug-";

        /// <summary>服务器侧 accepted 身份墓碑（key=ClientId，认证接受瞬间写入；连接清理不删除——
        /// 供断线窗口内离开者身份读取；ClientId 复用时由新 accept 覆盖，天然防陈旧）。</summary>
        private static readonly Dictionary<int, string> _acceptedTombstones = new();

        /// <summary>终局/kill feed/离开载荷的唯一身份入口（服务器构造载荷与客户端匹配"自己"共用）。</summary>
        public static string Resolve(NetworkCombatAuthority player)
        {
            if (player == null)
                return UnknownId;
            var nob = player.NetworkObject;
            if (nob == null || nob.Owner == null || nob.Owner.ClientId < 0)
                return UnknownId;

            var networkManager = InstanceFinder.NetworkManager;
            var authenticator = networkManager != null ? networkManager.GetComponent<JoinTicketAuthenticator>() : null;
            if (authenticator == null)
                return DebugFallback(nob.Owner.ClientId);

            // 服务器：查已认证档案；客户端：本结果广播携带的自身身份（远端玩家不应走到客户端分支——
            // 身份 id 全部由服务器载荷下发，客户端只解析"自己"用于结算匹配）
            if (networkManager.IsServerStarted)
                return ResolveServerIdentity(nob.Owner.ClientId, authenticator.AcceptedUsers);
            return ResolveFromSelf(authenticator, nob.Owner.ClientId, player.IsOwner);
        }

        /// <summary>
        /// 服务器侧身份解析纯核心（可测）：AcceptedUsers 活档案 → accepted 墓碑 → debug 回退。
        /// 墓碑命中保证断线清理竞态下（任何 Stopped 订阅顺序）离开者仍是后端权威 userId。
        /// </summary>
        public static string ResolveServerIdentity(
            int ownerClientId, IReadOnlyDictionary<int, TicketConsumeResult> acceptedUsers)
        {
            if (acceptedUsers != null
                && acceptedUsers.TryGetValue(ownerClientId, out var profile)
                && !string.IsNullOrEmpty(profile.UserId))
                return profile.UserId;
            if (_acceptedTombstones.TryGetValue(ownerClientId, out var tombstoneUserId)
                && !string.IsNullOrEmpty(tombstoneUserId))
                return tombstoneUserId;
            return DebugFallback(ownerClientId);
        }

        /// <summary>
        /// 认证接受瞬间记录身份墓碑（JoinTicketAuthenticator 在写入 AcceptedUsers 的同点调用；
        /// 同 ClientId 重复接受以最新为准——ClientId 复用即覆盖，无陈旧窗口）。
        /// </summary>
        public static void RecordServerAccepted(int clientId, string userId)
        {
            if (clientId < 0 || string.IsNullOrEmpty(userId))
                return;
            _acceptedTombstones[clientId] = userId;
        }

        /// <summary>纯逻辑核心：ClientId → 已认证档案 userId，未认证/调试旁路回退 debug-{ClientId}。</summary>
        public static string ResolveFromProfiles(IReadOnlyDictionary<int, TicketConsumeResult> acceptedUsers, int ownerClientId)
        {
            if (acceptedUsers != null
                && acceptedUsers.TryGetValue(ownerClientId, out var profile)
                && !string.IsNullOrEmpty(profile.UserId))
                return profile.UserId;
            return DebugFallback(ownerClientId);
        }

        /// <summary>测试隔离：清空身份墓碑（仅 EditMode 测试使用）。</summary>
        public static void ResetTombstonesForTests()
        {
            _acceptedTombstones.Clear();
        }

        private static string ResolveFromSelf(JoinTicketAuthenticator authenticator, int ownerClientId, bool isOwner)
        {
            // 远端玩家在客户端侧没有可靠身份来源（载荷已带服务器下发的 id）；只有"自己"可解析
            if (!isOwner)
                return ownerClientId.ToString();
            return string.IsNullOrEmpty(authenticator.SelfUserId) ? DebugFallback(ownerClientId) : authenticator.SelfUserId;
        }

        private static string DebugFallback(int ownerClientId) => DebugPrefix + ownerClientId;
    }
}
