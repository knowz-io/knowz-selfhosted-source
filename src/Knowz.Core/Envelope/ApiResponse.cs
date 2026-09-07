namespace Knowz.Core.Envelope;

/// <summary>
/// Standard API response envelope for all API responses.
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? TraceId { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null, string? traceId = null)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data,
            Message = message,
            TraceId = traceId
        };
    }

    public static ApiResponse<T> Fail(string error, string? traceId = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Errors = new List<string> { error },
            TraceId = traceId
        };
    }

    public static ApiResponse<T> Fail(List<string> errors, string? traceId = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Errors = errors,
            TraceId = traceId
        };
    }
}

/// <summary>
/// Non-generic version for responses without data.
/// </summary>
public class ApiResponse : ApiResponse<object>
{
    public static ApiResponse Ok(string? message = null, string? traceId = null)
    {
        return new ApiResponse
        {
            Success = true,
            Message = message,
            TraceId = traceId
        };
    }

    public new static ApiResponse Fail(string error, string? traceId = null)
    {
        return new ApiResponse
        {
            Success = false,
            Errors = new List<string> { error },
            TraceId = traceId
        };
    }

    public new static ApiResponse Fail(List<string> errors, string? traceId = null)
    {
        return new ApiResponse
        {
            Success = false,
            Errors = errors,
            TraceId = traceId
        };
    }
}
