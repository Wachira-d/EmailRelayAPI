using System.Collections.Concurrent;
using System.Text.Json;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public sealed record AdminLoginResult(bool Success, string? Error, AdminUser? User, string Mode, int? LockoutSecondsRemaining = null);

public interface IAdminAuthService
{
    /// <summary>
    /// Hybrid login (AuthenAPI CLIENT_INTEGRATION_GUIDE): verify against the local cache first, fall back to AuthenAPI,
    /// refresh the cache on success and allow offline logins when the directory is unreachable.
    /// </summary>
    Task<AdminLoginResult> LoginAsync(string username, string password, string ip, string? userAgent, CancellationToken ct = default);
    Task<AdminUser?> GetUserAsync(string username, CancellationToken ct = default);
    Task<List<AdminUser>> ListUsersAsync(CancellationToken ct = default);
    Task<bool> SetEnabledAsync(string username, bool enabled, string by, CancellationToken ct = default);
    Task<bool> ClearCacheAsync(string username, string by, CancellationToken ct = default);
    string ResolveRole(IEnumerable<string> groups);
}

public class AdminAuthService : IAdminAuthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuthenApiClient _authen;
    private readonly IOptionsMonitor<AuthenApiOptions> _options;
    private readonly IAuditService _audit;
    private readonly IAnomalyDetector _anomaly;
    private readonly ILogger<AdminAuthService> _logger;
    private readonly ConcurrentDictionary<string, (int Failures, DateTime? LockedUntil)> _memoryLockouts = new();

    public AdminAuthService(IServiceScopeFactory scopeFactory, IAuthenApiClient authen, IOptionsMonitor<AuthenApiOptions> options, IAuditService audit, IAnomalyDetector anomaly, ILogger<AdminAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _authen = authen;
        _options = options;
        _audit = audit;
        _anomaly = anomaly;
        _logger = logger;
    }

    public async Task<AdminLoginResult> LoginAsync(string username, string password, string ip, string? userAgent, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        username = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return Failed(username, ip, userAgent, "Username and password are required", "none");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var cached = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == username, ct);

        // ---- lockout ----
        var lockedUntil = cached?.LockedUntil ?? (_memoryLockouts.TryGetValue(username, out var ml) ? ml.LockedUntil : null);
        if (lockedUntil.HasValue && lockedUntil.Value > now)
        {
            var remaining = (int)Math.Ceiling((lockedUntil.Value - now).TotalSeconds);
            return Failed(username, ip, userAgent, $"Account is temporarily locked. Try again in {remaining} seconds.", "locked", remaining);
        }

        // ---- break-glass local admin ----
        if (opts.LocalAdmin.Enabled && username.Equals(NormalizeUsername(opts.LocalAdmin.Username), StringComparison.OrdinalIgnoreCase))
        {
            if (PasswordHasher.VerifyString(password, opts.LocalAdmin.PasswordHash))
            {
                cached ??= db.AdminUsers.Add(new AdminUser { Username = username, Source = "Local", Role = RelayClaims.RoleAdmin }).Entity;
                cached.Source = "Local";
                cached.Role = RelayClaims.RoleAdmin;
                cached.DisplayName ??= "Local Administrator";
                cached.IsEnabled = true;
                cached.Salt = string.IsNullOrEmpty(cached.Salt) ? PasswordHasher.GenerateSalt() : cached.Salt;
                cached.PasswordHash = PasswordHasher.Hash(password, cached.Salt, cached.Iterations);
                cached.CacheExpires = now.AddDays(Math.Max(1, opts.CacheDurationDays));
                cached.LastAdSync = now;
                await RecordSuccessAsync(db, cached, ip, ct);
                return Succeeded(cached, ip, userAgent, "local");
            }
            await RecordFailureAsync(db, cached, username, ip, ct);
            return Failed(username, ip, userAgent, "Invalid username or password", "local");
        }

        // ---- local cache hit ----
        if (opts.CacheEnabled && cached != null && cached.Source != "Local" && cached.IsEnabled && cached.CacheExpires > now
            && PasswordHasher.Verify(password, cached.Salt, cached.Iterations, cached.PasswordHash))
        {
            await RecordSuccessAsync(db, cached, ip, ct);
            return Succeeded(cached, ip, userAgent, "cache");
        }

        // ---- AuthenAPI ----
        var result = await _authen.AuthenticateAsync(username, password, ct);
        switch (result.Outcome)
        {
            case AuthenOutcome.Success:
            {
                var user = result.User!;
                var role = ResolveRoleOrNull(user.Groups);
                if (role == null)
                {
                    _audit.Log(new AuditLog
                    {
                        Category = AuditCategory.Authentication, Severity = AuditSeverity.Warning, Action = "AdminLogin", Success = false,
                        Username = username, IpAddress = ip, UserAgent = userAgent,
                        Details = $"Authenticated by AuthenAPI but not a member of any mapped group ({user.Groups.Count} groups)"
                    });
                    return new AdminLoginResult(false, "Your account is not authorized to use this console", null, "authenapi");
                }
                if (!user.IsEnabled || user.IsLocked)
                    return Failed(username, ip, userAgent, "Directory account is disabled or locked", "authenapi");

                cached ??= db.AdminUsers.Add(new AdminUser { Username = username }).Entity;
                cached.Source = "AuthenApi";
                cached.DisplayName = user.DisplayName ?? username;
                cached.Email = user.Email;
                cached.Department = user.Department;
                cached.Title = user.Title;
                cached.GroupsJson = JsonSerializer.Serialize(user.Groups);
                cached.Role = role;
                cached.IsEnabled = true;
                cached.Salt = PasswordHasher.GenerateSalt();
                cached.Iterations = PasswordHasher.DefaultIterations;
                cached.PasswordHash = PasswordHasher.Hash(password, cached.Salt, cached.Iterations);
                cached.LastAdSync = now;
                cached.CacheExpires = opts.CacheEnabled ? now.AddDays(Math.Max(1, opts.CacheDurationDays)) : now;
                await RecordSuccessAsync(db, cached, ip, ct);
                return Succeeded(cached, ip, userAgent, "authenapi");
            }

            case AuthenOutcome.InvalidCredentials:
                await RecordFailureAsync(db, cached, username, ip, ct);
                return Failed(username, ip, userAgent, "Invalid username or password", "authenapi");

            default:
            {
                // AuthenAPI unavailable or not configured: offline login from cache if permitted.
                if (opts.AllowOfflineLogin && cached != null && cached.IsEnabled && cached.Source != "Local"
                    && PasswordHasher.Verify(password, cached.Salt, cached.Iterations, cached.PasswordHash))
                {
                    _audit.Log(new AuditLog
                    {
                        Category = AuditCategory.Authentication, Severity = AuditSeverity.Warning, Action = "AdminLoginOffline", Success = true,
                        Username = username, IpAddress = ip, UserAgent = userAgent,
                        Details = $"AuthenAPI unavailable ({result.Message}); login accepted from local cache (last AD sync {cached.LastAdSync:u})"
                    });
                    await RecordSuccessAsync(db, cached, ip, ct);
                    return Succeeded(cached, ip, userAgent, "offline-cache", audit: false);
                }

                if (cached != null) await RecordFailureAsync(db, cached, username, ip, ct);
                var msg = result.Outcome == AuthenOutcome.NotConfigured
                    ? "AuthenAPI is not configured and no local administrator is enabled"
                    : "Authentication service is unavailable. Please try again later.";
                return Failed(username, ip, userAgent, msg, "unavailable");
            }
        }
    }

    public string ResolveRole(IEnumerable<string> groups) => ResolveRoleOrNull(groups) ?? RelayClaims.RoleViewer;

    private string? ResolveRoleOrNull(IEnumerable<string> groups)
    {
        var opts = _options.CurrentValue;
        var set = new HashSet<string>(groups.Select(NormalizeGroup), StringComparer.OrdinalIgnoreCase);
        if (opts.AdminGroups.Count == 0) return RelayClaims.RoleAdmin;
        if (opts.AdminGroups.Any(g => set.Contains(NormalizeGroup(g)))) return RelayClaims.RoleAdmin;
        if (opts.ViewerGroups.Any(g => set.Contains(NormalizeGroup(g)))) return RelayClaims.RoleViewer;
        return null;
    }

    private static string NormalizeGroup(string g)
    {
        // accept both "CN=Group,OU=..." and plain "Group"
        g = g.Trim();
        if (g.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            var comma = g.IndexOf(',');
            g = comma > 0 ? g[3..comma] : g[3..];
        }
        return g;
    }

    public async Task<AdminUser?> GetUserAsync(string username, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = NormalizeUsername(username);
        return await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(x => x.Username == u, ct);
    }

    public async Task<List<AdminUser>> ListUsersAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AdminUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task<bool> SetEnabledAsync(string username, bool enabled, string by, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = NormalizeUsername(username);
        var user = await db.AdminUsers.FirstOrDefaultAsync(x => x.Username == u, ct);
        if (user == null) return false;
        user.IsEnabled = enabled;
        user.UpdatedAt = DateTime.UtcNow;
        if (enabled) { user.FailedAttempts = 0; user.LockedUntil = null; }
        await db.SaveChangesAsync(ct);
        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = enabled ? "AdminUserEnabled" : "AdminUserDisabled", Username = by, TargetType = "AdminUser", TargetId = u, Details = $"{(enabled ? "Enabled" : "Disabled")} console user '{u}'" });
        return true;
    }

    public async Task<bool> ClearCacheAsync(string username, string by, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = NormalizeUsername(username);
        var user = await db.AdminUsers.FirstOrDefaultAsync(x => x.Username == u, ct);
        if (user == null) return false;
        user.CacheExpires = DateTime.UtcNow.AddMinutes(-1);
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _memoryLockouts.TryRemove(u, out _);
        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = "AdminUserCacheCleared", Username = by, TargetType = "AdminUser", TargetId = u, Details = $"Credential cache for '{u}' invalidated; next login must be verified by AuthenAPI" });
        return true;
    }

    // ---------------- helpers ----------------

    public static string NormalizeUsername(string? username)
    {
        var u = (username ?? string.Empty).Trim().ToLowerInvariant();
        // strip DOMAIN\ prefix
        var slash = u.LastIndexOf('\\');
        return slash >= 0 ? u[(slash + 1)..] : u;
    }

    private async Task RecordSuccessAsync(AppDbContext db, AdminUser user, string ip, CancellationToken ct)
    {
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        user.LastLogin = DateTime.UtcNow;
        user.LastLoginIp = ip;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _memoryLockouts.TryRemove(user.Username, out _);
    }

    private async Task RecordFailureAsync(AppDbContext db, AdminUser? user, string username, string ip, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var max = Math.Max(1, opts.MaxFailedAttempts);
        var lockFor = TimeSpan.FromMinutes(Math.Max(1, opts.LockoutMinutes));

        if (user != null)
        {
            user.FailedAttempts += 1;
            if (user.FailedAttempts >= max)
            {
                user.LockedUntil = DateTime.UtcNow.Add(lockFor);
                user.FailedAttempts = 0;
                _audit.Security("AdminAccountLocked", AuditSeverity.Critical, $"Console account '{username}' locked for {lockFor.TotalMinutes} min after {max} failed logins from {ip}", ip, username: username);
            }
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var entry = _memoryLockouts.AddOrUpdate(username, _ => (1, null), (_, old) => (old.Failures + 1, old.LockedUntil));
            if (entry.Failures >= max)
            {
                _memoryLockouts[username] = (0, DateTime.UtcNow.Add(lockFor));
                _audit.Security("AdminAccountLocked", AuditSeverity.Critical, $"Unknown console account '{username}' locked for {lockFor.TotalMinutes} min after {max} failed logins from {ip}", ip, username: username);
            }
        }

        _anomaly.RecordAuthFailure(ip, $"Admin login failed for '{username}'");
    }

    private AdminLoginResult Succeeded(AdminUser user, string ip, string? userAgent, string mode, bool audit = true)
    {
        if (audit)
        {
            _audit.Log(new AuditLog
            {
                Category = AuditCategory.Authentication, Action = "AdminLogin", Success = true,
                Username = user.Username, IpAddress = ip, UserAgent = userAgent,
                Details = $"Login successful via {mode} (role {user.Role})"
            });
        }
        _logger.LogInformation("Admin login: {User} via {Mode} from {Ip}", user.Username, mode, ip);
        return new AdminLoginResult(true, null, user, mode);
    }

    private AdminLoginResult Failed(string username, string ip, string? userAgent, string error, string mode, int? lockoutSeconds = null)
    {
        _audit.Log(new AuditLog
        {
            Category = AuditCategory.Authentication, Severity = AuditSeverity.Warning, Action = "AdminLogin", Success = false,
            Username = username, IpAddress = ip, UserAgent = userAgent, Details = $"{error} (mode {mode})"
        });
        return new AdminLoginResult(false, error, null, mode, lockoutSeconds);
    }
}
