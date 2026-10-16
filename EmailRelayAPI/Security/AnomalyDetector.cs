using System.Collections.Concurrent;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Security;

public interface IAnomalyDetector
{
    /// <summary>Record a failed authentication from an IP. May auto-block the IP.</summary>
    void RecordAuthFailure(string ip, string? details, string? traceId = null);

    /// <summary>Record an incoming request before authentication (burst detection per IP).</summary>
    void RecordRequest(string ip, string? traceId = null);

    /// <summary>Record an authenticated request (burst detection per client).</summary>
    void RecordClientRequest(int clientId, string ip, string? traceId = null);

    /// <summary>Record a client error response (4xx) for a client.</summary>
    void RecordClientError(int clientId, int statusCode, string path);

    /// <summary>
    /// Record an accepted message. Returns true when the message should be rejected as an abusive duplicate.
    /// </summary>
    bool RecordMessage(int clientId, string contentHash, int recipientCount, string ip, string? traceId = null);

    /// <summary>Checks whether the client has used this IP before; logs a security event the first time.</summary>
    Task RecordClientIpAsync(int clientId, string clientName, string ip, CancellationToken ct = default);
}

/// <summary>Detects abusive usage patterns (floods, brute force, duplicate storms, recipient spikes) and reacts.</summary>
public class AnomalyDetector : IAnomalyDetector
{
    private readonly SlidingCounterStore _counters = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAlert = new();
    private readonly ConcurrentDictionary<int, HashSet<string>> _seenIps = new();
    private readonly IOptionsMonitor<AnomalyOptions> _options;
    private readonly IAuditService _audit;
    private readonly IBlockListService _blocks;
    private readonly IAlertService _alerts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnomalyDetector> _logger;

