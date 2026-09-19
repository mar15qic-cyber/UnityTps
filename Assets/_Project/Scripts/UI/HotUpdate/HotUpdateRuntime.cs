using System;
using System.IO;
using UnityEngine;
using XLua;

namespace Game.UI
{
    /// <summary>
    /// xLua 运行时宿主（稳定 seam）：LuaEnv 生命周期 + 脚本加载链。
    /// 加载优先级：热更下载目录（P2 起由下载器写入 HotFilesRoot）→ 内置 Resources/Lua 兜底
    /// （首包体验/后端不可达时的可玩性回退，与"失败回退内置"的链路策略一致）。
    /// 由 AppRoot.Ensure 所在 GameObject 挂载（DontDestroyOnLoad 随宿主）；
    /// DS 侧没有 AppRoot/Boot 场景 → 服务器上不会初始化 Lua（红线：DS 不加载热更脚本）。
    /// </summary>
    public sealed class HotUpdateRuntime : MonoBehaviour
    {
        /// <summary>客户端三段版本（整包更新门的比对基准，随整包发版手工递增；
        /// 与网络协议代际 GameProtocolIdentity 相互独立）。</summary>
        public const string ClientVersion = "0.1.0";

        public static HotUpdateRuntime Instance { get; private set; }

        /// <summary>热更脚本下载目录（P2 下载器写入；null = 仅内置 Resources/Lua）。</summary>
        public static string HotFilesRoot { get; set; }

        public LuaEnv Env { get; private set; }
        public string BootstrapError { get; private set; }

        private void Awake() => Initialize();

        /// <summary>初始化（幂等）。运行时经 Awake 触发；EditMode 测试直调——
        /// EditMode 下 MonoBehaviour.Awake 不回调（项目既有惯例，反射/直调驱动）。</summary>
        public void Initialize()
        {
            if (Env != null) return; // 本实例已初始化
            if (Instance != null && Instance != this) // 运行时重复挂载：保留首个宿主
            {
                Destroy(this);
                return;
            }
            Instance = this;
            Env = new LuaEnv();
            Env.AddLoader(LoadScript);
            try
            {
                Env.DoString("require 'hot_bootstrap'", "hot_bootstrap");
                Debug.Log($"[HotUpdate] lua bootstrap ok (pages={HotPageRegistry.All.Count})");
            }
            catch (Exception e)
            {
                BootstrapError = e.Message;
                // 引导失败不阻断大厅：内置页面照常可用，热页缺页签（诊断走日志）
                Debug.LogError("[HotUpdate] lua bootstrap failed: " + e.Message);
            }
        }

        /// <summary>自定义 loader：require 名 → 1) 热更目录 &lt;name&gt;.lua 2) Resources/Lua/&lt;name&gt;.lua.txt。
        /// ★ Resources 双扩展名陷阱（2026-09-17 实证）："foo.lua.txt" 的资源名是 "Lua/foo.lua"
        /// （Unity 只剥最后一个扩展名），故 Resources.Load 必须带 .lua 中缀。
        /// 返回 null 交给 xLua 内置加载链。</summary>
        private static byte[] LoadScript(ref string filename)
        {
            try
            {
                var name = filename.Replace('.', '_');
                if (!string.IsNullOrEmpty(HotFilesRoot))
                {
                    var hotPath = Path.Combine(HotFilesRoot, name + ".lua");
                    if (File.Exists(hotPath)) return File.ReadAllBytes(hotPath);
                }
                var asset = Resources.Load<TextAsset>("Lua/" + name + ".lua");
                if (asset != null) return asset.bytes;
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HotUpdate] loader error for {filename}: {e.Message}");
                return null;
            }
        }

        private void Update()
        {
            if (Env != null) Env.Tick();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Env != null)
            {
                Env.Dispose();
                Env = null;
            }
        }
    }
}
