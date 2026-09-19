using System.Collections.Generic;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>第一人称武器视图隐藏原因（审计 2026-09-16 §3.2）：各系统只持自己的原因位，
    /// 最终可见性由全部原因合成——不再共享一个 bool/一张快照（旧实现退出 ADS 会释放死亡隐藏、
    /// 复活会释放镜内隐藏）。</summary>
    public enum FPViewHideReason
    {
        /// <summary>死亡（Owner 死亡边界起，复活边界释放）。</summary>
        Death = 0,
        /// <summary>实体镜/镜内遮罩接管（OpticAdsView；ADS 退出即释放）。</summary>
        ScopeOverlay = 1,
        /// <summary>复活准备（复活重建 Idle 的那一帧内保持隐藏，评估完成后释放）。</summary>
        RespawnPreparing = 2,
    }

    /// <summary>
    /// 第一人称武器视图可见性**唯一写者**（审计 2026-09-16 §3）。
    /// 为什么必须存在：旧链上 FPCameraRig（死亡）与 FPWeaponRig（死亡/镜内）各自保存并恢复同一批
    /// `Renderer.enabled`——相机先关（保存 true）、Rig 后关（把 false 存成"原始状态"）、复活相机恢复
    /// true 后下一帧 Rig 又按自己那份快照写回 false → 枪永久隐形。双写互相覆盖是确定性缺陷，
    /// 不是加载问题。
    /// 规则：
    /// ① 最终可见 = 所有原因位清空；每个系统只能 SetHidden/释放**自己的**原因；
    /// ② Renderer 基线在**受控注册**（视图创建/配件挂载）时确定，且首次注册生效——其它系统造成的
    ///    临时隐藏不得被读成作者原始状态；也绝不一律 true（作者本来就关闭的网格/附件不得被打开）；
    /// ③ 新注册的 Renderer 立即应用当前原因合成（隐藏期间挂载的新配件不闪帧）。
    /// </summary>
    public sealed class FPViewModelVisibility
    {
        private static readonly int ReasonCount = System.Enum.GetValues(typeof(FPViewHideReason)).Length;
        private readonly bool[] _reasons = new bool[ReasonCount];
        private readonly Dictionary<Renderer, bool> _baseline = new();
        private readonly List<Renderer> _renderers = new();
        private int _changeCount;

        /// <summary>当前是否处于隐藏（任一原因位生效）。</summary>
        public bool IsHidden { get; private set; }
        /// <summary>已注册的 Renderer 数（诊断用）。</summary>
        public int RegisteredCount => _renderers.Count;
        /// <summary>原因变化计数（诊断/测试用）。</summary>
        public int ChangeCount => _changeCount;

        public bool HasReason(FPViewHideReason reason) => _reasons[(int)reason];

        /// <summary>设置一个原因位并立即应用（死亡边界必须当帧关掉，不给持续写者留帧）。</summary>
        public void SetHidden(FPViewHideReason reason, bool hidden)
        {
            int index = (int)reason;
            if (_reasons[index] == hidden) return; // 幂等：重复死亡/复活广播不重放基线
            _reasons[index] = hidden;
            bool any = false;
            for (int i = 0; i < _reasons.Length; i++) any |= _reasons[i];
            IsHidden = any;
            _changeCount++;
            Apply();
        }

        /// <summary>清空全部原因（组件禁用/销毁边界），并按基线还原。</summary>
        public void ResetAll()
        {
            bool changed = false;
            for (int i = 0; i < _reasons.Length; i++) { changed |= _reasons[i]; _reasons[i] = false; }
            IsHidden = false;
            if (changed) _changeCount++;
            Apply();
        }

        /// <summary>
        /// 受控注册：首次注册捕获作者基线，之后永不覆盖基线（池化视图/动态配件反复注册安全）。
        /// 隐藏期间注册的新 Renderer 立即跟随当前合成结果，不闪帧。
        /// </summary>
        public void Register(Renderer renderer)
        {
            if (renderer == null) return;
            if (_baseline.ContainsKey(renderer))
            {
                // 已注册：只需保证当前状态正确（例如注册发生在死亡之后）
                if (IsHidden) renderer.enabled = false;
                return;
            }
            _baseline[renderer] = renderer.enabled;
            _renderers.Add(renderer);
            if (IsHidden) renderer.enabled = false;
        }

        /// <summary>按合成结果应用全部已注册 Renderer（顺带清理已销毁引用）。</summary>
        public void Apply()
        {
            for (int i = _renderers.Count - 1; i >= 0; i--)
            {
                var renderer = _renderers[i];
                if (renderer == null)
                {
                    _renderers.RemoveAt(i);
                    continue;
                }
                // 基线优先：作者本就关闭的网格在可见时也保持关闭
                renderer.enabled = IsHidden ? false : _baseline[renderer];
            }
        }
    }
}
