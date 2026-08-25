using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Day4.2 TP 枪口特效：与 FP WeaponView 订阅同一个 WeaponController.OnShotFired
    /// （同一事件源 → FP/TP 天然同步），在当前 TP 武器 Muzzle 挂点生成枪口闪光。
    /// 本地玩家的 TP 模型在 LocalPlayerBody 层（主相机剔除），特效在 Scene 视图与
    /// 未来远端代理可见；联网后远端开火事件（ObserversRpc）沿用本组件。
    /// </summary>
    public sealed class TPWeaponFX : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private TPWeaponMeshSwapper swapper;
        [SerializeField] private GameObject muzzleFlashPrefab;
        [SerializeField, Min(0.05f)] private float despawnSeconds = 0.15f;
        [SerializeField] private bool alsoSpawnPointLight = true;
        [SerializeField, Min(0.01f)] private float lightDuration = 0.05f;

        private Light _light;

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (swapper == null) swapper = GetComponentInParent<TPWeaponMeshSwapper>() ?? GetComponent<TPWeaponMeshSwapper>();
            if (muzzleFlashPrefab == null)
                muzzleFlashPrefab = Resources.Load<GameObject>("MuzzleFlash_LPFP");
            BuildLight();
        }

        private void OnEnable()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (controller != null) controller.OnShotFired += HandleShot;
        }

        private void OnDisable()
        {
            if (controller != null) controller.OnShotFired -= HandleShot;
        }

        private void Update()
        {
            if (_light != null && _light.enabled && Time.unscaledTime >= _lightOffTime) _light.enabled = false;
        }

        private float _lightOffTime;

        private void HandleShot(WeaponShot _) => SpawnMuzzleFlash();

        private void SpawnMuzzleFlash()
        {
            var muzzle = swapper != null ? swapper.CurrentMuzzle : null;
            if (muzzle == null || muzzleFlashPrefab == null) return;

            if (alsoSpawnPointLight && _light != null)
            {
                _light.transform.position = muzzle.position;
                _light.enabled = true;
                _lightOffTime = Time.unscaledTime + lightDuration;
            }

            var flash = Instantiate(muzzleFlashPrefab, muzzle.position, muzzle.rotation, muzzle);
            flash.transform.localPosition = Vector3.zero;
            flash.transform.localRotation = Quaternion.identity;
            SetLayerRecursive(flash, muzzle.gameObject.layer);
            var particle = flash.GetComponent<ParticleSystem>();
            if (particle != null) particle.Play();
            Destroy(flash, despawnSeconds);
        }

        /// <summary>挂在 TP 模型根（剔除层）的点光源，随枪口移动，仅开火瞬间开启。</summary>
        private void BuildLight()
        {
            if (!alsoSpawnPointLight) return;
            _light = GetComponentInChildren<Light>(true);
            if (_light == null)
            {
                var go = new GameObject("Runtime_TP_MuzzleLight");
                go.transform.SetParent(transform, false);
                _light = go.AddComponent<Light>();
                _light.type = LightType.Point;
                _light.color = new Color(1f, 0.62f, 0.18f);
                _light.intensity = 3f;
                _light.range = 2.5f;
            }
            _light.enabled = false;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }
    }
}
