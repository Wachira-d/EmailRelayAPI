using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Security;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates API clients by API key (header or bearer token), enforces per-client IP allow lists,
/// block lists and optional HMAC request signatures.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string ItemFailure = "RelayAuthFailure";

    private readonly IApiKeyService _keys;
    private readonly IOptionsMonitor<SecurityOptions> _security;
    private readonly IAuditService _audit;
    private readonly IAnomalyDetector _anomaly;
    private readonly IBlockListService _blocks;
    private readonly IRequestSignatureVerifier _signatures;
    private readonly ISecretProtector _secrets;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyService keys,
        IOptionsMonitor<SecurityOptions> security,
        IAuditService audit,
        IAnomalyDetector anomaly,
        IBlockListService blocks,
        IRequestSignatureVerifier signatures,
        ISecretProtector secrets)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _security = security;
        _audit = audit;
        _anomaly = anomaly;
        _blocks = blocks;
        _signatures = signatures;
        _secrets = secrets;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ExtractKey();
        if (key == null) return AuthenticateResult.NoResult();

        var ip = Context.GetClientIp();
        var remote = IpUtils.Normalize(Context.Connection.RemoteIpAddress);

        var validation = await _keys.ValidateAsync(key, Context.RequestAborted);
        if (!validation.Success)
        {
            var reason = validation.Failure switch
            {
                ApiKeyFailure.Revoked => "API key has been revoked",
                ApiKeyFailure.Expired => "API key has expired",
                ApiKeyFailure.ClientDisabled => "Client is disabled",
                ApiKeyFailure.Malformed => "Malformed API key",
                _ => "Invalid API key"
            };
            return Fail(reason, 401, ip, $"{validation.Failure}; key prefix {SafePrefix(key)}", countAsAuthFailure: true);
        }

        var cc = validation.Context!;

        var block = _blocks.Check(null, cc.ClientId, cc.KeyPrefix);
        if (block != null)
        {
            return Fail("Access temporarily blocked", 403, ip,
                $"Blocked {block.TargetType} {block.Target}: {block.Reason}", countAsAuthFailure: false, cc.ClientId, cc.ApiKeyId);
        }

        if (!string.IsNullOrWhiteSpace(cc.Client.AllowedIpRanges) && remote != null)
        {
            var ranges = IpUtils.SplitList(cc.Client.AllowedIpRanges);
            if (ranges.Count > 0 && !IpUtils.MatchesAny(remote, ranges))
            {
                return Fail("Source IP is not allowed for this client", 403, ip,
                    $"IP {ip} not in client allow list", countAsAuthFailure: true, cc.ClientId, cc.ApiKeyId);
            }
        }

        if (cc.Client.RequireSignedRequests)
        {
            var secret = _secrets.Unprotect(cc.Client.SigningSecretProtected);
            if (string.IsNullOrEmpty(secret))
            {
                return Fail("Signed requests are required but no signing secret is configured for this client", 403, ip,
                    "Signing secret missing", countAsAuthFailure: false, cc.ClientId, cc.ApiKeyId);
            }

            var error = await _signatures.VerifyAsync(Request, secret, Context.RequestAborted);
            if (error != null)
            {
                return Fail(error, 401, ip, $"Signature check failed: {error}", countAsAuthFailure: true, cc.ClientId, cc.ApiKeyId);
            }
        }

        Context.Items[RelayClaims.ItemClientContext] = cc;
        _keys.TouchUsage(cc.ApiKeyId, ip);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, cc.ClientName),
            new(ClaimTypes.NameIdentifier, cc.ClientId.ToString()),
            new(RelayClaims.ClientId, cc.ClientId.ToString()),
            new(RelayClaims.ClientName, cc.ClientName),
            new(RelayClaims.ApiKeyId, cc.ApiKeyId.ToString()),
            new(RelayClaims.KeyPrefix, cc.KeyPrefix),
            new(RelayClaims.AuthType, "apikey")
        };
        foreach (var scope in cc.Scopes)
        {
            claims.Add(new Claim(RelayClaims.Scope, scope.ToLowerInvariant()));
            if (scope.Equals(RelayClaims.ScopeAdmin, StringComparison.OrdinalIgnoreCase))
                claims.Add(new Claim(ClaimTypes.Role, RelayClaims.RoleAdmin));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var (status, reason) = Context.Items.TryGetValue(ItemFailure, out var f) && f is ValueTuple<int, string> tuple
            ? tuple
            : (401, $"API key is required. Provide it in the '{_security.CurrentValue.ApiKeyHeaderName}' header.");
        return WriteJson(status, reason);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => WriteJson(403, "The API key does not have the required scope for this operation");

    private string? ExtractKey()
    {
        var header = _security.CurrentValue.ApiKeyHeaderName;
        var value = Request.Headers[header].ToString();
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

        if (_security.CurrentValue.AllowBearerToken)
        {
            var auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth[7..].Trim();
                if (token.StartsWith("erk_", StringComparison.OrdinalIgnoreCase) || token.Length >= 20) return token;
            }
        }
        return null;
    }

    private AuthenticateResult Fail(string reason, int status, string ip, string details, bool countAsAuthFailure, int? clientId = null, int? apiKeyId = null)
    {
        Context.Items[ItemFailure] = (status, reason);
        var traceId = Context.GetTraceId();

        _audit.Log(new AuditLog
        {
            Category = AuditCategory.Authentication,
            Severity = AuditSeverity.Warning,
            Action = "ApiKeyAuth",
            Success = false,
            IpAddress = ip,
            ClientId = clientId,
            ApiKeyId = apiKeyId,
            UserAgent = Context.GetUserAgent(),
            RequestPath = Request.Path,
            HttpMethod = Request.Method,
            StatusCode = status,
            Details = details,
            TraceId = traceId
        });

        if (countAsAuthFailure) _anomaly.RecordAuthFailure(ip, details, traceId);
        return AuthenticateResult.Fail(reason);
    }

    private static string SafePrefix(string key) => key.Length >= 8 ? key[..8] + "…" : "(short)";

    private async Task WriteJson(int status, string message)
    {
        if (Response.HasStarted) return;
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.Fail(message), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
