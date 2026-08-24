using UnityEngine;

namespace Game.Gameplay.Health
{
    /// <summary>
    /// 可受击目标（Day2）：接受 CombatResolver 的伤害并广播事件。LifeFSM 的前身，
    /// Day9 联网时扩展为完整 Health（Alive/Dying/Dead/Respawning）。
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

        public void ApplyDamage(int amount, Vector3 hitPoint, Vector3 hitDirection)
        {
            if (!IsAlive || amount <= 0) return;
            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            OnHealthChanged?.Invoke(CurrentHealth, maxHealth);
            OnDamaged?.Invoke(hitPoint, hitDirection);
            if (CurrentHealth == 0) OnDied?.Invoke();
        }

        public void ResetHealth()
        {
            CurrentHealth = maxHealth;
            OnHealthChanged?.Invoke(CurrentHealth, maxHealth);
        }
    }
}
