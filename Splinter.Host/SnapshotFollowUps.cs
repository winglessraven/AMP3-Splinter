using System.Text.Json;

namespace Splinter.Host;

// A pending Discord DM is tied to the exact AMP snapshot and the user who
// requested it. Persist the request before returning from the interaction so
// an AMP/Web API restart does not silently discard completion notifications.
public sealed class SnapshotFollowUp
{
    public Guid SnapshotId { get; set; }
    public ulong RequesterId { get; set; }
    public string ServerName { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset LastDeliveryAttempt { get; set; }
}

public sealed class SnapshotFollowUpState
{
    public Guid ShardId { get; set; }
    public List<SnapshotFollowUp> Pending { get; set; } = [];
}

public sealed class SnapshotFollowUps
{
    private readonly Guid _shardId;
    private readonly object _sync = new();
    private readonly SnapshotFollowUpState _state;
    private readonly bool _writable;

    public static string PathFor(Guid shardId) =>
        Path.Combine(ConfigStore.Root, "snapshots", $"{shardId:D}.json");

    public SnapshotFollowUps(Guid shardId)
    {
        if (shardId == Guid.Empty) throw new ArgumentException("Shard ID is required.", nameof(shardId));
        _shardId = shardId;
        try
        {
            _state = ConfigStore.Read(PathFor(shardId), new SnapshotFollowUpState { ShardId = shardId });
            if (_state.ShardId != shardId || _state.Pending is null)
                throw new InvalidDataException("Snapshot notification state is invalid or belongs to another Shard.");
            _writable = true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A broken notification file must not prevent the Discord bot or
            // AMP from starting. Do not overwrite or trust the invalid file.
            _state = new SnapshotFollowUpState { ShardId = shardId };
            _writable = false;
            BotLog.Write($"Shard {shardId:D}: snapshot notification state cannot be read " +
                $"({ex.GetType().Name}); DMs disabled until the file is repaired.");
        }
    }

    public int Count { get { lock (_sync) return _state.Pending.Count; } }

    public void Track(Guid snapshotId, ulong requesterId, string serverName, DateTimeOffset at)
    {
        if (snapshotId == Guid.Empty || requesterId == 0)
            throw new ArgumentException("A snapshot and requester are required.");
        lock (_sync)
        {
            if (!_writable) throw new InvalidOperationException("Snapshot notification storage is unavailable.");
            if (_state.Pending.Any(p => p.SnapshotId == snapshotId)) return;
            // One Shard cannot indefinitely accumulate requests if a user
            // deletes the AMP snapshot before Splinter observes its outcome.
            if (_state.Pending.Count >= 100)
                throw new InvalidOperationException("Too many pending snapshot notifications.");
            _state.Pending.Add(new SnapshotFollowUp
            {
                SnapshotId = snapshotId, RequesterId = requesterId,
                ServerName = serverName, RequestedAt = at
            });
            Save();
        }
    }

    public IReadOnlyList<SnapshotFollowUp> Due(DateTimeOffset now)
    {
        lock (_sync)
            return _state.Pending.Where(p => p.DeliveryAttempts < 5 &&
                (p.LastDeliveryAttempt == default ||
                 now - p.LastDeliveryAttempt >= RetryInterval(p.DeliveryAttempts)))
                .Select(p => new SnapshotFollowUp
                {
                    SnapshotId = p.SnapshotId, RequesterId = p.RequesterId,
                    ServerName = p.ServerName, RequestedAt = p.RequestedAt,
                    DeliveryAttempts = p.DeliveryAttempts,
                    LastDeliveryAttempt = p.LastDeliveryAttempt
                }).ToArray();
    }

    public void Delivered(Guid snapshotId)
    {
        lock (_sync)
        {
            if (_state.Pending.RemoveAll(p => p.SnapshotId == snapshotId) == 0) return;
            Save();
        }
    }

    public void DeliveryFailed(Guid snapshotId, DateTimeOffset now)
    {
        lock (_sync)
        {
            var item = _state.Pending.FirstOrDefault(p => p.SnapshotId == snapshotId);
            if (item is null) return;
            item.DeliveryAttempts++;
            item.LastDeliveryAttempt = now;
            // Do not keep retrying or leaking persistent records when DMs are
            // disabled or Discord rejects the recipient.
            if (item.DeliveryAttempts >= 5) _state.Pending.Remove(item);
            Save();
        }
    }

    public static TimeSpan RetryInterval(int failures) =>
        TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, Math.Clamp(failures - 1, 0, 5))));

    private void Save() => ConfigStore.Write(PathFor(_shardId), _state);
}

public sealed record AmpSnapshotProgress(string Status, string? FailureReason)
{
    public bool Finished => Status is "Complete" or "Failed";
}

public static class SnapshotFollowUpMessage
{
    public static string Text(SnapshotFollowUp request, AmpSnapshotProgress progress)
    {
        if (!progress.Finished) throw new ArgumentException("Snapshot has not finished.", nameof(progress));
        string name = Escape(string.IsNullOrWhiteSpace(request.ServerName) ? "Game server" : request.ServerName);
        string result = progress.Status == "Complete"
            ? $"✅ **Snapshot complete** for **{name}**."
            : $"❌ **Snapshot failed** for **{name}**.";
        if (progress.Status == "Failed" && !string.IsNullOrWhiteSpace(progress.FailureReason))
            result += "\nReason: " + Escape(progress.FailureReason);
        return result;
    }

    private static string Escape(string value)
    {
        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (singleLine.Length > 300) singleLine = singleLine[..300] + "…";
        return singleLine.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("@", "@\u200b", StringComparison.Ordinal);
    }
}
