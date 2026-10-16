using EmailRelayAPI.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace EmailRelayAPI.Security;

public sealed record RateLimitDecision(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds);

public interface IRateLimitService
{
    RateLimitDecision Check(string key, int limitPerWindow, TimeSpan window);
    double Peek(string key, TimeSpan window);
}

/// <summary>Per-key sliding window rate limiter.</summary>
public class RateLimitService : IRateLimitService
{
    private readonly SlidingCounterStore _store = new();

    public RateLimitDecision Check(string key, int limitPerWindow, TimeSpan window)
    {
        if (limitPerWindow <= 0) return new RateLimitDecision(true, 0, int.MaxValue, 0);

        var current = _store.Peek(key, window);
        if (current >= limitPerWindow)
        {
            return new RateLimitDecision(false, limitPerWindow, 0, _store.SecondsUntilReset(key, window));
        }

        var after = _store.Increment(key, window);
        var remaining = Math.Max(0, limitPerWindow - (int)Math.Ceiling(after));
        return new RateLimitDecision(true, limitPerWindow, remaining, 0);
    }

    public double Peek(string key, TimeSpan window) => _store.Peek(key, window);
}

public interface IDailyQuotaService
{
    /// <summary>Returns the number of messages accepted today (UTC) for the client.</summary>
    Task<int> GetUsedTodayAsync(int clientId, CancellationToken ct = default);

    /// <summary>Reserves <paramref name="count"/> messages against the quota. Returns false when the quota would be exceeded.</summary>
    Task<bool> TryConsumeAsync(int clientId, int quota, int count, CancellationToken ct = default);
}

/// <summary>Daily quota counters, seeded from the database on first use each day.</summary>
public class DailyQuotaService : IDailyQuotaService
{
    private readonly ConcurrentDictionary<(int ClientId, DateOnly Day), int> _counters = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public DailyQuotaService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<int> GetUsedTodayAsync(int clientId, CancellationToken ct = default)
    {
        var key = (clientId, DateOnly.FromDateTime(DateTime.UtcNow));
        if (_counters.TryGetValue(key, out var used)) return used;
        return await SeedAsync(key, ct);
    }

    public async Task<bool> TryConsumeAsync(int clientId, int quota, int count, CancellationToken ct = default)
    {
        if (quota <= 0) return true;

        var key = (clientId, DateOnly.FromDateTime(DateTime.UtcNow));
        if (!_counters.ContainsKey(key)) await SeedAsync(key, ct);

        while (true)
        {
            var current = _counters.GetOrAdd(key, 0);
            if (current + count > quota) return false;
            if (_counters.TryUpdate(key, current + count, current)) return true;
        }
    }

    private async Task<int> SeedAsync((int ClientId, DateOnly Day) key, CancellationToken ct)
    {
        await _seedLock.WaitAsync(ct);
        try
        {
            if (_counters.TryGetValue(key, out var existing)) return existing;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var start = key.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var end = start.AddDays(1);
            var used = await db.Messages.CountAsync(m => m.ClientId == key.ClientId && m.CreatedAt >= start && m.CreatedAt < end, ct);
            _counters[key] = used;

            // forget yesterday
            foreach (var k in _counters.Keys.Where(k => k.Day < key.Day).ToList())
            {
                _counters.TryRemove(k, out _);
            }

            return used;
        }
        finally
        {
            _seedLock.Release();
        }
    }
}
