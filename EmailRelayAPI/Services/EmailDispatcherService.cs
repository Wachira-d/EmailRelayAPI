using System.Collections.Concurrent;
using System.Diagnostics;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services.Transports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

/// <summary>
/// Background delivery engine: pulls queued messages, sends them through the sender profile's transport,
/// retries with exponential back-off and records the outcome.
/// </summary>
public class EmailDispatcherService : BackgroundService
{
    private readonly IDeliveryQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmailQueueService _messages;
    private readonly IEmailTransportResolver _transports;
    private readonly IWebhookNotifier _webhooks;
    private readonly IRateLimitService _rateLimit;
    private readonly IAuditService _audit;
    private readonly IAlertService _alerts;
    private readonly IOptionsMonitor<DeliveryOptions> _options;
    private readonly ILogger<EmailDispatcherService> _logger;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _profileLocks = new();

    public EmailDispatcherService(IDeliveryQueue queue, IServiceScopeFactory scopeFactory, IEmailQueueService messages, IEmailTransportResolver transports, IWebhookNotifier webhooks, IRateLimitService rateLimit, IAuditService audit, IAlertService alerts, IOptionsMonitor<DeliveryOptions> options, ILogger<EmailDispatcherService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _messages = messages;
        _transports = transports;
        _webhooks = webhooks;
        _rateLimit = rateLimit;
        _audit = audit;
        _alerts = alerts;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, _options.CurrentValue.WorkerCount);
        _logger.LogInformation("Email dispatcher starting with {Workers} workers", workers);

        var tasks = new List<Task> { PollLoopAsync(stoppingToken) };
        for (var i = 0; i < workers; i++)
        {
            var id = i + 1;
            tasks.Add(WorkerLoopAsync(id, stoppingToken));
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("Email dispatcher stopped");
    }

    /// <summary>Periodically loads due / recovered messages from the database into the in-process queue.</summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        // small initial delay so the database is ready
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await LoadDueMessagesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatcher poll failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, _options.CurrentValue.PollIntervalSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task LoadDueMessagesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        // crash recovery: messages left in Sending for too long
        var stuckCutoff = now.AddMinutes(-Math.Max(1, _options.CurrentValue.StuckSendingMinutes));
        var stuck = await db.Messages
            .Where(m => m.Status == EmailStatus.Sending && (m.NextAttemptAt == null || m.NextAttemptAt < stuckCutoff))
            .Take(200)
            .ToListAsync(ct);
        foreach (var m in stuck)
        {
            m.Status = EmailStatus.Queued;
            m.NextAttemptAt = now;
            db.EmailEvents.Add(new EmailEvent { MessageId = m.Id, Type = "Recovered", Details = "Message was stuck in Sending and has been re-queued", Source = "dispatcher" });
        }
        if (stuck.Count > 0) await db.SaveChangesAsync(ct);

