using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 激光指示器光束（2026-09-05 用户需求）：腰射状态（Ads01 &lt; 0.5）下从激光器发出红色射线，
    /// 指向准星 HUD 中心对应的世界点（主相机屏幕中心射线命中点；无命中则延伸固定距离），
    /// 与准星/弹道同一套屏幕中心语义（CrosshairPresenter 同源 Camera.main）。
    /// 渲染关键（双相机架构修正）：LineRenderer 放在独立子节点并强制 Default 层（世界相机渲染）——
    /// 配件克隆本体在 FirstPersonView 层（仅武器相机渲染、窄 FOV），光束若随克隆层会被武器相机
    /// 投影出与准星脱节的视差（实测截图问题）；Default 层由世界相机渲染，终点精确收敛准星中心，
    /// 近原点段被第一人称枪模覆盖属正常（激光器本体由武器相机叠加绘制）。
    /// 挂载：仅本地第一人称视图的激光配件克隆——WeaponAttachmentView 按 LaserItemId &&
    /// laserBeamEnabled 挂载；TP 视图/枪匠预览/校准窗传 false。远端客户端本地视图整树停用，
    /// 该束不跨端可见。精度 buff = 配件既有 Spread ×0.85 修饰符（弹道锥与准星 Gap 同源）。
    /// </summary>
    public sealed class LaserSightBeam : MonoBehaviour
    {
        /// <summary>光束最大延伸距离（米）：准星中心无命中时打到哪。</summary>
        public const float BeamRange = 250f;
        /// <summary>腰射判定阈值：Ads01 低于此值视为腰射（与 CrosshairPresenter 显隐阈值一致）。</summary>
        public const float AimGateAds01 = 0.5f;
        /// <summary>光束节点层：Default(0)=世界相机可见（层 9 会被武器相机窄 FOV 投影出视差）。</summary>
        public const int BeamLayer = 0;

        private LineRenderer _line;
        private GameObject _lineNode;
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

        /// <summary>显式初始化（幂等）：构建独立光束子节点并重置起点缓存。
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
            // 独立子节点：①层可单独固定 Default（克隆根会被装备层的递归改层覆盖到 FirstPersonView）；
            // ②包围盒起点计算只认枪模网格，LineRenderer 自身的空 bounds 不参与
            if (_lineNode == null)
            {
                _lineNode = new GameObject("LaserBeamLine");
                _lineNode.transform.SetParent(transform, false);
            }
            _lineNode.layer = BeamLayer;
            var line = _lineNode.GetComponent<LineRenderer>();
            if (line == null) line = _lineNode.AddComponent<LineRenderer>();
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
            if (_lineNode.layer != BeamLayer) _lineNode.layer = BeamLayer; // 装备层递归改层后的每帧自愈
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
            Vector3 endpoint = ResolveAimPoint();
            _line.SetPosition(0, origin);
            _line.SetPosition(1, endpoint);
            _line.enabled = true;
        }

        /// <summary>光束起点：激光器模型网格包围盒中心（本地系缓存一次，随枪口摆动实时变换）。
        /// 只认 Mesh/Skinned 网格——LineRenderer 的空 bounds 在世界原点，混入会把起点拉离枪身（实测事故）。</summary>
        private Vector3 ResolveOrigin()
        {
            if (!_centerResolved)
            {
                _centerResolved = true;
                var renderers = GetComponentsInChildren<MeshRenderer>();
                var skinned = GetComponentsInChildren<SkinnedMeshRenderer>();
                if (renderers.Length + skinned.Length > 0)
                {
                    var center = Vector3.zero;
                    int total = 0;
                    foreach (var r in renderers) { center += r.bounds.center; total++; }
                    foreach (var r in skinned) { center += r.bounds.center; total++; }
                    center /= total;
                    _localCenter = transform.InverseTransformPoint(center);
                }
            }
            return transform.TransformPoint(_localCenter);
        }

        /// <summary>准星中心的世界点：主相机屏幕中心射线取首个非自身命中；无命中延伸 BeamRange。</summary>
        private Vector3 ResolveAimPoint()
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