    public AnomalyDetector(IOptionsMonitor<AnomalyOptions> options, IAuditService audit, IBlockListService blocks, IAlertService alerts, IServiceScopeFactory scopeFactory, ILogger<AnomalyDetector> logger)
    {
        _options = options;
        _audit = audit;
        _blocks = blocks;
        _alerts = alerts;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private AnomalyOptions O => _options.CurrentValue;

    public void RecordAuthFailure(string ip, string? details, string? traceId = null)
    {
        if (!O.Enabled) return;
        var window = TimeSpan.FromMinutes(O.AuthFailureWindowMinutes);
        var count = _counters.Increment($"authfail:{ip}", window);
        if (count >= O.AuthFailureThreshold && ShouldAlert($"authfail:{ip}", window))
        {
            Raise("AuthFailureFlood", AuditSeverity.Critical,
                $"{(int)count} failed authentications from {ip} within {O.AuthFailureWindowMinutes} min. {details}",
                ip, null, traceId);
            AutoBlock(BlockTargetType.Ip, ip, $"Repeated authentication failures ({(int)count} in {O.AuthFailureWindowMinutes} min)");
        }
    }

    public void RecordRequest(string ip, string? traceId = null)
    {
        if (!O.Enabled) return;
        var window = TimeSpan.FromSeconds(O.BurstWindowSeconds);
        var ipCount = _counters.Increment($"burst:ip:{ip}", window);
        if (ipCount >= O.BurstThreshold && ShouldAlert($"burst:ip:{ip}", window))
        {
            Raise("RequestBurst", AuditSeverity.Critical,
                $"{(int)ipCount} requests from {ip} within {O.BurstWindowSeconds}s (threshold {O.BurstThreshold})", ip, null, traceId);
            AutoBlock(BlockTargetType.Ip, ip, $"Request burst ({(int)ipCount} in {O.BurstWindowSeconds}s)");
        }
    }

    public void RecordClientRequest(int clientId, string ip, string? traceId = null)
    {
        if (!O.Enabled) return;
        var window = TimeSpan.FromSeconds(O.BurstWindowSeconds);
        var cCount = _counters.Increment($"burst:client:{clientId}", window);
        if (cCount >= O.BurstThreshold && ShouldAlert($"burst:client:{clientId}", window))
        {
            Raise("ClientRequestBurst", AuditSeverity.Warning,
                $"Client {clientId} sent {(int)cCount} requests within {O.BurstWindowSeconds}s from {ip}", ip, clientId, traceId);
        }
    }

    public void RecordClientError(int clientId, int statusCode, string path)
    {
        if (!O.Enabled) return;
        var window = TimeSpan.FromMinutes(O.ErrorRateWindowMinutes);
        var count = _counters.Increment($"err:client:{clientId}", window);
        if (count >= O.ErrorRateThreshold && ShouldAlert($"err:client:{clientId}", window))
        {
            Raise("ClientErrorRate", AuditSeverity.Warning,
                $"Client {clientId} produced {(int)count} client errors (4xx) in {O.ErrorRateWindowMinutes} min; last {statusCode} on {path}. Integration may be misconfigured.",
                null, clientId, null);
        }
    }

    public bool RecordMessage(int clientId, string contentHash, int recipientCount, string ip, string? traceId = null)
    {
        if (!O.Enabled) return false;

        var dupWindow = TimeSpan.FromMinutes(O.DuplicateWindowMinutes);
        var dupKey = $"dup:{clientId}:{contentHash}";
        var dupCount = _counters.Increment(dupKey, dupWindow);
        var reject = false;
        if (dupCount >= O.DuplicateThreshold)
        {
            if (ShouldAlert(dupKey, dupWindow))
            {
                Raise("DuplicateMessageStorm", AuditSeverity.Warning,
                    $"Client {clientId} submitted the same message {(int)dupCount} times within {O.DuplicateWindowMinutes} min (hash {contentHash[..Math.Min(12, contentHash.Length)]}). Possible retry loop.",
                    ip, clientId, traceId);
            }
            reject = O.RejectDuplicates;
        }

        var spikeWindow = TimeSpan.FromMinutes(O.RecipientSpikeWindowMinutes);
        var rc = _counters.Increment($"rcpt:{clientId}", spikeWindow, recipientCount);
        if (rc >= O.RecipientSpikeThreshold && ShouldAlert($"rcpt:{clientId}", spikeWindow))
        {
            Raise("RecipientSpike", AuditSeverity.Warning,
                $"Client {clientId} addressed {(int)rc} recipients within {O.RecipientSpikeWindowMinutes} min (threshold {O.RecipientSpikeThreshold}).",
                ip, clientId, traceId);
        }

        return reject;
    }

    public async Task RecordClientIpAsync(int clientId, string clientName, string ip, CancellationToken ct = default)
    {
        if (!O.Enabled || !O.NotifyNewSourceIp) return;

        var set = _seenIps.GetOrAdd(clientId, _ => new HashSet<string>());
        bool known;
        lock (set) known = set.Contains(ip);
        if (known) return;

        // Load from database once per client (first request after startup).
        bool loaded;
        lock (set) loaded = set.Contains("__loaded__");
        if (!loaded)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var ips = await db.ClientSeenIps.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.IpAddress).ToListAsync(ct);
                lock (set)
                {
                    foreach (var i in ips) set.Add(i);
                    set.Add("__loaded__");
                    known = set.Contains(ip);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load seen IPs for client {ClientId}", clientId);
            }
        }

        if (!known)
        {
            lock (set) set.Add(ip);
            _audit.Security("NewSourceIp", AuditSeverity.Info,
                $"Client '{clientName}' (#{clientId}) called from a new source IP {ip}.", ip, clientId, success: true);
        }
    }

    private void Raise(string action, AuditSeverity severity, string details, string? ip, int? clientId, string? traceId)
    {
        _audit.Security(action, severity, details, ip, clientId, traceId: traceId);
        if (severity == AuditSeverity.Critical)
        {
            _ = _alerts.RaiseAsync(action, $"Security: {action}", details);
        }
    }

    private void AutoBlock(BlockTargetType type, string target, string reason)
    {
        if (!O.AutoBlock) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _blocks.BlockAsync(type, target, reason, TimeSpan.FromMinutes(O.BlockDurationMinutes), "system");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto block failed for {Type} {Target}", type, target);
            }
        });
    }

    private bool ShouldAlert(string key, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var last = _lastAlert.GetOrAdd(key, DateTime.MinValue);
        if (now - last < window) return false;
        _lastAlert[key] = now;
        return true;
    }
}
