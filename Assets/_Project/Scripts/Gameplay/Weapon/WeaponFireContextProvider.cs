using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 场景聚合器：PlayerAimState + Locomotor → WeaponFireContext。挂在玩家根，只读、无副作用。
    /// CP2 仅建立状态源；WeaponController 在 CP4 接线消费（Docs/13 检查点 2 范围）。
    /// </summary>
    [DefaultExecutionOrder(-75)] // 晚于 PlayerAimState(-80)，早于 WeaponController(-50)
    public sealed class WeaponFireContextProvider : MonoBehaviour
    {
        [SerializeField] private PlayerAimState aimState;
        [SerializeField] private Locomotor locomotor;
        [SerializeField, Min(0.01f)] private float sprintReferenceSpeed = 3.44f;

        public WeaponFireContext Context => Build();

        private void Awake()
        {
            // 惰性解析兜底（Docs/23 表现迭代 2026-09-05）：序列化引用缺失（联网生成实例/
            // prefab 变更）时自动在玩家层级找齐——准心/散布情境链路不允许因引用悬空退化 Default
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            if (locomotor == null) locomotor = GetComponentInParent<Locomotor>();
        }

        private WeaponFireContext Build()
        {
            float ads = aimState != null ? aimState.Ads01 : 0f;
            float speed = locomotor != null ? locomotor.HorizontalSpeed : 0f;
            float speed01 = Mathf.Clamp01(speed / sprintReferenceSpeed);
            bool sprint = locomotor != null && locomotor.State == LocomotionState.Sprint;
            bool grounded = locomotor == null
                || (locomotor.State != LocomotionState.Jump && locomotor.State != LocomotionState.Air);
            return new WeaponFireContext(ads, speed01, sprint, grounded, false);
        }
    }
}
