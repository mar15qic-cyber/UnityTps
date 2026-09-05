using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 激光指示器光束（2026-09-05 用户需求 + 语义修正）：腰射状态（Ads01 &lt; 0.5）下从激光器
    /// 沿【器件轴向】（挂点 -X 前向，即枪身实际指向）发出恒定细红光，命中世界点即止。
    /// 指向语义（用户二次定案，参照真实 FPS）：光束永远跟随枪身指向——换弹/切枪枪身倾斜时光束
    /// 随之偏转，绝不"吸附准星 HUD"（背包现实感红线：任何 FPS 都不会让换弹中的激光仍指准星）。
    /// 宽度恒定（不随命中距离/旋转变化——锥形随距离展宽会在旋转时忽粗忽细，实测截图否定）。
    /// 渲染关键（双相机架构，用户截图实证）：光束与激光器器件必须在【同一台相机】的投影里——
    /// 器件由 overlay 相机（FirstPersonView 层）渲染，光束跟随配件克隆层由同一台 overlay 相机
    /// 绘制，起点天然贴合器件；瞄准射线也从该相机屏幕中心打出（与主相机同轴随动），终点收敛准星。
    /// 束形（用户定案）：宽到细——激光器端宽、命中点端细，向准星命中点汇聚成锥。
    /// 早期版本把光束放 Default 层交世界相机渲染：器件（武器相机）与光束（世界相机）两套投影
    /// 拼接产生视差——起点脱开器件、远端糊成圆斑（Scene 视图单相机看是正的，Play 视角错，实测截图）。
    /// 挂载：仅本地第一人称视图的激光配件克隆（WeaponAttachmentView 按 LaserItemId &&
    /// laserBeamEnabled 挂载）；TP 视图/枪匠预览/校准窗传 false。远端客户端本地视图整树停用不可见。
    /// 精度 buff = 配件既有 Spread ×0.85 修饰符（弹道锥与准星 Gap 同源），本组件只做视觉。
    /// </summary>
    public sealed class LaserSightBeam : MonoBehaviour
    {
        /// <summary>光束最大延伸距离（米）：准星中心无命中时打到哪。</summary>
        public const float BeamRange = 250f;
        /// <summary>腰射判定阈值：Ads01 低于此值视为腰射（与 CrosshairPresenter 显隐阈值一致）。</summary>
        public const float AimGateAds01 = 0.5f;
        /// <summary>光束恒定宽度（米）：物理激光视觉，恒定不随距离/旋转变化（实测锥形随距离展宽
        /// 会让旋转时粗细呼吸，用户否定）。</summary>
        public const float BeamWidth = 0.008f;

        private LineRenderer _line;
        private GameObject _lineNode;
        private PlayerAimState _aim;
        private Transform _playerRoot;
        private Vector3 _localCenter;
        private bool _centerResolved;
        private readonly RaycastHit[] _hits = new RaycastHit[16];

        /// <summary>腰射判定（纯函数）：Ads01 低于阈值即出束（开镜过渡过半即收束）。</summary>
        public static bool ShouldBeam(float ads01) => ads01 < AimGateAds01;

        /// <summary>器件发射方向（纯函数）：挂点局部 -X = 全局枪口前向约定（AttachmentSocket），
        /// 克隆的父节点即挂点——换弹/切枪枪身倾斜时光束随之偏转（真实 FPS 语义）。</summary>
        public static Vector3 DeviceAxis(Transform attachmentClone)
            => attachmentClone != null && attachmentClone.parent != null
                ? -attachmentClone.parent.right
                : Vector3.forward;

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
            // 独立子节点：层随配件克隆（与器件同相机渲染），包围盒起点计算只认枪模网格
            if (_lineNode == null)
            {
                _lineNode = new GameObject("LaserBeamLine");
                _lineNode.transform.SetParent(transform, false);
            }
            _lineNode.layer = gameObject.layer;
            var line = _lineNode.GetComponent<LineRenderer>();
            if (line == null) line = _lineNode.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.startWidth = BeamWidth;
            line.endWidth = BeamWidth;
            line.startColor = new Color(1f, 0.12f, 0.12f, 0.9f);
            line.endColor = new Color(1f, 0.20f, 0.20f, 0.55f);
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
            if (_lineNode.layer != gameObject.layer) _lineNode.layer = gameObject.layer; // 装备层递归改层后同步
            if (_aim == null)
            {
                _aim = GetComponentInParent<PlayerAimState>();
                _playerRoot = _aim != null ? _aim.transform : null;
            }
            if (!ShouldBeam(_aim != null ? _aim.Ads01 : 0f))
            {
                _line.enabled = false;
                return;
            }

            Vector3 origin = ResolveOrigin();
            Vector3 endpoint = ResolveAimPoint(origin, out _);
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

        /// <summary>沿器件轴向的世界命中点：从激光器原点沿挂点 -X 前向射线，取首个非自身命中；
        /// 无命中延伸 BeamRange。渲染由克隆层驱动（overlay 相机经 cullingMask 自动绘制），
        /// 指向永远跟枪身（换弹倾斜时光束随枪走，用户定案的真实 FPS 语义）。</summary>
        private Vector3 ResolveAimPoint(Vector3 origin, out float distance)
        {
            var dir = DeviceAxis(transform);
            var ray = new Ray(origin, dir);
            int count = Physics.RaycastNonAlloc(ray, _hits, BeamRange);
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (_playerRoot != null && hit.collider.transform.IsChildOf(_playerRoot))
                    continue; // 跳过自身（枪体/角色碰撞体）
                distance = Vector3.Distance(origin, hit.point);
                return hit.point;
            }
            distance = BeamRange;
            return origin + dir * BeamRange;
        }
    }
}
