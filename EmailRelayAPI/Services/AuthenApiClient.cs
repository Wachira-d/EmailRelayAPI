using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmailRelayAPI.Options;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

/// <summary>Outcome of one call to AuthenAPI, following the decision table of the integration guide (section 4).</summary>
public enum AuthenOutcome
{
    /// <summary>200 JSON with success=true.</summary>
    Success,

    /// <summary>401 JSON without "reason", or 400: wrong password, unknown/locked/disabled user. Stop; no fallback.</summary>
    InvalidCredentials,

    /// <summary>429: too many requests. Stop; no fallback.</summary>
    RateLimited,

    /// <summary>401 with "reason" (API key problem), 403, 404, 405, 3xx, 401 HTML: our configuration is wrong. Stop; alert.</summary>
    ConfigurationError,

    /// <summary>Timeout, 500, 504, reset while sending, 200 that is not AuthenAPI JSON: AuthenAPI may have checked AD already. Stop; no fallback.</summary>
    Indeterminate,

    /// <summary>Request never reached the app (refused, DNS, TLS, 502, 503 from IIS) or 503 JSON (AD down). Fallback allowed.</summary>
    Unreachable,

    /// <summary>AuthenApi:BaseUrl is not set.</summary>
    NotConfigured
}

/// <summary>Subset of the AuthenAPI user payload. Missing values arrive as "" (not null).</summary>
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
    public string? EmployeeId { get; set; }
    public string? Company { get; set; }
    public List<string> Groups { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public bool IsLocked { get; set; }

    /// <summary>
    /// The user key: lower-case UPN, else lower-case sAMAccountName, else null (login must be refused).
    /// Never derived from what the user typed: one person can type four different forms.
    /// </summary>
    public string? UserKey
        => !string.IsNullOrWhiteSpace(UserPrincipalName) ? UserPrincipalName.Trim().ToLowerInvariant()
         : !string.IsNullOrWhiteSpace(SamAccountName) ? SamAccountName.Trim().ToLowerInvariant()
         : null;
}

public sealed record AuthenResult(AuthenOutcome Outcome, AuthenUserInfo? User, string Detail, int? StatusCode = null, int? RetryAfterSeconds = null);

/// <summary>Client for AuthenAPI <c>POST /api/ldap/authenticate</c>.</summary>
public interface IAuthenApiClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// Sends the username exactly as typed (only trimmed) and the real password. One request, never retried.
    /// The password is never logged.
    /// </summary>
    Task<AuthenResult> AuthenticateAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Calls AuthenAPI's readiness endpoint (for the console's status check, never before a login).</summary>
    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);
}

public class AuthenApiClient : IAuthenApiClient
{
    public const string HttpClientName = "authen-api";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<AuthenApiOptions> _options;
    private readonly ILogger<AuthenApiClient> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string UserAgent = $"EmailRelayAPI/{AppInfo.Version.Split('+')[0]} ({Environment.MachineName})";

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
        if (!IsConfigured) return new AuthenResult(AuthenOutcome.NotConfigured, null, "AuthenApi:BaseUrl is not configured");

