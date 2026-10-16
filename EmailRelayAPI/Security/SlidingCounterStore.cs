using System.Collections.Concurrent;

namespace EmailRelayAPI.Security;

/// <summary>
/// Memory-efficient approximate sliding window counters (two fixed buckets weighted by elapsed time).
/// Used by rate limiting and anomaly detection.
/// </summary>
public sealed class SlidingCounterStore : IDisposable
{
    private sealed class Window
    {
        public long BucketStart;   // ticks
        public int Previous;
        public int Current;
        public long LastTouched;   // ticks
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly Timer _cleanup;

    public SlidingCounterStore()
    {
        _cleanup = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Adds <paramref name="amount"/> to the counter and returns the estimated count inside the window.</summary>
    public double Increment(string key, TimeSpan window, int amount = 1)
    {
        var w = _windows.GetOrAdd(key, _ => new Window { BucketStart = DateTime.UtcNow.Ticks });
        lock (w)
        {
            Roll(w, window);
            w.Current += amount;
            w.LastTouched = DateTime.UtcNow.Ticks;
            return Estimate(w, window);
        }
    }

    /// <summary>Returns the estimated count without incrementing.</summary>
    public double Peek(string key, TimeSpan window)
    {
        if (!_windows.TryGetValue(key, out var w)) return 0;
        lock (w)
        {
            Roll(w, window);
            return Estimate(w, window);
        }
    }

    /// <summary>Seconds until the window has rolled far enough for one more request to fit.</summary>
    public int SecondsUntilReset(string key, TimeSpan window)
    {
        if (!_windows.TryGetValue(key, out var w)) return 0;
        lock (w)
        {
            var elapsed = DateTime.UtcNow.Ticks - w.BucketStart;
            var remaining = window.Ticks - elapsed;
            return Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(Math.Max(0, remaining)).TotalSeconds));
        }
    }

    public void Reset(string key) => _windows.TryRemove(key, out _);

    private static void Roll(Window w, TimeSpan window)
    {
        var now = DateTime.UtcNow.Ticks;
        var elapsed = now - w.BucketStart;
        if (elapsed >= window.Ticks * 2)
        {
            w.Previous = 0;
            w.Current = 0;
            w.BucketStart = now;
        }
        else if (elapsed >= window.Ticks)
        {
            w.Previous = w.Current;
            w.Current = 0;
            w.BucketStart += window.Ticks;
        }
    }

    private static double Estimate(Window w, TimeSpan window)
    {
        var elapsed = DateTime.UtcNow.Ticks - w.BucketStart;
        var fraction = Math.Clamp(elapsed / (double)window.Ticks, 0, 1);
        return w.Previous * (1 - fraction) + w.Current;
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1).Ticks;
        foreach (var kv in _windows)
        {
            if (kv.Value.LastTouched < cutoff) _windows.TryRemove(kv.Key, out _);
        }
    }

    public void Dispose() => _cleanup.Dispose();
}
