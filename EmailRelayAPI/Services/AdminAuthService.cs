using System.Text.Json;
using System.Text.RegularExpressions;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public enum LoginFailure
{
    None,
    InvalidCredentials,
    Throttled,
    RateLimited,
    NotAuthorized,
    ConfigurationError,
    Unavailable
}

/// <summary>Result of a console login. <see cref="Role"/> is the effective role (degraded sessions are read-only).</summary>
public sealed record AdminLoginResult(
    bool Success,
    LoginFailure Failure,
    string? Error,
    AdminUser? User,
    string Role,
    string Mode,
    bool Degraded,
    int? RetryAfterSeconds = null);

public interface IAdminAuthService
{
    /// <summary>
    /// Console login following the AuthenAPI integration guide: AuthenAPI is asked on every login;
    /// only when it cannot be reached: one direct LDAPS bind (optional), then the offline cache, then "unavailable".
    /// </summary>
    Task<AdminLoginResult> LoginAsync(string username, string password, string ip, string? userAgent, CancellationToken ct = default);

    /// <summary>False when the console user was disabled; cached briefly. Used to end existing sessions.</summary>
    Task<bool> IsUserActiveAsync(string userKey, CancellationToken ct = default);

    Task<List<AdminUser>> ListUsersAsync(CancellationToken ct = default);
    Task<bool> SetEnabledAsync(string userKey, bool enabled, string by, CancellationToken ct = default);

    /// <summary>Removes the user's offline credential (e.g. during offboarding).</summary>
    Task<bool> ClearCacheAsync(string userKey, string by, CancellationToken ct = default);

    /// <summary>Removes offline credentials older than the offline window and any legacy (v1) hashes.</summary>
    Task<int> PurgeExpiredOfflineCredentialsAsync(CancellationToken ct = default);

    string? ResolveRole(IEnumerable<string> groups);
}

public partial class AdminAuthService : IAdminAuthService
{
    public const string ModeAuthenApi = "authenapi";
    public const string ModeDirectAd = "direct-ad";
    public const string ModeOffline = "offline";
    public const string ModeLocal = "local";

    public const string MsgInvalid = "Invalid username or password.";
    public const string MsgUnavailable = "The authentication service is temporarily unavailable. Please try again later.";
    public const string MsgConfiguration = "Sign-in is not available because of a configuration problem. Please contact the system administrator.";
    public const string MsgRateLimited = "Too many sign-in requests. Please try again later.";
    public const string MsgNotAuthorized = "Your account is not authorized to use this console.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuthenApiClient _authen;
    private readonly AuthenCircuitBreaker _breaker;
    private readonly IDirectAdAuthenticator _directAd;
    private readonly IOptionsMonitor<AuthenApiOptions> _options;
    private readonly IAuditService _audit;
    private readonly IAnomalyDetector _anomaly;
    private readonly IAlertService _alerts;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminAuthService> _logger;
    private readonly SlidingCounterStore _failures = new();

    public AdminAuthService(IServiceScopeFactory scopeFactory, IAuthenApiClient authen, AuthenCircuitBreaker breaker, IDirectAdAuthenticator directAd,
        IOptionsMonitor<AuthenApiOptions> options, IAuditService audit, IAnomalyDetector anomaly, IAlertService alerts, IMemoryCache cache, ILogger<AdminAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _authen = authen;
        _breaker = breaker;
        _directAd = directAd;
        _options = options;
        _audit = audit;
        _anomaly = anomaly;
        _alerts = alerts;
        _cache = cache;
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._@\\\-]{1,256}$")]
    private static partial Regex UsernameFormat();

    // =====================================================================================
    // Login
    // =====================================================================================

