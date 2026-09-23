using System;
using System.IO;
using System.Net;
using UnityEngine;

namespace Game.Core
{
    [Serializable]
    public sealed class ClientReleaseEnvironment
    {
        public string environmentId;
        public string releaseId;
        public string apiBaseUrl;
        public string hotUpdateBaseUrl;
        public bool inviteOnly;
        public string networkMode = "public";
        public string zeroTierNetworkId;
        public string hostOverlayAddress;
        public bool IsPrivateOverlay => networkMode == "private-overlay";
        private static ClientReleaseEnvironment _current;
        private static bool _loaded;

        public static ClientReleaseEnvironment Current
        {
            get
            {
                if (_loaded) return _current;
                var path = Path.Combine(Application.dataPath, "..", "client-environment.json");
                ClientReleaseEnvironment value = File.Exists(path)
                    ? JsonUtility.FromJson<ClientReleaseEnvironment>(File.ReadAllText(path)) : null;
#if PUBLIC_INVITE_TEST
                if (value == null || !value.inviteOnly || value.IsPrivateOverlay)
                    throw new InvalidOperationException("RELEASE_CONFIG_MISSING: invitation release requires client-environment.json");
#endif
#if PRIVATE_INVITE_TEST
                if (value == null || !value.inviteOnly || !value.IsPrivateOverlay)
                    throw new InvalidOperationException("PRIVATE_RELEASE_CONFIG_REQUIRED");
#endif
                if (value != null && !value.TryValidate(out var error)) throw new InvalidOperationException(error);
                _current = value;
                _loaded = true;
                return value;
            }
        }

        public bool TryValidate(out string error)
        {
            error = "RELEASE_CONFIG_INVALID";
            if (!SafeId(environmentId) || !SafeId(releaseId)) return false;
            if (IsPrivateOverlay)
            {
                if (!inviteOnly || !System.Text.RegularExpressions.Regex.IsMatch(zeroTierNetworkId ?? "", "^[0-9a-fA-F]{16}$")
                    || !IsPrivateIPv4(hostOverlayAddress)) return false;
                foreach (var endpoint in new[] { apiBaseUrl, hotUpdateBaseUrl })
                    if (!ValidUrl(endpoint, false) || new Uri(endpoint).Host != hostOverlayAddress) return false;
                error = null;
                return true;
            }
            if (networkMode != "public" && networkMode != "development" && !string.IsNullOrEmpty(networkMode)) return false;
            if (!ValidUrl(apiBaseUrl, inviteOnly) || !ValidUrl(hotUpdateBaseUrl, inviteOnly)) return false;
            error = null;
            return true;
        }

        public static bool IsPrivateIPv4(string address)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
        }

        private static bool SafeId(string id) => !string.IsNullOrWhiteSpace(id)
            && id.Length <= 80 && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]+$");

        private static bool ValidUrl(string value, bool publicEndpoint)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (!publicEndpoint) return uri.Scheme == "http" || uri.Scheme == "https";
            if (uri.Host.IndexOf("REPLACE", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (uri.Scheme != "https" || uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
            if (IPAddress.TryParse(uri.Host, out var ip))
            {
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                var b = ip.GetAddressBytes();
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
                if (b.Length == 4 && (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
                    || b[0] == 169 && b[1] == 254 || b[0] == 192 && b[1] == 168
                    || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 100 && b[1] >= 64 && b[1] <= 127)) return false;
                if (b.Length == 16 && (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || (b[0] & 0xfe) == 0xfc)) return false;
            }
            return uri.Host.Contains(".") || Uri.CheckHostName(uri.Host) == UriHostNameType.IPv6;
        }
    }
}
