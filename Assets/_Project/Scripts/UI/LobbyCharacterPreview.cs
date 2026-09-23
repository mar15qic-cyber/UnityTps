using System.Collections.Generic;
using Animancer;
using Game.Account;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Display-only rig. No player, networking, input or combat components are instantiated.</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RawImage))]
    public sealed class LobbyCharacterPreview : MonoBehaviour, IDragHandler
    {
        public const int StageLayer = 31;
        private GameObject stage, character, weapon;
        private Camera cameraView;
        private RenderTexture texture;
        private Animator animator;
        private Material roomLit, roomUnlit;
        private string equipmentKey;
        private LobbyWeaponGripCatalog.Grip gripPose;
        private readonly List<Material> materials = new();
        public string Error { get; private set; }
        public float LeftHandGripWeight { get; private set; }

        public void Initialize(bool renderEditorPreview = false)
        {
            InitializeWithMaterials(renderEditorPreview,
                Resources.Load<Material>("UI/LobbyRoomLit"), Resources.Load<Material>("UI/LobbyRoomUnlit"));
        }

        // Shared by player and editor: material assets keep their shader dependencies in the build.
        private void InitializeWithMaterials(bool renderEditorPreview, Material lit, Material unlit)
        {
            if (stage != null) return;
            var image = GetComponent<RawImage>();
            image.color = Color.clear;
            Error = null;
            try
            {
                roomLit = lit; roomUnlit = unlit;
                InitializeCore(renderEditorPreview);
            }
            catch (System.Exception exception)
            {
                ReleasePreview();
                Error = "角色展示暂不可用，请尝试重新进入大厅";
                Debug.LogWarning("[LobbyCharacterPreview] " + exception, this);
            }
        }

        private void InitializeCore(bool renderEditorPreview)
        {
            var source = Resources.Load<GameObject>("UI/LobbyCharacter");
            if (source == null) throw new System.InvalidOperationException("Missing UI/LobbyCharacter");
            stage = new GameObject("LobbyDisplayStage");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(stage, gameObject.scene);
            stage.transform.position = new Vector3(1500, 1500, 1500);
            character = Instantiate(source, stage.transform, false);
            character.transform.localPosition = Vector3.zero;
            character.transform.localRotation = Quaternion.Euler(0, 165, 0);
            animator = character.GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman) throw new System.InvalidOperationException("Lobby character requires a humanoid Animator");
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var clip = Resources.Load<AnimationClip>("UI/LobbyRelaxed");
            if (clip != null)
            {
                if (Application.isPlaying)
                {
                    var animation = animator.gameObject.AddComponent<AnimancerComponent>();
                    animation.Animator = animator;
                    animation.Play(clip);
                }
                else clip.SampleAnimation(animator.gameObject, 1f);
            }
            else Error = "待机动画尚未就绪";
            BuildRoom();
            SetLayer(stage);
            if (!Application.isPlaying && !renderEditorPreview) return;
            texture = new RenderTexture(1440, 1080, 24, RenderTextureFormat.ARGB32) { name = "LobbyCharacterRT", antiAliasing = 1 };
            if (!texture.Create()) throw new System.InvalidOperationException("Could not create lobby render texture");
            GetComponent<RawImage>().texture = texture;
            GetComponent<RawImage>().color = Color.white;
            var cameraObject = new GameObject("LobbyDisplayCamera", typeof(Camera));
            cameraObject.transform.SetParent(stage.transform, false);
            cameraView = cameraObject.GetComponent<Camera>();
#if UNITY_EDITOR
            if (renderEditorPreview) cameraView.scene = gameObject.scene;
#endif
            cameraView.transform.localPosition = new Vector3(0, 1.05f, -3.6f);
            cameraView.transform.LookAt(stage.transform.position + new Vector3(0, 0.95f, 0));
            cameraView.fieldOfView = 34;
            cameraView.nearClipPlane = 0.05f;
            cameraView.farClipPlane = 20;
            cameraView.cullingMask = 1 << StageLayer;
            cameraView.clearFlags = CameraClearFlags.SolidColor;
            cameraView.backgroundColor = UITheme.BackgroundDeep;
            cameraView.targetTexture = texture;
            cameraView.allowHDR = false;
            Light("Key", new Vector3(-2, 3, -2), Color.white, 3.2f);
            Light("Rim", new Vector3(2, 2.5f, 1.2f), UITheme.AccentPrimary, 4f);
            Light("Fill", new Vector3(1, 1.8f, -2), new Color(0.65f, 0.8f, 0.85f), 1.8f);
        }

        public void ApplyLoadout(LoadoutDto loadout, WeaponAssetCatalog catalog)
        {
            if (animator == null || loadout == null || catalog == null) return;
            var key = JsonUtility.ToJson(loadout);
            if (equipmentKey == key) return;
            equipmentKey = key;
            if (weapon != null) { weapon.SetActive(false); ReleaseObject(weapon); }
            if (!catalog.TryGet(loadout.primaryWeaponId ?? "", out var entry) || entry.definition == null || entry.definition.ThirdPersonViewPrefab == null)
            { Error = "主武器展示资源不可用"; return; }
            var rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (rightHand == null) { Error = "持枪骨骼不可用"; return; }
            weapon = Instantiate(entry.definition.ThirdPersonViewPrefab, rightHand, false);
            gripPose = Resources.Load<LobbyWeaponGripCatalog>("UI/LobbyWeaponGripCatalog")?.Find(loadout.primaryWeaponId);
            LobbyWeaponGripCatalog.Apply(gripPose, animator, weapon.transform);
            var attachmentCatalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            var view = weapon.GetComponent<WeaponAttachmentView>() ?? weapon.AddComponent<WeaponAttachmentView>();
            var selected = new List<AttachmentAssetEntry>();
            foreach (var item in loadout.attachments ?? System.Array.Empty<LoadoutAttachmentDto>())
                if (item.weaponSlot == "Primary" && attachmentCatalog != null && attachmentCatalog.TryGet(item.attachmentItemId, out var asset)) selected.Add(asset);
            if (attachmentCatalog != null) view.ApplyAttachments(attachmentCatalog, loadout.primaryWeaponId, selected, laserBeamEnabled: false);
            SetLayer(weapon);
            Error = null;
        }

        private void LateUpdate()
        {
            if (weapon != null) LobbyWeaponGripCatalog.Apply(gripPose, animator, weapon.transform);
            if (cameraView != null)
            {
                var rect = ((RectTransform)transform).rect;
                cameraView.aspect = Mathf.Max(0.1f, rect.width / Mathf.Max(1, rect.height));
            }
        }

