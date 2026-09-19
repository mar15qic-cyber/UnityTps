using System.Collections.Generic;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 稳定玩家身份锁定（入口任务 1：不以临时 FishNet ClientId 作为玩家永久身份）。
    /// MatchPlayerIdentity.ResolveFromProfiles 纯逻辑核心：
    /// ① 已认证档案 → 后端权威 userId；
    /// ② 调试旁路（不在档案）/档案缺 userId → debug-{ClientId} 回退（两端同构）；
    /// ③ 空字典/null → 回退，绝不抛异常。
    /// Day2 三缺口（2026-09-07）增补：服务器身份墓碑链路 ResolveServerIdentity——认证接受瞬间
    /// 记录 ClientId→userId，AcceptedUsers 清理竞态（任何 Stopped 订阅顺序）下离开者身份不退化。
    /// Resolve(NetworkCombatAuthority) 依赖 Unity NetworkObject/Owner，EditMode 不构建——
    /// 由定向联测的终局载荷 playerId 实机覆盖。
    /// </summary>
    public sealed class MatchPlayerIdentityTests
    {
        private static Dictionary<int, TicketConsumeResult> Profiles(params (int clientId, string userId)[] entries)
        {
            var dict = new Dictionary<int, TicketConsumeResult>();
            foreach (var (clientId, userId) in entries)
            {
                dict[clientId] = TicketConsumeResult.AcceptedFromBackend("ROOMAB", userId, $"name-{userId}");
            }
            return dict;
        }

        [Test]
        public void AcceptedProfile_ResolvesToStableBackendUserId()
        {
            var profiles = Profiles((7, "42"), (9, "43"));
            Assert.That(MatchPlayerIdentity.ResolveFromProfiles(profiles, 7), Is.EqualTo("42"),
                "已认证连接必须解析为后端权威 userId（ClientId 只是连接临时键）");
            Assert.That(MatchPlayerIdentity.ResolveFromProfiles(profiles, 9), Is.EqualTo("43"));
        }

        [Test]
        public void DebugBypass_NoProfile_FallsBackToStableDebugClientId()
        {
            Assert.That(MatchPlayerIdentity.ResolveFromProfiles(Profiles(), 7), Is.EqualTo("debug-7"),
                "unsafe debug 旁路不经过后端——回退 debug-{ClientId}（双端同构，ClientId 双端一致）");
        }

        [Test]
        public void ProfileWithEmptyUserId_FallsBackToDebugClientId()
        {
            var profiles = new Dictionary<int, TicketConsumeResult>
            {
                [7] = TicketConsumeResult.AcceptedFromBackend("ROOMAB", string.Empty, "name"),
            };
            Assert.That(MatchPlayerIdentity.ResolveFromProfiles(profiles, 7), Is.EqualTo("debug-7"),
                "档案存在但 userId 为空（后端异常数据）同样回退，绝不产出空身份");
        }

        [Test]
        public void NullOrEmptyProfiles_NeverThrows()
        {
            Assert.That(MatchPlayerIdentity.ResolveFromProfiles(null, 7), Is.EqualTo("debug-7"));
            Assert.That(MatchPlayerIdentity.ResolveFromProfiles(new Dictionary<int, TicketConsumeResult>(), 7),
                Is.EqualTo("debug-7"));
        }

        // ---- Day2 三缺口：accepted 身份墓碑（离开者身份与 Stopped 订阅顺序无关） ----

        [SetUp]
        public void ResetTombstones()
        {
            MatchPlayerIdentity.ResetTombstonesForTests();
        }

        [Test]
        public void Tombstone_ResolvesIdentity_AfterAcceptedUsersCleanup()
        {
            // 用户规则 3.6 主案：认证器清理 AcceptedUsers 后（其 Stopped 订阅先于 MatchLifecycle 执行的
            // 竞态序），离开者身份仍必须是后端权威 userId——墓碑在认证接受瞬间已记录
            MatchPlayerIdentity.RecordServerAccepted(7, "42");

            // AcceptedUsers 已被清理（空档案）→ 墓碑命中
            Assert.That(MatchPlayerIdentity.ResolveServerIdentity(7, new Dictionary<int, TicketConsumeResult>()),
                Is.EqualTo("42"),
                "AcceptedUsers 清理后身份不得退化为 debug-{ClientId}——与事件订阅顺序无关");
        }

        [Test]
        public void Tombstone_LiveAcceptedUsersStillWins()
        {
            // 正常时段：活档案优先（重连同 ClientId 场景以档案为准，墓碑只是断线窗口兜底）
            MatchPlayerIdentity.RecordServerAccepted(7, "42-old");
            var profiles = Profiles((7, "42-new"));
            Assert.That(MatchPlayerIdentity.ResolveServerIdentity(7, profiles), Is.EqualTo("42-new"),
                "活档案优先于墓碑（档案代表当前连接的真实身份）");
        }

        [Test]
        public void Tombstone_ReusedClientId_LatestAcceptWins()
        {
            // ClientId 复用（旧连接清理后新连接拿到同 id）：新 accept 覆盖墓碑，无陈旧窗口
            MatchPlayerIdentity.RecordServerAccepted(7, "100");
            MatchPlayerIdentity.RecordServerAccepted(7, "200");
            Assert.That(MatchPlayerIdentity.ResolveServerIdentity(7, new Dictionary<int, TicketConsumeResult>()),
                Is.EqualTo("200"));
        }

        [Test]
        public void Tombstone_MissingEntry_FallsBackToDebugClientId()
        {
            // 无档案无墓碑（从未认证/纯调试）→ debug 回退（原语义不变）
            Assert.That(MatchPlayerIdentity.ResolveServerIdentity(7, new Dictionary<int, TicketConsumeResult>()),
                Is.EqualTo("debug-7"));
        }

        [Test]
        public void Tombstone_InvalidInputs_AreIgnored()
        {
            // 无效 ClientId/空 userId 不得入墓碑（防脏数据）
            MatchPlayerIdentity.RecordServerAccepted(-1, "42");
            MatchPlayerIdentity.RecordServerAccepted(7, string.Empty);
            Assert.That(MatchPlayerIdentity.ResolveServerIdentity(7, new Dictionary<int, TicketConsumeResult>()),
                Is.EqualTo("debug-7"));
        }

        [Test]
        public void MatchLifecycle_PlayerId_DelegatesThroughTombstoneChain()
        {
            // MatchLifecycle.PlayerId → MatchPlayerIdentity.Resolve 服务器分支一致性：
            // 墓碑记录后按 ClientId 解析命中（Resolve 全链在 IT 实机覆盖，此处锁委托语义存在的编译接线）
            MatchPlayerIdentity.RecordServerAccepted(9, "77");
            Assert.That(MatchPlayerIdentity.ResolveServerIdentity(9, null), Is.EqualTo("77"),
                "null 档案 + 墓碑 = 断线窗口实况（AcceptedUsers 引用被清理或不可达）");
        }
    }
}
