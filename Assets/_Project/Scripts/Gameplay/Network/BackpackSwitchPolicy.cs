using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// CF 三背包对局内切换规则（2026-09-30 Phase C，纯函数唯一真相——EditMode 全矩阵锁定）：
    /// ① TDM（团队竞技）：仅本队大本营内可换——离开未开枪→回来仍可换；
    ///    离开过且开过枪→本生命锁定（复活恢复）；
    ///    判定式：allowed = inZone && !(hasFired && hasLeftZone)。
    /// ② KillRace（击杀竞赛/个人竞技）：复活点半径内可换；离开范围或开过枪即本生命锁定
    ///    （回来也不解锁）——比 TDM 多一个"离开即永久锁"项；
    ///    判定式：allowed = inZone && !hasFired && !hasLeftZone。
    /// 公共闸（调用方 NetworkCombatAuthority 组装）：存活 + InProgress + 非重复背包 + 有配装档案。
    /// 开火锁存口径（用户拍板 2026-09-29）：枪械实际开火（服务器接受）与投掷物出手都置位
    /// ——否则"扔完雷回出生点换包补雷"成为漏洞。
    /// </summary>
    public static class BackpackSwitchPolicy
    {
        /// <summary>TDM 大本营半径（米，XZ 平面）：锚点=本队出生簇质心。实机调参只动常量。</summary>
        public const float BaseCampRadiusMeters = 15f;

        /// <summary>KillRace 出生区半径（米，XZ 平面）：锚点=本人上次出生点。</summary>
        public const float SpawnZoneRadiusMeters = 8f;

        /// <summary>背包数（与后端 BackpackPolicy.BackpackCount 同值镜像——后端是权威，此处只做边界钳制）。</summary>
        public const int BackpackCount = 3;

        /// <summary>拒绝原因（冻结字面值：TargetRpc 投递给 Owner UI；序号稳定供测试断言）。</summary>
        public enum DenyReason : byte
        {
            None = 0,
            NotAlive = 1,
            NotInMatch = 2,
            AlreadyActive = 3,
            LockedByFire = 4,
            LockedByLeaving = 5,
            NotInZone = 6,
            PendingShots = 7,
            NoBackpackData = 8,
        }

        /// <summary>XZ 平面距离判定（Y 不参与——楼梯/跳台不改变"在区内"语义）。</summary>
        public static bool IsInZone(Vector3 position, Vector3 anchor, float radius)
        {
            float dx = position.x - anchor.x;
            float dz = position.z - anchor.z;
            return dx * dx + dz * dz <= radius * radius;
        }

        /// <summary>
        /// 规则矩阵（纯函数，服务器在切换请求时求值）。
        /// hasLeftZone=本生命内曾离开过区域（服务器 tick 维护的锁存）；
        /// hasFired=本生命内曾实际开火/投掷（服务器开火接受处置位）。
        /// </summary>
        public static DenyReason Evaluate(bool isTeamMatch, bool inZone, bool hasFired, bool hasLeftZone)
        {
            if (isTeamMatch)
            {
                // TDM：区域内即可换，除非"离开过且开过枪"（回来也不解锁——两个条件都命中才锁）
                if (!inZone) return DenyReason.NotInZone;
                if (hasFired && hasLeftZone) return DenyReason.LockedByFire;
                return DenyReason.None;
            }
            // KillRace：离开过或开过枪即永久锁（回来也不解锁）
            if (hasFired) return DenyReason.LockedByFire;
            if (hasLeftZone) return DenyReason.LockedByLeaving;
            if (!inZone) return DenyReason.NotInZone;
            return DenyReason.None;
        }

        /// <summary>Busy restricts an eligible player; it never restores lost zone/life eligibility.</summary>
        public static DenyReason EvaluateAvailability(bool isTeamMatch, bool inZone, bool hasFired,
            bool hasLeftZone, bool busy)
        {
            var eligibility = Evaluate(isTeamMatch, inZone, hasFired, hasLeftZone);
            return eligibility != DenyReason.None ? eligibility : busy ? DenyReason.PendingShots : DenyReason.None;
        }

        /// <summary>本生命区域半径（模式分派；离线/未知模式按 KillRace 半径——切换本就无档案可换）。</summary>
        public static float ZoneRadius(bool isTeamMatch)
            => isTeamMatch ? BaseCampRadiusMeters : SpawnZoneRadiusMeters;

        /// <summary>
        /// TDM 大本营中心 = 本队半场出生点质心（与 TeamSpawnDirectory.SplitByCentroid 同一切分，
        /// 纯函数——无出生点/无队伍回 false，调用方按 KillRace 锚点兜底）。
        /// </summary>
        public static bool TryResolveTeamBaseCenter(Vector3[] basePoints, string team, out Vector3 center)
        {
            center = default;
            if (basePoints == null || basePoints.Length == 0) return false;
            if (team != MatchRules.TeamRed && team != MatchRules.TeamBlue) return false;
            var centroid = Vector3.zero;
            for (int i = 0; i < basePoints.Length; i++) centroid += basePoints[i];
            centroid /= basePoints.Length;
            var sum = Vector3.zero;
            var count = 0;
            for (int i = 0; i < basePoints.Length; i++)
            {
                bool redSide = basePoints[i].x < centroid.x;
                if ((team == MatchRules.TeamRed) == redSide) { sum += basePoints[i]; count++; }
            }
            if (count == 0) return false;
            center = sum / count;
            return true;
        }

        /// <summary>背包索引钳制（0..2；越界回 0——调用方先拒绝非法值，此处只防御）。</summary>
        public static int ClampIndex(int backpackIndex)
            => backpackIndex < 0 || backpackIndex >= BackpackCount ? 0 : backpackIndex;
    }
}
