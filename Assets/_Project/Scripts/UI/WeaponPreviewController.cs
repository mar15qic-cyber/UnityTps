using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Renders an authored weapon prefab into a UI RawImage with drag orbit and wheel zoom.</summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class WeaponPreviewController : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IScrollHandler
    {
        private const int PreviewLayer = 30;
        private const float MinDistance = 0.45f;
        private const float MaxDistance = 8f;

        private RawImage output;
        private GameObject stage;
        private GameObject modelRoot;
        private GameObject modelInstance;
        private Camera previewCamera;
        private RenderTexture renderTexture;
        private float yaw = -28f;
        private float pitch = 10f;
        private float distance = 2.4f;
        private bool dragging;
        private Vector2 lastPointer;

        private void Awake()
        {
            output = GetComponent<RawImage>();
            output.raycastTarget = true;
        }

        public void Initialize(GameObject prefab)
        {
            CleanupStage();
            stage = new GameObject("WeaponPreviewStage");
            modelRoot = new GameObject("WeaponPreviewModelRoot");
            modelRoot.transform.SetParent(stage.transform, false);

            if (prefab != null)
            {
                modelInstance = Instantiate(prefab, modelRoot.transform);
                modelInstance.name = prefab.name + "_ShopPreview";
                SetLayerRecursively(modelInstance, PreviewLayer);
                FrameModel();
            }

            CreateCameraAndLighting();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            dragging = true;
            lastPointer = eventData.position;
        }

        public void OnPointerUp(PointerEventData eventData) => dragging = false;

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || modelRoot == null) return;
            var delta = eventData.position - lastPointer;
            lastPointer = eventData.position;
            yaw -= delta.x * 0.35f;
            pitch = Mathf.Clamp(pitch + delta.y * 0.22f, -55f, 55f);
        }

        public void OnScroll(PointerEventData eventData)
        {
            distance = Mathf.Clamp(distance - eventData.scrollDelta.y * 0.08f, MinDistance, MaxDistance);
        }

        private void LateUpdate()
        {
            if (modelRoot == null || previewCamera == null) return;
            modelRoot.transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            previewCamera.transform.position = new Vector3(0f, 0.05f, -distance);
            previewCamera.transform.LookAt(Vector3.zero);
        }

        private void FrameModel()
        {
            var renderers = modelInstance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            modelInstance.transform.localPosition = -bounds.center;
            var extent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            distance = Mathf.Clamp(Mathf.Max(extent * 2.8f, 0.8f), MinDistance, MaxDistance);
        }

        private void CreateCameraAndLighting()
        {
            renderTexture = new RenderTexture(720, 520, 24, RenderTextureFormat.ARGB32)
            {
                name = "WeaponPreviewRenderTexture",
                antiAliasing = 1
            };
            renderTexture.Create();
            output.texture = renderTexture;

            var cameraObject = new GameObject("WeaponPreviewCamera");
            cameraObject.transform.SetParent(stage.transform, false);
            previewCamera = cameraObject.AddComponent<Camera>();
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = new Color(0.015f, 0.055f, 0.11f, 1f);
            previewCamera.cullingMask = 1 << PreviewLayer;
            previewCamera.fieldOfView = 28f;
            previewCamera.nearClipPlane = 0.01f;
            previewCamera.farClipPlane = 50f;
            previewCamera.targetTexture = renderTexture;

            CreateLight("WeaponPreviewKey", new Vector3(-35f, -35f, -25f), 1.2f);
            CreateLight("WeaponPreviewFill", new Vector3(25f, 145f, 15f), 0.65f);
        }

        private void CreateLight(string name, Vector3 rotation, float intensity)
        {
            var lightObject = new GameObject(name);
            lightObject.transform.SetParent(stage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(rotation);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.color = new Color(0.72f, 0.88f, 1f);
            light.cullingMask = 1 << PreviewLayer;
        }

        private void CleanupStage()
        {
            if (stage != null) Destroy(stage);
            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
                renderTexture = null;
            }
            if (output != null) output.texture = null;
            modelInstance = null;
            modelRoot = null;
            previewCamera = null;
        }

        private void OnDestroy() => CleanupStage();

        private static void SetLayerRecursively(GameObject value, int layer)
        {
            value.layer = layer;
            foreach (Transform child in value.transform) SetLayerRecursively(child.gameObject, layer);
        }
    }
}

