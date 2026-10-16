using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Dashboard, usage reports and system operations.</summary>
[Route("api/admin")]
public class AdminReportsController : AdminControllerBase
{
    private readonly IStatsService _stats;
    private readonly IDeliveryQueue _queue;
    private readonly RetentionService _retention;
    private readonly KeyRotationService _rotation;
    private readonly IAuthenApiClient _authen;
    private readonly IWebHostEnvironment _env;
    private readonly IOptionsMonitor<DatabaseOptions> _dbOptions;
    private readonly IOptionsMonitor<SecurityOptions> _security;
    private readonly IOptionsMonitor<DeliveryOptions> _delivery;
    private readonly IOptionsMonitor<RetentionOptions> _retentionOptions;
    private readonly IOptionsMonitor<AnomalyOptions> _anomaly;
    private readonly IOptionsMonitor<AlertOptions> _alerts;
    private readonly IOptionsMonitor<AuthenApiOptions> _authenOptions;
    private readonly IAuditService _audit;

    public AdminReportsController(IStatsService stats, IDeliveryQueue queue, RetentionService retention, KeyRotationService rotation, IAuthenApiClient authen, IWebHostEnvironment env,
        IOptionsMonitor<DatabaseOptions> dbOptions, IOptionsMonitor<SecurityOptions> security, IOptionsMonitor<DeliveryOptions> delivery, IOptionsMonitor<RetentionOptions> retentionOptions,
        IOptionsMonitor<AnomalyOptions> anomaly, IOptionsMonitor<AlertOptions> alerts, IOptionsMonitor<AuthenApiOptions> authenOptions, IAuditService audit)
    {
        _stats = stats;
        _queue = queue;
        _retention = retention;
        _rotation = rotation;
        _authen = authen;
        _env = env;
        _dbOptions = dbOptions;
        _security = security;
        _delivery = delivery;
        _retentionOptions = retentionOptions;
        _anomaly = anomaly;
        _alerts = alerts;
        _authenOptions = authenOptions;
        _audit = audit;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
        => Ok(ApiResponse<DashboardDto>.Ok(await _stats.GetDashboardAsync(ct)));

    [HttpGet("reports/usage")]
    public async Task<IActionResult> Usage([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(ApiResponse<UsageReportDto>.Ok(await _stats.GetUsageAsync(f, t, clientId, ct)));
    }

    [HttpGet("reports/usage/export")]
    public async Task<IActionResult> UsageExport([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        var report = await _stats.GetUsageAsync(f, t, clientId, ct);
        return Csv($"usage-{f:yyyyMMdd}-{t:yyyyMMdd}.csv",
            new[] { "Client", "ClientId", "Accepted", "Sent", "Failed", "Queued", "Recipients", "AvgDurationMs", "Requests", "DailyQuota" },
            report.ByClient.Select(c => new object?[] { c.ClientName, c.ClientId, c.Accepted, c.Sent, c.Failed, c.Queued, c.Recipients, c.AvgDurationMs.HasValue ? Math.Round(c.AvgDurationMs.Value) : null, c.Requests, c.DailyQuota }));
    }

    [HttpGet("system/info")]
    public IActionResult SystemInfo()
    {
        var delivery = _delivery.CurrentValue;
        var retention = _retentionOptions.CurrentValue;
        var security = _security.CurrentValue;
        var authen = _authenOptions.CurrentValue;
        long storageBytes = 0;
        try
        {
            var dir = Path.GetFullPath(delivery.AttachmentStoragePath);
            if (Directory.Exists(dir)) storageBytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            // ignore
        }

        return Ok(ApiResponse<SystemInfoDto>.Ok(new SystemInfoDto
        {
            Version = AppInfo.Version,
            Environment = _env.EnvironmentName,
            StartedAt = AppInfo.StartedAt,
            DatabaseProvider = _dbOptions.CurrentValue.Provider,
            AuthenApiConfigured = _authen.IsConfigured,
            AuthenApiBaseUrl = _authen.IsConfigured ? authen.BaseUrl : null,
            LocalAdminEnabled = authen.LocalAdmin.Enabled,
            AlertsEnabled = _alerts.CurrentValue.Enabled,
            AnomalyDetectionEnabled = _anomaly.CurrentValue.Enabled,
            IpAllowListEnabled = security.EnableIpAllowList,
            GlobalRateLimitEnabled = security.EnableGlobalRateLimit,
            WorkerCount = delivery.WorkerCount,
            MaxAttempts = delivery.MaxAttempts,
            MessageRetentionDays = retention.MessageRetentionDays,
            AuditRetentionDays = retention.AuditRetentionDays,
            RequestLogRetentionDays = retention.RequestLogRetentionDays,
            AttachmentStoragePath = Path.GetFullPath(delivery.AttachmentStoragePath),
            AttachmentStorageBytes = storageBytes,
            ApiKeyHeaderName = security.ApiKeyHeaderName,
            QueueInFlight = _queue.InFlightCount,
            QueuePending = _queue.PendingCount
        }));
    }

    /// <summary>Run the retention purge now.</summary>
    [HttpPost("system/purge")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> PurgeNow(CancellationToken ct)
    {
        var (messages, audit, requests, blocks) = await _retention.PurgeAsync(ct);
        _audit.Log(new Data.Entities.AuditLog { Category = Data.Entities.AuditCategory.Admin, Action = "ManualPurge", Username = Actor, IpAddress = ClientIp, Details = $"Manual purge: {messages} messages, {audit} audit, {requests} requests, {blocks} blocks" });
        return Ok(ApiResponse<object>.Ok(new { messages, audit, requests, blocks }, "Purge completed"));
    }

    /// <summary>Run the key rotation check now.</summary>
    [HttpPost("system/rotation-check")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> RotationCheck(CancellationToken ct)
    {
        var flagged = await _rotation.CheckAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { flagged }, flagged == 0 ? "No keys due for rotation" : $"{flagged} key(s) flagged"));
    }

    private static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.UtcNow.Date.AddDays(1)).ToUniversalTime();
        var f = (from ?? t.AddDays(-30)).ToUniversalTime();
        if (f >= t) f = t.AddDays(-1);
        if ((t - f).TotalDays > 366) f = t.AddDays(-366);
        return (f, t);
    }
}
