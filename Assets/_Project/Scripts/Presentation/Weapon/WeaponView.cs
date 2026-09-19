using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>WeaponController 事件的只读表现端：弹道、枪口光、Day4 枪口特效/弹壳/命中反馈、Day2 调试 HUD。
    /// 2026-09-19 ADS 审计 S3 重构：曳光按"每发快照"冻结——起点=开火帧枪口、终点=跨相机屏幕匹配
    /// 收敛到本发真实弹着点（世界相机投影 → FP overlay 相机反投影），已发出的飞行曳光不再被
    /// 枪口动画/后坐逐帧拖动（旧 LateUpdate 重放已删除）。霰弹逐弹丸独立生命周期（池化）。
    /// 近段留在 FirstPersonView 层（与枪模同投影），远段飞行段放 Default 层：世界相机与
    /// 倍率镜 RT 均可见，接缝点经跨相机匹配对齐，合成画面连续。</summary>
    [DefaultExecutionOrder(30)]
    public sealed class WeaponView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform muzzle;
        [SerializeField] private Color tracerColor = new(1f, 0.78f, 0.15f, 1f);
        [SerializeField, Min(0.01f)] private float tracerDuration = 0.045f;
        [SerializeField, Min(0.01f)] private float muzzleFlashDuration = 0.035f;
        [Tooltip("FP 层近段长度：超过此距离的飞行段改由 Default 层世界段接力（跨相机接缝匹配对齐）。")]
        [SerializeField, Min(0.3f)] private float nearSegmentLength = 1.2f;
        [Tooltip("跨相机匹配不可用时的回退：近段最大长度（只影响视觉，不改命中点）。")]
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
        [Tooltip("命中角色（有伤害）反馈，如血花；空则不生成。绝不退回 impactPrefab——墙面弹孔不是角色贴花锚点")]
        [SerializeField] private GameObject damagedImpactPrefab;

        /// <summary>FPWeaponMotion 程序化 ADS 用：当前视图的枪口。</summary>
        public Transform Muzzle => muzzle;

        [Header("Day4.4 ADS 瞄准参考")]
        [Tooltip("ADS 瞄准线参考点（照门/瞄具线上）；空则回退用 Muzzle。物理 Muzzle 位于膛口，与瞄具线有 sight-height 高度差，ADS 必须对齐瞄具线而非膛线。")]
        [SerializeField] private Transform sightReference;
        [Tooltip("LPW 瞄具参考标记。枪模位置由 Prefab 静态校准，运行时不会为 ADS 单独移动枪模。")]
        [SerializeField] private bool alignAdsToSightAxis;

        [Header("调试")]
        [Tooltip("每发输出拖尾诊断日志（origin/firedDirection/selfHitSkip/visualEnd）——排查异常拖尾用，默认关")]
        [SerializeField] private bool debugShotDiagnostics;

        /// <summary>ADS 瞄准线参考点；空表示该武器未单独配置（回退 Muzzle）。</summary>
        public Transform SightReference => sightReference;
        /// <summary>替换枪模是否以显式 SightReference 修正动画底座的瞄准位置。</summary>
        public bool AlignAdsToSightAxis => alignAdsToSightAxis;

        /// <summary>
        /// Editor calibration hook. The override is intentionally runtime-only;
        /// LPWDualLayerCalibrationWindow persists the authored marker into the
        /// formal prefab after the designer confirms the view.
        /// </summary>
        public void OverrideSightReferenceForCalibration(Transform reference)
        {
            sightReference = reference;
        }

        private sealed class TracerSegment
        {
            public LineRenderer Line;
            public float Timer;
        }

        private const int TracerPoolSize = 8;
        private readonly TracerSegment[] _overlayTracers = new TracerSegment[TracerPoolSize]; // FP 层近段
        private readonly TracerSegment[] _worldTracers = new TracerSegment[TracerPoolSize];   // Default 层飞行段
        private Light _muzzleLight;
        private Material _tracerMaterial;
        private float _flashTimer;
        private UnityEngine.Camera _worldCamera;
        private UnityEngine.Camera _fpCamera;
        private bool _camerasResolved;

        // ---- S2（2026-09-19 ADS 审计）：Owner 预测→服务器确认闭环 ----
        // 纯客户端 Owner 时：本地预测表现登记入队；服务器确认（接受→按偏差纠偏弹孔 /
        // 拒绝→撤销弹孔）按 shotRequestId 去重后 FIFO 消费。Host/离线（本地即权威）不启用。
        private PredictedShotRegistry _registry;
        private NetworkCombatAuthority _authority;
        private bool _consumeConfirmations;
        [Tooltip("预测点与权威点偏差超过该值（米）才重定位持久表现（弹孔）")]
        [SerializeField, Min(0.05f)] private float reconcileThresholdMeters = 0.35f;

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
            // S2：仅纯客户端 Owner 消费服务器确认（Host/服务器本地权威无预测分叉；离线无 authority）
            if (_authority == null) _authority = GetComponentInParent<NetworkCombatAuthority>();
            _consumeConfirmations = _authority != null && !_authority.IsServerInitialized;
            if (_consumeConfirmations)
            {
                _registry ??= new PredictedShotRegistry();
                _authority.OnShotConfirmed += HandleShotConfirmed;
            }
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
            if (_authority != null) _authority.OnShotConfirmed -= HandleShotConfirmed;
            _consumeConfirmations = false;
            _registry?.Clear(); // 生命/视图边界：跨生命残留登记一律作废（epoch 双保险）
        }

        private void OnDestroy()
        {
            if (_tracerMaterial != null) Destroy(_tracerMaterial);
        }

        private void Update()
        {
            _flashTimer -= Time.deltaTime;
            TickPool(_overlayTracers);
            TickPool(_worldTracers);
            if (_muzzleLight != null) _muzzleLight.enabled = _flashTimer > 0f;
        }

        private static void TickPool(TracerSegment[] pool)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                var segment = pool[i];
                if (segment == null) continue;
                if (segment.Timer > 0f)
                {
                    segment.Timer -= Time.deltaTime;
                    if (segment.Line != null) segment.Line.enabled = segment.Timer > 0f;
                }
            }
        }

        private void HandleShot(WeaponShot shot)
        {
            ResolveCameras();
            // 每发快照（S3）：起点=开火帧枪口世界位（冻结，不随后坐/枪口动画移动）；
            // 方向/终点=本发权威结算结果。霰弹逐弹丸独立生成，各自收敛到各自的真实终点。
            Vector3 start = muzzle != null ? muzzle.position : shot.Origin;
            Vector3 mainDirection = shot.FiredDirection.sqrMagnitude > 1e-8f
                ? shot.FiredDirection.normalized
                : Vector3.forward;

            bool hasPellets = shot.Pellets != null && shot.Pellets.Length > 1;
            int count = hasPellets ? shot.Pellets.Length : 1;
            for (int i = 0; i < count; i++)
            {
                var result = hasPellets ? shot.Pellets[i] : shot.Result;
                Vector3 delta = result.Point - shot.Origin;
                Vector3 direction = delta.sqrMagnitude > 1e-6f ? delta.normalized : mainDirection;
                SpawnTracer(start, direction, result.Point);
            }

            _muzzleLight.enabled = true;
            _flashTimer = muzzleFlashDuration;

            if (debugShotDiagnostics)
            {
                Debug.Log($"[WeaponView] shot: origin={shot.Origin:F2} firedDir={mainDirection:F3} " +
                          $"hit={shot.Result.Hit} damaged={shot.Result.Damaged} selfSkip={shot.Result.SelfHitsSkipped} " +
                          $"resultPoint={shot.Result.Point:F2} pellets={(hasPellets ? count : 1)}", this);
            }

            SpawnMuzzleFlash();
            SpawnShellCasing();
            GameObject decal = SpawnImpact(shot);
            // S2：预测登记（在 SpawnImpact 拿到持久表现载体后入队，等待服务器确认消费）
            if (_consumeConfirmations && _registry != null)
                _registry.Register(shot.Result.Point, shot.Result.Normal, shot.Result.Hit,
                    _authority != null ? _authority.LifeEpochForPresentation : 0u, decal);
            // 命中标记已迁 CrosshairPresenter（CP5）；本组件只剩武器表现
        }

        /// <summary>服务器确认消费（S2）：去重 → FIFO 消费预测登记 → 接受按偏差纠偏持久表现 /
        /// 拒绝撤销（弹孔不得留存）。曳光寿命（45ms）短于 RTT，确认只纠偏持久表现——
        /// 预测暂态偏差符合票据"延迟允许暂态，确认后不得持续错误落点"。</summary>
        private void HandleShotConfirmed(RemoteShotPresentation shot, bool accepted)
        {
            if (!_consumeConfirmations || _registry == null) return;
            if (shot.ShotRequestId != 0u && _registry.IsDuplicateConfirm(shot.ShotRequestId)) return;
            var entry = _registry.ConsumeOldestPending(_authority != null ? _authority.LifeEpochForPresentation : 0u);
            if (entry == null) return;
            if (entry.Decal == null) return;

            if (!accepted)
            {
                Destroy(entry.Decal); // 拒发：本发未发生，弹孔不得留存
                return;
            }
            if (!shot.FinalHit && entry.Hit)
            {
                Destroy(entry.Decal); // 权威判 miss（本地预测命中被推翻）
                return;
            }
            if (!shot.FinalHit) return;
            float deviationSq = (shot.FinalPoint - entry.PredictedPoint).sqrMagnitude;
            if (deviationSq <= reconcileThresholdMeters * reconcileThresholdMeters) return;
            // 纠偏：父节点（命中角色时）保持，位置/朝向改到权威落点
            entry.Decal.transform.position = shot.FinalPoint + shot.FinalNormal * 0.01f;
            if (shot.FinalNormal.sqrMagnitude > 0.5f)
                entry.Decal.transform.rotation = Quaternion.LookRotation(shot.FinalNormal, Vector3.up);
        }

        /// <summary>本发曳光：FP 近段从冻结枪口沿真实弹道方向发出；终点（必要时经接缝接力）
        /// 跨相机屏幕匹配收敛到真实弹着点在世界相机下的屏幕位置——分划/命中特效/曳光
        /// 在最终合成画面里指向同一点（ADS 审计 A3）。</summary>
        private void SpawnTracer(Vector3 start, Vector3 direction, Vector3 endPoint)
        {
            bool matched = false;
            Vector3 overlayEnd = endPoint;
            if (_worldCamera != null && _fpCamera != null)
            {
                var worldProjection = CameraProjection.From(_worldCamera);
                var fpProjection = CameraProjection.From(_fpCamera);
                if (CameraProjection.TryMatchAcrossCameras(worldProjection, fpProjection, endPoint,
                        0.25f, Mathf.Max(1f, fpProjection.FarClip * 0.9f), out Vector3 matchedEnd))
                {
                    overlayEnd = matchedEnd;
                    matched = true;
                }
            }
            if (!matched)
            {
                // 回退：无世界/FP 相机对（非常规场景）→ 旧限长直线（已冻结，不再逐帧拖动）
                float projected = Vector3.Dot(endPoint - start, direction);
                overlayEnd = start + direction * Mathf.Clamp(projected, 0f, maxTracerLength);
            }

            float hitDistance = Vector3.Distance(start, endPoint);
            float overlaySpan = Vector3.Distance(start, overlayEnd);
            if (hitDistance <= nearSegmentLength || overlaySpan < 0.05f)
            {
                EnableSegment(_overlayTracers, start, overlayEnd);
                return;
            }

            // 近段截断 + 世界段接力：接缝点经 FP→世界跨相机匹配，消除双相机视差断线
            float t = Mathf.Clamp01(nearSegmentLength / Mathf.Max(0.01f, overlaySpan));
            Vector3 seam = Vector3.LerpUnclamped(start, overlayEnd, t);
            EnableSegment(_overlayTracers, start, seam);
            if (_worldCamera != null && _fpCamera != null)
            {
                var fpProjection = CameraProjection.From(_fpCamera);
                var worldProjection = CameraProjection.From(_worldCamera);
                if (CameraProjection.TryMatchAcrossCameras(fpProjection, worldProjection, seam,
                        0.25f, Mathf.Max(1f, worldProjection.FarClip * 0.95f), out Vector3 worldStart))
                {
                    EnableSegment(_worldTracers, worldStart, endPoint);
                }
            }
        }

        private void EnableSegment(TracerSegment[] pool, Vector3 start, Vector3 end)
        {
            var segment = AcquireSegment(pool);
            if (segment == null || segment.Line == null) return;
            segment.Line.SetPosition(0, start);
            segment.Line.SetPosition(1, end);
            segment.Line.enabled = true;
            segment.Timer = tracerDuration;
        }

        private static TracerSegment AcquireSegment(TracerSegment[] pool)
        {
            TracerSegment nearestExpiry = null;
            for (int i = 0; i < pool.Length; i++)
            {
                var candidate = pool[i];
                if (candidate == null) continue;
                if (candidate.Timer <= 0f) return candidate;
                if (nearestExpiry == null || candidate.Timer < nearestExpiry.Timer) nearestExpiry = candidate;
            }
            // 池满：回收剩余寿命最短的一段（高射速连发/霰弹时的有界表现）
            return nearestExpiry;
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

        /// <summary>生成本发命中反馈；返回持久表现载体（弹孔/血花实例，null=未生成）——
        /// S2 预测登记需要它做确认后的纠偏/撤销。</summary>
        private GameObject SpawnImpact(WeaponShot shot)
        {
            if (!shot.Result.Hit) return null;
            // 2026-09-18 审计 §6.3：命中角色与命中环境是两类特效，**绝不互相兜底**。
            // 角色命中点来自受击胶囊代理，不是蒙皮表面——把墙面弹孔贴到角色身上会在空气中
            // 留下实体弹孔（无敌/友军/已死目标 Damaged=false 走弹孔分支就是这个症状）。
            // 因此角色只播短时命中反馈；没配角色反馈就不生成，而不是退回弹孔。
            if (shot.Result.Target != null)
            {
                if (damagedImpactPrefab == null) return null;
                // 挂到目标视觉体下：尸体倒地/移动时命中反馈跟随身体，不悬停在原站立位置
                var characterFx = Instantiate(damagedImpactPrefab, shot.Result.Point + shot.Result.Normal * 0.01f,
                    Quaternion.LookRotation(shot.Result.Normal), shot.Result.Target.transform);
                LogImpactDiagnostics(shot, "character");
                return characterFx;
            }
            if (impactPrefab == null) return null;
            // 环境命中：继续落在真实命中表面（无父节点，世界坐标固定）
            var decal = Instantiate(impactPrefab, shot.Result.Point + shot.Result.Normal * 0.01f,
                Quaternion.LookRotation(shot.Result.Normal), null);
            LogImpactDiagnostics(shot, "environment");
            return decal;
        }

        /// <summary>本地预测特效的落点留证（与服务器权威 [FireTrace] 结算分开记录，审计 §6.3）：
        /// 本视图的特效来自 Owner 本地射线，不能当作服务器掉血证据。</summary>
        private void LogImpactDiagnostics(WeaponShot shot, string surface)
        {
            if (!debugShotDiagnostics) return;
            Debug.Log($"[WeaponView][Predict] surface={surface} damaged={shot.Result.Damaged} " +
                      $"point={shot.Result.Point.ToString("F3")} target=" +
                      (shot.Result.Target != null ? shot.Result.Target.name : "null"), this);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        private void BuildEffects()
        {
            // FP 视觉层：武器/枪口由 overlay 相机（FirstPersonView 层）渲染。近段曳光留在同层，
            // 保证与枪模同投影、起点即可见枪口。远段飞行段放 Default 层：世界相机 mask（剔除 8/9）
            // 与倍率镜 scope mask（同样剔除 8/9/UI）都渲染 Default → 镜内也有弹道，且远端观察者可复用。
            int firstPersonLayer = LayerMask.NameToLayer("FirstPersonView");
            if (firstPersonLayer < 0)
            {
                Debug.LogError("[WeaponView] FirstPersonView layer is missing.", this);
                firstPersonLayer = gameObject.layer;
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                _tracerMaterial = new Material(shader) { color = tracerColor };
            }

            for (int i = 0; i < TracerPoolSize; i++)
                _overlayTracers[i] = CreateSegment("Runtime_Tracer_FP", firstPersonLayer,
                    0.012f, 0.003f);
            for (int i = 0; i < TracerPoolSize; i++)
                _worldTracers[i] = CreateSegment("Runtime_Tracer_World", 0, 0.01f, 0.002f);

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

        private TracerSegment CreateSegment(string name, int layer, float startWidth, float endWidth)
        {
            var segment = new TracerSegment();
            var tracerObject = new GameObject(name);
            tracerObject.transform.SetParent(transform, false);
            tracerObject.layer = layer;
            var line = tracerObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = startWidth;
            line.endWidth = endWidth;
            line.startColor = tracerColor;
            line.endColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0f);
            if (_tracerMaterial != null) line.material = _tracerMaterial;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.enabled = false;
            segment.Line = line;
            return segment;
        }

        /// <summary>世界相机=本视图的祖先相机（Main Camera）；FP overlay 相机=其子相机（FP View Camera）。
        /// 两相机位姿/FOV 是跨投影匹配的输入；任一缺失时曳光退回限长直线（诚实降级）。</summary>
        private void ResolveCameras()
        {
            if (_camerasResolved) return;
            _camerasResolved = true;
            _worldCamera = GetComponentInParent<UnityEngine.Camera>();
            if (_worldCamera != null)
            {
                foreach (var candidate in _worldCamera.GetComponentsInChildren<UnityEngine.Camera>(false))
                {
                    if (candidate != null && candidate != _worldCamera)
                    {
                        _fpCamera = candidate;
                        break;
                    }
                }
            }
        }

                // CP5：准心/命中标记/弹药/操作提示已全部迁出 OnGUI → uGUI MVC
                //（CrosshairPresenter/View + WeaponHudView，Docs/13 检查点 5）。OnGUI 及样式字段删除。
            }
        }
