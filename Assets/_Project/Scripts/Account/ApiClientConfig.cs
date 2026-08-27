using UnityEngine;

namespace Game.Account
{

[CreateAssetMenu(menuName = "UnityFps/Account/API Client Config", fileName = "ApiClientConfig")]
public sealed class ApiClientConfig : ScriptableObject
{
    [SerializeField] private string baseUrl = "http://127.0.0.1:5080";
    [SerializeField, Min(1)] private int timeoutSeconds = 10;

    public string BaseUrl => NormalizeBaseUrl(baseUrl);
    public int TimeoutSeconds => Mathf.Max(1, timeoutSeconds);

    public static string NormalizeBaseUrl(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.TrimEnd('/');
    }
}
}