    public async Task<AdminLoginResult> LoginAsync(string username, string password, string ip, string? userAgent, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;

        // Guide rule 1: send what the user typed; only surrounding whitespace is removed. The password is never trimmed.
        var typed = (username ?? string.Empty).Trim();
        var loginName = typed.ToLowerInvariant();
        var ctx = new LoginContext(typed, loginName, ip, userAgent);

        if (string.IsNullOrEmpty(password) || !UsernameFormat().IsMatch(typed))
        {
            RecordFailure(ctx);
            return Fail(ctx, LoginFailure.InvalidCredentials, MsgInvalid, ModeAuthenApi, "Username has an invalid format or the password is empty (not sent to AuthenAPI)");
        }

        // Local throttle per username and per IP (AuthenAPI's own limit is shared by every user of this system).
        var throttle = CheckThrottle(ctx, opts);
        if (throttle != null) return throttle;

        // Break-glass local administrator (never sent to AuthenAPI).
        if (opts.LocalAdmin.Enabled && !string.IsNullOrWhiteSpace(opts.LocalAdmin.Username)
            && loginName.Equals(opts.LocalAdmin.Username.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            return await LocalAdminLoginAsync(ctx, password, opts, ct);
        }

        if (!_authen.IsConfigured)
        {
            return Fail(ctx, LoginFailure.ConfigurationError, MsgConfiguration, ModeAuthenApi, "AuthenApi:BaseUrl is not configured", AuditSeverity.Critical);
        }

        // Step 1: AuthenAPI on every login (unless the circuit breaker is open).
        if (!_breaker.TryAcquire())
        {
            return await FallbackAsync(ctx, password, "circuit breaker open (AuthenAPI recently unreachable)", opts, ct);
        }

        var result = await _authen.AuthenticateAsync(typed, password, ct);
        _breaker.Record(result.Outcome);
        var apiDetail = result.StatusCode.HasValue ? $"AuthenAPI HTTP {result.StatusCode}: {result.Detail}" : $"AuthenAPI: {result.Detail}";

        switch (result.Outcome)
        {
            case AuthenOutcome.Success:
                return await OnlineSuccessAsync(ctx, password, result.User!, opts, ct);

            case AuthenOutcome.InvalidCredentials:
                // Guide rule 4: stop. No AD retry, no cache. Remove this person's offline credential.
                await ClearOfflineCredentialByLoginNameAsync(loginName, ct);
                RecordFailure(ctx);
                return Fail(ctx, LoginFailure.InvalidCredentials, MsgInvalid, ModeAuthenApi, apiDetail);

            case AuthenOutcome.RateLimited:
                return Fail(ctx, LoginFailure.RateLimited, MsgRateLimited, ModeAuthenApi, apiDetail, AuditSeverity.Warning, result.RetryAfterSeconds);

            case AuthenOutcome.ConfigurationError:
                _logger.LogError("AuthenAPI configuration error: {Detail}", apiDetail);
                _ = _alerts.RaiseAsync("AuthenApiConfigurationError", "Console sign-in: AuthenAPI configuration error", apiDetail, CancellationToken.None);
                return Fail(ctx, LoginFailure.ConfigurationError, MsgConfiguration, ModeAuthenApi, apiDetail, AuditSeverity.Critical);

            case AuthenOutcome.Indeterminate:
                // Guide rule 5: AuthenAPI may already have checked AD; a fallback would count a second bad password.
                _logger.LogWarning("AuthenAPI result indeterminate, no fallback: {Detail}", apiDetail);
                return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeAuthenApi, apiDetail + " (no fallback)", AuditSeverity.Warning);

            case AuthenOutcome.Unreachable:
                _logger.LogWarning("AuthenAPI unreachable, using fallback: {Detail}", apiDetail);
                return await FallbackAsync(ctx, password, apiDetail, opts, ct);

            default:
                return Fail(ctx, LoginFailure.ConfigurationError, MsgConfiguration, ModeAuthenApi, apiDetail, AuditSeverity.Critical);
        }
    }