#if UNITY_EDITOR
        public void RenderEditorPreview()
        {
            LateUpdate();
            if (cameraView != null) cameraView.Render();
        }
#endif

        public void OnDrag(PointerEventData data)
        {
            if (character != null && data.button == PointerEventData.InputButton.Left)
                character.transform.Rotate(0, -data.delta.x * 0.3f, 0, Space.Self);
        }

        private void BuildRoom()
        {
            Box("Floor", new Vector3(0, -0.08f, 0), new Vector3(9, 0.12f, 8), new Color(0.10f, 0.14f, 0.15f));
            Box("BackWall", new Vector3(0, 2, 2.5f), new Vector3(9, 4, 0.15f), new Color(0.07f, 0.10f, 0.11f));
            for (int i = -3; i <= 3; i++)
                Box("WallRib", new Vector3(i * 1.1f, 1.7f, 2.35f), new Vector3(0.035f, 3.4f, 0.12f), new Color(0.17f, 0.25f, 0.26f));
            Box("LightStrip", new Vector3(0, 0.18f, 2.28f), new Vector3(7, 0.025f, 0.02f), UITheme.AccentPrimary, true);
            Box("EquipmentCase", new Vector3(-1.65f, 0.25f, 1.15f), new Vector3(0.9f, 0.5f, 0.65f), new Color(0.18f, 0.23f, 0.22f));
        }

        private void Box(string name, Vector3 position, Vector3 scale, Color color, bool unlit = false)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(stage.transform, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = scale;
            ReleaseObject(cube.GetComponent<Collider>());
            var template = unlit ? roomUnlit : roomLit;
            if (template == null || template.shader == null || !template.shader.isSupported)
                throw new System.InvalidOperationException("Missing or unsupported lobby room material: " + (unlit ? "Unlit" : "Lit"));
            var material = new Material(template);
            material.SetColor("_BaseColor", color);
            materials.Add(material);
            cube.GetComponent<Renderer>().sharedMaterial = material;
        }

        private void Light(string name, Vector3 position, Color color, float intensity)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(stage.transform, false);
            go.transform.localPosition = position;
            go.transform.LookAt(stage.transform.position + Vector3.up);
            var light = go.GetComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 85;
            light.range = 10;
            light.intensity = intensity;
            light.color = color;
            light.cullingMask = 1 << StageLayer;
        }

        private static void SetLayer(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = StageLayer;
        }
        private static void ReleaseObject(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
        private void OnDestroy() => ReleasePreview();

        private void ReleasePreview()
        {
            var image = GetComponent<RawImage>();
            if (image != null) { image.texture = null; image.color = Color.clear; }
            if (cameraView != null) { cameraView.enabled = false; cameraView.targetTexture = null; }
            if (texture != null) { texture.Release(); ReleaseObject(texture); }
            if (stage != null) stage.SetActive(false);
            ReleaseObject(stage);
            foreach (var material in materials) ReleaseObject(material);
            materials.Clear();
            stage = character = weapon = null;
            cameraView = null; texture = null; animator = null;
            LeftHandGripWeight = 0f;
            equipmentKey = null; roomLit = roomUnlit = null;
        }
    }
}
