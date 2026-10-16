using System.Net;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Security;

public interface IBlockListService
{
    /// <summary>Returns the effective block entry that matches, or null.</summary>
    BlockEntry? Check(IPAddress? ip, int? clientId = null, string? keyPrefix = null);
    Task<BlockEntry> BlockAsync(BlockTargetType type, string target, string reason, TimeSpan? duration, string createdBy, bool permanent = false, CancellationToken ct = default);
    Task<bool> UnblockAsync(int id, string removedBy, CancellationToken ct = default);
    Task RefreshAsync(CancellationToken ct = default);
    IReadOnlyList<BlockEntry> Snapshot();
}

/// <summary>In-memory mirror of active block entries with periodic refresh from the database.</summary>
public class BlockListService : IBlockListService, IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BlockListService> _logger;
    private readonly object _lock = new();
    private List<BlockEntry> _entries = new();
    private readonly Dictionary<int, long> _hitDeltas = new();
    private Timer? _timer;

    public BlockListService(IServiceScopeFactory scopeFactory, ILogger<BlockListService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(async _ =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Block list refresh failed"); }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public BlockEntry? Check(IPAddress? ip, int? clientId = null, string? keyPrefix = null)
    {
        var now = DateTime.UtcNow;
        List<BlockEntry> entries;
        lock (_lock) entries = _entries;

        foreach (var e in entries)
        {
            if (!e.IsEffective(now)) continue;
            var match = e.TargetType switch
            {
                BlockTargetType.Ip => ip != null && IpUtils.TryParseCidr(e.Target, out var net, out var prefix) && IpUtils.IsInRange(ip, net, prefix),
                BlockTargetType.Client => clientId.HasValue && e.Target == clientId.Value.ToString(),
                BlockTargetType.ApiKey => keyPrefix != null && string.Equals(e.Target, keyPrefix, StringComparison.Ordinal),
                _ => false
            };
            if (match)
            {
                lock (_lock)
                {
                    e.HitCount++;
                    _hitDeltas[e.Id] = _hitDeltas.GetValueOrDefault(e.Id) + 1;
                }
                return e;
            }
        }
        return null;
    }

    public async Task<BlockEntry> BlockAsync(BlockTargetType type, string target, string reason, TimeSpan? duration, string createdBy, bool permanent = false, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // extend an existing active block instead of stacking duplicates
        var now = DateTime.UtcNow;
        var existing = await db.BlockEntries
            .Where(b => b.TargetType == type && b.Target == target && b.RemovedAt == null)
            .ToListAsync(ct);
        var active = existing.FirstOrDefault(b => b.IsEffective(now));
        if (active != null)
        {
            if (permanent) { active.IsPermanent = true; active.ExpiresAt = null; }
            else if (!active.IsPermanent && duration.HasValue)
            {
                var newExpiry = now.Add(duration.Value);
                if (active.ExpiresAt == null || active.ExpiresAt < newExpiry) active.ExpiresAt = newExpiry;
            }
            active.Reason = reason;
            await db.SaveChangesAsync(ct);
            await RefreshAsync(ct);
            return active;
        }

        var entry = new BlockEntry
        {
            TargetType = type,
            Target = target,
            Reason = reason.Length > 500 ? reason[..500] : reason,
            CreatedBy = createdBy,
            IsPermanent = permanent,
            ExpiresAt = permanent ? null : now.Add(duration ?? TimeSpan.FromMinutes(30))
        };
        db.BlockEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        await RefreshAsync(ct);
        _logger.LogWarning("Blocked {Type} {Target} until {Expires} ({Reason}) by {By}", type, target, permanent ? "permanent" : (object?)entry.ExpiresAt, reason, createdBy);
        return entry;
    }

    public async Task<bool> UnblockAsync(int id, string removedBy, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.BlockEntries.FindAsync(new object[] { id }, ct);
        if (entry == null || entry.RemovedAt != null) return false;
        entry.RemovedAt = DateTime.UtcNow;
        entry.RemovedBy = removedBy;
        await db.SaveChangesAsync(ct);
        await RefreshAsync(ct);
        return true;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Dictionary<int, long> deltas;
        lock (_lock)
        {
            deltas = new Dictionary<int, long>(_hitDeltas);
            _hitDeltas.Clear();
        }

        if (deltas.Count > 0)
        {
            var ids = deltas.Keys.ToList();
            var rows = await db.BlockEntries.Where(b => ids.Contains(b.Id)).ToListAsync(ct);
            foreach (var r in rows) r.HitCount += deltas[r.Id];
            await db.SaveChangesAsync(ct);
        }

        var now = DateTime.UtcNow;
        var fresh = await db.BlockEntries.AsNoTracking()
            .Where(b => b.RemovedAt == null && (b.IsPermanent || b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        lock (_lock) _entries = fresh;
    }

    public IReadOnlyList<BlockEntry> Snapshot()
    {
        lock (_lock) return _entries.ToList();
    }

    public void Dispose() => _timer?.Dispose();
}
