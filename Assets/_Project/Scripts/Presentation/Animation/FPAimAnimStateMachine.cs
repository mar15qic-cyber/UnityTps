using System;

namespace Game.Presentation.Animation
{
    /// <summary>动画 ADS 状态机输出的播放指令（FPWeaponAnimator 映射到 Animancer）。</summary>
    public enum FPAimAnimCommand
    {
        None,        // 无操作
        PlayAimIn,   // 开镜过渡（一次性，播速适配过渡窗）
        PlayAimOut,  // 收镜过渡（一次性，从当前姿态淡出）
        PlayAimIdle, // ADS 保持姿势（aim_fire_pose 静态定格）
        PlayAimFire, // ADS 开火（一次性）
        PlayHipFire, // 腰射开火（走既有 Fire 通道；Hip/AimOut 态的开火分流）
        Yield,       // 让位：换弹/切枪接管姿态，aim 轨道静默（回到 Hip 等动作结束）
    }

    /// <summary>状态机每帧输入（"本帧事实"，由 FPWeaponAnimator 采样回填）。</summary>
    public readonly struct FPAimAnimInput
    {
        public readonly bool AimHeld;          // 右键按住（输入意图）
        public readonly float Ads01;           // PlayerAimState 混合值
        public readonly bool ActionBusy;       // 换弹/切枪占用动作槽
        public readonly bool ShotFired;        // WeaponController.OnShotFired 本帧标志
        public readonly bool DryFired;         // OnDryFire 本帧标志（aim 态无素材，只用于断言不产生指令）
        public readonly bool AimInFinished;    // 开镜过渡完成（ads01 已到高位）
        public readonly bool AimOutFinished;   // 收镜过渡完成（收镜 clip 适配窗播完）
        public readonly bool AimFireFinished;  // ADS 开火 clip 播完
        public readonly bool HolsterRequested; // 切枪收枪请求帧标志

        public FPAimAnimInput(
            bool aimHeld, float ads01, bool actionBusy, bool shotFired, bool dryFired,
            bool aimInFinished, bool aimOutFinished, bool aimFireFinished, bool holsterRequested)
        {
            AimHeld = aimHeld;
            Ads01 = ads01;
            ActionBusy = actionBusy;
            ShotFired = shotFired;
            DryFired = dryFired;
            AimInFinished = aimInFinished;
            AimOutFinished = aimOutFinished;
            AimFireFinished = aimFireFinished;
            HolsterRequested = holsterRequested;
        }

        /// <summary>全零输入（腰射静止帧）。</summary>
        public static FPAimAnimInput Idle => new(false, 0f, false, false, false, false, false, false, false);
    }

    /// <summary>
    /// 动画 ADS 状态机（Docs/18 §4.2）：纯 C# 决策核心，与 Unity 对象解耦。
    /// 决策全集 = aim 轨道 clip 选择 + 开火分流（腰射/ADS）；执行侧（Animancer）不在本类。
    /// 转移语义由 FPAimAnimStateMachineTests 锁死（T1~T10）。
    /// ADS 权威仍是 PlayerAimState.Ads01（Docs/13 §5.3-6），本机只读。
    /// </summary>
    public sealed class FPAimAnimStateMachine
    {
        private enum State { Hip, AimIn, Aim, AimFire, AimOut }

        private State _state = State.Hip;
        private bool _hasAimClips;

        /// <summary>当前是否处于 aim 轨道（AimIn/Aim/AimFire/AimOut 任一）。</summary>
        public bool IsOnAimTrack => _state != State.Hip;

        /// <summary>配置当前武器是否具备动画 ADS 轨道（切枪/LoadClips 时刷新）。</summary>
        public void SetHasAimClips(bool hasAimClips) => _hasAimClips = hasAimClips;

        /// <summary>外部重置到 Hip（切枪收枪/换弹开始时由执行侧直接调用，
        /// 消除"事件在 Update 之后到达"的同帧指令竞争——否则残留的 aim 状态
        /// 可能在下一帧发出覆盖收枪/换弹 clip 的指令）。</summary>
        public void ResetToHip() => _state = State.Hip;

