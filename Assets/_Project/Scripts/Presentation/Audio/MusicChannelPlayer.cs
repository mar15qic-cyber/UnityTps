using Game.Core;
using UnityEngine;

namespace Game.Presentation.Audio
{
    /// <summary>
    /// 背景音乐通道（共享设置 Phase B）：MusicVolume 的真实音频消费端。
    /// 约定：音乐 clip 放在 `Assets/_Project/Resources/Music/`（构建后 Resources/Music），
    /// 本组件在启动时自动发现并循环播放第一首，音量实时跟随 AudioBus.Category.Music
    /// （Master 由 AudioListener 全局承担）。
    /// 项目当前没有音乐资产：目录为空时本组件零副作用（不打日志刷屏），音乐资产放入后
    /// MusicVolume 立即生效——音量消费链路就此闭环，不出现「只存值无人消费」的断链。
    /// </summary>
    public sealed class MusicChannelPlayer : MonoBehaviour
    {
        private const string MusicResourcesFolder = "Music";

        private AudioSource _source;
        private float _baseVolume = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindFirstObjectByType<MusicChannelPlayer>() != null) return;
            var root = new GameObject("MusicChannel");
            DontDestroyOnLoad(root);
            root.AddComponent<MusicChannelPlayer>();
        }

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            var clips = Resources.LoadAll<AudioClip>(MusicResourcesFolder);
            if (clips == null || clips.Length == 0)
                return; // 无音乐资产：保持静默（不报错，资产就位后自动生效）
            _source.clip = clips[0];
            _source.Play();
        }

        private void OnEnable() => AudioBus.Changed += RefreshVolume;
        private void OnDisable() => AudioBus.Changed -= RefreshVolume;

        private void Start() => RefreshVolume();

        private void RefreshVolume()
        {
            // Music 分类因子消费点：0 = 真静音；线性相乘，Master 由 AudioListener 全局承担
            _source.volume = AudioBus.ComputeVolume(_baseVolume, AudioBus.Category.Music);
        }
    }
}
