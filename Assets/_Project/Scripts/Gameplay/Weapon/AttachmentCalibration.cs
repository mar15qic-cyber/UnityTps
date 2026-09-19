using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>单条校准数据：某武器 × 某配件的挂点局部位姿微调（叠加在挂点初始位姿之上）。
    /// authorFrame*：保存时挂点相对视图根的朝向（四元数，全零=未记录的旧行）。
    /// 2026-09-05 修正（用户实测激光错位）：FP 与 TP/预览挂点局部朝向不同
    /// （实测 SMG_01：TP=(0,.707,0,.707)，FP=(-0.202,.678,-0.202,.678)），
    /// 同一局部 delta 直接套用会指向不同世界方向——应用时若当前挂点帧 ≠ 作者帧，
    /// 把 delta 经两帧共轭换算到当前帧（模型空间位移/朝向等价），跨视图复现枪匠校准结果。</summary>
    [Serializable]
    public sealed class AttachmentCalibrationRow
    {
        public string weaponItemId;
        public string attachmentItemId;
        public Vector3 positionOffset = Vector3.zero;
        public Vector3 rotationEulerOffset = Vector3.zero;
        public float authorFrameX;
        public float authorFrameY;
        public float authorFrameZ;
        public float authorFrameW;

        /// <summary>是否记录了作者帧（全零哨兵=旧行，应用时按当前帧直通）。</summary>
        public bool HasAuthorFrame => authorFrameX != 0f || authorFrameY != 0f || authorFrameZ != 0f || authorFrameW != 0f;

        public Quaternion AuthorRotation => new Quaternion(authorFrameX, authorFrameY, authorFrameZ, authorFrameW);
    }

    /// <summary>
    /// 瞄具眼光轴校准（真瞄准镜 L1/L2）：眼点相对瞄具挂点（Attach_Optic）的局部坐标。
    /// 挂点局部系约定 -X=前向/+Y=上，故眼点 ≈ (+眼距, 光轴高, 0)。
    /// weaponItemId 空 = 该瞄具默认眼点（tier-1）；非空 = 逐 (武器×瞄具) 覆盖（tier-2，稀疏）。
    /// 有记录时 FPWeaponMotion 以眼点替代机瞄瞄线做 ADS 对位——视线穿过光轴，
    /// 瞄具网格自带准星落在屏幕中心=弹着点；无记录维持机瞄对位（诚实降级）。
    /// </summary>
    [Serializable]
    public sealed class OpticAimCalibrationRow
    {
        public string weaponItemId = "";
        public string opticItemId;
        public Vector3 eyePointLocal;
    }

    /// <summary>
    /// 配件贴合校准表（Docs/21 Phase E，对应后端 AttachmentCompat.CalibrationKey）。
    /// 批量工具按挂点锚点写入初始位姿（多数组合直接可用）；实机目检偏差的组合经校准窗口
    /// 微调后写入 offset，运行时叠加：最终局部位姿 = 挂点位姿 × offset。
    /// 空 offset = 直通（挂点初始位姿即最终位姿）。
    /// </summary>
    [CreateAssetMenu(fileName = "AttachmentCalibration", menuName = "Game/Attachment Calibration")]
    public sealed class AttachmentCalibration : ScriptableObject
    {
        [SerializeField] private List<AttachmentCalibrationRow> rows = new();
        [SerializeField] private List<OpticAimCalibrationRow> opticAimRows = new();
        private Dictionary<string, AttachmentCalibrationRow> _index;
        private Dictionary<string, OpticAimCalibrationRow> _opticIndex;

        public IReadOnlyList<AttachmentCalibrationRow> Rows => rows;
        public IReadOnlyList<OpticAimCalibrationRow> OpticAimRows => opticAimRows;

        /// <summary>查眼点：逐组合覆盖优先，缺省回退该瞄具默认（weaponItemId 空行）；无记录=false。</summary>
        public bool TryGetOpticEyePoint(string weaponItemId, string opticItemId, out Vector3 eyePointLocal)
        {
            eyePointLocal = Vector3.zero;
            if (string.IsNullOrEmpty(opticItemId)) return false;
            EnsureOpticIndex();
            if (!string.IsNullOrEmpty(weaponItemId)
                && _opticIndex.TryGetValue(OpticKey(weaponItemId, opticItemId), out var combo))
            {
                eyePointLocal = combo.eyePointLocal;
                return true;
            }
            if (_opticIndex.TryGetValue(OpticKey(string.Empty, opticItemId), out var fallback))
            {
                eyePointLocal = fallback.eyePointLocal;
                return true;
            }
            return false;
        }

        /// <summary>写入/更新一条眼点（校准窗口用；运行时只读）。weaponItemId 传空 = 写瞄具默认。</summary>
        public void SetOpticEyePoint(string weaponItemId, string opticItemId, Vector3 eyePointLocal)
        {
            if (string.IsNullOrEmpty(opticItemId)) return;
            EnsureOpticIndex();
            var key = OpticKey(weaponItemId ?? string.Empty, opticItemId);
            if (!_opticIndex.TryGetValue(key, out var row))
            {
                row = new OpticAimCalibrationRow { weaponItemId = weaponItemId ?? string.Empty, opticItemId = opticItemId };
                opticAimRows.Add(row);
                _opticIndex[key] = row;
            }
            row.eyePointLocal = eyePointLocal;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>删除一条眼点记录（返回是否有删除）。</summary>
        public bool RemoveOpticEyePoint(string weaponItemId, string opticItemId)
        {
            if (string.IsNullOrEmpty(opticItemId)) return false;
            EnsureOpticIndex();
            var key = OpticKey(weaponItemId ?? string.Empty, opticItemId);
            if (!_opticIndex.TryGetValue(key, out var row)) return false;
            opticAimRows.Remove(row);
            _opticIndex.Remove(key);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
            return true;
        }

        /// <summary>
        /// 清理全部 opticItemId 为空的失效行（索引器跳过空键——这类行运行时不可达）。
        /// Phase 5 数据修复用：历史批量写入时键字段丢失的 15 行即此形态。返回删除条数。
        /// </summary>
        public int RemoveInvalidOpticAimRows()
        {
            if (opticAimRows == null) return 0;
            int removed = opticAimRows.RemoveAll(row => row == null || string.IsNullOrEmpty(row.opticItemId));
            if (removed > 0)
            {
                _opticIndex = null; // 强制重建索引
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            return removed;
        }

        private void EnsureOpticIndex()
        {
            if (_opticIndex != null) return;
            _opticIndex = new Dictionary<string, OpticAimCalibrationRow>(StringComparer.Ordinal);
            if (opticAimRows == null) return;
            foreach (var row in opticAimRows)
                if (row != null && !string.IsNullOrEmpty(row.opticItemId))
                    _opticIndex[OpticKey(row.weaponItemId, row.opticItemId)] = row;
        }

        private static string OpticKey(string weaponItemId, string opticItemId)
            => (weaponItemId ?? string.Empty) + "|" + opticItemId;

        public bool TryGet(string weaponItemId, string attachmentItemId,
            out Vector3 positionOffset, out Vector3 rotationEulerOffset)
        {
            return TryGet(weaponItemId, attachmentItemId, out positionOffset, out rotationEulerOffset, out _);
        }

        /// <summary>带作者帧读取（authorFrame 全零 = 旧行无帧记录，调用方按当前帧直通）。</summary>
        public bool TryGet(string weaponItemId, string attachmentItemId,
            out Vector3 positionOffset, out Vector3 rotationEulerOffset, out Quaternion authorFrame)
        {
            positionOffset = Vector3.zero;
            rotationEulerOffset = Vector3.zero;
            authorFrame = default;
            if (string.IsNullOrEmpty(weaponItemId) || string.IsNullOrEmpty(attachmentItemId)) return false;
            EnsureIndex();
            if (!_index.TryGetValue(Key(weaponItemId, attachmentItemId), out var row)) return false;
            positionOffset = row.positionOffset;
            rotationEulerOffset = row.rotationEulerOffset;
            if (row.HasAuthorFrame) authorFrame = row.AuthorRotation;
            return true;
        }

        /// <summary>写入/更新一行（校准窗口与批量工具用；运行时只读）.</summary>
        public void Set(string weaponItemId, string attachmentItemId, Vector3 positionOffset, Vector3 rotationEulerOffset)
        {
            Set(weaponItemId, attachmentItemId, positionOffset, rotationEulerOffset, default);
        }

        /// <summary>带作者帧写入（枪匠拖拽校准用）：authorFrame = 保存时挂点相对视图根的朝向，
        /// 供跨视图（预览→FP/TP）应用时做帧换算。default = 不记录（旧行为直通）。</summary>
        public void Set(string weaponItemId, string attachmentItemId, Vector3 positionOffset, Vector3 rotationEulerOffset,
            Quaternion authorFrame)
        {
            EnsureIndex();
            var key = Key(weaponItemId, attachmentItemId);
            if (!_index.TryGetValue(key, out var row))
            {
                row = new AttachmentCalibrationRow { weaponItemId = weaponItemId, attachmentItemId = attachmentItemId };
                rows.Add(row);
                _index[key] = row;
            }
            row.positionOffset = positionOffset;
            row.rotationEulerOffset = rotationEulerOffset;
            row.authorFrameX = authorFrame.x;
            row.authorFrameY = authorFrame.y;
            row.authorFrameZ = authorFrame.z;
            row.authorFrameW = authorFrame.w;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private void EnsureIndex()
        {
            if (_index != null) return;
            _index = new Dictionary<string, AttachmentCalibrationRow>(StringComparer.Ordinal);
            if (rows == null) return;
            foreach (var row in rows)
                if (row != null && !string.IsNullOrEmpty(row.weaponItemId) && !string.IsNullOrEmpty(row.attachmentItemId))
                    _index[Key(row.weaponItemId, row.attachmentItemId)] = row;
        }

        private static string Key(string weaponItemId, string attachmentItemId) => weaponItemId + "|" + attachmentItemId;
    }
}
