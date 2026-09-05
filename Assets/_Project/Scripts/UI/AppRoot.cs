using UnityEngine;
using Game.Account;
using Game.Gameplay.Settings;

namespace Game.UI
{

public sealed class AppRoot : MonoBehaviour
{
    public static AppRoot Instance { get; private set; }
    public IApiClient ApiClient { get; private set; }
    public AccountSession Session { get; private set; }

    [SerializeField] private ApiClientConfig apiConfig;

    public static AppRoot Ensure()
    {
        if (Instance != null) return Instance;
        var root = new GameObject("AppRoot");
        return root.AddComponent<AppRoot>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        // 启动统一应用设置（Master/Music/SFX 音量 + 灵敏度实时层 + 锁帧/分辨率）：
        // 大厅与 Arena 共用同一持久值；直连 Arena 场景由 SettingsRuntime 的
        // RuntimeInitializeOnLoadMethod 兜底，二者幂等。
        SettingsRuntime.Initialize();
        if (apiConfig == null)
        {
            apiConfig = ScriptableObject.CreateInstance<ApiClientConfig>();
            apiConfig.name = "RuntimeApiClientConfig";
        }
        Session = new AccountSession();
        ApiClient = new ApiClient(apiConfig);
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        (ApiClient as System.IDisposable)?.Dispose();
        Instance = null;
    }
}
}
