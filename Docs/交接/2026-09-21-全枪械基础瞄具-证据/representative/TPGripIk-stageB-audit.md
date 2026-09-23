# StageB TPGripIk 失败审计（已收口，2026-09-21 Codely）

原四例 `TPGripIkTests.HandgunTpPrefabs_UseCalibratedRootPose` 失败的根因已定位：C1 阶段的 prefab 回写把 2026-09-21 01:47 的手枪根姿态校准覆盖回 HEAD 值（手枪被压回步枪基准姿态）。

已从 TPGripIkTests 常量恢复 4 把 TP 手枪根位姿（本阶段新增的 Attach_Optic 挂点保留），套件 9/9 复绿。本文件仅为历史说明保留，不再描述现存失败。