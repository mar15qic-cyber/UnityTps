using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Settings;
using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>Owner 投掷库存的轻量 HUD；库存来源始终为服务器 SyncVar。</summary>
    public sealed class ThrowableHudView : MonoBehaviour
    {
        private ThrowableController _owner;
        private float _scanAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            if (Application.isBatchMode || FindFirstObjectByType<ThrowableHudView>() != null) return;
            var host = new GameObject("ThrowableHud");
            DontDestroyOnLoad(host);
            host.AddComponent<ThrowableHudView>();
        }

        private void Update()
        {
            if (_owner != null && _owner.isActiveAndEnabled
                && FishNetLifecycleGuard.IsLocalOwner(_owner)) return;
            if (Time.unscaledTime < _scanAt) return;
            _scanAt = Time.unscaledTime + 0.5f;
            _owner = null;
            foreach (var candidate in FindObjectsByType<ThrowableController>(FindObjectsSortMode.None))
                if (FishNetLifecycleGuard.IsLocalOwner(candidate)) { _owner = candidate; break; }
        }

        private TMPro.TMP_Text _label;
        private void LateUpdate()
        {
            if (_owner == null || !_owner.isActiveAndEnabled) { if (_label != null) _label.enabled = false; return; }
            if (_label == null)
            {
                var hud = FindFirstObjectByType<WeaponHudView>();
                var canvas = hud != null ? hud.GetComponentInParent<Canvas>() : null;
                if (canvas == null) return;
                var go = new GameObject("ThrowableInventory", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
                go.transform.SetParent(canvas.transform, false);
                _label = go.GetComponent<TMPro.TMP_Text>();
                _label.font = Resources.Load<TMPro.TMP_FontAsset>("Fonts/NotoSansSC-Regular SDF") ?? TMPro.TMP_Settings.defaultFontAsset;
                _label.fontSize = 17; _label.alignment = TMPro.TextAlignmentOptions.MidlineRight; _label.raycastTarget = false;
                var rt = _label.rectTransform;
                rt.anchorMin = new Vector2(0.60f, 0.16f); rt.anchorMax = new Vector2(0.975f, 0.20f);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
            }
            _label.enabled = true;
            string[] names = { "FRAG", "FLASH", "SMOKE" };
            var parts = new string[3];
            for (int i = 0; i < 3; i++)
            {
                var type = (ThrowableType)i;
                string color = _owner.IsEquipped && _owner.SelectedType == type ? "#55E7B8" : _owner.Count(type) > 0 ? "#E8EFEE" : "#637575";
                parts[i] = $"<color={color}>{names[i]} {_owner.Count(type)}</color>";
            }
            _label.text = $"[{SettingsKeyMap.DisplayName(SettingsKeyMap.Get(SettingsKeyMap.Action.SelectThrowable))}]  " + string.Join("   ", parts)
                + (_owner.IsEquipped ? "   LMB THROW" : "");
        }
    }
}
