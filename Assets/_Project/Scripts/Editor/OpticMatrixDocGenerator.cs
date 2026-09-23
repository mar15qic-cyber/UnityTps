using System;
using System.Globalization;
using System.Text;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 从当前资产重新生成 C1/C2 瞄具矩阵文档（文档=资产快照，禁止手填表格漂移）。
    /// 数据源：AttachmentCalibration.asset（rows + opticAimRows）+ 13 把正式 FP prefab 的
    /// Attach_Optic 静态帧 + AttachmentAssetCatalog 的 mountOffset。156/156 运行时证据见
    /// Docs/交接/2026-09-21-全枪械基础瞄具-证据/（manifest.json 为机器可读真源）。
    /// 狙击族仅内置高倍镜，不在矩阵内（2026-09-21 用户拍板）。
    /// </summary>
    public static class OpticMatrixDocGenerator
    {
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";
        private const string CatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string C1Path = "Docs/交接/2026-09-21-阶段C1-AttachmentMount矩阵.md";
        private const string C2Path = "Docs/交接/2026-09-21-阶段C2-OpticAim矩阵.md";

        private static readonly (string id, string fp)[] Weapons =
        {
            ("weapon.m4", "FP_Rifle_View"), ("weapon.ak", "FP_Rifle02_View"),
            ("weapon.rifle03", "FP_Rifle03_View"), ("weapon.service_pistol", "FP_ServicePistol_View"),
            ("weapon.handgun02", "FP_Handgun02_View"), ("weapon.handgun03", "FP_Handgun03_View"),
            ("weapon.handgun04", "FP_Handgun04_View"), ("weapon.smg01", "FP_SMG01_View"),
            ("weapon.smg02", "FP_SMG02_View"), ("weapon.smg03", "FP_SMG03_View"),
            ("weapon.smg04", "FP_SMG04_View"), ("weapon.smg05", "FP_SMG05_View"),
            ("weapon.shotgun01", "FP_Shotgun01_View"),
        };

        private static readonly string[] Optics =
        {
            "attach.lpfp.optic.01", "attach.rifle.optic", "attach.lpfp.optic.03", "attach.lpfp.optic.02"
        };

        [MenuItem("Tools/Attachments/Regenerate C1 C2 Optic Matrix Docs")]
        public static void Generate()
        {
            var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(CatalogPath);
            if (calibration == null || catalog == null)
            {
                Debug.LogError("[OpticMatrixDocGen] catalog/calibration missing");
                return;
            }
            System.IO.File.WriteAllText(C1Path, BuildC1(calibration, catalog), Encoding.UTF8);
            System.IO.File.WriteAllText(C2Path, BuildC2(calibration), Encoding.UTF8);
            Debug.Log("[OpticMatrixDocGen] regenerated C1/C2 docs from current assets");
        }

        private static string BuildC1(AttachmentCalibration calibration, AttachmentAssetCatalog catalog)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 阶段 C1-A：13×4 Attachment Mount 矩阵（2026-09-22 重生成）");
            sb.AppendLine();
            sb.AppendLine("> 数据源：`Assets/_Project/Resources/AttachmentCalibration.asset` 的 `rows` + 正式 FP prefab 的 `Attach_Optic` 静态帧 + 目录 `mountOffset`。本文由 `Tools/Attachments/Regenerate C1 C2 Optic Matrix Docs` 从资产生成，禁止手填表格。");
            sb.AppendLine(">");
            sb.AppendLine("> 2026-09-22 口径：狙击族仅内置高倍镜退出矩阵（13 把）；13 把 FP+26 个 TP 挂点已按实测导轨顶重贴（用户拍板\"全部按实测校准\"）；SCAR 锁定行保留前向 x=0.08639714、垂直补偿清零（用户验收当前结构）。运行时证据 156/156 PASS（`Docs/交接/2026-09-21-全枪械基础瞄具-证据/manifest.json`）。");
            sb.AppendLine();
            sb.AppendLine("| weapon | optic | FP socket local | catalog mountOffset | combo delta | 状态 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var (id, fpName) in Weapons)
            {
                var fp = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Weapons/" + fpName + ".prefab");
                var socket = fp != null ? fp.transform.Find("Armature/weapon/Attach_Optic") : null;
                var socketLocal = socket != null ? socket.localPosition : Vector3.zero;
                foreach (var optic in Optics)
                {
                    catalog.TryGet(optic, out var entry);
                    var mo = entry != null ? entry.mountOffset : Vector3.zero;
                    var hasRow = calibration.TryGet(id, optic, out var delta, out _, out _);
                    var locked = id == "weapon.rifle03" && optic == "attach.lpfp.optic.02";
                    sb.AppendLine($"| {id} | {optic} | {Fmt(socketLocal)} | {Fmt(mo)} | {Fmt(delta)} | {(locked ? "locked(验收行)" : hasRow ? "ok" : "MISSING")} |");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## 备注");
            sb.AppendLine("- 全部 52 组合 delta=(0,0,0)：镜体底面经 mountEuler+mountOffset 落挂点设计面（AttachmentMountMatrixTests 锁定）。");
            sb.AppendLine("- 挂点面与真实导轨顶间隙=0（RepresentativeOpticRailContactTests 锁定 4 代表枪 FP+TP；运行时间隙见证据 manifest railGap 列）。");
            return sb.ToString();
        }

        private static string BuildC2(AttachmentCalibration calibration)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 阶段 C2-A：13×4 Optic Aim 矩阵（2026-09-22 重生成）");
            sb.AppendLine();
            sb.AppendLine("> 数据源：`AttachmentCalibration.asset` 的 `opticAimRows`（眼点/轴前点/镜窗中心/半宽半高/目标屏占比，均在挂点局部系）。本文由生成器从资产导出，禁止手填。");
            sb.AppendLine(">");
            sb.AppendLine("> 静态投影验证：`OpticAimGeometry.SolveWindowFramingLocalPosition`、垂直 FOV 45°、16:9 → 窗口中心投 (0.5,0.5)、高度=targetViewportHeight。运行时证据 156/156 PASS（同目录证据 manifest）。狙击族仅内置高倍镜不在矩阵内（3 条 builtin 组合行保留）。");
            sb.AppendLine();
            sb.AppendLine("| weapon | optic | eye local | axis front local | window center local | half W/H (m) | target |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var (id, _) in Weapons)
            {
                foreach (var optic in Optics)
                {
                    if (!calibration.TryGetOpticAim(id, optic, out var d))
                    {
                        sb.AppendLine($"| {id} | {optic} | **MISSING** | | | | |");
                        continue;
                    }
                    var halfW = d.WindowHalfWidthMeters.ToString("0.0000", CultureInfo.InvariantCulture);
                    var halfH = d.WindowHalfHeightMeters.ToString("0.0000", CultureInfo.InvariantCulture);
                    var target = d.TargetViewportHeight.ToString("0.00", CultureInfo.InvariantCulture);
                    sb.AppendLine($"| {id} | {optic} | {Fmt(d.EyePointLocal)} | {Fmt(d.AxisFrontPointLocal)} | {Fmt(d.WindowCenterLocal)} | {halfW}/{halfH} | {target} |");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## 固定证据");
            sb.AppendLine("- 正式行 52（13×4）；默认 fallback 行 3；内置狙击组合行 3（weapon.sniper01/02/03 × builtin.weapon.sniper0X）。");
            sb.AppendLine("- Scope01/03（低倍 3x）target=0.18；Scope02/04（1x 全息）=0.12。");
            sb.AppendLine("- SCAR×Scope04 行为用户验收的实测锁（eye/axis 保持，窗口采用审计后的 Scope_04 网格实测口径）。");
            return sb.ToString();
        }

        private static string Fmt(Vector3 v)
            => $"({v.x.ToString("0.000000", CultureInfo.InvariantCulture)},{v.y.ToString("0.000000", CultureInfo.InvariantCulture)},{v.z.ToString("0.000000", CultureInfo.InvariantCulture)})";
    }
}
