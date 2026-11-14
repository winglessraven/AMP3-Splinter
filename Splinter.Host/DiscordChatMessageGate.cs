namespace Splinter.Host;

// Discord gateway callbacks may overlap while sessions reconnect. Forward each
// Discord message at most once to a given Shard in this Web API process.
// Never deduplicate by message text: repeated player messages are valid.
internal sealed class DiscordChatMessageGate
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaximumMessages = 1024;
    private readonly object _sync = new();
    private readonly Dictionary<(Guid Shard, ulong Message), DateTimeOffset> _seen = new();
    private readonly Queue<(Guid Shard, ulong Message)> _order = new();

    public bool TryClaim(Guid shardId, ulong messageId, DateTimeOffset now)
    {
        if (shardId == Guid.Empty || messageId == 0) return false;
        lock (_sync)
        {
            // Only evict entries after their retention interval. The queue is
            // bounded separately, avoiding indefinite accumulation on busy Shards.
            while (_order.Count != 0 &&
                   (_order.Count > MaximumMessages ||
                    (_seen.TryGetValue(_order.Peek(), out DateTimeOffset when) &&
                     now - when >= Lifetime)))
                _seen.Remove(_order.Dequeue());
            if (_seen.ContainsKey((shardId, messageId))) return false;
            _seen.Add((shardId, messageId), now);
            _order.Enqueue((shardId, messageId));
            if (_order.Count > MaximumMessages)
                _seen.Remove(_order.Dequeue());
            return true;
        }
    }
}
