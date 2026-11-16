using System.Security.Claims;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Console login (AuthenAPI / Active Directory with local cache).</summary>
[Route("api/admin/auth")]
public class AdminAuthController : AdminControllerBase
{
    private readonly IAdminAuthService _auth;
    private readonly IAuthenApiClient _authen;
    private readonly IRateLimitService _rateLimit;
    private readonly IOptionsMonitor<AuthenApiOptions> _options;
    private readonly IAuditService _audit;
    private readonly AuthenCircuitBreaker _breaker;
    private readonly IDirectAdAuthenticator _directAd;

    public AdminAuthController(IAdminAuthService auth, IAuthenApiClient authen, IRateLimitService rateLimit, IOptionsMonitor<AuthenApiOptions> options, IAuditService audit,
        AuthenCircuitBreaker breaker, IDirectAdAuthenticator directAd)
    {
        _breaker = breaker;
        _directAd = directAd;
        _auth = auth;
        _authen = authen;
        _rateLimit = rateLimit;
        _options = options;
        _audit = audit;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AdminSessionInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request, CancellationToken ct)
    {
        var ip = ClientIp;
        var limit = _rateLimit.Check($"adminlogin:{ip}", 10, TimeSpan.FromMinutes(1));
        if (!limit.Allowed)
        {
            Response.Headers.RetryAfter = limit.RetryAfterSeconds.ToString();
            return StatusCode(429, ApiResponse.Fail("Too many sign-in requests. Please wait a minute."));
        }

        var result = await _auth.LoginAsync(request.Username, request.Password, ip, HttpContext.GetUserAgent(), ct);
        if (!result.Success || result.User == null)
        {
            if (result.RetryAfterSeconds is int retry) Response.Headers.RetryAfter = retry.ToString();
            var status = result.Failure switch
            {
                LoginFailure.InvalidCredentials => StatusCodes.Status401Unauthorized,
                LoginFailure.NotAuthorized => StatusCodes.Status403Forbidden,
                LoginFailure.Throttled or LoginFailure.RateLimited => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status503ServiceUnavailable
            };
            return StatusCode(status, ApiResponse.Fail(result.Error ?? "Sign-in failed"));
        }

        var opts = _options.CurrentValue;
        var user = result.User;
        var now = DateTimeOffset.UtcNow;
        var lifetime = TimeSpan.FromMinutes(Math.Max(5, result.Degraded ? opts.DegradedSessionMinutes : opts.SessionTimeoutMinutes));
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, result.Role),
            new(RelayClaims.AuthType, "admin"),
            new(RelayClaims.DisplayName, user.DisplayName ?? user.Username),
            new(RelayClaims.LoginMode, result.Mode),
            new(RelayClaims.Degraded, result.Degraded ? "true" : "false"),
            new(RelayClaims.AuthTime, now.ToUnixTimeSeconds().ToString()),
            new(RelayClaims.SessionExpires, now.Add(lifetime).ToUnixTimeSeconds().ToString())
        };
        var identity = new ClaimsIdentity(claims, RelayClaims.AdminCookieScheme);
        await HttpContext.SignInAsync(RelayClaims.AdminCookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = true
        });

        return Ok(ApiResponse<AdminSessionInfo>.Ok(new AdminSessionInfo
        {
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = result.Role,
            Mode = result.Mode,
            Degraded = result.Degraded,
            ExpiresAt = now.Add(lifetime).UtcDateTime,
            LastLogin = user.LastLogin,
            Email = user.Email,
            Department = user.Department
        }, result.Degraded
            ? "Signed in with a temporary read-only session because the authentication service is unavailable."
            : $"Signed in via {result.Mode}"));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        var name = User.Identity?.Name;
        await HttpContext.SignOutAsync(RelayClaims.AdminCookieScheme);
        if (name != null)
        {
            _audit.Log(new Data.Entities.AuditLog { Category = Data.Entities.AuditCategory.Authentication, Action = "AdminLogout", Username = name, IpAddress = ClientIp });
        }
        return Ok(ApiResponse.Ok("Signed out"));
    }

    [HttpGet("session")]
    [ProducesResponseType(typeof(ApiResponse<AdminSessionInfo>), StatusCodes.Status200OK)]
    public IActionResult Session()
    {
        DateTime? expires = long.TryParse(User.FindFirst(RelayClaims.SessionExpires)?.Value, out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null;
        return Ok(ApiResponse<AdminSessionInfo>.Ok(new AdminSessionInfo
        {
            Username = User.Identity?.Name ?? string.Empty,
            DisplayName = User.FindFirst(RelayClaims.DisplayName)?.Value ?? User.Identity?.Name,
            Role = User.IsInRole(RelayClaims.RoleAdmin) ? RelayClaims.RoleAdmin : RelayClaims.RoleViewer,
            Mode = User.FindFirst(RelayClaims.LoginMode)?.Value ?? (User.FindFirst(RelayClaims.AuthType)?.Value == "apikey" ? "apikey" : "cookie"),
            Degraded = User.FindFirst(RelayClaims.Degraded)?.Value == "true",
            ExpiresAt = expires
        }));
    }

    /// <summary>Check connectivity to AuthenAPI.</summary>
    [HttpGet("authen-status")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> AuthenStatus(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var info = new
        {
            configured = _authen.IsConfigured,
            reachable = (bool?)null,
            baseUrl = _authen.IsConfigured ? opts.NormalizedBaseUrl : null,
            circuit = _breaker.State,
            offlineCacheEnabled = opts.OfflineCacheEnabled,
            offlineWindowHours = opts.OfflineWindowHours,
            offlinePepperConfigured = (opts.OfflineCachePepper?.Length ?? 0) >= 32,
            directAdEnabled = _directAd.IsEnabled,
            directAdServer = _directAd.IsEnabled ? opts.DirectAd.Server : null,
            localAdminEnabled = opts.LocalAdmin.Enabled,
            sessionHours = Math.Round(opts.SessionTimeoutMinutes / 60.0, 1),
            degradedSessionMinutes = opts.DegradedSessionMinutes
        };
        if (!_authen.IsConfigured)
        {
            return Ok(ApiResponse<object>.Ok(info, "AuthenAPI is not configured"));
        }
        var (ok, message) = await _authen.TestConnectionAsync(ct);
        return Ok(ApiResponse<object>.Ok(info with { reachable = ok }, message));
    }
}
