using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>WeaponController 事件的只读表现端：弹道、枪口光、Day4 枪口特效/弹壳/命中反馈、Day2 调试 HUD。</summary>
    /// LateUpdate 顺序说明：必须在 FPWeaponMotion（order 20，写 viewmodel 后坐/姿态）之后执行，
    /// 拖尾起点逐帧贴枪口时才能取到"当帧最终枪口位"——否则后坐动画期间起点滞后一帧（审计清单：后坐动画期间仍与枪管重合）。
    [DefaultExecutionOrder(30)]
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

        [Header("调试")]
        [Tooltip("每发输出拖尾诊断日志（origin/firedDirection/selfHitSkip/visualEnd）——排查异常拖尾用，默认关")]
        [SerializeField] private bool debugShotDiagnostics;

        /// <summary>ADS 瞄准线参考点；空表示该武器未单独配置（回退 Muzzle）。</summary>
        public Transform SightReference => sightReference;

        private LineRenderer _tracer;
        private Light _muzzleLight;
        private Material _tracerMaterial;
        private float _tracerTimer;
        private float _flashTimer;
        private Vector3 _firedDirection = Vector3.forward;   // 本发弹道方向（拖尾姿态基准）
        private float _tracerLength;                          // 本发视觉长度（命中距离投影并限长）

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (muzzle == null) muzzle = transform;
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
            if (_tracer != null) _tracer.enabled = _tracerTimer > 0f;
            if (_muzzleLight != null) _muzzleLight.enabled = _flashTimer > 0f;
        }

        /// <summary>开火事件在动画求值前发生，HandleShot 记录的方向/长度在 LateUpdate 持续
        /// 贴回当前帧真实枪口（Animancer/Animator 更新骨骼后），使拖尾起点与枪口动画逐帧重合、
        /// 且始终平行于本发弹道方向（Day4 实机审计 §1：不再直接使用 Result.Point 作终点——
        /// 自身命中/贴脸命中点会形成竖直/向下短线；命中点沿相机射线，与枪口弹道线平行偏移）。</summary>
        private void LateUpdate()
        {
            if (_tracer == null || !_tracer.enabled || muzzle == null) return;
            Vector3 start = muzzle.position;
            _tracer.SetPosition(0, start);
            _tracer.SetPosition(1, start + _firedDirection * _tracerLength);
        }

        private void HandleShot(WeaponShot shot)
        {
            Vector3 start = muzzle != null ? muzzle.position : shot.Origin;

            // 拖尾几何（审计 §1）：起点=枪口；方向=本发 FiredDirection（散布后真实弹道）；
            // 长度=命中点在枪口弹道线上的投影，钳制 [0, maxTracerLength]（overlay 相机 far clip 5m）。
            // 无命中时 Result.Point 已是 origin+dir*maxRange 远点，投影结果≈maxRange，同样被限长截断。
            _firedDirection = shot.FiredDirection.sqrMagnitude > 1e-8f
                ? shot.FiredDirection.normalized
                : Vector3.forward;
            float projected = Vector3.Dot(shot.Result.Point - start, _firedDirection);
            _tracerLength = Mathf.Clamp(projected, 0f, maxTracerLength);

            _tracer.SetPosition(0, start);
            _tracer.SetPosition(1, start + _firedDirection * _tracerLength);
            _tracer.enabled = true;
            _tracerTimer = tracerDuration;
            _muzzleLight.enabled = true;
            _flashTimer = muzzleFlashDuration;

            if (debugShotDiagnostics)
            {
                Vector3 visualEnd = start + _firedDirection * _tracerLength;
                Debug.Log($"[WeaponView] shot: origin={shot.Origin:F2} firedDir={_firedDirection:F3} " +
                          $"hit={shot.Result.Hit} damaged={shot.Result.Damaged} selfSkip={shot.Result.SelfHitsSkipped} " +
                          $"resultPoint={shot.Result.Point:F2} visualEnd={visualEnd:F2} len={_tracerLength:F2}", this);
            }

            SpawnMuzzleFlash();
            SpawnShellCasing();
            SpawnImpact(shot);
            // 命中标记已迁 CrosshairPresenter（CP5）；本组件只剩武器表现
        }

        private void HandleDryFire() => _flashTimer = 0f;

        private void SpawnMuzzleFlash()
        {
            if (muzzleFlashPrefab == null || muzzle == null) return;
            var flash = Instantiate(muzzleFlashPrefab, muzzle.position, muzzle.rotation, muzzle);
            flash.transform.localPosition = Vector3.zero;
            flash.transform.localRotation = Quaternion.identity;
            // 火光必须与武器同层（FirstPersonView，overlay 相机渲染）。muzzle 挂点若被误配到
            // 其他层，回退用视图根层兜底，避免被主相机（FOV 60）渲染造成投影错位。
            int flashLayer = muzzle.gameObject.layer == gameObject.layer
                ? muzzle.gameObject.layer
                : gameObject.layer;
            SetLayerRecursive(flash, flashLayer);
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

                // CP5：准心/命中标记/弹药/操作提示已全部迁出 OnGUI → uGUI MVC
                //（CrosshairPresenter/View + WeaponHudView，Docs/13 检查点 5）。OnGUI 及样式字段删除。
            }
        }
