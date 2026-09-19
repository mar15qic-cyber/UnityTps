using System;

namespace Game.Account
{
    /// <summary>
    /// 传输层失败分类（F10 改造后仅基于异常文本：HttpClient(UseProxy=false) 无
    /// UnityWebRequest.Result 概念；status=0 的传输失败由 SendAsync 捕获分支归入本分类器）。
    /// </summary>
    public static class ApiTransportFailureClassifier
    {
        public static string Classify(string error)
        {
            if (ContainsTimeout(error)) return ApiClientErrorCodes.Timeout;

            var normalized = (error ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains("ssl") || normalized.Contains("tls") || normalized.Contains("certificate"))
                return ApiClientErrorCodes.Tls;
            if (normalized.Contains("dns") || normalized.Contains("resolve host") || normalized.Contains("name or service not known") || normalized.Contains("could not resolve"))
                return ApiClientErrorCodes.Dns;
            if (normalized.Contains("refused") || normalized.Contains("failed to connect") || normalized.Contains("cannot connect") || normalized.Contains("connection reset"))
                return ApiClientErrorCodes.Connection;
            return ApiClientErrorCodes.Network;
        }

        public static bool ContainsTimeout(string error)
        {
            var normalized = (error ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.Contains("timeout") || normalized.Contains("timed out") || normalized.Contains("time out");
        }
    }
}
