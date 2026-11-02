using System.Collections.Concurrent;

namespace PhotoClippy.Cli.Web.Auth;

/// <summary>In-memory sliding-window limit on failed sign-ins, per user name and per client address.</summary>
internal sealed class LoginThrottle(TimeProvider? time = null, int perUser = 5, int perAddress = 20, TimeSpan? window = null)
{
    private const int PruneThreshold = 10_000;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    /// <summary>How long to wait before trying again, or <c>null</c> when an attempt is allowed.</summary>
    public TimeSpan? RetryAfter(string user, string? address)
    {
        var now = _time.GetUtcNow();
        var byUser = Check(UserKey(user), perUser, now);
        var byAddress = address is null ? null : Check(AddressKey(address), perAddress, now);
        return byUser is null ? byAddress : byAddress is null ? byUser : byUser > byAddress ? byUser : byAddress;
    }

    public void Failed(string user, string? address)
    {
        var now = _time.GetUtcNow();
        Record(UserKey(user), now);
        if (address is not null)
        {
            Record(AddressKey(address), now);
        }

        if (_failures.Count > PruneThreshold)
        {
            Prune(now);
        }
    }

    public void Succeeded(string user) => _failures.TryRemove(UserKey(user), out _);

    private static string UserKey(string user) => "u:" + user.Trim().ToUpperInvariant();

    private static string AddressKey(string address) => "a:" + address;

    private TimeSpan? Check(string key, int limit, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var queue))
        {
            return null;
        }

        lock (queue)
        {
            Expire(queue, now);
            return queue.Count >= limit ? queue.Peek() + _window - now : null;
        }
    }

    private void Record(string key, DateTimeOffset now)
    {
        var queue = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Expire(queue, now);
            queue.Enqueue(now);
        }
    }

    private void Expire(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && queue.Peek() + _window <= now)
        {
            queue.Dequeue();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, queue) in _failures)
        {
            lock (queue)
            {
                Expire(queue, now);
                if (queue.Count == 0)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }
}
