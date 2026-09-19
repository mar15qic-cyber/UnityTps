using System.Collections.Generic;
using Game.Gameplay.Network;
using Game.UI;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-08 追加 P0（审计 §5/§6）定向测试：
    /// ① 在线/Dedicated 模式门（GameplayLoadoutBootstrap.ShouldDisableAuthoredPlayer）——
    ///    场景预置离线 Player 仅离线保留，联网启动上下文/Dedicated 进程必须整树禁用；
    /// ② 同 UserId 单活（JoinTicketAuthenticator.FindExistingConnectionForUser）——
    ///    新连接接管：只命中同 userId 的旧连接，排除自身/空身份；
    /// ③ 服务器权威配装（NetworkLoadoutPolicy）——槽位名映射 + 权威附件按武器槽过滤、
    ///    目录缺失条目跳过不 fail closed。
    /// </summary>
    public sealed class NetworkLoadoutAndSingleActiveTests
    {
        // ---- ① 在线/Dedicated 模式门 ----

        [Test]
        public void AuthoredPlayerGate_DisablesOnlyInOnlineOrDedicated()
        {
            Assert.That(GameplayLoadoutBootstrap.ShouldDisableAuthoredPlayer(
                hasPendingClientLaunch: false, isDedicatedServer: false), Is.False,
                "离线直开/StartGameplay：预置玩家必须保留（行为不回归）");
            Assert.That(GameplayLoadoutBootstrap.ShouldDisableAuthoredPlayer(
                hasPendingClientLaunch: true, isDedicatedServer: false), Is.True,
                "生产联网路径（建房/加入后加载 Arena）：预置玩家必须整树禁用");
            Assert.That(GameplayLoadoutBootstrap.ShouldDisableAuthoredPlayer(
                hasPendingClientLaunch: false, isDedicatedServer: true), Is.True,
                "Dedicated Server 进程：预置玩家必须整树禁用（服务器侧影子同理）");
            Assert.That(GameplayLoadoutBootstrap.ShouldDisableAuthoredPlayer(
                hasPendingClientLaunch: true, isDedicatedServer: true), Is.True);
        }

        // ---- ② 同 UserId 单活 ----

        [Test]
        public void UserIdSingleActive_FindsOnlyOldConnectionOfSameUser()
        {
            var accepted = new Dictionary<int, TicketConsumeResult>
            {
                [1] = TicketConsumeResult.AcceptedFromBackend("ROOMAB", "user-a", "A"),
                [7] = TicketConsumeResult.AcceptedFromBackend("ROOMAB", "user-b", "B"),
            };

            // 同 userId 旧连接命中（新连接 9 接管 user-a 的旧连接 1）
            Assert.That(JoinTicketAuthenticator.FindExistingConnectionForUser(accepted, "user-a", 9), Is.EqualTo(1));

            // 排除自身：同 userId 已是本连接（重复 accept 场景）不得自踢
            Assert.That(JoinTicketAuthenticator.FindExistingConnectionForUser(accepted, "user-a", 1), Is.Null);

            // 不同用户不受影响
            Assert.That(JoinTicketAuthenticator.FindExistingConnectionForUser(accepted, "user-c", 9), Is.Null);

            // 空身份（调试旁路）不参与单活
            Assert.That(JoinTicketAuthenticator.FindExistingConnectionForUser(accepted, "", 9), Is.Null);
            Assert.That(JoinTicketAuthenticator.FindExistingConnectionForUser(accepted, null, 9), Is.Null);

            // 空档案表安全
            Assert.That(JoinTicketAuthenticator.FindExistingConnectionForUser(
                new Dictionary<int, TicketConsumeResult>(), "user-a", 9), Is.Null);
        }

        // ---- ③ 服务器权威配装 ----

        [Test]
        public void SlotName_MapsIndicesToBackendWeaponSlotNames()
        {
            Assert.That(NetworkLoadoutPolicy.SlotName(0), Is.EqualTo("Primary"));
            Assert.That(NetworkLoadoutPolicy.SlotName(1), Is.EqualTo("Secondary"));
            Assert.That(NetworkLoadoutPolicy.SlotName(-1), Is.EqualTo("Primary"), "越界一律 Primary");
            Assert.That(NetworkLoadoutPolicy.SlotName(5), Is.EqualTo("Primary"));
        }

        [Test]
        public void ResolveAttachmentEntries_FiltersByWeaponSlot_AndSkipsMissingItems()
        {
            var loadout = new TicketLoadoutSnapshot
            {
                primaryWeaponId = "weapon.scar",
                secondaryWeaponId = "weapon.p30",
                attachments = new[]
                {
                    new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentSlot = "Muzzle", attachmentItemId = "attach.lpfp.muffler.01" },
                    new TicketLoadoutAttachment { weaponSlot = "Secondary", attachmentSlot = "Muzzle", attachmentItemId = "attach.lpfp.muffler.01" },
                    new TicketLoadoutAttachment { weaponSlot = "PRIMARY", attachmentSlot = "Muzzle", attachmentItemId = "attach.nonexistent.zz" }, // 大小写不敏感 + 目录缺失跳过
                    new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentSlot = "Optic", attachmentItemId = "" }, // 空条目跳过
                },
            };

            var primary = NetworkLoadoutPolicy.ResolveAttachmentEntries(loadout, slotIndex: 0);
            var secondary = NetworkLoadoutPolicy.ResolveAttachmentEntries(loadout, slotIndex: 1);

            // Primary：命中目录的消音器 1 条（大小写不敏感命中 + 缺失/空条目跳过不 fail closed）
            Assert.That(primary.Count, Is.EqualTo(1), "Primary 过滤：目录存在的消音器命中，缺失/空条目跳过");
            Assert.That(primary[0].itemId, Is.EqualTo("attach.lpfp.muffler.01"));

            // Secondary：同条目按槽位归入 Secondary
            Assert.That(secondary.Count, Is.EqualTo(1));
            Assert.That(secondary[0].itemId, Is.EqualTo("attach.lpfp.muffler.01"));
        }

        [Test]
        public void ResolveAttachmentEntries_NullOrEmptyLoadout_IsSafeNoOp()
        {
            Assert.That(NetworkLoadoutPolicy.ResolveAttachmentEntries(null, 0), Is.Empty);
            Assert.That(NetworkLoadoutPolicy.ResolveAttachmentEntries(new TicketLoadoutSnapshot(), 0), Is.Empty);
            Assert.That(NetworkLoadoutPolicy.ResolveAttachmentEntries(new TicketLoadoutSnapshot
            {
                attachments = new[] { new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentItemId = "attach.lpfp.muffler.01" } },
            }, 0).Count, Is.EqualTo(1), "对照：正常条目应命中（防误把 NoOp 断成全空）");
        }
    }
}
