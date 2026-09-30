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
    [DefaultExecutionOrder(100)] // After gun pose (30), hand IK (40/45), and magazine presentation (50).
    public sealed class WeaponView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform muzzle;
        [Tooltip("Native muzzle FX markers sit ahead of the barrel mesh. Pull only the owner tracer back to the visible bore; leave flash and hit geometry unchanged.")]
        [SerializeField, Min(0f)] private float tracerMuzzleInsetMeters;
        [SerializeField] private bool hasCalibratedBarrelTip;
        [SerializeField] private Vector3 barrelTipMuzzleLocal;
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
            public int PelletIndex;
            public bool Hidden;
        }

        private readonly struct PendingTracer
        {
            public readonly Vector3 FallbackStart;
            public readonly Vector3 EndPoint;
            public readonly uint RequestId;
            public readonly int PelletIndex;

            public PendingTracer(Vector3 fallbackStart, Vector3 endPoint, uint requestId = 0, int pelletIndex = 0)
            {
                FallbackStart = fallbackStart;
                EndPoint = endPoint;
                RequestId = requestId;
                PelletIndex = pelletIndex;
            }
        }

        private const int TracerPoolSize = 32;
        private readonly TracerSegment[] _overlayTracers = new TracerSegment[TracerPoolSize]; // one owner world-space streak per pellet
        private Light _muzzleLight;
        private Material _tracerMaterial;
        private float _flashTimer;
        private UnityEngine.Camera _worldCamera;
        private UnityEngine.Camera _fpCamera;
        private Transform _cachedMuzzleAttachment;
        private Vector3 _cachedAttachmentTipLocal;
        private bool _cachedAttachmentTipValid;
        private Transform _barePresentationMuzzle;
        // OnShotFired 发生在 Update；Main/FP camera、玩家跳跃位移与 FPWeaponMotion 的最终
        // 渲染姿态在 LateUpdate 才稳定。这里只冻结权威终点，不冻结世界坐标枪口；绘制帧末
        // 从当前可见枪口重采样起点，避免跳跃/向前移动后曳光仍从开火前旧位置冒出。
        private readonly List<PendingTracer> _pendingTracers = new(12);
        private readonly TracerVisibility _visibility = new();

        // ---- S2（2026-09-19 ADS 审计）：Owner 预测→服务器确认闭环 ----
        // 纯客户端 Owner 时：本地预测表现登记入队；服务器确认（接受→按偏差纠偏弹孔 /
        // 拒绝→撤销弹孔）按 shotRequestId 去重后 FIFO 消费。Host/离线（本地即权威）不启用。
        private PredictedShotRegistry _registry;
        private NetworkCombatAuthority _authority;
        private bool _consumeConfirmations;

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
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
            if (controller != null)
            {
                controller.OnShotFired -= HandleLocalAuthoritativeShot;
                controller.OnDryFire -= HandleDryFire;
            }
            if (_authority != null)
            {
                _authority.OnShotConfirmed -= HandleShotConfirmed;
                _authority.OnOwnerPredictedShot -= HandleOwnerPredictedShot;
            }
            _consumeConfirmations = false;
            _registry?.Clear(destroyPending: true);
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
            if (_muzzleLight != null) _muzzleLight.enabled = _flashTimer > 0f
                && muzzle != null && muzzle.gameObject.activeInHierarchy;
        }

        private void LateUpdate()
        {
            ResolveCameras();
            if (_muzzleLight != null) _muzzleLight.transform.position = ResolveTracerStart();
            // The short-lived muzzle streak stays attached to the visible barrel, while its
            // impact stays in world space. Reproject each visible frame (jump/recoil included).
            foreach (var segment in _overlayTracers)
                if (segment != null && segment.Timer > 0f && segment.Line != null)
                    UpdateTracerGeometry(segment, ResolveTracerStart());
            foreach (var tracer in _pendingTracers)
            {
                Vector3 start = ResolveTracerStart(tracer.FallbackStart);
                SpawnPelletTracer(start, tracer.EndPoint, tracer.RequestId, tracer.PelletIndex);
            }
            _pendingTracers.Clear();
        }

        private Vector3 ResolveTracerStart()
            => ResolveTracerStart(transform.position);

        private Vector3 ResolveTracerStart(Vector3 fallback)
        {
            Transform exit = ResolvePresentationMuzzle();
            return exit != null ? exit.position : fallback;
        }

        /// <summary>One authored exit for tracer, flash and light. Attachments own their exit frame.</summary>
        internal Transform ResolvePresentationMuzzle()
        {
            if (muzzle == null) return null;
            if (_barePresentationMuzzle == null)
                _barePresentationMuzzle = muzzle.Find("MuzzleExit");
            Vector3 bareBore = hasCalibratedBarrelTip && muzzle != null
                ? muzzle.TransformPoint(barrelTipMuzzleLocal)
                : ResolveVisualTracerStart(muzzle, transform.position, tracerMuzzleInsetMeters);
            if (_barePresentationMuzzle == null)
            {
                _barePresentationMuzzle = new GameObject("Runtime_MuzzleExit").transform;
                _barePresentationMuzzle.SetParent(muzzle, false);
                _barePresentationMuzzle.position = bareBore;
                _barePresentationMuzzle.gameObject.layer = gameObject.layer;
            }
            var attachmentView = GetComponent<WeaponAttachmentView>();
            var socket = attachmentView != null ? attachmentView.GetSocketTransform(AttachmentSlotType.Muzzle) : null;
            Transform attached = null;
            if (socket != null)
                foreach (Transform child in socket)
                    if (child.name.StartsWith("Att_", System.StringComparison.Ordinal) && child.gameObject.activeInHierarchy)
                    { attached = child; break; }
            if (attached == null) return _barePresentationMuzzle;
            var authoredExit = attached.Find("MuzzleExit");
            if (authoredExit != null) return authoredExit;
            if (attached != _cachedMuzzleAttachment)
                CacheAttachmentTip(attached);
            if (!_cachedAttachmentTipValid) return _barePresentationMuzzle;
            Vector3 tip = attached.TransformPoint(_cachedAttachmentTipLocal);
            // A fitted suppressor or brake extends beyond the original animated muzzle marker.
            // Fire from its visible aperture; retain the bare-bore point for cosmetic parts behind it.
            if (Vector3.Dot(tip - bareBore, muzzle.forward) <= .005f) return _barePresentationMuzzle;
            var legacyExit = new GameObject("MuzzleExit").transform;
            legacyExit.SetParent(attached, false);
            legacyExit.SetPositionAndRotation(tip, muzzle.rotation);
            legacyExit.gameObject.layer = gameObject.layer;
            return legacyExit;
        }

        private void CacheAttachmentTip(Transform attached)
        {
            _cachedMuzzleAttachment = attached;
            _cachedAttachmentTipValid = TryGetAttachmentTip(attached, muzzle, out _cachedAttachmentTipLocal);
        }

        internal static bool TryGetAttachmentTip(Transform attached, Transform muzzle, out Vector3 localTip)
        {
            localTip = default;
            if (muzzle == null || attached == null) return false;
            var authored = attached.Find("MuzzleExit");
            if (authored != null)
            {
                localTip = attached.InverseTransformPoint(authored.position);
                return true;
            }
            Vector3 axis = attached.InverseTransformDirection(muzzle.forward).normalized;
            var vertices = new List<Vector3>();
            foreach (var filter in attached.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                if (filter.sharedMesh.isReadable)
                    foreach (var vertex in filter.sharedMesh.vertices)
                        vertices.Add(attached.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
                else
                {
                    var b = filter.sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        vertices.Add(attached.InverseTransformPoint(filter.transform.TransformPoint(corner)));
                    }
                }
            }
            if (vertices.Count == 0) return false;
            float front = float.NegativeInfinity;
            foreach (var vertex in vertices) front = Mathf.Max(front, Vector3.Dot(vertex, axis));
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (var vertex in vertices)
                if (Vector3.Dot(vertex, axis) >= front - .003f) { sum += vertex; count++; }
            if (count == 0) return false;
            localTip = sum / count;
            return true;
        }

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
                    if (segment.Line != null) segment.Line.enabled = segment.Timer > 0f && !segment.Hidden;
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
                if (muzzle != null && muzzle.gameObject.activeInHierarchy)
                    _pendingTracers.Add(new PendingTracer(fallbackStart, result.Point, shotRequestId, i));
            }

            _muzzleLight.enabled = muzzle != null && muzzle.gameObject.activeInHierarchy;
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
                    bool showContact = hit.Target == null || !_consumeConfirmations && hit.DamageAmount > 0;
                    pelletDecals[i] = SpawnImpactAt(hit.Hit && showContact, hit.Target != null,
                        hit.Point, hit.Normal, null);
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
        /// 拒绝撤销（弹孔不得留存）。确认同时修正尚在队列或仍存活的逐弹丸曳光，
        /// 对已经过期的曳光不重播。</summary>
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
            CorrectTracerEndpoints(shot);
            if (entry.PelletDecals != null)
            {
                for (int i = 0; i < entry.PelletDecals.Length; i++)
                {
                    bool valid = shot.PelletPoints != null && shot.PelletHits != null
                        && shot.PelletNormals != null && shot.PelletCharacters != null
                        && i < shot.PelletCount && i < shot.PelletPoints.Length
                        && i < shot.PelletHits.Length && i < shot.PelletNormals.Length
                        && i < shot.PelletCharacters.Length;
                    if (!valid)
                    { DiscardImpact(entry.PelletDecals[i]); continue; }
                    bool damaged = shot.PelletDamageAmounts != null && i < shot.PelletDamageAmounts.Length
                        && shot.PelletDamageAmounts[i] > 0;
                    CorrectConfirmedImpact(entry.PelletDecals[i],
                        entry.PelletCharacters != null && i < entry.PelletCharacters.Length && entry.PelletCharacters[i],
                        shot.PelletHits[i], shot.PelletCharacters[i], damaged,
                        shot.PelletPoints[i], shot.PelletNormals[i]);
                }
                return;
            }
            entry.Decal = CorrectConfirmedImpact(entry.Decal, entry.CharacterHit,
                shot.FinalHit, shot.FinalHitCharacter, shot.DamageAmount > 0,
                shot.FinalPoint, shot.FinalNormal);
        }

        private static void DiscardImpact(GameObject effect)
        {
            if (effect == null) return;
            if (Application.isPlaying) Destroy(effect);
            else DestroyImmediate(effect);
        }

        private GameObject CorrectConfirmedImpact(GameObject effect, bool predictedCharacter,
            bool hit, bool character, bool damaged, Vector3 point, Vector3 normal)
        {
            if (!hit || character || predictedCharacter)
            { DiscardImpact(effect); effect = null; }
            if (!hit || character && !damaged) return null;
            // Blood is emitted only for authoritative HP loss. A short burst stays in world
            // space, so correcting A to B can never leave it parented to predicted victim A.
            if (effect == null) return SpawnImpactAt(true, character, point, normal, null);
            effect.transform.SetParent(null, true);
            effect.transform.position = point + normal * .01f;
            if (normal.sqrMagnitude > .001f) effect.transform.rotation = Quaternion.LookRotation(normal);
            return effect;
        }

        private static bool TryConfirmedEndpoint(RemoteShotPresentation shot, int pellet, out Vector3 end)
        {
            end = shot.FinalPoint;
            if (shot.PelletCount <= 1) return pellet == 0;
            if (shot.PelletPoints == null || pellet >= shot.PelletCount || pellet >= shot.PelletPoints.Length)
                return false;
            end = shot.PelletPoints[pellet];
            return true;
        }

        private void CorrectTracerEndpoints(RemoteShotPresentation shot)
        {
            // ACK can precede the first LateUpdate. Correct queued pellets too; never replay expired ones.
            for (int i = _pendingTracers.Count - 1; i >= 0; i--)
            {
                var pending = _pendingTracers[i];
                if (pending.RequestId != shot.ShotRequestId) continue;
                if (!TryConfirmedEndpoint(shot, pending.PelletIndex, out var end))
                    _pendingTracers.RemoveAt(i);
                else _pendingTracers[i] = new PendingTracer(pending.FallbackStart, end,
                    pending.RequestId, pending.PelletIndex);
            }
            foreach (var segment in _overlayTracers)
            {
                if (segment == null || segment.Timer <= 0f || segment.RequestId != shot.ShotRequestId) continue;
                if (!TryConfirmedEndpoint(shot, segment.PelletIndex, out var end)) segment.Hidden = true;
                else segment.EndPoint = end;
                UpdateTracerGeometry(segment, ResolveTracerStart());
            }
        }

        /// <summary>本发唯一短时曳光；起点在绘制时按可见枪口投影匹配，终点保持本发世界落点。</summary>
        private void SpawnTracer(Vector3 start, Vector3 endPoint, uint requestId)
            => SpawnPelletTracer(start, endPoint, requestId, 0);

        private void SpawnPelletTracer(Vector3 start, Vector3 endPoint, uint requestId, int pelletIndex)
        {
            if (muzzle == null || !muzzle.gameObject.activeInHierarchy) return;
            var segment = AcquireSegment(_overlayTracers);
            if (segment == null || segment.Line == null) return;
            segment.EndPoint = endPoint;
            segment.RequestId = requestId;
            segment.PelletIndex = pelletIndex;
            segment.Hidden = false;
            segment.Timer = tracerDuration;
            segment.Line.enabled = true;
            UpdateTracerGeometry(segment, start);
        }

        private void UpdateTracerGeometry(TracerSegment segment, Vector3 start)
        {
            // FPWeaponRig assigns its view layer recursively after Instantiate/Awake.
            // The tracer pool already exists by then, so its world layer is overwritten.
            // These vertices are projected for the world camera: restore ownership of
            // their layer before every draw, including cached views and attachment swaps.
            segment.Line.gameObject.layer = 0;
            if (segment.Hidden || muzzle == null || !muzzle.gameObject.activeInHierarchy)
            {
                segment.Hidden = true;
                segment.Line.enabled = false;
                return;
            }
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

            // Check the projected world start against the full result, not the shortened fallback streak.
            segment.Hidden = !_visibility.CanShow(worldStart, segment.EndPoint,
                controller != null ? controller.transform.root : transform.root,
                controller != null ? controller.ShotCollisionMask : Physics.DefaultRaycastLayers);
            segment.Line.enabled = !segment.Hidden && segment.Timer > 0f;
            if (segment.Hidden) return;
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
            var exit = ResolvePresentationMuzzle();
            if (exit == null) return;
            var flash = Instantiate(muzzleFlashPrefab, exit.position, exit.rotation, exit);
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
            if (Application.isPlaying) Destroy(flash, muzzleFlashDespawnSeconds);
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
                if (_consumeConfirmations || shot.Result.DamageAmount <= 0) return null;
                if (damagedImpactPrefab == null) return null;
                // Only a brief blood burst: target is the gameplay root, not an animated bone.
                // Persistent wound decals cannot be attached here (they float when the body falls).
                var characterFx = Instantiate(damagedImpactPrefab, shot.Result.Point + shot.Result.Normal * 0.01f,
                    Quaternion.LookRotation(shot.Result.Normal), null);
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
            lightObject.transform.SetParent(muzzle != null ? muzzle : transform, false);
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
