namespace UnityFps.Api.Common;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; }

    public ApiException(int statusCode, string code, string message, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Errors = errors;
    }
}
