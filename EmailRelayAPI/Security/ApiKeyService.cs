using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Security;

public enum ApiKeyFailure
{
    None,
    Malformed,
    NotFound,
    InvalidSecret,
    Revoked,
    Expired,
    ClientDisabled
}

public sealed record ApiKeyValidation(ApiKeyFailure Failure, ClientContext? Context)
{
    public bool Success => Failure == ApiKeyFailure.None && Context != null;
}

public sealed record GeneratedKey(string PlainKey, string Prefix, string Hash);

public interface IApiKeyService
{
    GeneratedKey Generate();
    string Hash(string plainKey);
    Task<ApiKeyValidation> ValidateAsync(string plainKey, CancellationToken ct = default);
    void TouchUsage(int keyId, string ip);
    Task FlushUsageAsync(CancellationToken ct = default);

    /// <summary>Invalidate all cached lookups (call after any client / key change).</summary>
    void InvalidateCache();
}

/// <summary>Generates, hashes and validates API keys with a short in-memory cache.</summary>
public class ApiKeyService : IApiKeyService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<SecurityOptions> _security;
    private readonly IOptionsMonitor<ApiKeyOptions> _keyOptions;
    private readonly ILogger<ApiKeyService> _logger;
    private readonly ConcurrentDictionary<int, (DateTime At, string Ip, long Count)> _usage = new();
    private int _generation;

    public const int PrefixLength = 12;

    public ApiKeyService(IServiceScopeFactory scopeFactory, IMemoryCache cache, IOptionsMonitor<SecurityOptions> security, IOptionsMonitor<ApiKeyOptions> keyOptions, ILogger<ApiKeyService> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _security = security;
        _keyOptions = keyOptions;
        _logger = logger;
    }

    public GeneratedKey Generate()
    {
        var prefix = _keyOptions.CurrentValue.KeyPrefix;
        var random = RandomNumberGenerator.GetBytes(32);
        var body = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = prefix + body;
        return new GeneratedKey(key, key[..PrefixLength], Hash(key));
    }

    public string Hash(string plainKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey))).ToLowerInvariant();

    public async Task<ApiKeyValidation> ValidateAsync(string plainKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plainKey) || plainKey.Length < PrefixLength + 8 || plainKey.Length > 200)
            return new ApiKeyValidation(ApiKeyFailure.Malformed, null);

        var prefix = plainKey[..PrefixLength];
        var generation = Volatile.Read(ref _generation);
        var cacheKey = $"apikey:{generation}:{prefix}";

        var cached = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(5, _security.CurrentValue.ApiKeyCacheSeconds));
            entry.Size = 1;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.ApiKeys.AsNoTracking()
                .Include(k => k.Client).ThenInclude(c => c.DefaultSenderProfile)
                .FirstOrDefaultAsync(k => k.Prefix == prefix, ct);
        });

        if (cached == null) return new ApiKeyValidation(ApiKeyFailure.NotFound, null);

        var actual = Encoding.UTF8.GetBytes(Hash(plainKey));
        var expected = Encoding.UTF8.GetBytes(cached.KeyHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return new ApiKeyValidation(ApiKeyFailure.InvalidSecret, null);

        if (cached.RevokedAt != null) return new ApiKeyValidation(ApiKeyFailure.Revoked, null);
        if (cached.ExpiresAt != null && cached.ExpiresAt <= DateTime.UtcNow) return new ApiKeyValidation(ApiKeyFailure.Expired, null);
        if (!cached.Client.IsActive) return new ApiKeyValidation(ApiKeyFailure.ClientDisabled, null);

        return new ApiKeyValidation(ApiKeyFailure.None, new ClientContext { Client = cached.Client, Key = cached });
    }

    public void TouchUsage(int keyId, string ip)
    {
        _usage.AddOrUpdate(keyId, _ => (DateTime.UtcNow, ip, 1), (_, old) => (DateTime.UtcNow, ip, old.Count + 1));
    }

    public async Task FlushUsageAsync(CancellationToken ct = default)
    {
        if (_usage.IsEmpty) return;
        var snapshot = _usage.ToArray();
        foreach (var kv in snapshot) _usage.TryRemove(kv.Key, out _);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = snapshot.Select(s => s.Key).ToList();
            var keys = await db.ApiKeys.Where(k => ids.Contains(k.Id)).ToListAsync(ct);
            foreach (var k in keys)
            {
                var u = snapshot.First(s => s.Key == k.Id).Value;
                k.LastUsedAt = u.At;
                k.LastUsedIp = u.Ip;
                k.UseCount += u.Count;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush API key usage");
        }
    }

    public void InvalidateCache() => Interlocked.Increment(ref _generation);
}
