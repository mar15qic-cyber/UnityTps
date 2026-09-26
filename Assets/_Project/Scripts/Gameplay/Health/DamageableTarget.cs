using FishNet;
using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Health
{
    /// <summary>
    /// 可受击目标（Day2）：接受 CombatResolver 的伤害并广播事件。LifeFSM 的前身，
    /// Day9 联网时扩展为完整 Health（Alive/Dying/Dead/Respawning）。
    /// Docs/23 P0-6（G3）：联网时伤害结算服务器权威——纯客户端只表现不扣血，
    /// HP 真相读 NetworkCombatAuthority.Health（SyncVar）。
    /// </summary>
    public sealed class DamageableTarget : MonoBehaviour
    {
        [SerializeField, Min(1)] private int maxHealth = 100;

        public int CurrentHealth { get; private set; }
        public bool IsAlive => CurrentHealth > 0;

        public event System.Action<int, int> OnHealthChanged;
        public event System.Action<Vector3, Vector3> OnDamaged;
        public event System.Action OnDied;

        private void Awake() => CurrentHealth = maxHealth;

        /// <summary>是否结算本次伤害（纯函数便于 EditMode 测试——Docs/23 P0-6）：
        /// 网络未启动（离线）→ 本地结算照旧；在线且是服务器 → 结算；在线非服务器（纯客户端）→ 只表现不扣血。</summary>
        internal static bool ShouldApplyDamage(bool networkActive, bool isServer)
        {
            if (!networkActive) return true;
            return isServer;
        }

        /// <summary>网络是否已启动（判定写法参照 OfflinePlayerGate：InstanceFinder 每帧判定，无事件依赖）。</summary>
        private static bool IsNetworkActive()
        {
            var nm = InstanceFinder.NetworkManager;
            return nm != null && (nm.IsServerStarted || nm.IsClientStarted);
        }

        public void ApplyDamage(int amount, Vector3 hitPoint, Vector3 hitDirection, Game.Gameplay.Network.NetworkCombatAuthority damageSource = null)
            => ApplyDamageMeasured(amount, hitPoint, hitDirection, damageSource);

        /// <summary>Returns actual HP lost, including authority and spawn-protection rejection.</summary>
        public int ApplyDamageMeasured(int amount, Vector3 hitPoint, Vector3 hitDirection,
            Game.Gameplay.Network.NetworkCombatAuthority damageSource = null,
            HitBodyRegion bodyRegion = HitBodyRegion.Torso, string weaponId = null)
        {
            // Docs/23 G3 服务器权威门：纯客户端的本地预测射线只出视觉，不扣生命值
            bool networkActive = IsNetworkActive();
            if (!ShouldApplyDamage(networkActive, networkActive && InstanceFinder.NetworkManager.IsServerStarted)) return 0;
            if (!IsAlive || amount <= 0) return 0;
            // Phase 2 出生保护终闸兜底：按服务器"当前时刻"拒绝一切伤害——
            // 所有伤害入口（两段/单段/霰弹 pellet/离线）都收敛于本方法，闸在此保证不因新增入口绕过；
            // 射击时刻口径（LagComp 回溯语境）另在 CombatResolver.ResolveHitscanTwoStage 的代际闸执行。
            // 无 authority（靶子/authored 目标）或保护窗为 0（离线常态）恒放行。
            var authority = GetComponentInParent<Game.Gameplay.Network.NetworkCombatAuthority>();
            if (authority != null && authority.IsInvincibleNow) return 0;
            int applied = Mathf.Min(CurrentHealth, amount);
            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            // F01（2026-09-19 审计）：击杀归因在"伤害实际被结算"时登记，且先于 OnDied 回调——
            // 原实现在 OnShotFired（结算完成后）补登记：首发致死时注册表为空 → 击杀无归属，
            // 死亡处理清表后又被本次开火事件把已死目标登记回去（污染下一生命归因）。
            // 被拒绝的伤害（保护期/友军/旧生命/死体/零伤害）不会走到这里，不会覆盖上一有效伤害者。
            // damageSource 仅服务器权威结算传入（离线/客户端预测为 null，不进注册表）；霰弹逐
            // pellet 传入 → 多目标各归其位。登记资格闸（服务器已初始化）在 RegisterHit 内。
            if (damageSource != null)
                Game.Gameplay.Network.MatchLifecycle.RegisterHit(damageSource, this, bodyRegion, weaponId);
#if UNITY_SERVER && !UNITY_EDITOR
            // Day4 Gate B 诊断：服务器权威受击留痕（谁掉血/掉到多少）
            Debug.Log($"[CombatDamage] target={gameObject.transform.root.name} dmg={amount} hpNow={CurrentHealth} at={hitPoint.ToString("F1")}");
#endif
            OnHealthChanged?.Invoke(CurrentHealth, maxHealth);
            OnDamaged?.Invoke(hitPoint, hitDirection);
            if (CurrentHealth == 0) OnDied?.Invoke();
            return applied;
        }

        public void ResetHealth()
        {
            CurrentHealth = maxHealth;
            OnHealthChanged?.Invoke(CurrentHealth, maxHealth);
        }
    }
}
