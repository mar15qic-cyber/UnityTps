using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 配件标准挂点（Docs/21 Phase E）。由 AttachmentSocketBuilder 批量生成，不在运行时创建。
    /// 挂点坐标系约定（全局统一）：-X = 枪口方向（前向），+Y = 枪械上方向。
    /// 挂载父节点：LPW 枪 = LPW_Gun wrapper（与方案C 锚点同空间）；原生 FP = Armature/weapon 骨骼；
    /// 原生 TP = 视图根。配件 prefab 经 AttachmentAssetEntry.mountEuler 校正后挂入
    /// （LPW 配件恒等；LPFP 配件 (0,-90,0) 将其 +Z 长轴对齐挂点 -X 前向）。
    /// </summary>
    public sealed class AttachmentSocket : MonoBehaviour
    {
        [SerializeField] private AttachmentSlotType slot = AttachmentSlotType.Optic;

        public AttachmentSlotType Slot => slot;
    }
}
