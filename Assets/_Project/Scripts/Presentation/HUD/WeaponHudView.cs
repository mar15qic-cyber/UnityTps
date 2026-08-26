using Game.Gameplay.Weapon;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// 武器 HUD 总控（Docs/13 检查点 5，§9-12 拍板：弹药与操作提示随准心一并迁出 OnGUI）：
    /// 订阅 WeaponController/Arsenal 事件 → 只写 uGUI Text（TMP 包未装，文本全英文用内置
    /// ugui 足够；Canvas 层级由编辑器脚本 CP5_HudBuilder 一次性构建）。
    /// </summary>
    public sealed class WeaponHudView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private Text ammoText;
        [SerializeField] private Text weaponText;
        [SerializeField] private Text hintText;

        private void Awake()
        {
            if (controller == null) controller = FindObjectOfType<WeaponController>();
            if (arsenal == null) arsenal = FindObjectOfType<Arsenal>();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnAmmoChanged += HandleAmmo;
            controller.OnWeaponEquipped += HandleWeapon;
            controller.OnReloadCompleted += HandleReloadEnd;
            controller.OnReloadInterrupted += HandleReloadInterrupted;
            if (arsenal != null) arsenal.OnActiveWeaponChanged += HandleWeapon;
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnAmmoChanged -= HandleAmmo;
            controller.OnWeaponEquipped -= HandleWeapon;
            controller.OnReloadCompleted -= HandleReloadEnd;
            controller.OnReloadInterrupted -= HandleReloadInterrupted;
            if (arsenal != null) arsenal.OnActiveWeaponChanged -= HandleWeapon;
        }

        private void Start() => Refresh();

        private void Update()
        {
            // 换弹进度（原 OnGUI 的 RELOAD % 行）
            if (controller != null && controller.Runtime != null
                && controller.Runtime.State == WeaponRuntimeState.Reloading)
                SetAmmo(controller.Runtime.CurrentAmmo, controller.Runtime.ReserveAmmo, true);
        }

        private void HandleAmmo(int current, int reserve) => SetAmmo(current, reserve, false);
        private void HandleReloadEnd() => Refresh();
        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _) => Refresh();
        private void HandleWeapon(WeaponDefinition _) => Refresh();

        private void Refresh()
        {
            if (controller == null || !controller.IsInitialized) return;
            if (weaponText != null && controller.Definition != null)
                weaponText.text = controller.Definition.DisplayName;
            SetAmmo(controller.Runtime.CurrentAmmo, controller.Runtime.ReserveAmmo,
                controller.Runtime.State == WeaponRuntimeState.Reloading);
            if (hintText != null)
                hintText.text = "LMB FIRE    RMB ADS    R RELOAD    WASD MOVE    SHIFT SPRINT    0-9 SWITCH";
        }

        private void SetAmmo(int current, int reserve, bool reloading)
        {
            if (ammoText == null) return;
            string pct = reloading
                ? $"  RELOAD {controller.Actions.NormalizedProgress * 100f:0}%"
                : "";
            ammoText.text = $"{current:00} / {reserve:000}{pct}";
        }
    }
}