        var url = opts.NormalizedBaseUrl + opts.AuthenticatePath;
        var body = JsonSerializer.Serialize(new { username, password });

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation(opts.ApiKeyHeaderName, opts.ApiKey);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var client = _httpFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, opts.TimeoutSeconds));
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ClassifyException(ex);
        }

        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // the status line arrived but the body was cut: AuthenAPI has processed the request
                return new AuthenResult(AuthenOutcome.Indeterminate, null, $"Response body could not be read: {ex.GetType().Name}", (int)response.StatusCode);
            }
            return Classify(response, text);
        }
    }

    /// <summary>Maps an HTTP response to an outcome (guide section 4). Public for tests.</summary>
    public static AuthenResult Classify(HttpResponseMessage response, string text)
    {
        var status = (int)response.StatusCode;
        var envelope = TryParseEnvelope(response, text);
        var isJson = envelope != null;

        switch (status)
        {
            case 200:
                if (envelope is { Success: true, Data: not null })
                    return new AuthenResult(AuthenOutcome.Success, envelope.Data, "Authentication successful", status);
                if (isJson)
                    return new AuthenResult(AuthenOutcome.Indeterminate, null, "200 without success=true", status);
                return new AuthenResult(AuthenOutcome.Indeterminate, null, "200 that is not AuthenAPI JSON (proxy page or wrong URL?)", status);

            case 400:
                return new AuthenResult(AuthenOutcome.InvalidCredentials, null, "400 Bad Request (invalid input)", status);

            case 401:
                if (!isJson)
                    return new AuthenResult(AuthenOutcome.ConfigurationError, null, "401 that is not AuthenAPI JSON (IIS authentication or wrong URL)", status);
                if (!string.IsNullOrWhiteSpace(envelope!.Reason))
                    return new AuthenResult(AuthenOutcome.ConfigurationError, null, $"API key rejected by AuthenAPI (reason: {envelope.Reason})", status);
                return new AuthenResult(AuthenOutcome.InvalidCredentials, null, "401 invalid credentials", status);

            case 403:
                return new AuthenResult(AuthenOutcome.ConfigurationError, null, "403 Forbidden (IP not whitelisted or API key lacks the authenticate scope)", status);

            case 429:
                var retry = response.Headers.RetryAfter?.Delta is TimeSpan d ? (int)Math.Ceiling(d.TotalSeconds)
                          : response.Headers.RetryAfter?.Date is DateTimeOffset at ? Math.Max(1, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalSeconds))
                          : (int?)null;
                return new AuthenResult(AuthenOutcome.RateLimited, null, "429 Too Many Requests", status, retry);

            case 502:
                return new AuthenResult(AuthenOutcome.Unreachable, null, "502 Bad Gateway (request did not reach AuthenAPI)", status);

            case 503:
                return isJson
                    ? new AuthenResult(AuthenOutcome.Unreachable, null, "503 from AuthenAPI: Active Directory unavailable", status)
                    : new AuthenResult(AuthenOutcome.Unreachable, null, "503 from IIS (AuthenAPI application not running)", status);

            case 500:
            case 504:
                return new AuthenResult(AuthenOutcome.Indeterminate, null, $"{status} from AuthenAPI (it may already have checked Active Directory)", status);
        }

        if (status is >= 300 and < 400)
            return new AuthenResult(AuthenOutcome.ConfigurationError, null, $"{status} redirect to '{response.Headers.Location}' (use the https base URL without a path)", status);
        if (status is 404 or 405)
            return new AuthenResult(AuthenOutcome.ConfigurationError, null, $"{status} (wrong URL or IIS configuration)", status);
        if (status >= 500)
            return new AuthenResult(AuthenOutcome.Indeterminate, null, $"{status} from AuthenAPI", status);
        return new AuthenResult(AuthenOutcome.ConfigurationError, null, $"Unexpected HTTP {status}", status);
    }

    /// <summary>Maps a transport exception: connect failures may fall back; anything after sending may not. Public for tests.</summary>
    public static AuthenResult ClassifyException(Exception ex)
    {
        switch (ex)
        {
            case TaskCanceledException:
            case OperationCanceledException:
                // HttpClient.Timeout: the request may be inside AuthenAPI, checking AD
                return new AuthenResult(AuthenOutcome.Indeterminate, null, "Timed out waiting for AuthenAPI");

            case HttpRequestException http:
                switch (http.HttpRequestError)
                {
                    case HttpRequestError.NameResolutionError:
                        return new AuthenResult(AuthenOutcome.Unreachable, null, "DNS lookup failed: " + http.Message);
                    case HttpRequestError.ConnectionError:
                        return new AuthenResult(AuthenOutcome.Unreachable, null, "Connection failed: " + http.Message);
                    case HttpRequestError.SecureConnectionError:
                        return new AuthenResult(AuthenOutcome.Unreachable, null, "TLS handshake failed: " + http.Message);
                    case HttpRequestError.ProxyTunnelError:
                        return new AuthenResult(AuthenOutcome.Unreachable, null, "Proxy tunnel failed: " + http.Message);
                    default:
                        // ResponseEnded, InvalidResponse, Unknown...: the request may have been delivered
                        return new AuthenResult(AuthenOutcome.Indeterminate, null, $"Connection lost while waiting for AuthenAPI ({http.HttpRequestError}): {http.Message}");
                }

            default:
                return new AuthenResult(AuthenOutcome.Indeterminate, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsConfigured) return (false, "AuthenApi:BaseUrl is not configured");
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(15);
            using var request = new HttpRequestMessage(HttpMethod.Get, opts.NormalizedBaseUrl + opts.HealthPath);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var res = await client.SendAsync(request, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > 300) body = body[..300];
            return (res.IsSuccessStatusCode, $"GET {opts.HealthPath} → HTTP {(int)res.StatusCode} {body}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, ClassifyException(ex).Detail);
        }
    }

    private sealed class Envelope
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public AuthenUserInfo? Data { get; set; }
        public string? Reason { get; set; }
    }

    private static Envelope? TryParseEnvelope(HttpResponseMessage response, string text)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("success", out _)) return null;
            return doc.RootElement.Deserialize<Envelope>(JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Shared circuit breaker for AuthenAPI (guide section 8). Opens after N consecutive "unreachable" results,
/// lets logins skip straight to the fallback while open, then allows one trial request (half-open).
/// 401, timeouts and 500s are not counted: they are not evidence that AuthenAPI is down.
/// </summary>
public class AuthenCircuitBreaker
{
    private readonly object _lock = new();
    private readonly IOptionsMonitor<AuthenApiOptions> _options;
    private int _consecutiveUnreachable;
    private DateTime? _openedUntil;
    private bool _trialInFlight;

    public AuthenCircuitBreaker(IOptionsMonitor<AuthenApiOptions> options) => _options = options;

    public string State
    {
        get
        {
            lock (_lock)
            {
                if (_openedUntil == null) return "closed";
                return DateTime.UtcNow < _openedUntil ? "open" : "half-open";
            }
        }
    }

    /// <summary>True when the caller may send a request to AuthenAPI now.</summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_openedUntil == null) return true;
            if (DateTime.UtcNow < _openedUntil) return false;
            if (_trialInFlight) return false; // half-open: only one trial at a time
            _trialInFlight = true;
            return true;
        }
    }

    public void Record(AuthenOutcome outcome)
    {
        lock (_lock)
        {
            _trialInFlight = false;
            if (outcome == AuthenOutcome.Unreachable)
            {
                _consecutiveUnreachable++;
                if (_openedUntil != null || _consecutiveUnreachable >= Math.Max(1, _options.CurrentValue.CircuitFailureThreshold))
                {
                    _openedUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.CurrentValue.CircuitOpenSeconds));
                }
            }
            else if (outcome is AuthenOutcome.Success or AuthenOutcome.InvalidCredentials or AuthenOutcome.RateLimited or AuthenOutcome.ConfigurationError)
            {
                // AuthenAPI answered: it is reachable
                _consecutiveUnreachable = 0;
                _openedUntil = null;
            }
            // Indeterminate (timeout/500) neither opens nor closes the circuit
        }
    }
}
