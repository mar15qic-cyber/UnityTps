using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP 武器挂点 mesh 切换：订阅 Arsenal 交换点事件，把当前武器的
    /// ThirdPersonViewPrefab 实例挂到右手骨骼下（远端可见的武器外形）。
    /// 挂点局部偏移烘焙在各武器预制体根 Transform 上（LPFP 原厂约定：
    /// 武器挂 hand_R + 每武器一个握把对齐偏移，本组件不覆盖该变换）。
    /// </summary>
    public sealed class TPWeaponMeshSwapper : MonoBehaviour
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private Transform weaponBone;

        private GameObject _current;

        /// <summary>当前 TP 武器实例（TPWeaponFX / TPLeftHandIK 只读消费）。</summary>
        public GameObject CurrentInstance => _current;
        /// <summary>当前武器枪口挂点（prefab 内 "Muzzle" 子节点）。</summary>
        public Transform CurrentMuzzle { get; private set; }
        /// <summary>当前武器左手持枪挂点（prefab 内 "LeftHandTarget" 子节点）。</summary>
        public Transform CurrentLeftHandTarget { get; private set; }

        private void Awake()
        {
            if (arsenal == null) arsenal = GetComponentInParent<Arsenal>();
            if (weaponBone == null)
            {
                var anim = GetComponentInParent<Animator>();
                if (anim != null)
                {
                    var bone = anim.GetBoneTransform(HumanBodyBones.RightHand);
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
            // 握把对齐偏移保留在武器预制体根 Transform 上（Instantiate 挂到父骨骼时原样生效）
            _current = Instantiate(definition.ThirdPersonViewPrefab, weaponBone);
            // 武器与 TP 身体同层：本地主相机剔除 LocalPlayerBody（不渲染自己的 TP 武器），
            // 远端代理渲染身体时武器随之可见。Instantiate 保留 prefab 层（Default），子节点不继承父骨骼层，须显式同步。
            int bodyLayer = weaponBone.gameObject.layer;
            foreach (var t in _current.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = bodyLayer;
            _current.name = definition.ThirdPersonViewPrefab.name;

            // 枪口/左手挂点（Day4.2：TPWeaponFX 与 TPLeftHandIK 消费）
            CurrentMuzzle = _current.transform.Find("Muzzle");
            CurrentLeftHandTarget = _current.transform.Find("LeftHandTarget");
        }
    }
}
