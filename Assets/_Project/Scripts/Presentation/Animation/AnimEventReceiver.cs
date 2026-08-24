using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// 动画事件白名单接收者（架构 §7.3）：clip 内 Unity AnimationEvent 的唯一接收点，
    /// 翻译成 C# 事件仅供表现层消费；未知事件名仅告警，Gameplay 永不依赖动画事件。
    /// </summary>
    public sealed class AnimEventReceiver : MonoBehaviour
    {
        private static readonly HashSet<string> Whitelist = new(StringComparer.Ordinal)
        {
            "EVT_Fire_Muzzle",
            "EVT_Reload_MagOut",
            "EVT_Reload_MagIn",
            "EVT_Switch_Holstered",
            "EVT_Switch_Drawn",
            "ShowMagazine",
            "HideMagazine",
            "PlaySound",
            "PlaySoundAtPosition",
        };

        public event Action<string, float> OnWhitelistedEvent;

        // Unity AnimationEvent 入口（同名方法匹配）
        private void EVT_Fire_Muzzle() => Raise(nameof(EVT_Fire_Muzzle), 0f);
        private void EVT_Reload_MagOut() => Raise(nameof(EVT_Reload_MagOut), 0f);
        private void EVT_Reload_MagIn() => Raise(nameof(EVT_Reload_MagIn), 0f);
        private void EVT_Switch_Holstered() => Raise(nameof(EVT_Switch_Holstered), 0f);
        private void EVT_Switch_Drawn() => Raise(nameof(EVT_Switch_Drawn), 0f);

        private void ShowMagazine() => Raise(nameof(ShowMagazine), 0f);
        private void HideMagazine() => Raise(nameof(HideMagazine), 0f);
        private void PlaySound(AnimationEvent evt) => Raise(nameof(PlaySound), evt.floatParameter);
        private void PlaySoundAtPosition(AnimationEvent evt) => Raise(nameof(PlaySoundAtPosition), evt.floatParameter);

        private void Raise(string eventName, float parameter)
        {
            if (!Whitelist.Contains(eventName))
            {
                Debug.LogWarning($"[AnimEventReceiver] 未白名单动画事件被忽略: {eventName}", this);
                return;
            }
            OnWhitelistedEvent?.Invoke(eventName, parameter);
        }
    }
}
