using UnityEngine;
using System;

namespace Game.Account
{

[CreateAssetMenu(menuName = "UnityFps/Account/API Client Config", fileName = "ApiClientConfig")]
public sealed class ApiClientConfig : ScriptableObject
{
    [SerializeField] private string baseUrl = "http://127.0.0.1:5080";
    [SerializeField, Min(1)] private int timeoutSeconds = 10;

    public string BaseUrl => NormalizeBaseUrl(baseUrl);
    public int TimeoutSeconds => Mathf.Max(1, timeoutSeconds);

    public bool TryValidate(out string error)
    {
        var endpoint = BaseUrl;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "API BaseUrl 必须是有效的 http/https 地址";
            return false;
        }

        if (timeoutSeconds < 1)
        {
            error = "API TimeoutSeconds 必须大于 0";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeBaseUrl(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.TrimEnd('/');
    }
}
}
