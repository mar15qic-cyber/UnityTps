using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 配件标准挂点（Docs/21 Phase E）。由 AttachmentSocketBuilder 批量生成，不在运行时创建。
    /// 挂点坐标系约定（全局统一）：-X = 枪口方向（前向），+Y = 枪械上方向。
    /// 挂载父节点：LPW 枪 = LPW_Gun wrapper（与方案C 锚点同空间）；原生 FP = Armature/weapon 骨骼；
    /// 原生 TP = 视图根。配件 prefab 经 AttachmentAssetEntry.mountEuler 校正后挂入
    /// （配件依各自模型的安装端标定，例如 LPFP Silencer 为 +90°）。
    /// </summary>
    public sealed class AttachmentSocket : MonoBehaviour
    {
        [SerializeField] private AttachmentSlotType slot = AttachmentSlotType.Optic;
        [SerializeField] private bool geometryVerified;

        public AttachmentSlotType Slot => slot;
        public bool GeometryVerified => geometryVerified;
    }
}
