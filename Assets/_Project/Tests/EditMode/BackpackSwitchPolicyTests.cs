using System.Reflection;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CF 三背包对局内切换（2026-09-30 Phase C）：
    /// ① BackpackSwitchPolicy 规则矩阵全锁定（TDM 双条件锁 / KillRace 单条件锁）；
    /// ② 区域判定（XZ 平面/半径/队基心切分）；
    /// ③ ThrowableLoadoutPolicy 配包映射（standard=原版默认、frag_assault=全破片、空=零雷）；
    /// ④ PlayerNetworkAdapter.ServerSwitchBackpack 重配路径（成功换槽/解析失败回滚）。
    /// </summary>
    public sealed class BackpackSwitchPolicyTests
    {
        // ---- ① TDM 规则矩阵（用户口径 2026-09-29：离开未开枪→回来仍可换；离开过且开过枪→锁）----

        [Test]
        public void Evaluate_Tdm_AllowedOnlyInsideBaseUnlessLeftAndFired()
        {
            // 区内 + 未离开 + 未开火 → 允许
            Assert.That(BackpackSwitchPolicy.Evaluate(isTeamMatch: true, inZone: true, hasFired: false, hasLeftZone: false),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.None));
            // 区外 → 拒（NotInZone 优先呈现）
            Assert.That(BackpackSwitchPolicy.Evaluate(true, false, false, false),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.NotInZone));
            // 区内 + 开过火但从未离开 → 仍可换（用户字面规则：锁=离开过 且 开过火）
            Assert.That(BackpackSwitchPolicy.Evaluate(true, true, true, false),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.None));
            // 离开过 + 开过火 + 回到区内 → 本生命锁定（LockedByFire）
            Assert.That(BackpackSwitchPolicy.Evaluate(true, true, true, true),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.LockedByFire));
            // 离开过 + 开过火 + 区外 → 锁定（NotInZone 先呈现——离区者提示"仅出生区域可更换"）
            Assert.That(BackpackSwitchPolicy.Evaluate(true, false, true, true),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.NotInZone));
        }

        // ---- ② KillRace 规则矩阵（离开范围或开过枪即永久锁——回来也不解锁）----

        [Test]
        public void Evaluate_KillRace_LeavingOrFiringLocksForLife()
        {
            Assert.That(BackpackSwitchPolicy.Evaluate(false, true, false, false),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.None), "出生圈内未开火 → 允许");
            Assert.That(BackpackSwitchPolicy.Evaluate(false, false, false, false),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.NotInZone), "离开范围 → 区外拒绝");
            Assert.That(BackpackSwitchPolicy.Evaluate(false, true, true, false),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.LockedByFire), "开过火即永久锁");
            Assert.That(BackpackSwitchPolicy.Evaluate(false, true, false, true),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.LockedByLeaving), "离开过即永久锁（回来也不解锁）");
            Assert.That(BackpackSwitchPolicy.Evaluate(false, true, true, true),
                Is.EqualTo(BackpackSwitchPolicy.DenyReason.LockedByFire), "双锁并存优先呈现开火锁");
        }

        [Test]
        public void IsInZone_XzPlaneDistance_Boundary_Inclusive()
        {
            var anchor = new Vector3(10f, 5f, -3f);
            Assert.That(BackpackSwitchPolicy.IsInZone(new Vector3(10f, 99f, -3f), anchor, 15f), Is.True, "Y 不参与判定");
            Assert.That(BackpackSwitchPolicy.IsInZone(new Vector3(25f, 0f, -3f), anchor, 15f), Is.True, "恰好 15m 边界（含）");
            Assert.That(BackpackSwitchPolicy.IsInZone(new Vector3(25.01f, 0f, -3f), anchor, 15f), Is.False, "越界即区外");
        }

        [Test]
        public void ZoneRadius_ModeDispatch()
        {
            Assert.That(BackpackSwitchPolicy.ZoneRadius(true), Is.EqualTo(BackpackSwitchPolicy.BaseCampRadiusMeters));
            Assert.That(BackpackSwitchPolicy.ZoneRadius(false), Is.EqualTo(BackpackSwitchPolicy.SpawnZoneRadiusMeters));
        }

        [Test]
        public void TryResolveTeamBaseCenter_SplitsByCentroidLikeSpawnDirectory()
        {
            // 两半场：红 x<0（-10,-20），蓝 x>0（+10,+20）——与 TeamSpawnDirectory.SplitByCentroid 同切分
            var points = new[]
            {
                new Vector3(-10f, 0f, 0f), new Vector3(-20f, 0f, 5f),
                new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, -5f),
            };
            Assert.That(BackpackSwitchPolicy.TryResolveTeamBaseCenter(points, MatchRules.TeamRed, out var red), Is.True);
            Assert.That(red.x, Is.EqualTo(-15f).Within(0.001f), "红队基心=红半场出生点均值");
            Assert.That(BackpackSwitchPolicy.TryResolveTeamBaseCenter(points, MatchRules.TeamBlue, out var blue), Is.True);
            Assert.That(blue.x, Is.EqualTo(15f).Within(0.001f));
            // 无队伍/空出生点 → false（调用方按 KillRace 锚点兜底）
            Assert.That(BackpackSwitchPolicy.TryResolveTeamBaseCenter(points, MatchRules.TeamNone, out _), Is.False);
            Assert.That(BackpackSwitchPolicy.TryResolveTeamBaseCenter(null, MatchRules.TeamRed, out _), Is.False);
            Assert.That(BackpackSwitchPolicy.TryResolveTeamBaseCenter(new Vector3[0], MatchRules.TeamRed, out _), Is.False);
        }

        [Test]
        public void ClampIndex_Defensive()
        {
            Assert.That(BackpackSwitchPolicy.ClampIndex(0), Is.EqualTo(0));
            Assert.That(BackpackSwitchPolicy.ClampIndex(2), Is.EqualTo(2));
            Assert.That(BackpackSwitchPolicy.ClampIndex(3), Is.EqualTo(0));
            Assert.That(BackpackSwitchPolicy.ClampIndex(-1), Is.EqualTo(0));
        }

        // ---- ③ 投掷配包映射 ----

        private static ThrowableCatalog BuildCatalog(int frag, int flash, int smoke)
        {
            var catalog = ScriptableObject.CreateInstance<ThrowableCatalog>();
            catalog.Frag = ScriptableObject.CreateInstance<ThrowableDefinition>();
            catalog.Frag.Type = ThrowableType.Frag; catalog.Frag.InitialCount = frag;
            catalog.Flash = ScriptableObject.CreateInstance<ThrowableDefinition>();
            catalog.Flash.Type = ThrowableType.Flash; catalog.Flash.InitialCount = flash;
            catalog.Smoke = ScriptableObject.CreateInstance<ThrowableDefinition>();
            catalog.Smoke.Type = ThrowableType.Smoke; catalog.Smoke.InitialCount = smoke;
            return catalog;
        }

        [Test]
        public void ThrowablePolicy_Standard_MirrorsCatalogDefaults()
        {
            var counts = new int[3];
            Assert.That(ThrowableLoadoutPolicy.TryResolve("throwable.standard", BuildCatalog(2, 1, 1), counts), Is.True);
            Assert.That(counts[(int)ThrowableType.Frag], Is.EqualTo(2));
            Assert.That(counts[(int)ThrowableType.Flash], Is.EqualTo(1));
            Assert.That(counts[(int)ThrowableType.Smoke], Is.EqualTo(1));
        }

        [Test]
        public void ThrowablePolicy_FragAssault_ThrowsFragOnly()
        {
            var counts = new int[3];
            Assert.That(ThrowableLoadoutPolicy.TryResolve("throwable.frag_assault", BuildCatalog(2, 1, 1), counts), Is.True);
            Assert.That(counts[(int)ThrowableType.Frag], Is.EqualTo(3));
            Assert.That(counts[(int)ThrowableType.Flash], Is.EqualTo(0));
            Assert.That(counts[(int)ThrowableType.Smoke], Is.EqualTo(0));
        }

        [Test]
        public void ThrowablePolicy_EmptyOrUnknown_ZerosWithoutGuessing()
        {
            var counts = new int[3];
            // JsonUtility 把 JSON null 解析为空串——null 与 "" 同语义（不带雷）
            Assert.That(ThrowableLoadoutPolicy.TryResolve(null, BuildCatalog(2, 1, 1), counts), Is.False);
            Assert.That(ThrowableLoadoutPolicy.TryResolve("", BuildCatalog(2, 1, 1), counts), Is.False);
            for (int i = 0; i < 3; i++) Assert.That(counts[i], Is.EqualTo(0));
            // 未知 id：宁缺勿错（零雷 + 警警），不猜默认
            Assert.That(ThrowableLoadoutPolicy.TryResolve("throwable.future_pack", BuildCatalog(2, 1, 1), counts), Is.False);
            for (int i = 0; i < 3; i++) Assert.That(counts[i], Is.EqualTo(0));
        }

        // ---- ④ PNA.ServerSwitchBackpack 重配路径（EditMode 直驱：真实目录解析 + 槽位交换 + 回滚）----

        private static TicketLoadoutSnapshot Snapshot(string primary, string secondary, int index)
            => new() { primaryWeaponId = primary, secondaryWeaponId = secondary, version = 1, backpackIndex = index };

        [Test]
        public void ServerSwitchBackpack_ReconfiguresArsenalAndAdvancesIndex()
        {
            var go = new GameObject("pna-switch");
            try
            {
                go.AddComponent<Arsenal>();
                var adapter = go.AddComponent<PlayerNetworkAdapter>();
                var backpacks = new[]
                {
                    Snapshot("weapon.m4", "weapon.service_pistol", 0),
                    Snapshot("weapon.ak", "weapon.handgun02", 1),
                    Snapshot("weapon.m4", "weapon.service_pistol", 2),
                };
                SetPrivate(adapter, "_authoritativeBackpacks", backpacks);
                SetPrivate(adapter, "_activeBackpackIndex", 0);
                SetPrivate(adapter, "_authoritativeLoadout", backpacks[0]);

                string error = InvokeServerSwitch(adapter, 1);

                Assert.That(error, Is.Null, "合法背包必须切换成功（weapon.ak/handgun02 均为 LPFP 正式条目）");
                Assert.That(adapter.ActiveBackpackIndex, Is.EqualTo(1));
                var arsenal = go.GetComponent<Arsenal>();
                Assert.That(arsenal.SlotCount, Is.EqualTo(2), "重配后仍为权威两槽");
                Assert.That(arsenal.Slots[0].CatalogItemId, Is.EqualTo("weapon.ak"), "主槽换为背包 2 主武器");
                Assert.That(arsenal.Slots[1].CatalogItemId, Is.EqualTo("weapon.handgun02"), "副槽换为背包 2 副武器");
                Assert.That(adapter.ActiveBackpackSnapshot.primaryWeaponId, Is.EqualTo("weapon.ak"),
                    "活动快照指针随切换推进（后续装备期配件重套读它）");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ServerSwitchBackpack_InvalidEntry_RollsBackState()
        {
            var go = new GameObject("pna-switch-fail");
            try
            {
                go.AddComponent<Arsenal>();
                var adapter = go.AddComponent<PlayerNetworkAdapter>();
                var backpacks = new[]
                {
                    Snapshot("weapon.m4", "weapon.service_pistol", 0),
                    Snapshot("not.a.weapon", "weapon.handgun02", 1),
                    Snapshot("weapon.m4", "weapon.service_pistol", 2),
                };
                SetPrivate(adapter, "_authoritativeBackpacks", backpacks);
                SetPrivate(adapter, "_activeBackpackIndex", 0);
                SetPrivate(adapter, "_authoritativeLoadout", backpacks[0]);

                string error = InvokeServerSwitch(adapter, 1);

                Assert.That(error, Is.Not.Null, "非 LPFP 条目必须拒绝");
                Assert.That(adapter.ActiveBackpackIndex, Is.EqualTo(0), "失败必须回滚活动下标");
                Assert.That(adapter.ActiveBackpackSnapshot.primaryWeaponId, Is.EqualTo("weapon.m4"), "失败必须回滚快照指针");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ServerSwitchBackpack_MissingData_ReturnsError()
        {
            var go = new GameObject("pna-nodata");
            try
            {
                var adapter = go.AddComponent<PlayerNetworkAdapter>();
                Assert.That(adapter.HasBackpackData, Is.False);
                Assert.That(InvokeServerSwitch(adapter, 1), Is.Not.Null, "无三背包数据（旧后端/调试 Host）必须拒绝");
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static string InvokeServerSwitch(PlayerNetworkAdapter adapter, int index)
        {
            var method = typeof(PlayerNetworkAdapter).GetMethod("ServerSwitchBackpack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "ServerSwitchBackpack 薄壳必须存在（供 NCA 调用与测试直驱）");
            return (string)method.Invoke(adapter, new object[] { index });
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, $"字段 {field} 必须存在（测试接缝）");
            info.SetValue(target, value);
        }
    }
}
