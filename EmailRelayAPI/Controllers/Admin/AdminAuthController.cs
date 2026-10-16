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

    public AdminAuthController(IAdminAuthService auth, IAuthenApiClient authen, IRateLimitService rateLimit, IOptionsMonitor<AuthenApiOptions> options, IAuditService audit)
    {
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
            return StatusCode(429, ApiResponse.Fail("Too many login attempts. Please wait a minute."));
        }

        var result = await _auth.LoginAsync(request.Username, request.Password, ip, HttpContext.GetUserAgent(), ct);
        if (!result.Success || result.User == null)
        {
            return Unauthorized(ApiResponse.Fail(result.Error ?? "Login failed"));
        }

        var user = result.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role),
            new(RelayClaims.AuthType, "admin"),
            new(RelayClaims.DisplayName, user.DisplayName ?? user.Username),
            new("login_mode", result.Mode)
        };
        var identity = new ClaimsIdentity(claims, RelayClaims.AdminCookieScheme);
        var timeout = TimeSpan.FromMinutes(Math.Max(5, _options.CurrentValue.SessionTimeoutMinutes));
        await HttpContext.SignInAsync(RelayClaims.AdminCookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(timeout)
        });

        return Ok(ApiResponse<AdminSessionInfo>.Ok(new AdminSessionInfo
        {
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = user.Role,
            Mode = result.Mode,
            LastLogin = user.LastLogin,
            Email = user.Email,
            Department = user.Department
        }, $"Signed in via {result.Mode}"));
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
        return Ok(ApiResponse<AdminSessionInfo>.Ok(new AdminSessionInfo
        {
            Username = User.Identity?.Name ?? string.Empty,
            DisplayName = User.FindFirst(RelayClaims.DisplayName)?.Value ?? User.Identity?.Name,
            Role = User.IsInRole(RelayClaims.RoleAdmin) ? RelayClaims.RoleAdmin : RelayClaims.RoleViewer,
            Mode = User.FindFirst("login_mode")?.Value ?? (User.FindFirst(RelayClaims.AuthType)?.Value == "apikey" ? "apikey" : "cookie")
        }));
    }

    /// <summary>Check connectivity to AuthenAPI.</summary>
    [HttpGet("authen-status")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> AuthenStatus(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!_authen.IsConfigured)
        {
            return Ok(ApiResponse<object>.Ok(new { configured = false, localAdminEnabled = opts.LocalAdmin.Enabled }, "AuthenAPI is not configured"));
        }
        var (ok, message) = await _authen.TestConnectionAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { configured = true, reachable = ok, baseUrl = opts.BaseUrl, localAdminEnabled = opts.LocalAdmin.Enabled, cacheDays = opts.CacheDurationDays }, message));
    }
}
