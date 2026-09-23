# C2-B 图片证据捕获状态

读者：任意代理/用户，零上下文可读。

## 状态

目标是使用实际 Arena Player、`FPWeaponRig`、`WeaponAttachmentView`、`FPWeaponMotion` 与 `OpticAimGeometry` 捕获 16 把正式 FP prefab × 4 个正式 optic prefab × 3 个状态，共 192 张 GameView PNG，并生成每把枪 12 格 contact sheet。

捕获工具已编译：

- `Assets/_Project/Scripts/Debug/FormalOpticEvidenceCaptureRuntime.cs`
- 入口：给 Arena 场景 Player 添加该组件后，在 PlayMode 调用 `BeginCapture()`。

## 阻塞

本轮 `manage_editor(action="play")` 被自动审批拦截，原因为线程早先的 EditMode-only 约束与本轮 PlayMode 截图要求冲突。未通过 PlayMode 审批前没有运行场景、没有调用 ScreenCapture，也没有生成占位 PNG 或伪造 manifest。

因此当前：

- PNG：`0/192`，均为 `BLOCKED_AUTOREVIEW`。
- contact sheet：`0/16`。
- `manifest.json`：未生成，避免把缺失文件误报为证据。

## 可复核证据

- `OpticAimCoverageTests` 的四镜目录映射与默认眼点断言已修复并通过 EditMode。
- TPGripIk 的 4 个失败审计：`TP_Weapon_Handgun_02/03/04.prefab` diff 仅新增 `Attach_Optic` GameObject/AttachmentSocket 和 prefab addedObject 记录，没有 root Transform position/rotation 改动；Handgun_01 没有该阶段 TP diff。因此没有为过绿修改 TP 根姿态。
