using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// 根运动中继（架构§13问题3修订版）：OnAnimatorMove 只在与 Animator 同 GameObject 的脚本上生效；
    /// 本组件消费 TP clip 的根运动增量并转发给 Locomotor（唯一位移写者）——不调用 ApplyBuiltinRootMotion，
    /// TP_Model 自身因此不会被动画拖走，位移唯一落点仍是 Locomotor→CC.Move。
    /// Day7 联网时：远端实例禁用本组件（位置来自 NetworkTransform）。
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class RootMotionRelay : MonoBehaviour
    {
        [SerializeField] private Game.Gameplay.Movement.Locomotor locomotor;

        private Animator _animator;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _animator.applyRootMotion = true; // 增量由本组件接管，不会直写 Transform
            if (locomotor == null) locomotor = GetComponentInParent<Game.Gameplay.Movement.Locomotor>();
        }

        private void OnAnimatorMove()
        {
            if (locomotor == null) return;
            locomotor.OnAnimatorRootMotion(_animator.deltaPosition, _animator.deltaRotation);
        }
    }
}
