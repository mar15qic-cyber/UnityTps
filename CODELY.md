

## Codely Structured Memories

### User

### Feedback

### Project
- [2026-08-28 19:12:56] LowPolyWeapons(LPW) 替换 LPFP FP 手中枪械审计结论（2026-08-28）：可行但非整 prefab 直换——LPFP 动画 clip 固定驱动 30 骨骼+4 武器部件节点（Armature/weapon、weapon/slider、mag、knife，各 10 曲线），枪身蒙皮权重 weapon 77.8%/mag 18.8%/slider 3.3%；10 把武器 FBX 骨架根节点同名（10 个根节点）。正确模式：手臂骨架+动画集按武器族复用为底座，LPW 刚体枪身挂 Armature/weapon（内置旋转 56.7°/180°/180° 需校准）、弹匣部件挂 mag、枪机部件挂 slider；LPW 全部 ~280 prefab 零挂点（无 Muzzle/Sight 标记），尺寸与 LPFP 同为米制（步枪 0.96m vs 0.93m，比例≈1），材质同为 URP/Lit；整枪 prefab 均为枪身+2-3 独立部件网格硬编码偏移拼装（部件名 _2/_3/_4 无语义需逐枪识别）。配件改装复用 = 通用配件池(消音12+瞄具8)+每组合校准数据（对齐 Docs/15 CalibrationKey/IsImplemented）。Why: 用户为 Docs/15 商城/配件系统做素材可行性预审。How to apply: 后续给 LPW 枪做 FP 集成时按此 SOP；首把约 1 天（含校准工具），后续每把 1-2h。
- [2026-08-28 21:22:56] LPW 武器接入 LPFP FP/TP 动画管线 spike 已完成并全绿（2026-08-28）：3 把测试枪（AssaultRifle2_02/SMG1_01/Pistol5_06）经"复制 LPFP 手臂视图底座 + LPW 枪挂 Armature/weapon wrapper(0,90,0) + 弹匣部件挂 Armature/mag + Muzzle/ShellPort/SightReference 重建 + 原 LPFP 枪械蒙皮禁用"流程构建完毕。产物在 Assets/_Project/Prefabs|ScriptableObjects/Weapons/LPWTest/ + Arena_LPWTest.unity（Arsenal 场景 override 3 槽）+ Editor/LPWTestViewValidator.cs（Tools 菜单，69 断言）。校验 69/69 PASS、EditMode 67/67、编译 0 错。关键经验：①TPWeaponMeshSwapper 用 transform.Find 直查根子节点——TP prefab 的 Muzzle/LeftHandTarget 必须是根直接子节点；②TP prefab Muzzle/weapon 轴校验用 -wrapper.right；③prefab 保存后校验器可能读到缓存旧层，重跑即恢复；④枪口方向可用网格 -X 端锥度+弹匣弯向判定（LPW 三枪皆 -X）。Why: Docs/15 商城/配件系统前置素材可行性验证。How to apply: 扩产其余 LPW 武器按此 SOP（校准 wrapper euler(0,90,0) + 弹匣→mag + 挂点重建）；待用户实机验收（切枪/换弹弹匣联动/ADS/TP 持枪/消音器贴合）。
- [2026-08-29 22:07:56] FP 开镜瞄准动画接入 Phase 0+1+实机审计修复 已完成（Docs/18，含 §12）：①WeaponAnimationSet 增 aim 四件套+HasAimClips；②FPAimAnimStateMachine 纯逻辑状态机（15 EditMode 用例）；③FPWeaponAnimator 开火延迟 Update 决策+播速适配+ResetToHip+空 reload clip 防护；④10/10 原生武器资产已填 clip；⑤**§12 照门对中修复**：LPFP aim 动画把铁瞄对中到 Armature/camera 节点（作者相机标记=原版 Gun Camera (0,0.09,-0.18)，BakeMesh 实测瞄尖恰在其高度±7mm），我们 view camera 差 (−0.05,−0.006,−0.12) 导致照门停中心下方 10%——修复=动画 ADS 武器 root 偏移"相机−节点"全 3D 对位（PlayMode 实测残差 2.1mm），程序化武器 x/y 对中且修正旧符号错误（Day4"只是变焦"根因之一）；SightReference 标记偏低 6.6cm 已知（膛线标定），对中不依赖它。EditMode 88/89（LPW 狙击 reload 存量缺口）。**Why:** 用户视频实测开镜照门不在屏幕中心、腰射感。**How to apply:** 新武器族对中一律用 Armature/camera 节点法（无需逐枪标定）；LPW 接入时瞄准线校准目标=节点射线（Phase 2）；调手感动 PlayerAimState.adsTransitionSeconds；冒烟竞态用 API 直调+协程；analyze_multimedia 本会话故障中（后端"该模型始终思考"）——视觉验证用几何断言替代。待用户实机复核（screenshots/ads_centering_fix.mp4 + 7 槽逐枪）。



### Reference

