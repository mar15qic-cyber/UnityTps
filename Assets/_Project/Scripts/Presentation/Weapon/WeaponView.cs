using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>WeaponController 事件的只读表现端：弹道、枪口光、Day4 枪口特效/弹壳/命中反馈、Day2 调试 HUD。</summary>
    public sealed class WeaponView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform muzzle;
        [SerializeField] private Color tracerColor = new(1f, 0.78f, 0.15f, 1f);
        [SerializeField, Min(0.01f)] private float tracerDuration = 0.045f;
        [SerializeField, Min(0.01f)] private float muzzleFlashDuration = 0.035f;
        [Tooltip("拖尾视觉终点到枪口的最大长度（overlay 相机 far clip 5m，留余量避免生硬截断）。只影响视觉，不改命中点。")]
        [SerializeField, Min(0.5f)] private float maxTracerLength = 4.25f;

        [Header("Day4 特效 prefab（空 = 不生成）")]
        [Tooltip("枪口闪光粒子。挂在 Muzzle 下、切到 FirstPersonView 层，只随 overlay 相机渲染")]
        [SerializeField] private GameObject muzzleFlashPrefab;
        [SerializeField, Min(0.05f)] private float muzzleFlashDespawnSeconds = 0.15f;
        [Tooltip("抛壳 prefab（自带 Rigidbody 抛射与自毁，如 LPFP CasingScript）")]
        [SerializeField] private GameObject shellCasingPrefab;
        [Tooltip("抛壳口；空则用 Muzzle")]
        [SerializeField] private Transform shellPort;
        [Tooltip("普通表面命中反馈（弹孔/火花，含音效，自毁）")]
        [SerializeField] private GameObject impactPrefab;
        [Tooltip("命中目标（有伤害）反馈，如血液；空则退回 impactPrefab")]
        [SerializeField] private GameObject damagedImpactPrefab;

        /// <summary>FPWeaponMotion 程序化 ADS 用：当前视图的枪口。</summary>
        public Transform Muzzle => muzzle;

        [Header("Day4.4 ADS 瞄准参考")]
        [Tooltip("ADS 瞄准线参考点（照门/瞄具线上）；空则回退用 Muzzle。物理 Muzzle 位于膛口，与瞄具线有 sight-height 高度差，ADS 必须对齐瞄具线而非膛线。")]
        [SerializeField] private Transform sightReference;

        /// <summary>ADS 瞄准线参考点；空表示该武器未单独配置（回退 Muzzle）。</summary>
        public Transform SightReference => sightReference;

        private LineRenderer _tracer;
        private Light _muzzleLight;
        private Material _tracerMaterial;
        private float _tracerTimer;
        private float _flashTimer;
        private float _hitMarkerTimer;
        private GUIStyle _ammoStyle;
        private GUIStyle _hintStyle;
        private FPCameraRig _cameraRig;

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (muzzle == null) muzzle = transform;
            _cameraRig = GetComponentInParent<FPCameraRig>();
            BuildEffects();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnShotFired += HandleShot;
            controller.OnDryFire += HandleDryFire;
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
        }

        private void OnDestroy()
        {
            if (_tracerMaterial != null) Destroy(_tracerMaterial);
        }

        private void Update()
        {
            _tracerTimer -= Time.deltaTime;
            _flashTimer -= Time.deltaTime;
            _hitMarkerTimer -= Time.deltaTime;
            if (_tracer != null) _tracer.enabled = _tracerTimer > 0f;
            if (_muzzleLight != null) _muzzleLight.enabled = _flashTimer > 0f;
        }

        /// <summary>开火事件在动画求值前发生，HandleShot 记录的第 0 点是动画前的旧枪口位；
        /// 这里在 Animancer/Animator 更新骨骼后（LateUpdate）持续贴回当前帧真实枪口，
        /// 使拖尾起点与火光/枪口动画逐帧重合。终点（命中点）不在此更新。</summary>
        private void LateUpdate()
        {
            if (_tracer == null || !_tracer.enabled || muzzle == null) return;
            _tracer.SetPosition(0, muzzle.position);
            // 视觉终点限长：拖尾由 overlay 相机渲染（far clip 5m），命中点常在远裁剪面外，
            // 直接用真实命中点会被生硬截断。沿弹道方向把视觉终点钳制在枪口前方 maxTracerLength。
            Vector3 end = _tracer.GetPosition(1);
            Vector3 start = muzzle.position;
            Vector3 delta = end - start;
            if (delta.sqrMagnitude > maxTracerLength * maxTracerLength)
                _tracer.SetPosition(1, start + delta.normalized * maxTracerLength);
        }

        private void HandleShot(WeaponShot shot)
        {
            Vector3 start = muzzle != null ? muzzle.position : shot.Origin;
            _tracer.SetPosition(0, start);
            _tracer.SetPosition(1, shot.Result.Point);
            _tracer.enabled = true;
            _tracerTimer = tracerDuration;
            _muzzleLight.enabled = true;
            _flashTimer = muzzleFlashDuration;

            SpawnMuzzleFlash();
            SpawnShellCasing();
            SpawnImpact(shot);
            if (shot.Result.Damaged) _hitMarkerTimer = 0.12f;
        }

        private void HandleDryFire() => _flashTimer = 0f;

        private void SpawnMuzzleFlash()
        {
            if (muzzleFlashPrefab == null || muzzle == null) return;
            var flash = Instantiate(muzzleFlashPrefab, muzzle.position, muzzle.rotation, muzzle);
            flash.transform.localPosition = Vector3.zero;
            flash.transform.localRotation = Quaternion.identity;
            SetLayerRecursive(flash, muzzle.gameObject.layer);
            // LPFP 枪口粒子 playOnAwake=false（原版靠脚本启停），这里显式触发
            var particle = flash.GetComponent<ParticleSystem>();
            if (particle != null) particle.Play();
            Destroy(flash, muzzleFlashDespawnSeconds);
        }

        private void SpawnShellCasing()
        {
            if (shellCasingPrefab == null) return;
            var port = shellPort != null ? shellPort : muzzle;
            var shell = Instantiate(shellCasingPrefab, port != null ? port.position : transform.position,
                transform.rotation);
            // LPFP CasingScript 在 Awake 自带相对抛射力/自转/自毁，这里只负责放置
            SetLayerRecursive(shell, 0);
        }

        private void SpawnImpact(WeaponShot shot)
        {
            if (!shot.Result.Hit) return;
            GameObject prefab = shot.Result.Damaged && damagedImpactPrefab != null
                ? damagedImpactPrefab
                : impactPrefab;
            if (prefab == null) return;
            // LPFP Impact Prefab 按 +Z 朝向表面法线，自带命中音效与自毁
            Instantiate(prefab, shot.Result.Point + shot.Result.Normal * 0.01f,
                Quaternion.LookRotation(shot.Result.Normal));
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        private void BuildEffects()
        {
            // FP 视觉层：武器/枪口由 overlay 相机（FirstPersonView 层）渲染。tracer 与枪口灯若留在
            // Layer 0 会被主相机（FOV 60）渲染——同一 muzzle.position 经两套 FOV 投影后屏幕位置不同，
            // 拖尾起点会错位到准心方向。所有 FP 表现必须与武器同层。
            int firstPersonLayer = LayerMask.NameToLayer("FirstPersonView");
            if (firstPersonLayer < 0)
            {
                Debug.LogError("[WeaponView] FirstPersonView layer is missing.", this);
                firstPersonLayer = gameObject.layer;
            }

            var tracerObject = new GameObject("Runtime_Tracer");
            tracerObject.transform.SetParent(transform, false);
            tracerObject.layer = firstPersonLayer;
            _tracer = tracerObject.AddComponent<LineRenderer>();
            _tracer.useWorldSpace = true;
            _tracer.positionCount = 2;
            _tracer.startWidth = 0.012f;
            _tracer.endWidth = 0.003f;
            _tracer.startColor = tracerColor;
            _tracer.endColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0f);
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                _tracerMaterial = new Material(shader) { color = tracerColor };
                _tracer.material = _tracerMaterial;
            }
            _tracer.enabled = false;

            var lightObject = new GameObject("Runtime_MuzzleFlash");
            lightObject.transform.SetParent(muzzle, false);
            lightObject.layer = firstPersonLayer;
            _muzzleLight = lightObject.AddComponent<Light>();
            _muzzleLight.type = LightType.Point;
            _muzzleLight.color = new Color(1f, 0.62f, 0.18f);
            _muzzleLight.intensity = 3f;
            _muzzleLight.range = 2f;
            _muzzleLight.enabled = false;
        }

        private void OnGUI()
        {
            if (controller == null || !controller.IsInitialized) return;
            EnsureStyles();

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            // ADS 时枪瞄具对准屏幕中心，程序准星隐藏
            bool showCrosshair = _cameraRig == null || _cameraRig.AdsBlend < 0.5f;
            if (showCrosshair)
            {
                GUI.DrawTexture(new Rect(cx - 8f, cy - 1f, 16f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1f, cy - 8f, 2f, 16f), Texture2D.whiteTexture);
            }
            if (_hitMarkerTimer > 0f)
            {
                GUI.Label(new Rect(cx - 20f, cy - 22f, 40f, 40f), "×", _ammoStyle);
            }

            var runtime = controller.Runtime;
            string ammo = $"{runtime.CurrentAmmo:00} / {runtime.ReserveAmmo:000}";
            if (runtime.State == WeaponRuntimeState.Reloading)
                ammo += $"  RELOAD {controller.Actions.NormalizedProgress * 100f:0}%";
            GUI.Label(new Rect(Screen.width - 360f, Screen.height - 80f, 340f, 50f), ammo, _ammoStyle);
            string weapon = controller.Definition != null ? controller.Definition.DisplayName : "";
            GUI.Label(new Rect(Screen.width - 360f, Screen.height - 118f, 340f, 30f), weapon, _hintStyle);
            GUI.Label(new Rect(18f, 18f, 760f, 80f),
                "LMB FIRE    RMB ADS    R RELOAD    WASD MOVE    SHIFT SPRINT    0-9 SWITCH", _hintStyle);
        }

        private void EnsureStyles()
        {
            if (_ammoStyle != null) return;
            _ammoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white }
            };
            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = new Color(1f, 1f, 1f, 0.85f) }
            };
        }
    }
}
