using UnityEngine;
using UnityEngine.Audio;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 武器音频配置（Docs/13 §5.2 v3，CP3）：**按武器族共享**（handgun/rifle/smg/shotgun/sniper
    /// 共 5 份），单武器可在 WeaponDefinition 覆盖引用。纯数据资产，无播放逻辑——播放归
    /// CP6 的 WeaponAudioView（Gameplay 不直接操作 AudioSource）。
    /// 素材策略（§9-11 拍板）：LPFP 已有 clip 才接；缺失槽位一律留空，由 Editor Validator
    /// 报告，不用近义 clip 假装完成。Jam 仅序列化槽位（无消费者，不加空事件）。
    /// </summary>
    [CreateAssetMenu(menuName = "UnityFps/Audio/Weapon Audio Profile", fileName = "AudioProfile_Weapon")]
    public sealed class WeaponAudioProfile : ScriptableObject
    {
        [System.Serializable]
        public struct ClipEntry
        {
            public AudioClip Clip;
            [Tooltip("音量随机范围")] public Vector2 VolumeRange;
            [Tooltip("音高随机范围")] public Vector2 PitchRange;
        }

        [System.Serializable]
        public struct StageEntry
        {
            public AudioClip Clip;
            [Range(0f, 1f), Tooltip("动画归一化时间（与播放速度无关）")] public float NormalizedTime;
        }

        [Header("开火")]
        [Tooltip("同类 Fire 变体：随机轮换（CP6 池 2~3 路 OneShot 防高速截断）")]
        public ClipEntry[] FireVariants;

        [Header("扳机")]
        public ClipEntry DryFire;          // LPFP 无专属 → 留空待 Validator

        [Header("换弹（整段过渡；分阶段见下方 Stages）")]
        public ClipEntry ReloadAmmoLeft;
        public ClipEntry ReloadOutOfAmmo;

        [Header("换弹分阶段（CP6 消费；shotgun/sniper01 的 open/insert/close 直配）")]
        public StageEntry MagOut;
        public StageEntry MagIn;
        public StageEntry BoltRack;

        [Header("切枪")]
        public ClipEntry Draw;             // LPFP Misc/take_out_weapon
        public ClipEntry Holster;          // LPFP Misc/holster_weapon
        public ClipEntry WeaponSwitch;     // LPFP 无独立 switch 音 → 留空

        [Header("机构音")]
        public ClipEntry ShellEject;       // LPFP 弹壳落地音随弹壳 prefab 自带 → 留空
        public ClipEntry Pump;             // Shotgun pump（LPFP 无 → 留空）
        public ClipEntry SniperBolt;       // LPFP 无独立 → 留空

        [Header("故障（仅槽位预留，无消费者）")]
        public ClipEntry Jam;

        [Header("输出")]
        [Tooltip("CP6 接 AudioMixer（Weapon/SFX 组）；CP3 空")]
        public AudioMixerGroup MixerGroup;
        [Range(0f, 1f), Tooltip("本地第一人称 2D；未来远端 3D 由 WeaponAudioView 切换")]
        public float SpatialBlend = 0f;
    }
}
