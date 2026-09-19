using System.Collections.Generic;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Gate A-2（2026-09-08 P0 追加复审 §1.2）定向测试：权威附件网络快照——
    /// ① 编解码契约（权威空哨兵 vs 快照不在位 vs 列表）；
    /// ② 两个玩家附件不同 → 各自快照解析出各自的条目；
    /// ③ 远端不能读取本机账号附件（WeaponAttachmentStore/PlayerPrefs 不参与快照路径）；
    /// ④ 缺失目录条目安全降级（跳过不 fail closed）；
    /// ⑤ 槽位映射（主/副/未知定义）与离线实例（快照不在位）回退语义。
    /// </summary>
    public sealed class AttachmentSnapshotSyncTests
    {
        private const string WeaponItemA = "weapon.test.scar";
        private const string WeaponItemB = "weapon.test.ak";
        private const string AttachMuffler = "attach.test.muffler";
        private const string AttachOptic = "attach.test.optic";

        private AttachmentAssetCatalog _testCatalog;
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            if (_testCatalog != null) Object.DestroyImmediate(_testCatalog);
            _testCatalog = null;
            foreach (var created in _created)
                if (created != null) Object.DestroyImmediate(created);
            _created.Clear();
            // WeaponAttachmentStore 走 PlayerPrefs——清理测试写入的键（写空即清）
            WeaponAttachmentStore.Save(WeaponItemA, new Dictionary<string, string>());
        }

        private AttachmentAssetCatalog TestCatalog()
        {
            if (_testCatalog != null) return _testCatalog;
            _testCatalog = ScriptableObject.CreateInstance<AttachmentAssetCatalog>();
            _testCatalog.EditorEntries.Add(new AttachmentAssetEntry { itemId = AttachMuffler, slot = AttachmentSlotType.Muzzle });
            _testCatalog.EditorEntries.Add(new AttachmentAssetEntry { itemId = AttachOptic, slot = AttachmentSlotType.Optic });
            return _testCatalog;
        }

        // ---- ① 编解码契约 ----

        [Test]
        public void EncodeDecode_EmptyList_ProducesAuthoritativeEmptySentinel()
        {
            Assert.That(AttachmentSnapshotCodec.Encode(new List<string>()), Is.EqualTo("-"));
            Assert.That(AttachmentSnapshotCodec.Encode(null), Is.EqualTo("-"));

            Assert.That(AttachmentSnapshotCodec.TryDecode("-", out var empty), Is.True, "哨兵 = 快照在位（权威空装配）");
            Assert.That(empty, Is.Empty);

            Assert.That(AttachmentSnapshotCodec.TryDecode("", out _), Is.False, "空串 = 快照不在位（允许回退）");
            Assert.That(AttachmentSnapshotCodec.TryDecode(null, out _), Is.False);
        }

        [Test]
        public void EncodeDecode_RoundTrip_MultipleIds_AndIgnoresMalformedTokens()
        {
            var encoded = AttachmentSnapshotCodec.Encode(new List<string> { AttachMuffler, "", "  ", AttachOptic });
            Assert.That(encoded, Is.EqualTo(AttachMuffler + ";" + AttachOptic));

            Assert.That(AttachmentSnapshotCodec.TryDecode(encoded, out var ids), Is.True);
            Assert.That(ids, Is.EqualTo(new[] { AttachMuffler, AttachOptic }));

            Assert.That(AttachmentSnapshotCodec.TryDecode(";;", out var blanks), Is.True);
            Assert.That(blanks, Is.Empty, "全空白 token = 权威在位但无有效条目");
        }

        [Test]
        public void EncodeForSlot_FiltersByWeaponSlot_CaseInsensitive_AndSkipsBlankEntries()
        {
            var loadout = new TicketLoadoutSnapshot
            {
                attachments = new[]
                {
                    new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentItemId = AttachMuffler },
                    new TicketLoadoutAttachment { weaponSlot = "primary", attachmentItemId = AttachOptic },
                    new TicketLoadoutAttachment { weaponSlot = "Secondary", attachmentItemId = AttachOptic },
                    new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentItemId = "" },
                    new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentItemId = null },
                },
            };

            Assert.That(AttachmentSnapshotCodec.EncodeForSlot(loadout, "Primary"),
                Is.EqualTo(AttachMuffler + ";" + AttachOptic), "Primary 过滤（大小写不敏感，空条目跳过）");
            Assert.That(AttachmentSnapshotCodec.EncodeForSlot(loadout, "Secondary"), Is.EqualTo(AttachOptic));
            Assert.That(AttachmentSnapshotCodec.EncodeForSlot(null, "Primary"), Is.EqualTo("-"));
            Assert.That(AttachmentSnapshotCodec.EncodeForSlot(new TicketLoadoutSnapshot(), "Primary"), Is.EqualTo("-"),
                "无附件数组 = 权威空装配（快照在位）");
        }

        // ---- ② 两个玩家附件不同 ----

        [Test]
        public void TwoPlayers_WithDifferentAttachments_ResolveDifferentEntries()
        {
            var playerALoadout = new TicketLoadoutSnapshot
            {
                primaryWeaponId = WeaponItemA,
                attachments = new[] { new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentItemId = AttachMuffler } },
            };
            var playerBLoadout = new TicketLoadoutSnapshot
            {
                primaryWeaponId = WeaponItemB,
                attachments = new[] { new TicketLoadoutAttachment { weaponSlot = "Primary", attachmentItemId = AttachOptic } },
            };
            var encodedA = AttachmentSnapshotCodec.EncodeForSlot(playerALoadout, "Primary");
            var encodedB = AttachmentSnapshotCodec.EncodeForSlot(playerBLoadout, "Primary");

            var entries = new List<AttachmentAssetEntry>();
            Assert.That(AttachmentSnapshotCodec.TryResolveEntries(encodedA, entries, TestCatalog()), Is.True);
            Assert.That(entries.ConvertAll(e => e.itemId), Is.EqualTo(new[] { AttachMuffler }), "玩家 A 快照 → 消音器");
            Assert.That(AttachmentSnapshotCodec.TryResolveEntries(encodedB, entries, TestCatalog()), Is.True);
            Assert.That(entries.ConvertAll(e => e.itemId), Is.EqualTo(new[] { AttachOptic }), "玩家 B 快照 → 瞄具（互不串扰）");
        }

        [Test]
        public void SlotResolution_MapsDefinitionToPrimaryOrSecondary_UnknownFallsBack()
        {
            Assert.That(NetworkWeaponState.ResolveSlotIndex(WeaponItemA, WeaponItemB, WeaponItemA), Is.EqualTo(0));
            Assert.That(NetworkWeaponState.ResolveSlotIndex(WeaponItemA, WeaponItemB, WeaponItemB), Is.EqualTo(1));
            Assert.That(NetworkWeaponState.ResolveSlotIndex(WeaponItemA, WeaponItemB, "weapon.debug.ten_slots"), Is.EqualTo(-1),
                "调试十槽定义不属于权威两槽 → 回退本机存储");
            Assert.That(NetworkWeaponState.ResolveSlotIndex(WeaponItemA, WeaponItemB, null), Is.EqualTo(-1));
        }

        // ---- ③ 远端不能读取本机账号附件 ----

        [Test]
        public void AuthoritativeSnapshotPresent_LocalStoreContents_MustNotLeakIntoResolution()
        {
            // 本机账号存储写入「别人账号才有的」配件——快照在位时绝不参与解析
            WeaponAttachmentStore.Save(WeaponItemA, new Dictionary<string, string> { ["Muzzle"] = AttachOptic });

            var entries = new List<AttachmentAssetEntry>();
            // 权威空装配（"-"）在位：即使本机存储有内容，解析结果必须为空且返回 true（快照驱动）
            Assert.That(AttachmentSnapshotCodec.TryResolveEntries("-", entries, TestCatalog()), Is.True,
                "权威空装配 = 快照在位，调用方不得回退本机存储");
            Assert.That(entries, Is.Empty, "本机 WeaponAttachmentStore 的配件绝不得出现在权威快照解析结果里");

            // 快照不在位（""）才允许回退——该分支由 FPWeaponRig/TPWeaponMeshSwapper 走既有存储路径
            Assert.That(AttachmentSnapshotCodec.TryDecode("", out _), Is.False);
        }

        [Test]
        public void NetworkWeaponState_OfflineInstance_ReportsSnapshotAbsent()
        {
            var go = new GameObject("P30Probe");
            _created.Add(go);
            var state = go.AddComponent<NetworkWeaponState>(); // EditMode 不触发 Awake/FishNet 生命周期
            var definition = ScriptableObject.CreateInstance<WeaponDefinition>();
            _created.Add(definition);

            var entries = new List<AttachmentAssetEntry>();
            Assert.That(state.TryGetAuthoritativeAttachments(null, entries), Is.False);
            Assert.That(state.TryGetAuthoritativeAttachments(definition, entries), Is.False,
                "离线（SyncVar 初值空）= 快照不在位 → 调用方回退本机存储");
        }

        // ---- ④ 缺失目录条目安全降级 ----

        [Test]
        public void MissingCatalogEntries_AreSkipped_NotFailClosed()
        {
            var encoded = AttachmentSnapshotCodec.Encode(new List<string> { AttachMuffler, "attach.missing.zz", AttachOptic });
            var entries = new List<AttachmentAssetEntry>();

            Assert.That(AttachmentSnapshotCodec.TryResolveEntries(encoded, entries, TestCatalog()), Is.True);
            Assert.That(entries.ConvertAll(e => e.itemId), Is.EqualTo(new[] { AttachMuffler, AttachOptic }),
                "缺失条目跳过（告警），其余正常应用——配件缺失不应踢出合法玩家");
        }
    }
}
