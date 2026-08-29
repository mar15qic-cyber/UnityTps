using System;
using UnityEngine.Networking;

namespace Game.Account
{
    public static class ApiTransportFailureClassifier
    {
        public static string Classify(UnityWebRequest.Result result, string error, bool explicitTimeout)
        {
            if (explicitTimeout || ContainsTimeout(error)) return ApiClientErrorCodes.Timeout;

            var normalized = (error ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains("ssl") || normalized.Contains("tls") || normalized.Contains("certificate"))
                return ApiClientErrorCodes.Tls;
            if (normalized.Contains("dns") || normalized.Contains("resolve host") || normalized.Contains("name or service not known") || normalized.Contains("could not resolve"))
                return ApiClientErrorCodes.Dns;
            if (result == UnityWebRequest.Result.ConnectionError || normalized.Contains("connection refused") || normalized.Contains("failed to connect") || normalized.Contains("cannot connect") || normalized.Contains("connection reset"))
                return ApiClientErrorCodes.Connection;
            if (result == UnityWebRequest.Result.DataProcessingError)
                return ApiClientErrorCodes.Response;
            return ApiClientErrorCodes.Network;
        }

        public static bool ContainsTimeout(string error)
        {
            var normalized = (error ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.Contains("timeout") || normalized.Contains("timed out") || normalized.Contains("time out");
        }
    }
}

