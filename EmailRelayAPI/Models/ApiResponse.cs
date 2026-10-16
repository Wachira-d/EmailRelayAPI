namespace EmailRelayAPI.Models;

/// <summary>Standard API envelope (same shape as AuthenAPI).</summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse<T> Ok(T data, string message = "Success") => new() { Success = true, Message = message, Data = data };
    public static ApiResponse<T> Fail(string message, string? error = null) => new() { Success = false, Message = message, Error = error };
    public static ApiResponse<T> Fail(string message, T? data) => new() { Success = false, Message = message, Data = data };
}

public class ApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse Ok(string message = "Success") => new() { Success = true, Message = message };
    public static ApiResponse Fail(string message, string? error = null) => new() { Success = false, Message = message, Error = error };
}

/// <summary>Paged list wrapper.</summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
