using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailRelayAPI.Options;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public enum AuthenOutcome
{
    Success,
    InvalidCredentials,
    Unavailable,
    NotConfigured
}

/// <summary>Subset of the AuthenAPI UserInfo payload.</summary>
public class AuthenUserInfo
{
    public string? DistinguishedName { get; set; }
    public string? SamAccountName { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Department { get; set; }
    public string? Title { get; set; }
    public List<string> Groups { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public bool IsLocked { get; set; }
}

public sealed record AuthenResult(AuthenOutcome Outcome, AuthenUserInfo? User, string? Message);

/// <summary>Client for the AuthenAPI (Active Directory via LDAPS) authentication service.</summary>
public interface IAuthenApiClient
{
    bool IsConfigured { get; }
    Task<AuthenResult> AuthenticateAsync(string username, string password, CancellationToken ct = default);
    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);
}

public class AuthenApiClient : IAuthenApiClient
{
    public const string HttpClientName = "authen-api";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<AuthenApiOptions> _options;
    private readonly ILogger<AuthenApiClient> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class Envelope
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public AuthenUserInfo? Data { get; set; }
        public string? Error { get; set; }
    }

    public AuthenApiClient(IHttpClientFactory httpFactory, IOptionsMonitor<AuthenApiOptions> options, ILogger<AuthenApiClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.CurrentValue.BaseUrl);

    public async Task<AuthenResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsConfigured) return new AuthenResult(AuthenOutcome.NotConfigured, null, "AuthenAPI is not configured");

        try
        {
            var client = CreateClient(opts);
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(opts, "ldap/authenticate"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { username, password }), Encoding.UTF8, "application/json")
            };
            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                // AuthenAPI answers 401 both for bad user credentials and for a bad API key; distinguish by message.
                var env401 = TryParse(body);
                if (env401?.Message?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true || body.Contains("API key", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("AuthenAPI rejected our API key: {Body}", body);
                    return new AuthenResult(AuthenOutcome.Unavailable, null, "AuthenAPI rejected the relay's API key");
                }
                return new AuthenResult(AuthenOutcome.InvalidCredentials, null, env401?.Message ?? "Invalid username or password");
            }

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("AuthenAPI returned {Status}: {Body}", (int)res.StatusCode, body);
                return new AuthenResult(AuthenOutcome.Unavailable, null, $"AuthenAPI returned HTTP {(int)res.StatusCode}");
            }

            var env = TryParse(body);
            if (env == null || !env.Success || env.Data == null)
                return new AuthenResult(AuthenOutcome.InvalidCredentials, null, env?.Message ?? "Authentication failed");

            return new AuthenResult(AuthenOutcome.Success, env.Data, env.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AuthenAPI unreachable");
            return new AuthenResult(AuthenOutcome.Unavailable, null, "Authentication service is unreachable");
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsConfigured) return (false, "AuthenAPI BaseUrl is not configured");
        try
        {
            var client = CreateClient(opts);
            using var res = await client.GetAsync(Url(opts, "ldap/test-connection"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var env = TryParse(body);
            return (res.IsSuccessStatusCode && env?.Success == true, env?.Message ?? $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private HttpClient CreateClient(AuthenApiOptions opts)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.TimeoutSeconds));
        client.DefaultRequestHeaders.Remove(opts.ApiKeyHeaderName);
        client.DefaultRequestHeaders.TryAddWithoutValidation(opts.ApiKeyHeaderName, opts.ApiKey);
        return client;
    }

    private static string Url(AuthenApiOptions opts, string path)
        => opts.BaseUrl.TrimEnd('/') + "/" + path;

    private static Envelope? TryParse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
