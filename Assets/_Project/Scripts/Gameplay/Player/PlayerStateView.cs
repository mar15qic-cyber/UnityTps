using Game.Gameplay.Action;
using Game.Gameplay.Movement;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Player
{
    /// <summary>Presentation 和未来 NetworkAdapter 读取的聚合只读状态；不拥有任何 Gameplay 真相。</summary>
    [DefaultExecutionOrder(-150)]
    public sealed class PlayerStateView : MonoBehaviour
    {
        [SerializeField] private Locomotor locomotor;
        [SerializeField] private ActionSystem actionSystem;
        [SerializeField] private WeaponController weaponController;

        public LocomotionState LocomotionState => locomotor != null ? locomotor.State : LocomotionState.Idle;
        public Vector2 MoveInput => locomotor != null ? locomotor.MoveInput : Vector2.zero;
        public float HorizontalSpeed => locomotor != null ? locomotor.HorizontalSpeed : 0f;
        public float GaitPhase => locomotor != null ? locomotor.GaitPhase : 0f;
        public PlayerActionType CurrentAction => actionSystem != null ? actionSystem.CurrentAction : PlayerActionType.None;
        public WeaponDefinition Weapon => weaponController != null ? weaponController.Definition : null;

        private void Awake()
        {
            if (locomotor == null) locomotor = GetComponentInParent<Locomotor>();
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            if (weaponController == null) weaponController = GetComponentInParent<WeaponController>();
        }
    }
}
