using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Tests;

public sealed class TestOptions<T> : IOptionsMonitor<T>
{
    public TestOptions(T value) => CurrentValue = value;
    public T CurrentValue { get; set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public sealed class FakeAuthenApi : IAuthenApiClient
{
    public Func<string, string, AuthenResult> Handler { get; set; } = (_, _) => new AuthenResult(AuthenOutcome.Unreachable, null, "not set");
    public List<string> SentUsernames { get; } = new();
    public int Calls => SentUsernames.Count;
    public bool IsConfigured => true;

    public Task<AuthenResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        lock (SentUsernames) SentUsernames.Add(username);
        return Task.FromResult(Handler(username, password));
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
}

public sealed class FakeDirectAd : IDirectAdAuthenticator
{
    public bool IsEnabled { get; set; }
    public Func<string, string, DirectAdResult> Handler { get; set; } = (_, _) => new DirectAdResult(DirectAdOutcome.Unavailable, "down");
    public int Calls;
    public List<string> BoundAs { get; } = new();

    public Task<DirectAdResult> BindAsync(string userPrincipalName, string password, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        lock (BoundAs) BoundAs.Add(userPrincipalName);
        return Task.FromResult(Handler(userPrincipalName, password));
    }
}

public sealed class NoopAnomaly : IAnomalyDetector
{
    public void RecordAuthFailure(string ip, string? details, string? traceId = null) { }
    public void RecordRequest(string ip, string? traceId = null) { }
    public void RecordClientRequest(int clientId, string ip, string? traceId = null) { }
    public void RecordClientError(int clientId, int statusCode, string path) { }
    public bool RecordMessage(int clientId, string contentHash, int recipientCount, string ip, string? traceId = null) => false;
    public Task RecordClientIpAsync(int clientId, string clientName, string ip, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class RecordingAlerts : IAlertService
{
    public List<string> Raised { get; } = new();
    public Task RaiseAsync(string type, string title, string message, CancellationToken ct = default)
    {
        lock (Raised) Raised.Add(type);
        return Task.CompletedTask;
    }
}

/// <summary>An AdminAuthService wired to a fresh SQLite file, a scripted AuthenAPI and a scripted directory.</summary>
public sealed class LoginHarness : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;

    public FakeAuthenApi Api { get; } = new();
    public FakeDirectAd DirectAd { get; } = new();
    public RecordingAlerts Alerts { get; } = new();
    public AuthenApiOptions Options { get; }
    public AuthenCircuitBreaker Breaker { get; }
    public AdminAuthService Service { get; }

    public const string Pepper = "unit-test-pepper-0123456789abcdefghijklmnop";

    public LoginHarness(Action<AuthenApiOptions>? configure = null)
    {
        Options = new AuthenApiOptions
        {
            BaseUrl = "https://authen.example.test",
            ApiKey = "key",
            AdminGroups = new() { "TH-RelayAdmins" },
            ViewerGroups = new() { "TH-IT" },
            OfflineWindowHours = 24,
            OfflineCachePepper = Pepper
        };
        configure?.Invoke(Options);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        var monitor = new TestOptions<AuthenApiOptions>(Options);
        Breaker = new AuthenCircuitBreaker(monitor);
        Service = new AdminAuthService(
            _provider.GetRequiredService<IServiceScopeFactory>(), Api, Breaker, DirectAd, monitor,
            new AuditService(NullLogger<AuditService>.Instance), new NoopAnomaly(), Alerts,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<AdminAuthService>.Instance);
    }

    public static AuthenResult Ok(string upn, string sam, params string[] groups)
        => new(AuthenOutcome.Success, new AuthenUserInfo { UserPrincipalName = upn, SamAccountName = sam, DisplayName = sam, Groups = groups.ToList() }, "ok", 200);

    public static AuthenResult Result(AuthenOutcome outcome, int? status = null) => new(outcome, null, outcome.ToString(), status);

    public Task<AdminLoginResult> Login(string user, string password, string ip = "10.0.0.1") => Service.LoginAsync(user, password, ip, "test");

    public T Db<T>(Func<AppDbContext, T> query)
    {
        using var scope = _provider.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
