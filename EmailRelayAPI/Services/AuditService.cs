using System.Text.Json;
using System.Threading.Channels;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

/// <summary>Non-blocking audit / request logging. Entries are queued and written in batches by <see cref="LogWriterService"/>.</summary>
public interface IAuditService
{
    void Log(AuditLog entry);
    void LogRequest(RequestLog entry);
    void TouchSeenIp(int clientId, string ip);

    /// <summary>Convenience overload for security events.</summary>
    void Security(string action, AuditSeverity severity, string details, string? ip = null, int? clientId = null, int? apiKeyId = null, string? username = null, bool success = false, string? traceId = null);
}

public class AuditService : IAuditService
{
    private readonly Channel<object> _channel;
    private readonly ILogger<AuditService> _logger;

    public AuditService(ILogger<AuditService> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    public ChannelReader<object> Reader => _channel.Reader;

    public void Log(AuditLog entry)
    {
        Truncate(entry);
        if (!_channel.Writer.TryWrite(entry))
        {
            _logger.LogWarning("Audit queue full, entry dropped: {Action}", entry.Action);
        }

        if (entry.Severity == AuditSeverity.Critical)
            _logger.LogWarning("SECURITY [{Category}/{Action}] {Details} ip={Ip} client={Client}", entry.Category, entry.Action, entry.Details, entry.IpAddress, entry.ClientId);
    }

    public void LogRequest(RequestLog entry)
    {
        if (entry.Path.Length > 500) entry.Path = entry.Path[..500];
        if (entry.Query?.Length > 1000) entry.Query = entry.Query[..1000];
        if (entry.Error?.Length > 1000) entry.Error = entry.Error[..1000];
        _channel.Writer.TryWrite(entry);
    }

    public void TouchSeenIp(int clientId, string ip)
        => _channel.Writer.TryWrite(new SeenIpTouch(clientId, ip));

    public void Security(string action, AuditSeverity severity, string details, string? ip = null, int? clientId = null, int? apiKeyId = null, string? username = null, bool success = false, string? traceId = null)
        => Log(new AuditLog
        {
            Category = AuditCategory.Security,
            Severity = severity,
            Action = action,
            Details = details,
            IpAddress = ip,
            ClientId = clientId,
            ApiKeyId = apiKeyId,
            Username = username,
            Success = success,
            TraceId = traceId
        });

    private static void Truncate(AuditLog e)
    {
        if (e.Details?.Length > 4000) e.Details = e.Details[..4000];
        if (e.UserAgent?.Length > 500) e.UserAgent = e.UserAgent[..500];
        if (e.RequestPath?.Length > 500) e.RequestPath = e.RequestPath[..500];
        if (e.Action.Length > 100) e.Action = e.Action[..100];
    }

    internal sealed record SeenIpTouch(int ClientId, string Ip);
}

/// <summary>Drains the audit queue into the database (and optional JSON-lines files) in batches.</summary>
public class LogWriterService : BackgroundService
{
    private readonly AuditService _audit;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApiKeyService _apiKeys;
    private readonly AuditOptions _options;
    private readonly ILogger<LogWriterService> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LogWriterService(AuditService audit, IServiceScopeFactory scopeFactory, IApiKeyService apiKeys, IOptions<AuditOptions> options, ILogger<LogWriterService> logger)
    {
        _audit = audit;
        _scopeFactory = scopeFactory;
        _apiKeys = apiKeys;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _audit.Reader;
        var batch = new List<object>(512);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one item, then gather for up to ~1 second.
                if (!await reader.WaitToReadAsync(stoppingToken)) break;
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (batch.Count < 500)
                {
                    if (reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        continue;
                    }
                    if (DateTime.UtcNow >= deadline) break;
                    if (!await WaitShortAsync(reader, stoppingToken)) break;
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, stoppingToken);
                    batch.Clear();
                }

                await _apiKeys.FlushUsageAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write log batch ({Count} items)", batch.Count);
                batch.Clear();
                await Task.Delay(2000, stoppingToken);
            }
        }

        // final drain
        try
        {
            while (reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count > 0) await FlushAsync(batch, CancellationToken.None);
            await _apiKeys.FlushUsageAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final log flush failed");
        }
    }

    private static async Task<bool> WaitShortAsync(ChannelReader<object> reader, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(200);
        try
        {
            return await reader.WaitToReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task FlushAsync(List<object> batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var audits = batch.OfType<AuditLog>().ToList();
        var requests = batch.OfType<RequestLog>().ToList();
        var touches = batch.OfType<AuditService.SeenIpTouch>()
            .GroupBy(t => (t.ClientId, t.Ip))
            .Select(g => (g.Key.ClientId, g.Key.Ip, Count: g.Count()))
            .ToList();

        if (audits.Count > 0) db.AuditLogs.AddRange(audits);
        if (requests.Count > 0) db.RequestLogs.AddRange(requests);

        foreach (var t in touches)
        {
            var existing = await db.ClientSeenIps.FirstOrDefaultAsync(s => s.ClientId == t.ClientId && s.IpAddress == t.Ip, ct);
            if (existing == null)
            {
                db.ClientSeenIps.Add(new ClientSeenIp { ClientId = t.ClientId, IpAddress = t.Ip, RequestCount = t.Count });
            }
            else
            {
                existing.LastSeenAt = DateTime.UtcNow;
                existing.RequestCount += t.Count;
            }
        }

        await db.SaveChangesAsync(ct);

        if (_options.EnableFileLogging && audits.Count > 0)
        {
            await WriteFilesAsync(audits, ct);
        }
    }

    private async Task WriteFilesAsync(List<AuditLog> audits, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetFullPath(_options.LogDirectory);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var lines = audits.Select(a => JsonSerializer.Serialize(a, JsonOpts));
            await File.AppendAllLinesAsync(file, lines, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append audit file");
        }
    }
}
