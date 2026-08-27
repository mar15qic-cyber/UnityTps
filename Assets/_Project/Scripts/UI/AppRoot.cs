using UnityEngine;
using Game.Account;

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
