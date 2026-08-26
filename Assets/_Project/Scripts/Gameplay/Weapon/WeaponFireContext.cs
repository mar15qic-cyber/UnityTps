using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// CP2 开火情境只读快照（Docs/13 §5.2）：Accuracy（Spread/ADS 倍率）、准心 HUD、相机反馈
    /// 消费同一份状态，杜绝 Gameplay→Presentation 反向依赖（WeaponController 不可读 FPCameraRig）。
    /// 字段为 0..1 归一化值或布尔，纯数值、可测试、网络可序列化。
    /// </summary>
    public readonly struct WeaponFireContext
    {
        public readonly float Ads01;              // 0=腰射 1=ADS（来自 PlayerAimState）
        public readonly float HorizontalSpeed01; // 0..1（相对冲刺基准速度）
        public readonly bool IsSprinting;
        public readonly bool IsGrounded;
        public readonly bool IsCrouching;        // Day4 未实现蹲姿，恒 false（接口预留）

        public WeaponFireContext(float ads01, float horizontalSpeed01, bool isSprinting, bool isGrounded, bool isCrouching)
        {
            Ads01 = ads01;
            HorizontalSpeed01 = horizontalSpeed01;
            IsSprinting = isSprinting;
            IsGrounded = isGrounded;
            IsCrouching = isCrouching;
        }

        public static readonly WeaponFireContext Default = new(0f, 0f, false, true, false);
    }
}