        /// <summary>每帧决策：唯一指令输出。</summary>
        public FPAimAnimCommand Tick(in FPAimAnimInput input)
        {
            if (!_hasAimClips)
            {
                // 无动画轨道：开火分流固定走腰射通道，ADS 由程序化轨道接管（T8）；
                // 状态归零防切枪后旧状态残留
                _state = State.Hip;
                return input.ShotFired ? FPAimAnimCommand.PlayHipFire : FPAimAnimCommand.None;
            }

            // 动作槽占用 / 收枪：无条件让位（T5/T6）。Reload/Switch 由 OnReloadStarted/
            // PlayHolster 直接接管 Animancer 主轨道；这里把 aim 状态归位 Hip，
            // 让动作结束后的重新开镜从干净状态进入（T10）。
            if (input.ActionBusy || input.HolsterRequested)
            {
                _state = State.Hip;
                return FPAimAnimCommand.Yield;
            }

            switch (_state)
            {
                case State.Hip:
                    if (input.ShotFired)
                        return FPAimAnimCommand.PlayHipFire; // 同帧先开后瞄：腰射开火优先，下一帧再入镜
                    if (input.AimHeld && input.Ads01 > 0f)
                    {
                        _state = State.AimIn;
                        return FPAimAnimCommand.PlayAimIn;
                    }
                    return FPAimAnimCommand.None;

                case State.AimIn:
                    if (!input.AimHeld)
                    {
                        // 快速点按：aim_in 未完就松开 → 直接收镜（T7）；
                        // aim_out 起始姿态 ≈ aim_in 末帧（曲线探针证实），淡入衔接不跳变
                        _state = State.AimOut;
                        return FPAimAnimCommand.PlayAimOut;
                    }
                    if (input.ShotFired)
                    {
                        // 过渡中开火：一律走 ADS 开火（过渡窗仅 ~80ms，腰射 Fire 会被
                        // 紧随的 PlayAimIdle 截断产生姿势跳变；探针证实 aim_fire 起始≈贴腮态）
                        _state = State.AimFire;
                        return FPAimAnimCommand.PlayAimFire;
                    }
                    if (input.AimInFinished)
                    {
                        _state = State.Aim;
                        return FPAimAnimCommand.PlayAimIdle;
                    }
                    return FPAimAnimCommand.None;

                case State.Aim:
                    if (!input.AimHeld)
                    {
                        _state = State.AimOut;
                        return FPAimAnimCommand.PlayAimOut;
                    }
                    if (input.ShotFired)
                    {
                        _state = State.AimFire;
                        return FPAimAnimCommand.PlayAimFire;
                    }
                    // ADS 空仓（DryFired）：无 aim_dry 素材，保持贴腮姿势不动（T9）
                    return FPAimAnimCommand.None;

                case State.AimFire:
                    if (!input.AimHeld)
                    {
                        _state = State.AimOut;
                        return FPAimAnimCommand.PlayAimOut;
                    }
                    if (input.ShotFired)
                        return FPAimAnimCommand.PlayAimFire; // 连射：重启（FromStart），状态不回
                    if (input.AimFireFinished)
                    {
                        _state = State.Aim;
                        return FPAimAnimCommand.PlayAimIdle;
                    }
                    return FPAimAnimCommand.None;

                case State.AimOut:
                    if (input.ShotFired)
                    {
                        // 收镜中开火：视同腰射（ads01 大概率已低于半开镜线）
                        _state = State.Hip;
                        return FPAimAnimCommand.PlayHipFire;
                    }
                    if (input.AimOutFinished)
                    {
                        if (input.AimHeld)
                        {
                            // 收镜途中又按住：从当前姿态重新入镜（点按抖动场景）
                            _state = State.AimIn;
                            return FPAimAnimCommand.PlayAimIn;
                        }
                        _state = State.Hip;
                        return FPAimAnimCommand.None;
                    }
                    return FPAimAnimCommand.None;

                default:
                    return FPAimAnimCommand.None;
            }
        }
    }
}
