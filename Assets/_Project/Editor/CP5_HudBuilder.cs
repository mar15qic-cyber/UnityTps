using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.HUD;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>
    /// CP5 HUD 一次性构建（Docs/13 检查点 5）：Screen Space-Overlay Canvas + 准心四线/中心点/
    /// 命中标记（CrosshairView）+ 弹药/武器名/操作提示（WeaponHudView）+ CrosshairConfig 资产。
    /// 菜单触发：Tools/Build Weapon HUD（幂等：已存在则只复核接线）。
    /// </summary>
    public static class CP5_HudBuilder
    {
        private const string ConfigPath = "Assets/_Project/ScriptableObjects/HUD/CrosshairConfig.asset";

        [MenuItem("Tools/Build Weapon HUD")]
        public static void Build()
        {
            // Config 资产（幂等）
            var config = AssetDatabase.LoadAssetAtPath<CrosshairConfig>(ConfigPath);
            if (config == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/_Project/ScriptableObjects/HUD"))
                    AssetDatabase.CreateFolder("Assets/_Project/ScriptableObjects", "HUD");
                config = ScriptableObject.CreateInstance<CrosshairConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.path.Contains("Arena")) { Debug.LogError("[CP5-HUD] 请在 Arena 场景执行"); return; }

            // Canvas（幂等：按名查找）。注意 Unity 伪 null——??/?. 对 fake-null 不生效，全部显式判空。
            var existing = GameObject.Find("WeaponHudCanvas");
            var canvasGo = existing != null ? existing : new GameObject("WeaponHudCanvas");
            var canvas = canvasGo.GetComponent<Canvas>();
            if (canvas == null) canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            if (canvasGo.GetComponent<GraphicRaycaster>() == null) canvasGo.AddComponent<GraphicRaycaster>();
            Undo.RegisterCreatedObjectUndo(canvasGo, "Build Weapon HUD");

            // Day4 实机审计 §2：只归一化位置/旋转。Screen Space-Overlay 的根
            // RectTransform 尺寸与缩放由 Canvas/CanvasScaler 驱动，不能在运行时把
            // scale 当作 HUD 像素单位写回，否则会与 CrosshairView 的像素换算叠加。
            var rootRt = canvasGo.GetComponent<RectTransform>();
            if (rootRt != null)
            {
                rootRt.localPosition = Vector3.zero;
                rootRt.localRotation = Quaternion.identity;
            }
            canvasGo.layer = 0;   // Overlay UI 不需要专门层（历史防御：防止被误设为剔除层）

            RectTransform Line(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Color color)
            {
                var t = canvasGo.transform.Find(name);
                var go = t != null ? t.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(canvasGo.transform, false);
                rt.anchorMin = anchorMin;
                rt.anchorMax = anchorMax;
                rt.sizeDelta = size;
                var img = go.GetComponent<Image>();
                img.color = color;
                img.raycastTarget = false;
                return rt;
            }

            RectTransform Dot(string name, float size, Color color)
            {
                var rt = Line(name, Vector2.one * 0.5f, Vector2.one * 0.5f, new Vector2(size, size), color);
                rt.pivot = Vector2.one * 0.5f;
                return rt;
            }

            // 配置资产存在性防御（幂等重入时 LoadAssetAtPath 可能失败）
            if (config == null)
            {
                Debug.LogError("[CP5-HUD] CrosshairConfig 资产创建/加载失败");
                return;
            }
            var line = config.LineColor;
            var len = config.LineLength;
            var th = config.LineThickness;
            var top = Line("Crosshair_Top", Vector2.one * .5f, Vector2.one * .5f, new Vector2(th, len), line);
            var bottom = Line("Crosshair_Bottom", Vector2.one * .5f, Vector2.one * .5f, new Vector2(th, len), line);
            var left = Line("Crosshair_Left", Vector2.one * .5f, Vector2.one * .5f, new Vector2(len, th), line);
            var right = Line("Crosshair_Right", Vector2.one * .5f, Vector2.one * .5f, new Vector2(len, th), line);
            var dot = Dot("Crosshair_Dot", config.DotSize, line);
            // 命中标记：纯 Text（Image/Text 同为 Graphic 互斥——不可用 Dot() 建）
            var hit = canvasGo.transform.Find("Crosshair_HitMarker");
            GameObject hitGo;
            if (hit != null) hitGo = hit.gameObject;
            else
            {
                hitGo = new GameObject("Crosshair_HitMarker", typeof(RectTransform));
                var hrt = hitGo.GetComponent<RectTransform>();
                hrt.SetParent(canvasGo.transform, false);
                hrt.anchorMin = hrt.anchorMax = hrt.pivot = Vector2.one * 0.5f;
                hrt.sizeDelta = new Vector2(40f, 40f);
            }
            var hitImg = hitGo.GetComponent<Image>();
            if (hitImg != null) Undo.DestroyObjectImmediate(hitImg);

            // 命中标记：Text "×"
            var hitText = hitGo.GetComponent<Text>();
            if (hitText == null) hitText = hitGo.AddComponent<Text>();
            if (hitText == null) { Debug.LogError("[CP5-HUD] hitText AddComponent 失败"); return; }
            hitText.text = "×";
            hitText.fontSize = 36;
            hitText.alignment = TextAnchor.MiddleCenter;
            hitText.color = config.HitMarkerColor;
            hitText.raycastTarget = false;
            var builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (builtinFont != null) hitText.font = builtinFont;

            Text MakeText(string name, TextAnchor anchor, int fontSize, Vector2 pos, Vector2 size)
            {
                var go = canvasGo.transform.Find(name) != null
                    ? canvasGo.transform.Find(name).gameObject
                    : new GameObject(name, typeof(RectTransform), typeof(Text));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(canvasGo.transform, false);
                rt.anchorMin = rt.anchorMax = new Vector2(anchor == TextAnchor.MiddleLeft ? 0f : 1f,
                    anchor == TextAnchor.UpperLeft || anchor == TextAnchor.UpperRight ? 1f : 0f);
                rt.pivot = rt.anchorMin;
                rt.anchoredPosition = pos;
                rt.sizeDelta = size;
                var txt = go.GetComponent<Text>();
                txt.fontSize = fontSize;
                txt.color = new Color(1f, 1f, 1f, 0.92f);
                txt.alignment = anchor;
                txt.raycastTarget = false;
                if (builtinFont != null) txt.font = builtinFont;
                return txt;
            }

            var ammoText = MakeText("AmmoText", TextAnchor.MiddleRight, 30, new Vector2(-40f, 46f), new Vector2(560f, 44f));
            var weaponText = MakeText("WeaponText", TextAnchor.MiddleRight, 20, new Vector2(-40f, 96f), new Vector2(560f, 30f));
            var hintText = MakeText("HintText", TextAnchor.UpperLeft, 17, new Vector2(30f, -20f), new Vector2(1100f, 30f));

            // Presenter + View + HudView（幂等）
            var presenter = canvasGo.GetComponent<CrosshairPresenter>();
            if (presenter == null) presenter = canvasGo.AddComponent<CrosshairPresenter>();
            var pso = new SerializedObject(presenter);
            pso.FindProperty("controller").objectReferenceValue = Object.FindObjectOfType<WeaponController>();
            pso.FindProperty("aimState").objectReferenceValue = Object.FindObjectOfType<PlayerAimState>();
            pso.FindProperty("playerState").objectReferenceValue = Object.FindObjectOfType<Game.Gameplay.Player.PlayerStateView>();
            pso.FindProperty("config").objectReferenceValue = config;
            pso.ApplyModifiedPropertiesWithoutUndo();

            var view = canvasGo.GetComponent<CrosshairView>();
            if (view == null) view = canvasGo.AddComponent<CrosshairView>();
            var vso = new SerializedObject(view);
            vso.FindProperty("presenter").objectReferenceValue = presenter;
            vso.FindProperty("top").objectReferenceValue = top;
            vso.FindProperty("bottom").objectReferenceValue = bottom;
            vso.FindProperty("left").objectReferenceValue = left;
            vso.FindProperty("right").objectReferenceValue = right;
            vso.FindProperty("centerDot").objectReferenceValue = dot;
            vso.FindProperty("hitMarker").objectReferenceValue = hitGo.GetComponent<RectTransform>();
            vso.FindProperty("config").objectReferenceValue = config;
            vso.ApplyModifiedPropertiesWithoutUndo();

            var hud = canvasGo.GetComponent<WeaponHudView>();
            if (hud == null) hud = canvasGo.AddComponent<WeaponHudView>();
            var hso = new SerializedObject(hud);
            hso.FindProperty("controller").objectReferenceValue = Object.FindObjectOfType<WeaponController>();
            hso.FindProperty("arsenal").objectReferenceValue = Object.FindObjectOfType<Arsenal>();
            hso.FindProperty("ammoText").objectReferenceValue = ammoText;
            hso.FindProperty("weaponText").objectReferenceValue = weaponText;
            hso.FindProperty("hintText").objectReferenceValue = hintText;
            hso.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[CP5-HUD] 构建完成并保存：WeaponHudCanvas（准心MVC+弹药/武器/提示）@" + scene.path);
        }
    }
}
