static class SocketJoinRateLimiter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows =
        new(StringComparer.Ordinal);

    public static bool Allow(string systemId, DateTimeOffset now)
    {
        var queue = _windows.GetOrAdd(systemId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var cutoff = now.AddSeconds(-1);
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= 2)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}

