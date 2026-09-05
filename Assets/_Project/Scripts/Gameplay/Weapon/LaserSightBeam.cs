using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 激光指示器光束（2026-09-05 用户需求）：腰射状态（Ads01 &lt; 0.5）下从激光器发出红色射线，
    /// 指向准星 HUD 中心对应的世界点（主相机屏幕中心射线命中点；无命中则延伸固定距离），
    /// 与准星/弹道同一套屏幕中心语义（CrosshairPresenter 同源 Camera.main）。
    /// 挂载：仅本地第一人称视图的激光配件克隆——WeaponAttachmentView 按 entry.isLaserEmitter
    /// && laserBeamEnabled 挂载；TP 视图/枪匠预览/编辑器校准窗传 false（射线只服务本地瞄准，
    /// 远端玩家不可见）。精度 buff = 配件既有 Spread ×0.85 修饰符（WeaponStatResolver →
    /// WeaponAccuracyState.CurrentSpread → 弹道锥与准星 Gap 同源），本组件只做视觉。
    /// </summary>
    public sealed class LaserSightBeam : MonoBehaviour
    {
        /// <summary>光束最大延伸距离（米）：准星中心无命中时打到哪。</summary>
        public const float BeamRange = 250f;
        /// <summary>腰射判定阈值：Ads01 低于此值视为腰射（与 CrosshairPresenter 显隐阈值一致）。</summary>
        public const float AimGateAds01 = 0.5f;

        private LineRenderer _line;
        private PlayerAimState _aim;
        private Transform _playerRoot;
        private UnityEngine.Camera _cam;
        private Vector3 _localCenter;
        private bool _centerResolved;
        private readonly RaycastHit[] _hits = new RaycastHit[16];

        /// <summary>腰射判定（纯函数）：Ads01 低于阈值即出束（开镜过渡过半即收束）。</summary>
        public static bool ShouldBeam(float ads01) => ads01 < AimGateAds01;

        private void OnEnable() => Setup();

        private void OnDisable()
        {
            if (_line != null) _line.enabled = false;
        }

        /// <summary>显式初始化（幂等）：构建 LineRenderer 并重置起点缓存。
        /// 挂载路径（ApplyAttachments）与 OnEnable 都会调用——EditMode 下普通组件
        /// 不触发 OnEnable，挂载即显式初始化保证结构可测、运行时状态一致。</summary>
        public void Setup()
        {
            if (_line == null) _line = BuildLine();
            _line.enabled = false; // 首帧 LateUpdate 定位后再显示，避免原点闪烁
            _centerResolved = false;
        }

        private LineRenderer BuildLine()
        {
            var line = gameObject.GetComponent<LineRenderer>();
            if (line == null) line = gameObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.startWidth = 0.02f;
            line.endWidth = 0.004f;
            line.startColor = new Color(1f, 0.15f, 0.15f, 0.95f);
            line.endColor = new Color(1f, 0.25f, 0.25f, 0.35f);
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            // 优先 Sprites/Default（无光照顶点色，URP 下可直接渲染线）；缺失回退 URP Unlit
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null) line.material = new Material(shader);
            return line;
        }

        private void LateUpdate()
        {
            if (_line == null) return;
            if (_aim == null)
            {
                _aim = GetComponentInParent<PlayerAimState>();
                _playerRoot = _aim != null ? _aim.transform : null;
            }
            if (_cam == null) _cam = UnityEngine.Camera.main;
            if (_cam == null || !ShouldBeam(_aim != null ? _aim.Ads01 : 0f))
            {
                _line.enabled = false;
                return;
            }

            Vector3 origin = ResolveOrigin();
            Vector3 endpoint = ResolveAimPoint(origin);
            _line.SetPosition(0, origin);
            _line.SetPosition(1, endpoint);
            _line.enabled = true;
        }

        /// <summary>光束起点：激光模型渲染包围盒中心（本地系缓存一次，随枪口摆动实时变换）。</summary>
        private Vector3 ResolveOrigin()
        {
            if (!_centerResolved)
            {
                _centerResolved = true;
                var renderers = GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var center = Vector3.zero;
                    foreach (var r in renderers)
                        center += r.bounds.center;
                    center /= renderers.Length;
                    _localCenter = transform.InverseTransformPoint(center);
                }
            }
            return transform.TransformPoint(_localCenter);
        }

        /// <summary>准星中心的世界点：主相机屏幕中心射线取首个非自身命中；无命中延伸 BeamRange。</summary>
        private Vector3 ResolveAimPoint(Vector3 origin)
        {
            var ray = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            int count = Physics.RaycastNonAlloc(ray, _hits, BeamRange);
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (_playerRoot != null && hit.collider.transform.IsChildOf(_playerRoot))
                    continue; // 跳过自身（枪体/角色碰撞体），取准星真正指向的世界点
                return hit.point;
            }
            return ray.origin + ray.direction * BeamRange;
        }
    }
}
