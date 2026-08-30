using System.Collections.Generic;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Debugging
{
    /// <summary>
    /// Arena_LPWTest 专用的最小 ADS 对位辅助。它不改变武器姿态：仅把当前
    /// SightReference 的屏幕投影与摄像机中心同时标出，供人工检查真实照门/
    /// 准星是否也位于中心十字。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LPWAimInTestOverlay : MonoBehaviour
    {
        [Header("仅用于 Arena_LPWTest 人工验收")]
        [SerializeField, Min(1f)] private float centeredTolerancePixels = 18f;
        [SerializeField] private Camera firstPersonViewCamera;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField] private WeaponController weaponController;
        [SerializeField] private Arsenal arsenal;
        [SerializeField] private LPWWeaponManifest productionManifest;
        [SerializeField] private WeaponAssetCatalog weaponAssets;

        private readonly List<WeaponView> viewBuffer = new();
        private GUIStyle labelStyle;
        private WeaponView activeView;
        private Vector3 markerScreenPosition;
        private bool markerVisible;
        private readonly List<WeaponDefinition> productionWeapons = new();

        private void Awake()
        {
            aimState ??= GetComponent<PlayerAimState>();
            weaponController ??= GetComponent<WeaponController>();
            arsenal ??= GetComponent<Arsenal>();
            firstPersonViewCamera ??= ResolveFirstPersonViewCamera();
            ConfigureProductionWeapons();
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || arsenal == null || productionWeapons.Count == 0) return;
            if (keyboard.pageDownKey.wasPressedThisFrame || keyboard.rightBracketKey.wasPressedThisFrame)
                SelectRelative(1);
            else if (keyboard.pageUpKey.wasPressedThisFrame || keyboard.leftBracketKey.wasPressedThisFrame)
                SelectRelative(-1);
        }

        private void LateUpdate()
        {
            if (firstPersonViewCamera == null)
                firstPersonViewCamera = ResolveFirstPersonViewCamera();

            activeView = FindActiveView();
            Transform sight = activeView != null ? activeView.SightReference : null;
            markerVisible = firstPersonViewCamera != null && sight != null;
            if (markerVisible)
            {
                markerScreenPosition = firstPersonViewCamera.WorldToScreenPoint(sight.position);
                markerVisible = markerScreenPosition.z > 0f;
            }
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            EnsureLabelStyle();

            float ads01 = aimState != null ? aimState.Ads01 : 0f;
            string weaponId = weaponController != null && weaponController.Definition != null
                ? weaponController.Definition.WeaponId
                : "未装备";
            string indexText = arsenal != null && arsenal.ActiveIndex >= 0 && productionWeapons.Count > 0
                ? $"{arsenal.ActiveIndex + 1}/{productionWeapons.Count}"
                : $"0/{productionWeapons.Count}";
            GUI.Label(new Rect(16f, 112f, 560f, 24f),
                "LPW AIM-IN TEST  |  [ / ] 或 PageUp/PageDown 切换 29 枪，按住 RMB 开镜", labelStyle);
            GUI.Label(new Rect(16f, 136f, 560f, 24f),
                $"{indexText}  ·  {weaponId}  ·  ADS {ads01:0.00}", labelStyle);

            Vector2 center = new(Screen.width * 0.5f, Screen.height * 0.5f);
            DrawCross(center, 7f, Color.white);

            if (!markerVisible)
            {
                GUI.Label(new Rect(16f, 160f, 640f, 24f),
                    "未找到当前武器的 SightReference 或 FP View Camera。", labelStyle);
                return;
            }

            Vector2 sight = new(markerScreenPosition.x, Screen.height - markerScreenPosition.y);
            float error = Vector2.Distance(sight, center);
            Color markerColor = error <= centeredTolerancePixels ? Color.green : Color.red;
            DrawCross(sight, 10f, markerColor);

            string status = ads01 >= .95f
                ? (error <= centeredTolerancePixels ? "参考点已居中" : "参考点偏离中心")
                : "等待 ADS 收敛";
            GUI.color = markerColor;
            GUI.Label(new Rect(16f, 160f, 700f, 24f),
                $"SightReference 偏差：{error:0.0}px  ·  {status}", labelStyle);
            GUI.color = Color.white;
            GUI.Label(new Rect(16f, 184f, 780f, 24f),
                "白色＝摄像机中心；绿色/红色＝SightReference。请以真实照门/准星是否压住白十字为最终结论。", labelStyle);
        }

        private void ConfigureProductionWeapons()
        {
            productionWeapons.Clear();
            if (arsenal == null || productionManifest == null || weaponAssets == null) return;
            foreach (LPWWeaponSpec spec in productionManifest.Weapons)
                if (spec != null && weaponAssets.TryResolveDefinition(spec.itemId, out WeaponDefinition definition))
                    productionWeapons.Add(definition);
            if (productionWeapons.Count == 29)
                arsenal.ConfigureSlots(productionWeapons, 0);
            else
                Debug.LogError($"[LPWAimInTestOverlay] 期望 29 把 LPW，实际解析 {productionWeapons.Count} 把。", this);
        }

        private void SelectRelative(int direction)
        {
            int current = Mathf.Clamp(arsenal.ActiveIndex, 0, productionWeapons.Count - 1);
            int next = (current + direction + productionWeapons.Count) % productionWeapons.Count;
            arsenal.TrySelectSlot(next);
        }

        private WeaponView FindActiveView()
        {
            GetComponentsInChildren(false, viewBuffer);
            WeaponView best = null;
            foreach (WeaponView candidate in viewBuffer)
            {
                if (candidate != null && candidate.isActiveAndEnabled)
                {
                    best = candidate;
                    break;
                }
            }
            viewBuffer.Clear();
            return best;
        }

        private Camera ResolveFirstPersonViewCamera()
        {
            foreach (Camera candidate in GetComponentsInChildren<Camera>(true))
            {
                if (candidate.name == "FP View Camera") return candidate;
            }
            return Camera.main;
        }

        private void EnsureLabelStyle()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
        }

        private static void DrawCross(Vector2 position, float radius, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(position.x - radius, position.y - 1f, radius * 2f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(position.x - 1f, position.y - radius, 2f, radius * 2f), Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
