using System;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// 兼容旧 Unity 资产数据库中的脚本路径。原有“移动肘部避让瞄具”方案未命中
    /// ADS 根节点纵深问题，已经从 FPWeaponRig 运行链路移除；此类型不再执行任何写入。
    /// </summary>
    [Obsolete("Optical ADS arm clearance is handled by FPWeaponMotion depth ownership.")]
    [DisallowMultipleComponent]
    public sealed class FPOpticAdsArmClearance : MonoBehaviour
    {
    }
}