    private async Task<AdminLoginResult> OnlineSuccessAsync(LoginContext ctx, string password, AuthenUserInfo info, AuthenApiOptions opts, CancellationToken ct)
    {
        var userKey = info.UserKey;
        if (userKey == null)
        {
            _logger.LogError("AuthenAPI returned a user without userPrincipalName and samAccountName for login '{Login}'", ctx.LoginName);
            return Fail(ctx, LoginFailure.ConfigurationError, MsgConfiguration, ModeAuthenApi, "AuthenAPI returned neither userPrincipalName nor samAccountName", AuditSeverity.Critical);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == userKey, ct);
        var role = ResolveRole(info.Groups);

        if (!info.IsEnabled || info.IsLocked)
        {
            if (user != null) { user.PasswordHash = string.Empty; user.OfflineFailures = 0; await db.SaveChangesAsync(ct); }
            RecordFailure(ctx);
            return Fail(ctx, LoginFailure.InvalidCredentials, MsgInvalid, ModeAuthenApi, $"AuthenAPI reported user '{userKey}' as disabled or locked");
        }

        user ??= db.AdminUsers.Add(new AdminUser { Username = userKey, CreatedAt = now }).Entity;
        user.Source = "AuthenApi";
        user.UserPrincipalName = Blank(info.UserPrincipalName);
        user.SamAccountName = Blank(info.SamAccountName);
        user.DisplayName = Blank(info.DisplayName) ?? Blank(info.UserPrincipalName) ?? userKey;
        user.Email = Blank(info.Email);
        user.Department = Blank(info.Department);
        user.Title = Blank(info.Title);
        user.GroupsJson = JsonSerializer.Serialize(info.Groups ?? new List<string>());
        user.Role = role ?? RelayClaims.RoleViewer;
        user.LastAdSync = now;
        user.OfflineFailures = 0;
        user.UpdatedAt = now;

        // The offline credential is written only after an online 200, never for admins, and only when the cache is enabled.
        user.PasswordHash = opts.OfflineCacheEnabled && role == RelayClaims.RoleViewer
            ? OfflineCredentialHasher.Hash(password, opts.OfflineCachePepper)
            : string.Empty;

        if (role == null)
        {
            user.PasswordHash = string.Empty;
            await db.SaveChangesAsync(ct);
            await UpsertAliasAsync(db, ctx.LoginName, user, ct);
            return Fail(ctx, LoginFailure.NotAuthorized, MsgNotAuthorized, ModeAuthenApi,
                $"User '{userKey}' authenticated but is not in AdminGroups or ViewerGroups ({info.Groups?.Count ?? 0} groups)", AuditSeverity.Warning);
        }

        if (!user.IsEnabled)
        {
            user.PasswordHash = string.Empty;
            await db.SaveChangesAsync(ct);
            await UpsertAliasAsync(db, ctx.LoginName, user, ct);
            return Fail(ctx, LoginFailure.NotAuthorized, MsgNotAuthorized, ModeAuthenApi, $"Console account '{userKey}' is disabled", AuditSeverity.Warning);
        }

        user.LastLogin = now;
        user.LastLoginIp = ctx.Ip;
        user.LastLoginMode = ModeAuthenApi;
        await db.SaveChangesAsync(ct);
        await UpsertAliasAsync(db, ctx.LoginName, user, ct);

        ResetFailures(ctx);
        return Succeed(ctx, user, user.Role, ModeAuthenApi, degraded: false,
            $"Verified by AuthenAPI as '{userKey}' (role {user.Role}; offline credential {(user.HasOfflineCredential ? "stored" : "not stored")})");
    }

    /// <summary>Steps 2 and 3 of the guide, used only when AuthenAPI could not be reached.</summary>
    private async Task<AdminLoginResult> FallbackAsync(LoginContext ctx, string password, string reason, AuthenApiOptions opts, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alias = await db.AdminUserAliases.Include(a => a.AdminUser).FirstOrDefaultAsync(a => a.LoginName == ctx.LoginName, ct);
        var user = alias?.AdminUser;
        if (user == null)
        {
            // Never logged in successfully with this name: nothing to authorize against.
            return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeOffline, $"{reason}; no cached profile for '{ctx.LoginName}'", AuditSeverity.Warning);
        }
        if (!user.IsEnabled)
        {
            return Fail(ctx, LoginFailure.NotAuthorized, MsgNotAuthorized, ModeOffline, $"{reason}; console account '{user.Username}' is disabled", AuditSeverity.Warning);
        }