        var due = await db.Messages.AsNoTracking()
            .Where(m => (m.Status == EmailStatus.Queued || m.Status == EmailStatus.Scheduled) && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderByDescending(m => m.Priority).ThenBy(m => m.NextAttemptAt).ThenBy(m => m.CreatedAt)
            .Select(m => m.Id)
            .Take(1000)
            .ToListAsync(ct);

        foreach (var id in due) _queue.Enqueue(id);
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken ct)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var id))
                {
                    if (!_queue.TryBeginProcessing(id)) continue;
                    try
                    {
                        await ProcessAsync(id, workerId, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Worker {Worker} failed processing message {Id}", workerId, id);
                    }
                    finally
                    {
                        _queue.EndProcessing(id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProcessAsync(Guid id, int workerId, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var message = await db.Messages
            .Include(m => m.Attachments)
            .Include(m => m.SenderProfile)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (message == null) return;
        if (message.Status != EmailStatus.Queued && message.Status != EmailStatus.Scheduled) return;
        var now = DateTime.UtcNow;
        if (message.NextAttemptAt.HasValue && message.NextAttemptAt.Value > now) return; // poll loop re-adds it later

        var profile = message.SenderProfile;
        if (!profile.IsActive)
        {
            message.NextAttemptAt = now.AddMinutes(5);
            db.EmailEvents.Add(new EmailEvent { MessageId = id, Type = "Deferred", Details = $"Sender profile '{profile.Name}' is disabled; will retry in 5 minutes", Source = "dispatcher" });
            await db.SaveChangesAsync(ct);
            return;
        }

        // profile throttle (messages per minute)
        if (profile.MaxPerMinute > 0)
        {
            var decision = _rateLimit.Check($"profile:{profile.Id}", profile.MaxPerMinute, TimeSpan.FromMinutes(1));
            if (!decision.Allowed)
            {
                message.NextAttemptAt = now.AddSeconds(decision.RetryAfterSeconds + 1);
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        // mark as sending
        message.Status = EmailStatus.Sending;
        message.Attempts += 1;
        message.NextAttemptAt = now; // used as "started at" for stuck detection
        await db.SaveChangesAsync(ct);

        var attempt = message.Attempts;
        var sem = _profileLocks.GetOrAdd(profile.Id, _ => new SemaphoreSlim(Math.Max(1, profile.MaxConcurrency), Math.Max(1, profile.MaxConcurrency)));
        await sem.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        TransportResult result;
        try
        {
            OutboundEmail? outbound = null;
            string? loadError = null;
            try
            {
                outbound = await _messages.ToOutboundAsync(message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loadError = ex.Message;
            }

            if (outbound == null)
            {
                result = TransportResult.Fail("Cannot load message content: " + loadError, retryable: false);
            }
            else
            {
                var transport = _transports.Resolve(profile);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, profile.TimeoutSeconds * 3)));
                result = await transport.SendAsync(profile, outbound, timeout.Token);
            }
        }
        finally
        {
            sem.Release();
        }

        sw.Stop();
        var finished = DateTime.UtcNow;
        message.DurationMs = sw.ElapsedMilliseconds;

        if (result.Success)
        {
            message.Status = EmailStatus.Sent;
            message.SentAt = finished;
            message.CompletedAt = finished;
            message.NextAttemptAt = null;
            message.LastError = null;
            message.ProviderMessageId = result.ProviderMessageId;
            message.ProviderResponse = result.Response;
            db.EmailEvents.Add(new EmailEvent { MessageId = id, Type = "Sent", Attempt = attempt, DurationMs = sw.ElapsedMilliseconds, Details = $"Delivered via '{profile.Name}' ({profile.Type}). {Truncate(result.Response, 500)}", Source = $"worker-{workerId}" });

            profile.LastSuccessAt = finished;
            profile.ConsecutiveFailures = 0;
            profile.LastError = null;

            _logger.LogInformation("Message {Id} sent via {Profile} in {Ms}ms (attempt {Attempt})", id, profile.Name, sw.ElapsedMilliseconds, attempt);
        }
        else
        {
            var retry = result.Retryable && attempt < Math.Max(1, opts.MaxAttempts);
            message.LastError = result.Error;
            message.ProviderResponse = result.Response;

            if (retry)
            {
                var delay = ComputeBackoff(attempt, opts);
                message.Status = EmailStatus.Queued;
                message.NextAttemptAt = finished.Add(delay);
                db.EmailEvents.Add(new EmailEvent { MessageId = id, Type = "Retry", Attempt = attempt, DurationMs = sw.ElapsedMilliseconds, Details = $"Attempt {attempt} failed: {result.Error}. Next attempt at {message.NextAttemptAt:u}", Source = $"worker-{workerId}" });
                _logger.LogWarning("Message {Id} attempt {Attempt} failed via {Profile}: {Error}. Retrying in {Delay}s", id, attempt, profile.Name, result.Error, (int)delay.TotalSeconds);
            }
            else
            {
                message.Status = EmailStatus.Failed;
                message.CompletedAt = finished;
                message.NextAttemptAt = null;
                db.EmailEvents.Add(new EmailEvent { MessageId = id, Type = "Failed", Attempt = attempt, DurationMs = sw.ElapsedMilliseconds, Details = $"Giving up after {attempt} attempt(s): {result.Error}", Source = $"worker-{workerId}" });
                _audit.Log(new AuditLog
                {
                    Category = AuditCategory.Delivery,
                    Severity = AuditSeverity.Warning,
                    Action = "MessageFailed",
                    Success = false,
                    ClientId = message.ClientId,
                    ApiKeyId = message.ApiKeyId,
                    Details = $"Message {id} to {message.RecipientCount} recipient(s) failed permanently via '{profile.Name}': {result.Error}",
                    TargetType = "Message",
                    TargetId = id.ToString(),
                    TraceId = message.TraceId
                });
                _logger.LogError("Message {Id} failed permanently via {Profile}: {Error}", id, profile.Name, result.Error);
            }

            profile.LastFailureAt = finished;
            profile.LastError = Truncate(result.Error, 2000);
            profile.ConsecutiveFailures += 1;
            if (profile.ConsecutiveFailures == Math.Max(1, opts.ProfileUnhealthyThreshold))
            {
                _audit.Log(new AuditLog
                {
                    Category = AuditCategory.Delivery,
                    Severity = AuditSeverity.Critical,
                    Action = "SenderProfileUnhealthy",
                    Success = false,
                    Details = $"Sender profile '{profile.Name}' has {profile.ConsecutiveFailures} consecutive failures. Last error: {result.Error}",
                    TargetType = "SenderProfile",
                    TargetId = profile.Id.ToString()
                });
                _ = _alerts.RaiseAsync("SenderProfileUnhealthy", $"Sender profile '{profile.Name}' is failing", $"{profile.ConsecutiveFailures} consecutive delivery failures.\nLast error: {result.Error}", CancellationToken.None);
            }
        }

        await db.SaveChangesAsync(ct);

        if ((message.Status == EmailStatus.Sent || message.Status == EmailStatus.Failed) && message.ClientId.HasValue)
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == message.ClientId.Value, ct);
            if (client != null && !string.IsNullOrWhiteSpace(client.WebhookUrl))
            {
                _ = Task.Run(() => _webhooks.NotifyAsync(message, client, CancellationToken.None), CancellationToken.None);
            }
        }
    }

    private static TimeSpan ComputeBackoff(int attempt, DeliveryOptions opts)
    {
        var baseDelay = Math.Max(1, opts.RetryBaseDelaySeconds);
        var max = Math.Max(baseDelay, opts.RetryMaxDelaySeconds);
        var exp = baseDelay * Math.Pow(2, Math.Max(0, attempt - 1));
        var jitter = Random.Shared.NextDouble() * baseDelay * 0.5;
        return TimeSpan.FromSeconds(Math.Min(max, exp + jitter));
    }

    private static string? Truncate(string? s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);
}
