using System;
using System.Collections.Generic;

namespace Game.Account
{

public sealed class ApiResult<T>
{
    public bool Success { get; }
    public long StatusCode { get; }
    public string Code { get; }
    public string Message { get; }
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
    public T Data { get; }

    private ApiResult(bool success, long statusCode, string code, string message, IReadOnlyDictionary<string, string[]> fieldErrors, T data)
    {
        Success = success;
        StatusCode = statusCode;
        Code = code ?? string.Empty;
        Message = message ?? string.Empty;
        FieldErrors = fieldErrors ?? new Dictionary<string, string[]>();
        Data = data;
    }

    public static ApiResult<T> Ok(T data, long statusCode = 200) => new(true, statusCode, string.Empty, string.Empty, null, data);
    public static ApiResult<T> Fail(long statusCode, string code, string message, IReadOnlyDictionary<string, string[]> fieldErrors = null) => new(false, statusCode, code, message, fieldErrors, default);
}

public sealed class AccountClientException : Exception
{
    public AccountClientException(string message) : base(message) { }
}
}