        // ---- Step 2: one LDAPS bind with the UPN from the cache ----
        var upn = user.UserPrincipalName ?? (user.Username.Contains('@') ? user.Username : null);
        if (_directAd.IsEnabled && upn != null)
        {
            var ad = await _directAd.BindAsync(upn, password, ct);
            switch (ad.Outcome)
            {
                case DirectAdOutcome.Success:
                    await MarkLoginAsync(db, user, ctx, ModeDirectAd, ct);
                    ResetFailures(ctx);
                    return Succeed(ctx, user, RelayClaims.RoleViewer, ModeDirectAd, degraded: true,
                        $"{reason}; verified by direct LDAPS bind as '{upn}' ({ad.Detail}); degraded read-only session");

                case DirectAdOutcome.InvalidCredentials:
                    // The UPN came from the cache, so 49 means this person's password is wrong (or the account was locked/disabled).
                    await ClearOfflineCredentialAsync(db, user, ct);
                    RecordFailure(ctx);
                    return Fail(ctx, LoginFailure.InvalidCredentials, MsgInvalid, ModeDirectAd, $"{reason}; direct AD bind for '{upn}' rejected: {ad.Detail}");

                default:
                    _logger.LogWarning("Direct AD fallback unavailable: {Detail}", ad.Detail);
                    reason += $"; direct AD unavailable ({ad.Detail})";
                    break;
            }
        }

