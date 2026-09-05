using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/23 离线回归修复（Codex 复盘 2026-09-05）锁定：
    /// Arena authored player 经 GameplayLoadoutBootstrap.SetIsNetworked(false) 后，
    /// 其上 NetworkBehaviour 的所有权缓存未建立——直接读 FishNet IsOwner 每帧 NRE
    /// （实机影响 TPAimDriver / WeaponHudView / NetworkLocomotionState）。
    /// 本套件在 EditMode（无 FishNet 运行时 = 离线语境）验证：所有曾直接读 IsOwner 的
    /// 组件入口在"组件存在但无有效 NetworkObject 缓存"时返回离线本地语义且不抛异常。
    /// 说明：在线分支（IsSpawned/IsOwner 真实路径）无法在 EditMode 模拟，属 PlayMode/实机覆盖。
    /// </summary>
    public sealed class FishNetOfflineSafetyTests
    {
        private GameObject _player;

        [SetUp]
        public void SetUp()
        {
            _player = new GameObject("OfflineAuthoredPlayer");
        }

        [TearDown]
        public void TearDown()
        {
            if (_player != null) Object.DestroyImmediate(_player);
        }

        [Test]
        public void CombatAuthority_IsOwnerPlayer_DoesNotThrow_AndReturnsOfflineLocal()
        {
            var authority = _player.AddComponent<NetworkCombatAuthority>();
            bool isLocal = false;
            Assert.DoesNotThrow(() => isLocal = authority.IsOwnerPlayer,
                "authored player 无所有权缓存时读 IsOwnerPlayer 不得 NRE（实机回归 TPAimDriver 每帧崩溃根因）");
            Assert.That(isLocal, Is.True, "离线 authored player 必须被视为本地玩家");
        }

        [Test]
        public void WeaponState_IsOwnerPlayerSafe_DoesNotThrow_AndReturnsOfflineLocal()
        {
            var state = _player.AddComponent<NetworkWeaponState>();
            bool isLocal = false;
            Assert.DoesNotThrow(() => isLocal = state.IsOwnerPlayerSafe,
                "实机回归：WeaponHudView.TryGetAuthoritativeAmmo 离线 NRE 根因");
            Assert.That(isLocal, Is.True, "离线 HUD 不得把 authored player 误判为远端");
        }

        [Test]
        public void WeaponState_AmmoAccessors_DoesNotThrow()
        {
            var state = _player.AddComponent<NetworkWeaponState>();
            int current = -1, reserve = -1;
            Assert.DoesNotThrow(() => { current = state.CurrentAmmo; reserve = state.ReserveAmmo; });
            Assert.That(current, Is.EqualTo(0));
            Assert.That(reserve, Is.EqualTo(0));
        }

        [Test]
        public void LocomotionState_RemoteProxyAccessors_DoesNotThrow_AndFallbackToLocal()
        {
            var locomotion = _player.AddComponent<NetworkLocomotionState>();
            LocomotionState state = default;
            Vector2 moveInput = default;
            float speed = -1f;
            Assert.DoesNotThrow(() =>
            {
                state = locomotion.State;
                moveInput = locomotion.MoveInput;
                speed = locomotion.HorizontalSpeed;
            }, "实机回归：NetworkLocomotionState.UseRemoteState 同生命周期 IsOwner NRE");
            // 无 Locomotor 组件的离线对象：走本地 fallback（Idle / 零输入 / 零速度）
            Assert.That(state, Is.EqualTo(LocomotionState.Idle));
            Assert.That(moveInput, Is.EqualTo(Vector2.zero));
            Assert.That(speed, Is.EqualTo(0f));
        }

        [Test]
        public void CombatAuthority_SubmitRequests_DoesNotThrow_Offline()
        {
            var authority = _player.AddComponent<NetworkCombatAuthority>();
            Assert.DoesNotThrow(() =>
            {
                authority.SubmitFireRequest();
                authority.SubmitReloadRequest();
                authority.SubmitSwitchRequest(0);
            }, "离线 authored player 的输入转发路径不得 NRE，也不得发送任何 RPC");
        }

        [Test]
        public void HudAmmoSourceGate_Offline_FallsBackToLocal()
        {
            // WeaponHudView 数据源纯函数：网络未启动 → 无论引用是否残留都必须回本地路径
            Assert.That(Game.Presentation.HUD.WeaponHudView.ShouldReadAuthoritativeAmmo(
                networkActive: false, hasOwnerNetworkState: true), Is.False);
            Assert.That(Game.Presentation.HUD.WeaponHudView.ShouldReadAuthoritativeAmmo(
                networkActive: false, hasOwnerNetworkState: false), Is.False);
            Assert.That(Game.Presentation.HUD.WeaponHudView.ShouldReadAuthoritativeAmmo(
                networkActive: true, hasOwnerNetworkState: true), Is.True);
            Assert.That(Game.Presentation.HUD.WeaponHudView.ShouldReadAuthoritativeAmmo(
                networkActive: true, hasOwnerNetworkState: false), Is.False);
        }
    }
}
