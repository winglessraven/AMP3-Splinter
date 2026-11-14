namespace Splinter.Host;

public sealed record PlayerRosterDelta(IReadOnlyList<OnlinePlayer> Joined, IReadOnlyList<OnlinePlayer> Left);

public sealed class PlayerPlaytime
{
    private readonly object _sync = new();
    public Dictionary<string, PlayerTime> Players { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string PathFor(Guid id) => Path.Combine(ConfigStore.Root, "playtime", id.ToString("D") + ".json");
    public static PlayerPlaytime Load(Guid id)
    {
        var state = ConfigStore.Read(PathFor(id), new PlayerPlaytime());
        state.Players = new Dictionary<string, PlayerTime>(state.Players, StringComparer.OrdinalIgnoreCase);
        return state;
    }
    public void Save(Guid id)
    {
        lock (_sync) ConfigStore.Write(PathFor(id), this);
    }

    public bool Observe(string name, bool joined, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!Players.TryGetValue(name, out PlayerTime? player))
            {
                if (!joined) return false;
                Players[name] = new PlayerTime { LastSeen = now, JoinedAt = now };
                return true;
            }
            if (joined)
            {
                if (player.JoinedAt.HasValue) return false;
                player.JoinedAt = now;
            }
            else
            {
                if (!player.JoinedAt.HasValue) return false;
                player.TotalSeconds += Math.Max(0, (now - player.JoinedAt.Value).TotalSeconds);
                player.JoinedAt = null;
            }
            player.LastSeen = now;
            return true;
        }
    }

    // Player events, once bound to AMP's verified publisher, enter the same
    // state machine as connected-user snapshots. Repeated notifications and
    // subsequent roster polls must not create duplicate sessions.
    public bool PlayerJoined(OnlinePlayer connected, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(connected.Id) ||
            string.IsNullOrWhiteSpace(connected.Name) ||
            occurredAt == default)
            throw new ArgumentException("A player event requires an ID, name and timestamp.");
        lock (_sync)
        {
            string key = connected.Id;
            if (!Players.TryGetValue(key, out PlayerTime? player))
            {
                player = new PlayerTime();
                Players[key] = player;
            }
            DateTimeOffset joinedAt = connected.ConnectedAt is DateTimeOffset since &&
                since <= occurredAt ? since : occurredAt;
            if (player.JoinedAt.HasValue)
            {
                // A repeated join event for the same session is not a new visit.
                if (player.JoinedAt.Value >= joinedAt) return false;
                // If AMP explicitly reports a later session, close the old one
                // at its last confirmed observation rather than counting a gap.
                DateTimeOffset ended = player.LastSeen > player.JoinedAt.Value
                    ? player.LastSeen : player.JoinedAt.Value;
                if (ended > joinedAt) ended = joinedAt;
                player.TotalSeconds += Math.Max(0, (ended - player.JoinedAt.Value).TotalSeconds);
            }
            player.JoinedAt = joinedAt;
            player.LastSeen = occurredAt;
            player.AwaitingNativeRoster = true;
            player.PlayerId = key;
            player.DisplayName = connected.Name;
            RosterInitialized = true;
            return true;
        }
    }

    public bool PlayerLeft(string playerId, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(playerId) || occurredAt == default)
            throw new ArgumentException("A player leave event requires an ID and timestamp.");
        lock (_sync)
        {
            if (!Players.TryGetValue(playerId, out PlayerTime? player) || !player.JoinedAt.HasValue)
                return false;
            DateTimeOffset end = occurredAt >= player.JoinedAt.Value ? occurredAt : player.JoinedAt.Value;
            player.TotalSeconds += Math.Max(0, (end - player.JoinedAt.Value).TotalSeconds);
            player.JoinedAt = null;
            player.LastSeen = end;
            player.AwaitingNativeRoster = false;
            RosterInitialized = true;
            return true;
        }
    }

    // Reconcile AMP's authoritative connected-user snapshot. The first snapshot
    // starts tracking without fabricating join notifications. Subsequent changes
    // generate transitions once per player; missing/unknown snapshots are ignored.
    public PlayerRosterDelta Reconcile(IReadOnlyList<OnlinePlayer> current, DateTimeOffset now)
    {
        lock (_sync)
        {
            bool first = !RosterInitialized;
            var joined = new List<OnlinePlayer>();
            var left = new List<OnlinePlayer>();
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OnlinePlayer connected in current)
            {
                string key = !string.IsNullOrWhiteSpace(connected.Id) ? connected.Id : connected.Name;
                // Existing playtime records used player names as keys. When AMP
                // supplies an ID, adopt that record without discarding accumulated time.
                if (!Players.ContainsKey(key) && Players.TryGetValue(connected.Name, out PlayerTime? legacy) &&
                    (string.IsNullOrEmpty(legacy.PlayerId) ||
                     string.Equals(legacy.PlayerId, key, StringComparison.OrdinalIgnoreCase)))
                {
                    Players.Remove(connected.Name);
                    Players[key] = legacy;
                }
                if (!active.Add(key)) continue;
                if (!Players.TryGetValue(key, out PlayerTime? player))
                {
                    var start = connected.ConnectedAt is DateTimeOffset joinedAt && joinedAt <= now
                        ? joinedAt : now;
                    player = new PlayerTime { LastSeen = now, JoinedAt = start };
                    Players[key] = player;
                    if (!first) joined.Add(connected);
                }
                else if (player.JoinedAt is null)
                {
                    // The agent's leave event can reach Splinter before its
                    // webapi roster store updates. Never resurrect an already
                    // closed session from a stale roster entry.
                    if (connected.ConnectedAt is DateTimeOffset oldStart &&
                        oldStart <= player.LastSeen)
                        continue;
                    player.JoinedAt = connected.ConnectedAt is DateTimeOffset since && since <= now
                        ? since : now;
                    if (!first) joined.Add(connected);
                }
                else if (connected.ConnectedAt is DateTimeOffset newSince &&
                    newSince > player.JoinedAt.Value && newSince <= now)
                {
                    // AMP reports a NEW session for the same ID: close the earlier
                    // session at the last observed time before starting this one.
                    DateTimeOffset end = player.LastSeen.AddSeconds(15) < newSince
                        ? player.LastSeen.AddSeconds(15) : newSince;
                    player.TotalSeconds += Math.Max(0, (end - player.JoinedAt.Value).TotalSeconds);
                    player.JoinedAt = newSince;
                    if (!first) { left.Add(connected); joined.Add(connected); }
                }
                player.PlayerId = key;
                player.DisplayName = connected.Name;
                player.LastSeen = now;
                player.AwaitingNativeRoster = false;
            }
            foreach ((string key, PlayerTime player) in Players)
            {
                if (player.JoinedAt is null || active.Contains(key)) continue;
                // The native join event may precede the authoritative roster
                // publication. Give the roster a short window to catch up.
                if (player.AwaitingNativeRoster &&
                    now - player.LastSeen < TimeSpan.FromSeconds(8)) continue;
                // A missed poll or host restart must not count unobserved downtime.
                DateTimeOffset end = player.LastSeen.AddSeconds(15) < now
                    ? player.LastSeen.AddSeconds(15) : now;
                player.TotalSeconds += Math.Max(0, (end - player.JoinedAt.Value).TotalSeconds);
                player.JoinedAt = null;
                player.LastSeen = end;
                player.AwaitingNativeRoster = false;
                if (!first) left.Add(new OnlinePlayer(key, player.DisplayName ?? key));
            }
            RosterInitialized = true;
            return new PlayerRosterDelta(joined, left);
        }
    }

    public bool RosterInitialized { get; set; }

    public bool CloseAll(DateTimeOffset now)
    {
        lock (_sync)
        {
            bool changed = false;
            foreach (var player in Players.Values)
            {
                if (!player.JoinedAt.HasValue) continue;
                DateTimeOffset end = player.LastSeen.AddSeconds(15) < now
                    ? player.LastSeen.AddSeconds(15) : now;
                player.TotalSeconds += Math.Max(0, (end - player.JoinedAt.Value).TotalSeconds);
                player.JoinedAt = null;
                player.LastSeen = end;
                changed = true;
            }
            return changed;
        }
    }

    public string Leaderboard(int count, DateTimeOffset now, bool includeSeen = false)
    {
        lock (_sync)
        {
            var top = Players.Select(pair => (Name: pair.Value.DisplayName ?? pair.Key, Player: pair.Value,
                    Seconds: pair.Value.TotalSeconds + (pair.Value.JoinedAt.HasValue
                        ? Math.Max(0, (now - pair.Value.JoinedAt.Value).TotalSeconds) : 0)))
                .OrderByDescending(x => x.Seconds).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(count).ToArray();
            if (top.Length == 0) return "No playtime recorded yet.";
            return string.Join("\n", top.Select((x, index) =>
            {
                long seconds = (long)Math.Max(0, x.Seconds);
                long days = seconds / 86400;
                long hours = (seconds % 86400) / 3600;
                long minutes = (seconds % 3600) / 60;
                return $"{index + 1}. {x.Name} — {days}d {hours}h {minutes}m" +
                    (includeSeen ? $" (last seen {x.Player.LastSeen:yyyy-MM-dd})" : "");
            }));
        }
    }
}

public sealed class PlayerTime
{
    public string? PlayerId { get; set; }
    public string? DisplayName { get; set; }
    public double TotalSeconds { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    // A native join may precede AMP's roster publication; only those joins
    // receive a short grace period before a missing-roster disconnect.
    public bool AwaitingNativeRoster { get; set; }
}
