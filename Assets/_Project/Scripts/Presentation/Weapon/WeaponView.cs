using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>WeaponController 事件的只读表现端：弹道、枪口光、Day4 枪口特效/弹壳/命中反馈、Day2 调试 HUD。
    /// Owner 的短时枪口曳光只有一个写入者：LateUpdate 用当前可见枪口与本发世界落点
    /// 构造一条世界直线，起点经 FP→世界屏幕匹配。不拼接两种相机的线段，不等 RTT 后重放旧枪声。</summary>
    [DefaultExecutionOrder(30)]
    public sealed class WeaponView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform muzzle;
        [Tooltip("Native muzzle FX markers sit ahead of the barrel mesh. Pull only the owner tracer back to the visible bore; leave flash and hit geometry unchanged.")]
        [SerializeField, Min(0f)] private float tracerMuzzleInsetMeters;
        [SerializeField] private Color tracerColor = new(1f, 0.78f, 0.15f, 1f);
        [SerializeField, Min(0.01f)] private float tracerDuration = 0.045f;
        [SerializeField, Min(0.01f)] private float muzzleFlashDuration = 0.035f;
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
            public Vector3 EndPoint;
            public uint RequestId;
        }

        private readonly struct PendingTracer
        {
            public readonly Vector3 FallbackStart;
            public readonly Vector3 EndPoint;
            public readonly uint RequestId;

            public PendingTracer(Vector3 fallbackStart, Vector3 endPoint, uint requestId = 0)
            {
                FallbackStart = fallbackStart;
                EndPoint = endPoint;
                RequestId = requestId;
            }
        }

        private const int TracerPoolSize = 32;
        private readonly TracerSegment[] _overlayTracers = new TracerSegment[TracerPoolSize]; // one owner world-space streak per pellet
        private Light _muzzleLight;
        private Material _tracerMaterial;
        private float _flashTimer;
        private UnityEngine.Camera _worldCamera;
        private UnityEngine.Camera _fpCamera;
        // OnShotFired 发生在 Update；Main/FP camera、玩家跳跃位移与 FPWeaponMotion 的最终
        // 渲染姿态在 LateUpdate 才稳定。这里只冻结权威终点，不冻结世界坐标枪口；绘制帧末
        // 从当前可见枪口重采样起点，避免跳跃/向前移动后曳光仍从开火前旧位置冒出。
        private readonly List<PendingTracer> _pendingTracers = new(12);

        // ---- S2（2026-09-19 ADS 审计）：Owner 预测→服务器确认闭环 ----
        // 纯客户端 Owner 时：本地预测表现登记入队；服务器确认（接受→按偏差纠偏弹孔 /
        // 拒绝→撤销弹孔）按 shotRequestId 去重后 FIFO 消费。Host/离线（本地即权威）不启用。
        private PredictedShotRegistry _registry;
        private NetworkCombatAuthority _authority;
        private bool _consumeConfirmations;

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (muzzle == null) muzzle = transform;
            BuildEffects();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnDryFire += HandleDryFire;
            if (_authority == null) _authority = GetComponentInParent<NetworkCombatAuthority>();
            _registry ??= new PredictedShotRegistry();
            // OnEnable can precede FishNet's owner initialization. Bind both event sources,
            // then select exactly one using live ownership at the shot boundary.
            controller.OnShotFired += HandleLocalAuthoritativeShot;
            if (_authority != null)
            {
                _authority.OnShotConfirmed += HandleShotConfirmed;
                // 成功预测与 requestId 必须在 NCA 内原子配对后才到这里；不能再通过
                // WeaponController 事件顺序/FIFO 推断确认对应哪一发。
                _authority.OnOwnerPredictedShot += HandleOwnerPredictedShot;
            }
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnShotFired -= HandleLocalAuthoritativeShot;
            controller.OnDryFire -= HandleDryFire;
            if (_authority != null)
            {
                _authority.OnShotConfirmed -= HandleShotConfirmed;
                _authority.OnOwnerPredictedShot -= HandleOwnerPredictedShot;
            }
            _consumeConfirmations = false;
            _registry?.Clear(); // 生命/视图边界：跨生命残留登记一律作废（epoch 双保险）
            _pendingTracers.Clear();
            foreach (var segment in _overlayTracers)
                if (segment != null)
                { segment.Timer = 0f; if (segment.Line != null) segment.Line.enabled = false; }
        }

        private void OnDestroy()
        {
            if (_tracerMaterial != null) Destroy(_tracerMaterial);
        }

        private void Update()
        {
            _flashTimer -= Time.deltaTime;
            TickPool(_overlayTracers);
            if (_muzzleLight != null) _muzzleLight.enabled = _flashTimer > 0f;
        }

        private void LateUpdate()
        {
            ResolveCameras();
            // The short-lived muzzle streak stays attached to the visible barrel, while its
            // impact stays in world space. Reproject each visible frame (jump/recoil included).
            foreach (var segment in _overlayTracers)
                if (segment != null && segment.Timer > 0f && segment.Line != null)
                    UpdateTracerGeometry(segment, ResolveTracerStart());
            foreach (var tracer in _pendingTracers)
            {
                Vector3 start = ResolveVisualTracerStart(muzzle, tracer.FallbackStart, tracerMuzzleInsetMeters);
                SpawnTracer(start, tracer.EndPoint, tracer.RequestId);
            }
            _pendingTracers.Clear();
        }

        private Vector3 ResolveTracerStart()
            => ResolveVisualTracerStart(muzzle, transform.position, tracerMuzzleInsetMeters);

        internal static Vector3 ResolveVisualTracerStart(Transform currentMuzzle, Vector3 fallback,
            float insetMeters = 0f)
            => currentMuzzle != null
                ? currentMuzzle.position - currentMuzzle.forward * Mathf.Max(0f, insetMeters)
                : fallback;

        internal static Vector3 ResolveVisualTracerDirection(Vector3 start, Vector3 end)
        {
            Vector3 delta = end - start;
            return delta.sqrMagnitude > 1e-8f ? delta.normalized : Vector3.forward;
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

        private void HandleLocalAuthoritativeShot(WeaponShot shot)
        {
            if (FishNetLifecycleGuard.CanSubmitRpc(_authority) && _authority.IsOwnerPlayer && !_authority.IsServerInitialized) return;
            _consumeConfirmations = false;
            HandleShot(shot, 0u);
        }

        private void HandleOwnerPredictedShot(WeaponShot shot, uint shotRequestId)
        {
            _consumeConfirmations = true;
            HandleShot(shot, shotRequestId);
        }

        private void HandleShot(WeaponShot shot, uint shotRequestId)
        {
            ResolveCameras();
            // 每发只冻结权威终点；可见枪口在 LateUpdate 绘制时重采样。霰弹逐弹丸独立生成，
            // 各自收敛到各自的真实终点。
            Vector3 fallbackStart = shot.Origin;
            Vector3 mainDirection = shot.FiredDirection.sqrMagnitude > 1e-8f
                ? shot.FiredDirection.normalized
                : Vector3.forward;

            bool hasPellets = shot.Pellets != null && shot.Pellets.Length > 1;
            int count = hasPellets ? shot.Pellets.Length : 1;
            // Render immediately from the same deterministic per-shot sample. Waiting one RTT
            // and drawing an old shot against a newer crosshair made recoil look like downward spread.
            for (int i = 0; i < count; i++)
            {
                var result = hasPellets ? shot.Pellets[i] : shot.Result;
                _pendingTracers.Add(new PendingTracer(fallbackStart, result.Point, shotRequestId));
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
            GameObject[] pelletDecals = null;
            GameObject decal = null;
            if (hasPellets)
            {
                pelletDecals = new GameObject[count];
                for (int i = 0; i < count; i++)
                {
                    var hit = shot.Pellets[i];
                    pelletDecals[i] = SpawnImpactAt(hit.Hit, hit.Target != null, hit.Point, hit.Normal,
                        hit.Target != null ? hit.Target.transform : null);
                }
            }
            else decal = SpawnImpact(shot);
            // S2：预测登记（在 SpawnImpact 拿到持久表现载体后入队，等待服务器确认消费）
            if (_consumeConfirmations && _registry != null && shotRequestId != 0u)
            {
                var entry = _registry.Register(shotRequestId, shot.Result.Point, shot.Result.Normal, shot.Result.Hit,
                    _authority != null ? _authority.LifeEpochForPresentation : 0u, decal);
                entry.CharacterHit = shot.Result.Target != null;
                entry.PelletDecals = pelletDecals;
                if (hasPellets)
                {
                    entry.PelletCharacters = new bool[count];
                    for (int i = 0; i < count; i++) entry.PelletCharacters[i] = shot.Pellets[i].Target != null;
                }
            }
            // 命中标记已迁 CrosshairPresenter（CP5）；本组件只剩武器表现
        }

        /// <summary>服务器确认消费（S2）：去重 → FIFO 消费预测登记 → 接受按偏差纠偏持久表现 /
        /// 拒绝撤销（弹孔不得留存）。曳光寿命（45ms）短于 RTT，确认只纠偏持久表现——
        /// 预测暂态偏差符合票据"延迟允许暂态，确认后不得持续错误落点"。</summary>
        private void HandleShotConfirmed(RemoteShotPresentation shot, bool accepted)
        {
            if (!_consumeConfirmations || _registry == null) return;
            if (shot.ShotRequestId != 0u && _registry.IsDuplicateConfirm(shot.ShotRequestId)) return;
            uint shotRequestId = shot.ShotRequestId;
            var entry = _registry.ConsumePending(shotRequestId,
                _authority != null ? _authority.LifeEpochForPresentation : 0u);
            if (entry == null)
            {
                return;
            }

            if (!accepted)
            {
                if (entry.PelletDecals != null)
                    foreach (var fx in entry.PelletDecals) if (fx != null) Destroy(fx);
                if (entry.Decal != null) Destroy(entry.Decal); // 拒发：本发未发生，弹孔不得留存
                _pendingTracers.RemoveAll(tracer => tracer.RequestId == shotRequestId);
                foreach (var segment in _overlayTracers)
                    if (segment != null && segment.RequestId == shotRequestId)
                    { segment.Timer = 0f; if (segment.Line != null) segment.Line.enabled = false; }
                return;
            }
            if (entry.PelletDecals != null)
            {
                for (int i = 0; i < entry.PelletDecals.Length; i++)
                {
                    var fx = entry.PelletDecals[i];
                    if (shot.PelletPoints == null || shot.PelletHits == null || shot.PelletNormals == null
                        || shot.PelletCharacters == null || i >= shot.PelletCount || i >= shot.PelletPoints.Length
                        || i >= shot.PelletHits.Length || i >= shot.PelletNormals.Length || i >= shot.PelletCharacters.Length)
                    { if (fx != null) Destroy(fx); continue; }
                    if (!shot.PelletHits[i]) { if (fx != null) Destroy(fx); continue; }
                    if (fx != null && entry.PelletCharacters != null && entry.PelletCharacters[i] == shot.PelletCharacters[i])
                    {
                        fx.transform.position = shot.PelletPoints[i] + shot.PelletNormals[i] * .01f;
                        if (shot.PelletNormals[i].sqrMagnitude > .001f)
                            fx.transform.rotation = Quaternion.LookRotation(shot.PelletNormals[i]);
                    }
                    else
                    {
                        if (fx != null) Destroy(fx);
                        SpawnImpactAt(true, shot.PelletCharacters[i], shot.PelletPoints[i], shot.PelletNormals[i], null);
                    }
                }
                return;
            }
            if (!shot.FinalHit && entry.Hit && entry.Decal != null)
            {
                Destroy(entry.Decal); // 权威判 miss（本地预测命中被推翻）
            }
            if (shot.FinalHit && entry.CharacterHit != shot.FinalHitCharacter)
            {
                if (entry.Decal != null) Destroy(entry.Decal);
                entry.Decal = null;
            }
            if (shot.FinalHit && !shot.FinalHitCharacter && entry.Decal == null && impactPrefab != null)
                entry.Decal = Instantiate(impactPrefab, shot.FinalPoint + shot.FinalNormal * .01f,
                    Quaternion.LookRotation(shot.FinalNormal), null);
            if (shot.FinalHit && entry.Decal != null)
            {
                float deviationSq = (shot.FinalPoint - entry.PredictedPoint).sqrMagnitude;
                if (deviationSq > 0.000001f)
                {
                    // 纠偏：父节点（命中角色时）保持，位置/朝向改到权威落点
                    entry.Decal.transform.position = shot.FinalPoint + shot.FinalNormal * 0.01f;
                    if (shot.FinalNormal.sqrMagnitude > 0.5f)
                        entry.Decal.transform.rotation = Quaternion.LookRotation(shot.FinalNormal, Vector3.up);
                }
            }

            // 同 id 的唯一 Owner 曳光严格消费服务器最终点；RemoteShotFxView 会过滤 Owner，
            // 因此不会和观察者世界段双画。
            if (shot.PelletCount <= 1)
            {
                foreach (var segment in _overlayTracers)
                    if (segment != null && segment.Timer > 0f && segment.RequestId == shotRequestId)
                        segment.EndPoint = shot.FinalPoint;
            }
        }

        /// <summary>本发唯一短时曳光；起点在绘制时按可见枪口投影匹配，终点保持本发世界落点。</summary>
        private void SpawnTracer(Vector3 start, Vector3 endPoint, uint requestId)
        {
            var segment = AcquireSegment(_overlayTracers);
            if (segment == null || segment.Line == null) return;
            segment.EndPoint = endPoint;
            segment.RequestId = requestId;
            segment.Timer = tracerDuration;
            segment.Line.enabled = true;
            UpdateTracerGeometry(segment, start);
        }

        private void UpdateTracerGeometry(TracerSegment segment, Vector3 start)
        {
            Vector3 endPoint = segment.EndPoint;
            Vector3 worldStart = start;
            if (_worldCamera != null && _fpCamera != null)
            {
                var worldProjection = CameraProjection.From(_worldCamera);
                var fpProjection = CameraProjection.From(_fpCamera);
                if (CameraProjection.TryMatchAcrossCameras(fpProjection, worldProjection, start,
                        Mathf.Max(.02f, worldProjection.NearClip + .001f), worldProjection.FarClip * .9f,
                        out Vector3 matchedStart)) worldStart = matchedStart;
            }
            else
            {
                Vector3 direction = ResolveVisualTracerDirection(start, endPoint);
                float projected = Vector3.Dot(endPoint - start, direction);
                endPoint = start + direction * Mathf.Clamp(projected, 0f, maxTracerLength);
            }

            // One line on Default: real world endpoint, screen-matched cosmetic muzzle.
            // World depth testing and magnified scope RT still see it; there is no seam.
            segment.Line.SetPosition(0, worldStart);
            segment.Line.SetPosition(1, endPoint);
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
                // Only a brief blood burst: target is the gameplay root, not an animated bone.
                // Persistent wound decals cannot be attached here (they float when the body falls).
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

        private GameObject SpawnImpactAt(bool hit, bool character, Vector3 point, Vector3 normal, Transform parent)
        {
            var prefab = character ? damagedImpactPrefab : impactPrefab;
            if (!hit || prefab == null) return null;
            return Instantiate(prefab, point + normal * .01f,
                normal.sqrMagnitude > .001f ? Quaternion.LookRotation(normal) : Quaternion.identity, parent);
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
            // Flash remains on FP; the single tracer lives on Default for world occlusion / scope RT.
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
                _overlayTracers[i] = CreateSegment("Runtime_Tracer_Owner", 0,
                    0.012f, 0.003f);

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
            if (_worldCamera != null && _fpCamera != null && _worldCamera.isActiveAndEnabled
                && _fpCamera.isActiveAndEnabled) return;
            _worldCamera = null;
            _fpCamera = null;
            // Never interpret an arbitrary ancestor/first child camera as world/FP.
            int fpLayer = LayerMask.NameToLayer("FirstPersonView");
            int fpBit = fpLayer >= 0 ? 1 << fpLayer : 0;
            for (var node = transform.parent; node != null; node = node.parent)
            {
                var candidate = node.GetComponent<UnityEngine.Camera>();
                if (candidate != null && candidate.targetTexture == null && (candidate.cullingMask & fpBit) == 0)
                { _worldCamera = candidate; break; }
            }
            if (_worldCamera != null)
            {
                foreach (var candidate in _worldCamera.GetComponentsInChildren<UnityEngine.Camera>(false))
                {
                    if (candidate != null && candidate != _worldCamera && candidate.targetTexture == null
                        && (candidate.cullingMask & fpBit) != 0)
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
