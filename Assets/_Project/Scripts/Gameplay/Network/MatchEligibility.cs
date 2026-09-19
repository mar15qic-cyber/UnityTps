using FishNet;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 联网比赛普查资格（Day2 F11 修复，2026-09-07）：Dedicated/联网比赛只统计
    /// 「NetworkObject 已生成 + 有有效 Owner + 对应连接已认证」的玩家实例。
    /// 背景：Arena 内置作者离线玩家（Player_Day2_Rebuilt，NetworkObject Owner 为 FishNet
    /// 空连接 ClientId=-1）会被 FindObjectsByType 计入普查——1 真人 + 1 离线假人即开局、
    /// 假人进终局载荷甚至"获胜"、还可能成为比赛中继宿主（IT-10 实锤）。
    /// 修复口径（用户定案）：不删除/不修改 Arena 作者对象、不改 FishNet，普查侧唯一过滤。
    /// 过滤覆盖 MatchLifecycle 全部消费点（FindPlayersStatic 单点收敛）：
    ///   FindPlayers / CountPlayers / 开局门槛 / 终局载荷 / 胜负计算 / 击杀中继宿主 / 离开人数判定。
    /// 时序依据（FishNet 4.7.2 源码已核实）：OnRemoteConnectionState(Stopped) 事件先于
    /// Objects.ClientDisconnected(despawn) 与 conn.ResetState()——离开者在 Stopped 事件
    /// 窗口内仍是「已生成 + 有效 Owner + 已认证」，按连接查找离开者不受本过滤影响。
    /// 单人离线零影响：无服务器连接时 MatchLifecycle 服务器路径本就不运行。
    /// </summary>
    public static class MatchEligibility
    {
        /// <summary>
        /// 资格判定纯核心（EditMode 锁定）。三条件与用户定案逐字对应：
        /// networkObjectSpawned=NetworkObject.IsSpawned；ownerValid=Owner.IsValid（ClientId ≥ 0，
        /// 排除 FishNet 空连接/作者对象的 server-owned）；ownerAuthenticated=Owner.IsAuthenticated。
        /// </summary>
        public static bool Evaluate(bool networkObjectSpawned, bool ownerValid, bool ownerAuthenticated)
        {
            return networkObjectSpawned && ownerValid && ownerAuthenticated;
        }

        /// <summary>
        /// 玩家实例资格提取（服务器侧普查入口；MatchLifecycle.IsEligibleNetworkPlayer 委托于此）。
        /// 任何一层缺失（未生成/authored 空连接/认证未完成）都视为无资格。
        /// </summary>
        public static bool EvaluatePlayer(NetworkCombatAuthority player)
        {
            if (player == null) return false;
            var networkObject = player.NetworkObject;
            if (networkObject == null || !networkObject.IsSpawned) return false;
            var owner = networkObject.Owner;
            // IsValid = ClientId ≥ 0：作者场景对象 Owner 为 NetworkManager 空连接（ClientId=-1），在此排除
            if (owner == null || !owner.IsValid) return false;
            return Evaluate(true, true, owner.IsAuthenticated);
        }

        /// <summary>按资格过滤普查集合（服务器侧唯一过滤入口；CountEligible 供门槛判定测试）。</summary>
        public static int CountEligible(System.Collections.Generic.IReadOnlyList<bool> probes)
        {
            int count = 0;
            for (int i = 0; i < probes.Count; i++)
                if (probes[i]) count++;
            return count;
        }
    }
}
