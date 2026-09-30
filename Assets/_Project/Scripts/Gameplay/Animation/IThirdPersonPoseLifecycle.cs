namespace Game.Gameplay.Animation
{
    /// <summary>
    /// 第三人称姿态写者的死亡/复活生命周期契约（2026-09-18 审计 §4.2）。
    ///
    /// 定义在 Gameplay、由 Presentation 的 TP 驱动器实现：Gameplay 不能引用 Presentation
    /// （三层单向依赖），但可以让低层定义接口、高层实现——避免 FPWeaponRig 那套反射桥。
    ///
    /// 存在的原因：AnimancerComponent 的 _ActionOnDisable=Reset 在停用时执行 Graph.Stop +
    /// Animator.Rebind + PauseGraph，而 OnEnable 只 UnpauseGraph——图已 Stop，没有任何状态在播；
    /// 同时 TPAnimDriver 用 _currentState 缓存做"状态未变不重播"，于是 Idle→死亡→Idle 之后
    /// TP 模型停在 Rebind 出来的骨架姿态上，站桩也不会恢复。因此复活需要一条显式的
    /// "解冻 + 强制重播 + 求值一帧"入口，而不是只把 enabled 设回 true。
    /// </summary>
    public interface IThirdPersonPoseLifecycle
    {
        /// <summary>死亡：冻结骨骼写者。实现方不得依赖 GameObject/组件停用副作用（会触发 Rebind）。</summary>
        void FreezePoseForDeath();
        void PlayDeathPose(float elapsedSeconds);
        bool DeathPoseComplete { get; }

        /// <summary>复活：解冻并强制重播当前有效 locomotion，就地求值一帧（幂等，禁止每帧调用）。</summary>
        void RecoverPoseAfterRespawn();
    }
}