        // ---- Step 3: offline credential cache ----
        if (!opts.OfflineCacheEnabled)
            return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeOffline, $"{reason}; offline cache disabled", AuditSeverity.Warning);
        if (user.Role == RelayClaims.RoleAdmin || !user.HasOfflineCredential)
            return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeOffline, $"{reason}; no offline credential for '{user.Username}' (admins are never cached)", AuditSeverity.Warning);

        var window = TimeSpan.FromHours(opts.OfflineWindowHours);
        if (DateTime.UtcNow - user.LastAdSync > window)
        {
            await ClearOfflineCredentialAsync(db, user, ct);
            return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeOffline, $"{reason}; offline credential older than {opts.OfflineWindowHours} h", AuditSeverity.Warning);
        }

        if (_failures.Peek("offline-global", TimeSpan.FromMinutes(1)) >= Math.Max(1, opts.OfflineMaxFailuresPerMinute))
        {
            _audit.Security("OfflineLoginSprayGuard", AuditSeverity.Critical, $"Offline logins suspended: more than {opts.OfflineMaxFailuresPerMinute} wrong offline passwords in one minute", ctx.Ip, username: ctx.LoginName);
            return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeOffline, $"{reason}; offline attempts suspended (global limit)", AuditSeverity.Critical);
        }

        // Reserve one attempt atomically before checking the hash (guards concurrent guessing).
        var max = Math.Max(1, opts.OfflineMaxFailuresPerUser);
        var reserved = await db.AdminUsers
            .Where(u => u.Id == user.Id && u.OfflineFailures < max)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.OfflineFailures, u => u.OfflineFailures + 1), ct);
        if (reserved == 0)
        {
            return Fail(ctx, LoginFailure.Unavailable, MsgUnavailable, ModeOffline, $"{reason}; offline attempts exhausted for '{user.Username}' ({max})", AuditSeverity.Warning);
        }

        if (OfflineCredentialHasher.Verify(password, opts.OfflineCachePepper, user.PasswordHash))
        {
            await db.AdminUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.OfflineFailures, 0), ct);
            await MarkLoginAsync(db, user, ctx, ModeOffline, ct);
            ResetFailures(ctx);
            return Succeed(ctx, user, RelayClaims.RoleViewer, ModeOffline, degraded: true,
                $"{reason}; verified by offline credential (last online verification {user.LastAdSync:u}); degraded read-only session");
        }

        _failures.Increment("offline-global", TimeSpan.FromMinutes(1));
        RecordFailure(ctx);
        return Fail(ctx, LoginFailure.InvalidCredentials, MsgInvalid, ModeOffline, $"{reason}; offline credential did not match");
    }

    private async Task<AdminLoginResult> LocalAdminLoginAsync(LoginContext ctx, string password, AuthenApiOptions opts, CancellationToken ct)
    {
        if (!PasswordHasher.VerifyString(password, opts.LocalAdmin.PasswordHash))
        {
            RecordFailure(ctx);
            return Fail(ctx, LoginFailure.InvalidCredentials, MsgInvalid, ModeLocal, "Local administrator password did not match");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = ctx.LoginName;
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == key, ct)
                   ?? db.AdminUsers.Add(new AdminUser { Username = key, DisplayName = "Local Administrator" }).Entity;
        if (!user.IsEnabled && user.Source == "Local")
        {
            // the break-glass account cannot be locked out from the console
            user.IsEnabled = true;
        }
        user.Source = "Local";
        user.Role = RelayClaims.RoleAdmin;
        user.PasswordHash = string.Empty;
        user.LastAdSync = DateTime.UtcNow;
        await MarkLoginAsync(db, user, ctx, ModeLocal, ct);
        ResetFailures(ctx);
        return Succeed(ctx, user, RelayClaims.RoleAdmin, ModeLocal, degraded: false, "Break-glass local administrator");
    }

    // =====================================================================================
    // Users
    // =====================================================================================

    public string? ResolveRole(IEnumerable<string> groups)
    {
        var opts = _options.CurrentValue;
        var set = new HashSet<string>((groups ?? Enumerable.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(NormalizeGroup), StringComparer.OrdinalIgnoreCase);
        if (opts.AdminGroups.Count == 0) return RelayClaims.RoleAdmin;
        if (opts.AdminGroups.Any(g => set.Contains(NormalizeGroup(g)))) return RelayClaims.RoleAdmin;
        if (opts.ViewerGroups.Any(g => set.Contains(NormalizeGroup(g)))) return RelayClaims.RoleViewer;
        return null;
    }

    public async Task<bool> IsUserActiveAsync(string userKey, CancellationToken ct = default)
    {
        var key = (userKey ?? string.Empty).Trim().ToLowerInvariant();
        if (_cache.TryGetValue("adminuser-active:" + key, out bool active)) return active;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        active = await db.AdminUsers.AsNoTracking().Where(u => u.Username == key).Select(u => (bool?)u.IsEnabled).FirstOrDefaultAsync(ct) ?? false;
        _cache.Set("adminuser-active:" + key, active, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60), Size = 1 });
        return active;
    }

    public async Task<List<AdminUser>> ListUsersAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AdminUsers.AsNoTracking().Include(u => u.Aliases).OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task<bool> SetEnabledAsync(string userKey, bool enabled, string by, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = (userKey ?? string.Empty).Trim().ToLowerInvariant();
        var user = await db.AdminUsers.FirstOrDefaultAsync(x => x.Username == key, ct);
        if (user == null) return false;
        user.IsEnabled = enabled;
        user.UpdatedAt = DateTime.UtcNow;
        if (!enabled) { user.PasswordHash = string.Empty; user.OfflineFailures = 0; }
        await db.SaveChangesAsync(ct);
        _cache.Remove("adminuser-active:" + key);
        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = enabled ? "AdminUserEnabled" : "AdminUserDisabled", Username = by, TargetType = "AdminUser", TargetId = key,
            Details = $"{(enabled ? "Enabled" : "Disabled")} console user '{key}'" + (enabled ? string.Empty : "; active sessions end within a minute and the offline credential was removed") });
        return true;
    }

    public async Task<bool> ClearCacheAsync(string userKey, string by, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = (userKey ?? string.Empty).Trim().ToLowerInvariant();
        var user = await db.AdminUsers.FirstOrDefaultAsync(x => x.Username == key, ct);
        if (user == null) return false;
        await ClearOfflineCredentialAsync(db, user, ct);
        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = "AdminUserCacheCleared", Username = by, TargetType = "AdminUser", TargetId = key,
            Details = $"Offline credential of '{key}' removed" });
        return true;
    }

    public async Task<int> PurgeExpiredOfflineCredentialsAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(0, opts.OfflineWindowHours));
        var purged = await db.AdminUsers
            .Where(u => u.PasswordHash != "" && (!u.PasswordHash.StartsWith("v2$") || u.LastAdSync < cutoff || u.Role == RelayClaims.RoleAdmin || !opts.OfflineCacheEnabled))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "").SetProperty(u => u.OfflineFailures, 0), ct);
        if (purged > 0) _logger.LogInformation("Removed {Count} expired, legacy or disallowed offline credentials", purged);
        return purged;
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private sealed record LoginContext(string Typed, string LoginName, string Ip, string? UserAgent);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string NormalizeGroup(string g)
    {
        g = g.Trim();
        if (g.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            var comma = g.IndexOf(',');
            g = comma > 0 ? g[3..comma] : g[3..];
        }
        return g;
    }

    private static async Task UpsertAliasAsync(AppDbContext db, string loginName, AdminUser user, CancellationToken ct)
    {
        var alias = await db.AdminUserAliases.FirstOrDefaultAsync(a => a.LoginName == loginName, ct);
        if (alias == null)
        {
            db.AdminUserAliases.Add(new AdminUserAlias { LoginName = loginName, AdminUserId = user.Id });
        }
        else
        {
            alias.AdminUserId = user.Id; // a name may move to another account (e.g. a re-used short name)
            alias.LastUsedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task MarkLoginAsync(AppDbContext db, AdminUser user, LoginContext ctx, string mode, CancellationToken ct)
    {
        user.LastLogin = DateTime.UtcNow;
        user.LastLoginIp = ctx.Ip;
        user.LastLoginMode = mode;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ClearOfflineCredentialByLoginNameAsync(string loginName, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alias = await db.AdminUserAliases.Include(a => a.AdminUser).FirstOrDefaultAsync(a => a.LoginName == loginName, ct);
        if (alias != null) await ClearOfflineCredentialAsync(db, alias.AdminUser, ct);
    }

    private static async Task ClearOfflineCredentialAsync(AppDbContext db, AdminUser user, CancellationToken ct)
    {
        if (!user.HasOfflineCredential && user.OfflineFailures == 0) return;
        await db.AdminUsers.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "").SetProperty(u => u.OfflineFailures, 0), ct);
        user.PasswordHash = string.Empty;
        user.OfflineFailures = 0;
    }

    private AdminLoginResult? CheckThrottle(LoginContext ctx, AuthenApiOptions opts)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, opts.FailedAttemptWindowMinutes));
        var max = Math.Max(1, opts.MaxFailedAttempts);
        var userCount = _failures.Peek("user:" + ctx.LoginName, window);
        var ipCount = _failures.Peek("ip:" + ctx.Ip, window);
        if (userCount < max && ipCount < max) return null;

        var by = userCount >= max ? "username" : "IP address";
        var retry = _failures.SecondsUntilReset(userCount >= max ? "user:" + ctx.LoginName : "ip:" + ctx.Ip, window);
        var minutes = Math.Max(1, (int)Math.Ceiling(retry / 60.0));
        return Fail(ctx, LoginFailure.Throttled, $"Too many failed sign-in attempts. Please wait {minutes} minute(s) and try again.", "throttle",
            $"Login blocked locally: {max} failures within {window.TotalMinutes} min for this {by} (AuthenAPI not called)", AuditSeverity.Warning, retry);
    }

    private void RecordFailure(LoginContext ctx)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.FailedAttemptWindowMinutes));
        _failures.Increment("user:" + ctx.LoginName, window);
        _failures.Increment("ip:" + ctx.Ip, window);
        _anomaly.RecordAuthFailure(ctx.Ip, $"Console login failed for '{ctx.LoginName}'");
    }

    private void ResetFailures(LoginContext ctx) => _failures.Reset("user:" + ctx.LoginName);

    private AdminLoginResult Succeed(LoginContext ctx, AdminUser user, string role, string mode, bool degraded, string details)
    {
        _audit.Log(new AuditLog
        {
            Category = AuditCategory.Authentication,
            Severity = degraded ? AuditSeverity.Warning : AuditSeverity.Info,
            Action = degraded ? "AdminLoginDegraded" : "AdminLogin",
            Success = true,
            Username = user.Username,
            IpAddress = ctx.Ip,
            UserAgent = ctx.UserAgent,
            Details = $"Typed '{ctx.Typed}' → '{user.Username}' via {mode}; {details}"
        });
        _logger.LogInformation("Console login: {User} via {Mode}{Degraded} from {Ip}", user.Username, mode, degraded ? " (degraded)" : string.Empty, ctx.Ip);
        return new AdminLoginResult(true, LoginFailure.None, null, user, role, mode, degraded);
    }

    private AdminLoginResult Fail(LoginContext ctx, LoginFailure failure, string message, string mode, string details, AuditSeverity severity = AuditSeverity.Warning, int? retryAfter = null)
    {
        _audit.Log(new AuditLog
        {
            Category = AuditCategory.Authentication,
            Severity = severity,
            Action = "AdminLogin",
            Success = false,
            Username = ctx.LoginName,
            IpAddress = ctx.Ip,
            UserAgent = ctx.UserAgent,
            Details = $"{failure} via {mode}: {details}"
        });
        return new AdminLoginResult(false, failure, message, null, RelayClaims.RoleViewer, mode, false, retryAfter);
    }
}
