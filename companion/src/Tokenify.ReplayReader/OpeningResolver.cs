namespace Tokenify.ReplayReader;

/// <summary>One replicated update of a container's state, in the order the replay delivered it.</summary>
/// <param name="Time">Replay time in seconds.</param>
/// <param name="Channel">Actor channel the update arrived on (a chest gets a new channel each time it comes back into view).</param>
/// <param name="Key">Identity of the physical chest for the whole match (level path, or class + spawn position).</param>
/// <param name="StableKey">False when the only identity available was a per-channel network id.</param>
/// <param name="ClassPath">Replicated class of the container.</param>
public readonly record struct ContainerUpdate(
    float Time,
    uint Channel,
    string Key,
    bool StableKey,
    string ClassPath,
    uint? SearchingPawn,
    bool? Searched,
    bool? Destroyed,
    uint? AnimCount);

/// <summary>One opening of a container (searched, or destroyed so its loot spilled).</summary>
/// <param name="Stale">The state arrived together with the channel (searched before it came into view).</param>
/// <param name="Reopened">The same physical container had been opened before in this match (it was refilled).</param>
public sealed record ContainerOpening(
    float Time,
    string Key,
    string ClassPath,
    uint? Opener,
    bool Destroyed,
    bool Stale,
    bool StableKey,
    bool Reopened);

/// <summary>
/// Turns the raw stream of container updates into openings, without double counting.
///
/// Replays repeat state constantly: every time a chest streams back into view its whole state is sent again,
/// and spawned chests even get a new network id. The rules (after LED-N/Fortnite-ChestCounter, MIT):
/// <list type="bullet">
/// <item>An opening is the moment a container's <c>bAlreadySearched</c> turns true on a channel. The opener is the
/// most recent <c>SearchingPawn</c> on that channel.</item>
/// <item>State that arrives with the channel itself ("stale") is the container being seen again. If the same
/// physical chest was already recorded as opened by the same pawn, it is not a new opening.</item>
/// <item><c>bAlreadySearched</c> going back to false on the same channel means the chest was refilled. Opening it
/// again is a new opening (when the registry allows it).</item>
/// <item>A container destroyed with a <c>SearchingPawn</c> (the server sets it to whoever destroyed it) counts
/// like an opening, once (when the registry allows it).</item>
/// </list>
/// This class is pure logic so it can be unit-tested without replay files.
/// </summary>
public sealed class OpeningResolver
{
    private sealed class ChannelState
    {
        public float OpenedAt;
        public bool? Searched;
        public uint? LastSearcher;
    }

    private readonly ChestRegistry _registry;
    private readonly Dictionary<uint, ChannelState> _channels = new();
    private readonly HashSet<string> _opened = new(StringComparer.Ordinal);
    private readonly List<ContainerOpening> _openings = new();

    public OpeningResolver(ChestRegistry registry) => _registry = registry;

    public IReadOnlyList<ContainerOpening> Openings => _openings;

    /// <summary>A channel (re)opened: everything known about its previous occupant is forgotten.</summary>
    public void ChannelOpened(uint channel, float time) => _channels[channel] = new ChannelState { OpenedAt = time };

    public void ChannelClosed(uint channel) => _channels.Remove(channel);

    public void Observe(ContainerUpdate u)
    {
        if (!_channels.TryGetValue(u.Channel, out var ch))
        {
            ch = new ChannelState { OpenedAt = u.Time };
            _channels[u.Channel] = ch;
        }

        if (u.SearchingPawn is uint searcher && searcher != 0)
        {
            ch.LastSearcher = searcher;
        }

        var stale = u.Time - ch.OpenedAt < _registry.StaleWindowSeconds;

        if (u.Searched == false)
        {
            // Closed: either its first state, or refilled after an opening. It can be opened (and counted) again.
            ch.Searched = false;
            return;
        }

        if (u.Searched == true && ch.Searched != true)
        {
            ch.Searched = true;
            // State re-sent with the channel for a chest already recorded as opened is that old opening seen
            // again, even if SearchingPawn changed since (whoever later broke the empty chest becomes the
            // "searcher"). A genuinely new opening by the recording player can't be stale: they were right
            // there, so the chest was in view and its closed (refilled) state was seen first.
            if (stale && _opened.Contains(u.Key))
            {
                return;
            }

            Record(u, ch.LastSearcher, destroyed: false, stale);
            return;
        }

        if (_registry.CountDestroyed && u.Destroyed == true && u.SearchingPawn is uint destroyer && destroyer != 0
            && ch.Searched != true && (!_opened.Contains(u.Key) || ch.Searched == false))
        {
            // Broken instead of opened: the loot spills out and the server records who broke it.
            ch.Searched = true;
            Record(u, destroyer, destroyed: true, stale);
        }
    }

    private void Record(ContainerUpdate u, uint? opener, bool destroyed, bool stale)
    {
        var reopened = _opened.Contains(u.Key);
        if (reopened && !_registry.CountReopenedAfterRefill)
        {
            return;
        }

        _opened.Add(u.Key);
        _openings.Add(new ContainerOpening(u.Time, u.Key, u.ClassPath, opener, destroyed, stale, u.StableKey, reopened));
    }

    /// <summary>
    /// The openings made by any of <paramref name="pawns"/>. A stale opening only counts when the chest has a
    /// stable identity (otherwise it could be an old opening seen again under a new network id).
    /// </summary>
    public IEnumerable<ContainerOpening> OpenedBy(IReadOnlySet<uint> pawns) =>
        _openings.Where(o => o.Opener is uint p && pawns.Contains(p) && (!o.Stale || o.StableKey));
}
