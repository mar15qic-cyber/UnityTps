namespace Game.Account
{
    public static class ApiClientErrorCodes
    {
        public const string Cancelled = "CLIENT_CANCELLED";
        public const string Timeout = "CLIENT_TIMEOUT";
        public const string Connection = "CLIENT_CONNECTION_ERROR";
        public const string Dns = "CLIENT_DNS_ERROR";
        public const string Tls = "CLIENT_TLS_ERROR";
        public const string Network = "CLIENT_NETWORK_ERROR";
        public const string Request = "CLIENT_REQUEST_ERROR";
        public const string Response = "CLIENT_RESPONSE_ERROR";
        public const string InvalidJson = "CLIENT_INVALID_JSON";

        public static bool IsTransportFailure(string code)
        {
            return code == Timeout || code == Connection || code == Dns || code == Tls || code == Network || code == Request || code == Response;
        }
    }
}

