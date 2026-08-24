using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP 武器挂点 mesh 切换：订阅 Arsenal 交换点事件，把当前武器的
    /// ThirdPersonViewPrefab 实例挂到角色 weapon 骨骼下（远端可见的武器外形）。
    /// </summary>
    public sealed class TPWeaponMeshSwapper : MonoBehaviour
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private Transform weaponBone;

        private GameObject _current;

        private void Awake()
        {
            if (arsenal == null) arsenal = GetComponentInParent<Arsenal>();
            if (weaponBone == null)
            {
                var anim = GetComponentInParent<Animator>();
                if (anim != null)
                {
                    var bone = anim.GetBoneTransform(HumanBodyBones.Chest);
                    if (bone != null) weaponBone = bone;
                }
            }
        }

        private void OnEnable()
        {
            if (arsenal == null) return;
            arsenal.OnActiveWeaponChanged += HandleWeaponChanged;
        }

        private void OnDisable()
        {
            if (arsenal == null) return;
            arsenal.OnActiveWeaponChanged -= HandleWeaponChanged;
        }

        private void Start()
        {
            var initial = arsenal != null ? arsenal.ActiveWeapon : null;
            if (initial != null) HandleWeaponChanged(initial);
        }

        private void HandleWeaponChanged(WeaponDefinition definition)
        {
            if (weaponBone == null || definition?.ThirdPersonViewPrefab == null) return;
            if (_current != null) Destroy(_current);
            _current = Instantiate(definition.ThirdPersonViewPrefab, weaponBone);
            _current.transform.localPosition = Vector3.zero;
            _current.transform.localRotation = Quaternion.identity;
            _current.name = definition.ThirdPersonViewPrefab.name;
        }
    }
}
