using Game.Gameplay.Action;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// FP 武器视图唯一管理者：按 Arsenal 事件切换 WeaponDefinition.FirstPersonViewPrefab
    /// 实例（收旧枪 → 交换点换实例并播出枪 → 完成收尾）。视图实例自带
    /// FPWeaponAnimator/WeaponView，激活即接管表现。
    /// </summary>
    public sealed class FPWeaponRig : MonoBehaviour
    {
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform viewRoot;

        private readonly System.Collections.Generic.Dictionary<WeaponDefinition, GameObject> _views = new();
        private GameObject _activeView;
        private WeaponDefinition _activeDefinition;

        private void Awake()
        {
            if (arsenal == null) arsenal = GetComponentInParent<Arsenal>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (viewRoot == null) viewRoot = transform;
        }

        private void OnEnable()
        {
            if (arsenal == null) return;
            arsenal.OnSwitchStarted += HandleSwitchStarted;
            arsenal.OnActiveWeaponChanged += HandleActiveWeaponChanged;
        }

        private void OnDisable()
        {
            if (arsenal == null) return;
            arsenal.OnSwitchStarted -= HandleSwitchStarted;
            arsenal.OnActiveWeaponChanged -= HandleActiveWeaponChanged;
        }

        private void Start()
        {
            // 初始武器视图（Arsenal.Start 已广播过，这里直接取当前武器兜底）
            var initial = arsenal != null ? arsenal.ActiveWeapon : controller != null ? controller.Definition : null;
            if (initial != null) ShowView(initial, playDraw: true);
        }

        private void HandleSwitchStarted(WeaponDefinition oldWeapon, int _)
        {
            // 收旧枪：由旧视图的 FPWeaponAnimator 播 Holster
            if (_activeView != null && _activeView.TryGetComponent(out FPWeaponAnimator animator))
                animator.PlayHolster();
        }

        private void HandleActiveWeaponChanged(WeaponDefinition newWeapon)
        {
            if (newWeapon == null) return;
            ShowView(newWeapon, playDraw: true);
        }

        private void ShowView(WeaponDefinition definition, bool playDraw)
        {
            if (definition == null || definition == _activeDefinition) return;
            var next = GetOrCreateView(definition);
            if (next == null) return;

            if (_activeView != null && _activeView != next)
                _activeView.SetActive(false);

            _activeView = next;
            _activeDefinition = definition;
            _activeView.SetActive(true);

            if (playDraw && _activeView.TryGetComponent(out FPWeaponAnimator animator))
                animator.PlayDraw();
        }

        private GameObject GetOrCreateView(WeaponDefinition definition)
        {
            if (_views.TryGetValue(definition, out var view) && view != null)
                return view;

            var prefab = definition.FirstPersonViewPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"[FPWeaponRig] WeaponDefinition '{definition.name}' 未配置 FirstPersonViewPrefab。", this);
                return null;
            }

            view = Instantiate(prefab, viewRoot);
            view.transform.localPosition = Vector3.zero;
            view.transform.localRotation = Quaternion.identity;
            view.name = prefab.name;
            _views[definition] = view;
            view.SetActive(false);
            return view;
        }
    }
}
