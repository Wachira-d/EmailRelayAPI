using System.Collections.Concurrent;
using System.Threading.Channels;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

/// <summary>Receives application log entries from <see cref="DbLoggerProvider"/>; keeps a live ring buffer and a queue for the database writer.</summary>
public class SystemLogSink
{
    private readonly Channel<SystemLog> _channel = Channel.CreateBounded<SystemLog>(new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentQueue<SystemLog> _recent = new();
    private const int RecentSize = 500;

    public ChannelReader<SystemLog> Reader => _channel.Reader;

    public void Write(SystemLog entry)
    {
        _recent.Enqueue(entry);
        while (_recent.Count > RecentSize && _recent.TryDequeue(out _)) { }
        _channel.Writer.TryWrite(entry);
    }

    /// <summary>Latest entries kept in memory (newest first), available even if the database is down.</summary>
    public List<SystemLog> Recent(int max = 200) => _recent.Reverse().Take(max).ToList();
}

/// <summary>ILogger provider that forwards entries at or above the configured level to the <see cref="SystemLogSink"/>.</summary>
public class DbLoggerProvider : ILoggerProvider
{
    private readonly SystemLogSink _sink;
    private readonly IHttpContextAccessor _http;
    private readonly IOptionsMonitor<AuditOptions> _options;

    public DbLoggerProvider(SystemLogSink sink, IHttpContextAccessor http, IOptionsMonitor<AuditOptions> options)
    {
        _sink = sink;
        _http = http;
        _options = options;
    }

    public ILogger CreateLogger(string categoryName) => new DbLogger(categoryName, _sink, _http, _options);

    public void Dispose() { }

    private sealed class DbLogger : ILogger
    {
        private readonly string _category;
        private readonly SystemLogSink _sink;
        private readonly IHttpContextAccessor _http;
        private readonly IOptionsMonitor<AuditOptions> _options;

        public DbLogger(string category, SystemLogSink sink, IHttpContextAccessor http, IOptionsMonitor<AuditOptions> options)
        {
            _category = category;
            _sink = sink;
            _http = http;
            _options = options;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.None) return false;
            var min = ParseLevel(_options.CurrentValue.SystemLogMinimumLevel);
            if (logLevel >= min) return true;
            // keep the relay's own informational messages (deliveries, logins) when configured
            return _options.CurrentValue.SystemLogIncludeAppInfo && logLevel >= LogLevel.Information && _category.StartsWith("EmailRelayAPI", StringComparison.Ordinal);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (_category == "EmailRelayAPI.Services.LogWriterService" && logLevel >= LogLevel.Error) return; // avoid feedback loops when the DB is down

            string message;
            try { message = formatter(state, exception); } catch { message = state?.ToString() ?? string.Empty; }
            if (message.Length > 4000) message = message[..4000];

            var ctx = _http.HttpContext;
            var entry = new SystemLog
            {
                Level = logLevel.ToString(),
                Category = _category.Length > 200 ? _category[..200] : _category,
                Message = message,
                Exception = exception?.ToString(),
                EventId = eventId.Id,
                TraceId = ctx?.GetTraceId(),
                RequestPath = ctx?.Request.Path.Value,
                HttpMethod = ctx?.Request.Method
            };
            if (entry.Exception?.Length > 16000) entry.Exception = entry.Exception[..16000];
            _sink.Write(entry);
        }

        private static LogLevel ParseLevel(string? s)
            => Enum.TryParse<LogLevel>(s, true, out var l) ? l : LogLevel.Warning;
    }
}
