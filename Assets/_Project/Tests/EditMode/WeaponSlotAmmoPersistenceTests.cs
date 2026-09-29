using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day4 残余审计 P0-2 回归：切槽不得补满弹药——每把武器持有独立且持久的服务器权威
    /// 运行时状态（WeaponController 按 WeaponId 缓存弹匣/备弹），切槽只切换当前引用；
    /// 换弹中切枪只取消换弹不赠送子弹；配件容量变化按新容量钳制当前弹药（不隐式补满）；
    /// OnAmmoChanged（服务器 SyncVar 数据源）不回退。
    /// 搭建方式与 ArsenalTests 一致（AddComponent 不跑 Awake——引用反射直写）。
    /// </summary>
    public sealed class WeaponSlotAmmoPersistenceTests
    {
        private GameObject _root;
        private ActionSystem _actions;
        private WeaponController _controller;
        private Arsenal _arsenal;
        private WeaponDefinition _pistol;
        private WeaponDefinition _rifle;
        private DemoBalanceConfig _balance;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("SlotAmmo_Test");
            _root.transform.position = new Vector3(4600, 4600, 4600);
            _actions = _root.AddComponent<ActionSystem>();
            _root.AddComponent<CombatResolver>();
            _controller = _root.AddComponent<WeaponController>();
            _arsenal = _root.AddComponent<Arsenal>();

            _pistol = NewWeapon("test.pistol", 0.3f, 0.3f);
            _rifle = NewWeapon("test.rifle", 0.3f, 0.3f);
            _balance = NewBalance(("test.pistol", 34, 12, 48, 1.0f), ("test.rifle", 26, 30, 120, 2.0f));

            SetField(_controller, "definition", _pistol);
            SetField(_controller, "balanceConfigAsset", _balance);
            SetField(_controller, "actionSystem", _actions);
            SetField(_controller, "combatResolver", _root.GetComponent<CombatResolver>());
            SetField(_controller, "processLocalInput", false);
            SetField(_controller, "aimPivot", _root.transform);
            _controller.Initialize(_pistol, _balance);

            SetField(_arsenal, "controller", _controller);
            SetField(_arsenal, "actionSystem", _actions);
            SetField(_arsenal, "slots", new[] { _pistol, _rifle });
            _arsenal.TrySelectSlot(0);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_pistol);
            Object.DestroyImmediate(_rifle);
            Object.DestroyImmediate(_balance);
        }

        private void CompleteSwitch()
        {
            // 小步 Tick + 逐帧 EvaluateSwap（交换点=收枪时长耗尽且动作未完成；
            // 一口气 Tick 越过整个动作会让 CurrentAction 变 None、交换窗口错过）
            for (int i = 0; i < 200; i++)
            {
                _actions.Tick(0.01f);
                _arsenal.EvaluateSwap();
            }
        }

        [Test]
        public void ServerSwitch_TwoSlotsWithSameWeapon_AdvancesEquipmentCommandAndSlot()
        {
            SetField(_arsenal, "slots", new[] { _pistol, _pistol });
            var authority = _root.AddComponent<NetworkCombatAuthority>();
            SetField(authority, "_controller", _controller);
            var method = typeof(NetworkCombatAuthority).GetMethod("ExecuteCombatAction",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That((bool)method.Invoke(authority, new object[] { new TimedFireRequest {
                Kind = 2, Slot = 1, CommandId = 17, WeaponId = _pistol.WeaponId, ShotSeconds = 1 } }), Is.True);
            CompleteSwitch();
            Assert.That(_arsenal.ActiveIndex, Is.EqualTo(1));
            Assert.That(typeof(NetworkCombatAuthority).GetField("_executedEquipmentCommand",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(authority), Is.EqualTo(17u));
        }

        [Test]
        public void RejectedSwitch_RestoresWeaponAndEpoch_WhileLateAcknowledgmentsCannotUndoNewerActions()
        {
            var authority = _root.AddComponent<NetworkCombatAuthority>();
            SetField(authority, "_controller", _controller);
            var networkWeapon = _root.AddComponent<NetworkWeaponState>();
            SetField(networkWeapon, "_controller", _controller);
            SetField(networkWeapon, "_arsenal", _arsenal);
            _controller.EquipDefinition(_rifle);
            _arsenal.AlignToEquippedDefinition(_rifle);
            SetField(authority, "_lastSubmittedActionCommand", 17u);
            SetField(authority, "_submittedEquipmentCommand", 17u);
            _actions.TryStart(PlayerActionType.SwitchWeapon, 1);
            authority.ApplyCombatActionResult(0, 17, 2, false, 8, _pistol.WeaponId);
            Assert.That(_actions.IsBusy, Is.False);
            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(_arsenal.ActiveIndex, Is.Zero);
            var epoch = typeof(NetworkCombatAuthority).GetField("_submittedEquipmentCommand",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(epoch.GetValue(authority), Is.EqualTo(8u));
            SetField(authority, "_lastSubmittedActionCommand", 18u);
            SetField(authority, "_submittedEquipmentCommand", 18u);
            _actions.TryStart(PlayerActionType.SwitchWeapon, 1);
            authority.ApplyCombatActionResult(0, 17, 2, false, 8, _pistol.WeaponId);
            Assert.That(epoch.GetValue(authority), Is.EqualTo(18u));
            Assert.That(_actions.IsBusy, Is.True);
            authority.ObserveOwnerAmmoLifeEpoch(1);
            authority.ApplyCombatActionResult(0, 18, 2, true, 18, _rifle.WeaponId);
            Assert.That(epoch.GetValue(authority), Is.EqualTo(0u), "Old-life ACK cannot restore an old equipment epoch");
        }

        [Test]
        public void ReloadReply_AfterRejectedSwitch_RepairsEquipmentEpoch()
        {
            var authority = _root.AddComponent<NetworkCombatAuthority>();
            SetField(authority, "_controller", _controller);
            SetField(authority, "_lastSubmittedActionCommand", 19u);
            SetField(authority, "_submittedEquipmentCommand", 18u);
            authority.ApplyCombatActionResult(0, 18, 2, false, 8, _pistol.WeaponId);
            authority.ApplyCombatActionResult(0, 19, 1, false, 8, _pistol.WeaponId);
            Assert.That(typeof(NetworkCombatAuthority).GetField("_submittedEquipmentCommand",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(authority), Is.EqualTo(8u));
        }

        [Test]
        public void RejectedPrediction_PreservesNextShotBloom_WithoutAmmoDamageOrDuplicatePulse()
        {
            var serialized = new SerializedObject(_balance);
            var accuracy = serialized.FindProperty("weapons").GetArrayElementAtIndex(0)
                .FindPropertyRelative("Stat").FindPropertyRelative("Accuracy");
            accuracy.FindPropertyRelative("BaseHipSpread").floatValue = .25f;
            accuracy.FindPropertyRelative("ShotBloomPerShot").floatValue = .35f;
            accuracy.FindPropertyRelative("MaxBloom").floatValue = 2f;
            accuracy.FindPropertyRelative("BloomRecoveryDelay").floatValue = .2f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _controller.Initialize(_pistol, _balance);
            int ammo = _controller.Runtime.CurrentAmmo;
            int emitted = 0;
            WeaponShot shot = default;
            _controller.OnShotFired += s => { emitted++; shot = s; };
            var expected = new WeaponAccuracyState();
            expected.OnShot(_controller.Resolved);
            _controller.AdvanceRejectedPrediction(10);
            _controller.AdvanceRejectedPrediction(10); // duplicate does not add bloom twice
            _controller.AdvanceRejectedPrediction(9); // stale request cannot rewind it
            Assert.That(emitted, Is.Zero);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(ammo));
            expected.Tick(.1f, _controller.Resolved);
            float spread = expected.CurrentSpread(WeaponFireContext.Default, _controller.Resolved);
            Assert.That(spread, Is.EqualTo(.6f).Within(.00001f), "Fixture must contain a nonzero rejected-shot bloom pulse");
            Assert.That(_controller.TryFireWithServerSnapshot(_root.transform.position, Vector3.forward,
                WeaponFireContext.Default, 123, default, true, 10.1), Is.True);
            Assert.That(emitted, Is.EqualTo(1));
            Assert.That(shot.FinalSpreadDegrees, Is.EqualTo(spread).Within(.00001f));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(ammo - 1));
        }

        private void FireRounds(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.That(_controller.TryFire(), Is.True, $"第 {i + 1} 发必须成功（冷却用 Runtime.Tick 清除）");
                _controller.Runtime.Tick(0.2f); // 清除射速冷却（internal，InternalsVisibleTo）
            }
        }

        [Test]
        public void ExtendedMagazine_SwitchBackAndReapplyAttachments_KeepsLoadedRounds()
        {
            var extended = new AttachmentAssetEntry
            {
                slot = AttachmentSlotType.Magazine,
                modifiers = { new AttachmentModifierEntry { stat = WeaponStatId.MagazineSize, op = ModifierOperation.Add, value = 6f } },
            };
            _controller.SetAttachments(new[] { extended });
            Assert.That(_controller.TryReload(), Is.True);
            _controller.Runtime.CompleteReload(); // EditMode fixture 不依赖 MonoBehaviour.OnEnable 的动作事件接线
            _actions.Tick(2f);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(18));
            FireRounds(1);
            var reserve = _controller.Runtime.ReserveAmmo;

            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(30), "副枪不得继承主枪扩容");
            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            _controller.SetAttachments(new[] { extended }); // 服务器装备回调重套同一权威配装
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(17));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(reserve));

            _controller.SetAttachments(System.Array.Empty<AttachmentAssetEntry>());
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12), "真实卸下扩容仍按新容量钳制");
        }

        [Test]
        public void FireSwitchAwayAndBack_KeepsPrimaryAmmo_NoFreeRefill()
        {
            FireRounds(3);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(9), "前置：主枪已耗 3 发");

            Assert.That(_arsenal.TrySelectSlot(1), Is.True);
            CompleteSwitch();
            Assert.That(_controller.Definition, Is.SameAs(_rifle));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30), "副枪首次装备=满弹（独立状态）");

            Assert.That(_arsenal.TrySelectSlot(0), Is.True);
            CompleteSwitch();
            Assert.That(_controller.Definition, Is.SameAs(_pistol));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(9),
                "切回主枪必须恢复切出时的 9 发——不得隐式补满（P0-2 玩法漏洞回归）");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48), "备弹同样持久");
        }

        [Test]
        public void SecondaryKeepsOwnFiredState_Independently()
        {
            FireRounds(3); // pistol 9/48
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            FireRounds(2); // rifle 28/120
            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(9));

            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(28),
                "副枪自己打掉的 2 发必须持久（两槽状态互不串扰）");
        }

        [Test]
        public void CancelReloadBySwitch_ThenBack_GrantsNoAmmo()
        {
            FireRounds(1); // 11/48
            Assert.That(_controller.TryReload(), Is.True, "前置：进入换弹");

            _arsenal.TrySelectSlot(1);
            CompleteSwitch(); // 切枪打断换弹（不赠送子弹）
            _arsenal.TrySelectSlot(0);
            CompleteSwitch();

            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(11),
                "换弹中切枪再切回：弹药保持打断时状态，不得因换弹赠送");
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(48), "备弹未被换弹消耗");
        }

        [Test]
        public void AttachmentMagSizeChange_ClampsCurrent_NoImplicitRefill()
        {
            FireRounds(2); // pistol 10/12
            var before = _controller.Runtime.CurrentAmmo;

            // 加长弹匣（MagazineSize Add +6 → 12→18）
            var extended = new AttachmentAssetEntry
            {
                itemId = "test.mag.extended",
                slot = AttachmentSlotType.Magazine,
                modifiers = { new AttachmentModifierEntry { stat = WeaponStatId.MagazineSize, op = ModifierOperation.Add, value = 6f } },
            };
            _controller.SetAttachments(new[] { extended });
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(18), "容量按配件重算");
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(before),
                "容量变化只钳制不补满（当前弹药原样保留）");

            // 卸下配件（容量回落 12）——当前弹药被钳制在 10 < 12 不变
            _controller.SetAttachments(System.Array.Empty<AttachmentAssetEntry>());
            Assert.That(_controller.Runtime.MagazineSize, Is.EqualTo(12));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(before), "回落钳制后弹药不变");
        }

        [Test]
        public void OnAmmoChanged_FiresWithRestoredValues_ServerSyncContract()
        {
            FireRounds(3); // 9/48
            (int current, int reserve)? lastEvent = null;
            _controller.OnAmmoChanged += (current, reserve) => lastEvent = (current, reserve);

            _arsenal.TrySelectSlot(1);
            CompleteSwitch(); // 副枪满弹事件
            Assert.That(lastEvent.HasValue && lastEvent.Value.current == 30 && lastEvent.Value.reserve == 120,
                Is.True, "切到副枪：服务器 SyncVar 数据源必须收到副枪满弹");

            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            Assert.That(lastEvent.HasValue && lastEvent.Value.current == 9 && lastEvent.Value.reserve == 48,
                Is.True, "切回主枪：SyncVar 数据源必须收到恢复后的 9/48（服务器权威不退化）");
        }

        [Test]
        public void AuthoritativeSnapshot_ReconcilesRuntimeBeforeReload_AndKeepsAmmoConserved()
        {
            // Owner 本地曾显示 0/30，而服务器实际只接受部分发次后为 15/30。快照必须先
            // 回写真正的 Runtime，随后 Reload 才在同一状态上搬运弹药。
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            _controller.ApplyAuthoritativeAmmoSnapshot("test.rifle", 0, 30);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.Zero);
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(30));

            _controller.ApplyAuthoritativeAmmoSnapshot("test.rifle", 15, 30);
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(15));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(30));
            Assert.That(_controller.TryReload(), Is.True);
            _controller.Runtime.CompleteReload();

            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(15));
            Assert.That(_controller.Runtime.CurrentAmmo + _controller.Runtime.ReserveAmmo, Is.EqualTo(45));
        }

        [Test]
        public void AuthoritativeSnapshot_ForInactiveWeapon_IsRestoredWhenSwitchingBack()
        {
            _controller.ApplyAuthoritativeAmmoSnapshot("test.rifle", 11, 37);
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();

            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(11));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(37),
                "权威副枪快照必须写入槽位缓存，不能在切枪时重新满弹");
        }

        [Test]
        public void AtomicSnapshot_ReloadCompletionMovesZeroThirtyToThirtyZeroWithoutClearingCooldown()
        {
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.rifle", LifeEpoch = 1, Sequence = 1,
                CurrentAmmo = 0, ReserveAmmo = 30, ReloadState = WeaponRuntimeState.Ready
            });
            Assert.That(_controller.TryReload(), Is.True);
            _controller.Runtime.StartCooldown(0.5f);
            float cooldown = _controller.Runtime.CooldownRemaining;

            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.rifle", LifeEpoch = 1, Sequence = 2,
                CurrentAmmo = 30, ReserveAmmo = 0, ReloadState = WeaponRuntimeState.Ready
            });

            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(30));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.Zero);
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(_controller.Runtime.CooldownRemaining, Is.EqualTo(cooldown), "ammo ACK must not reset cooldown");
        }

        [Test]
        public void AtomicSnapshot_OlderSequenceAndOtherWeaponCannotRefillCurrentRuntime()
        {
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", LifeEpoch = 2, Sequence = 5,
                CurrentAmmo = 4, ReserveAmmo = 9, ReloadState = WeaponRuntimeState.Ready
            });
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", LifeEpoch = 2, Sequence = 4,
                CurrentAmmo = 12, ReserveAmmo = 48, ReloadState = WeaponRuntimeState.Ready
            });
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.rifle", LifeEpoch = 2, Sequence = 6,
                CurrentAmmo = 30, ReserveAmmo = 120, ReloadState = WeaponRuntimeState.Ready
            });

            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(4));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(9), "old or cross-weapon ACK cannot refill held weapon");
        }

        [Test]
        public void AtomicSnapshot_ForInactiveWeapon_CachesServerAmmoMinusLaterPendingShots()
        {
            _controller.RegisterPredictedShotForAmmo(10, 3);
            _controller.RegisterPredictedShotForAmmo(11, 3);
            _controller.RegisterPredictedShotForAmmo(12, 3);
            _arsenal.TrySelectSlot(1);
            CompleteSwitch();

            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", LifeEpoch = 3, Sequence = 1,
                LastProcessedShotRequestId = 10, CurrentAmmo = 10, ReserveAmmo = 48,
                ReloadState = WeaponRuntimeState.Ready
            });

            _arsenal.TrySelectSlot(0);
            CompleteSwitch();
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(8),
                "late A ACK must retain A's N+1/N+2 debt while B is equipped");
        }

        [Test]
        public void BurstWithDelayedAcks_HudKeepsPendingDebt_StopsAtEmpty_ThenReloads()
        {
            var hudObject = new GameObject("AmmoHudRegression");
            hudObject.SetActive(false); // no runtime canvas/font construction in EditMode
            try
            {
                var hud = hudObject.AddComponent<Game.Presentation.HUD.WeaponHudView>();
                var textObject = new GameObject("AmmoText", typeof(RectTransform));
                textObject.transform.SetParent(hudObject.transform);
                var label = textObject.AddComponent<TMPro.TextMeshProUGUI>();
                SetField(hud, "controller", _controller);
                SetField(hud, "_ammoLine", label);
                // A cached online component must not suppress owner prediction events.
                SetField(hud, "_netWeaponState", _root.AddComponent<NetworkWeaponState>());
                Invoke(hud, "Subscribe");
                Invoke(_controller, "OnDisable");
                Invoke(_controller, "OnEnable");
                uint sequence = 0;
                for (uint shot = 1; shot <= 12; shot++)
                {
                    Assert.That(_controller.TryFire(), Is.True);
                    _controller.RegisterPredictedShotForAmmo(shot, 0);
                    Assert.That(label.text, Does.Contain($">{12 - shot:00}</size>"), "same-frame event");
                    if (shot > 2)
                    {
                        _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
                        {
                            WeaponId = "test.pistol", Sequence = ++sequence,
                            CurrentAmmo = 12 - (int)(shot - 2), ReserveAmmo = 48,
                            LastProcessedShotRequestId = shot - 2
                        });
                    }
                    Invoke(hud, "RefreshAmmo");
                    Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12 - shot));
                    Assert.That(label.text, Does.Contain($">{12 - shot:00}</size>"), "poll must preserve pending debt");
                    _controller.Runtime.Tick(.2f);
                }
                Assert.That(_controller.TryFire(), Is.False, "an empty magazine cannot keep predicting fire");
                _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
                {
                    WeaponId = "test.pistol", Sequence = ++sequence,
                    CurrentAmmo = 0, ReserveAmmo = 48, LastProcessedShotRequestId = 12
                });
                Assert.That(_controller.TryReload(), Is.True);
                _actions.Tick(1.1f);
                Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12));
                Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(36));
                Assert.That(_actions.IsBusy, Is.False);
                Assert.That(_controller.TryFire(), Is.True);
                Assert.That(_controller.TryReload(), Is.True, "next reload remains available");
                Invoke(hud, "Unsubscribe");
            }
            finally { Object.DestroyImmediate(hudObject); }
        }

        [Test]
        public void LateReloadSnapshot_RestoresTimer_AndReadyAckReleasesActionWithoutExtraRounds()
        {
            Invoke(_controller, "OnDisable");
            Invoke(_controller, "OnEnable");
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", Sequence = 1, CurrentAmmo = 0, ReserveAmmo = 24,
                ReloadState = WeaponRuntimeState.Reloading, ReloadRemaining = .3f
            });
            Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.Reload));
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", Sequence = 2, CurrentAmmo = 12, ReserveAmmo = 12
            });
            Assert.That(_actions.IsBusy, Is.False, "authority completion releases the action slot");
            _actions.Tick(1f);
            Assert.That(_controller.Runtime.CurrentAmmo + _controller.Runtime.ReserveAmmo, Is.EqualTo(24));
            Assert.That(_controller.TryFire(), Is.True);
            Assert.That(_controller.TryReload(), Is.True);
        }

        [Test]
        public void DelayedFireAck_ChangingMagazineDoesNotCancelPredictedReload()
        {
            Invoke(_controller, "OnDisable");
            Invoke(_controller, "OnEnable");
            FireRounds(4);
            Assert.That(_controller.TryReload(), Is.True);
            _actions.Tick(.2f);
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", Sequence = 1, CurrentAmmo = 10, ReserveAmmo = 48,
                ReloadState = WeaponRuntimeState.Ready
            });
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Reloading));
            Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.Reload));
            Assert.That(_actions.Elapsed, Is.EqualTo(.2f).Within(.001f));
            Assert.That(_controller.TryFire(), Is.False, "late shot ACK cannot unlock fire during reload");
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", Sequence = 2, CurrentAmmo = 12, ReserveAmmo = 46
            });
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(_actions.IsBusy, Is.False);
        }

        [Test]
        public void LateReloadSnapshot_WithoutAnotherAck_CompletesInsteadOfSticking()
        {
            Invoke(_controller, "OnDisable");
            Invoke(_controller, "OnEnable");
            _controller.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot
            {
                WeaponId = "test.pistol", Sequence = 1, CurrentAmmo = 0, ReserveAmmo = 24,
                ReloadState = WeaponRuntimeState.Reloading, ReloadRemaining = .3f
            });
            _actions.Tick(.31f);
            Assert.That(_controller.Runtime.State, Is.EqualTo(WeaponRuntimeState.Ready));
            Assert.That(_controller.Runtime.CurrentAmmo, Is.EqualTo(12));
            Assert.That(_controller.Runtime.ReserveAmmo, Is.EqualTo(12));
            Assert.That(_controller.TryFire(), Is.True);
        }

        private static void Invoke(object target, string method)
            => target.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance).Invoke(target, null);

        // ---------- 辅助（与 ArsenalTests 同款） ----------

        private static WeaponDefinition NewWeapon(string id, float draw, float holster)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("weaponId").stringValue = id;
            so.FindProperty("drawTime").floatValue = draw;
            so.FindProperty("holsterTime").floatValue = holster;
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static DemoBalanceConfig NewBalance(params (string id, int damage, int mag, int reserve, float reload)[] entries)
        {
            var balance = ScriptableObject.CreateInstance<DemoBalanceConfig>();
            var so = new SerializedObject(balance);
            var weapons = so.FindProperty("weapons");
            weapons.arraySize = entries.Length;
            for (int i = 0; i < entries.Length; i++)
            {
                var e = weapons.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("WeaponId").stringValue = entries[i].id;
                var stat = e.FindPropertyRelative("Stat");
                stat.FindPropertyRelative("Damage").intValue = entries[i].damage;
                stat.FindPropertyRelative("Rpm").intValue = 360;
                stat.FindPropertyRelative("MagSize").intValue = entries[i].mag;
                stat.FindPropertyRelative("ReserveAmmo").intValue = entries[i].reserve;
                stat.FindPropertyRelative("ReloadTime").floatValue = entries[i].reload;
                stat.FindPropertyRelative("Spread").floatValue = 0.25f;
                stat.FindPropertyRelative("MaxRange").floatValue = 120;
                stat.FindPropertyRelative("AdsFov").floatValue = 50f;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return balance;
        }

        private static void SetField(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new System.InvalidOperationException($"field not found: {target.GetType().Name}.{field}");
            f.SetValue(target, value);
        }
    }
}
