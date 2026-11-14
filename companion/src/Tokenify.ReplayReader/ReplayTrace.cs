using System.Text.RegularExpressions;

namespace Tokenify.ReplayReader;

/// <summary>What to record besides the chest count. Used by the audit viewer, diagnostics and parser research.</summary>
public sealed class TraceOptions
{
    /// <summary>Record every replicated field of every container update, and container channel opens/closes.</summary>
    public bool Containers { get; init; }

    /// <summary>Also record fields of any other class whose path matches (research: e.g. an ability's actors).</summary>
    public Regex? ExtraGroups { get; init; }

    /// <summary>For <see cref="ExtraGroups"/> classes: only keep fields whose name matches (keeps busy classes small).</summary>
    public Regex? FieldFilter { get; init; }

    /// <summary>Collect exported object paths matching this (research: discovering class and item names).</summary>
    public Regex? Keywords { get; init; }

    /// <summary>Count property updates per class and spawned actors per archetype (research).</summary>
    public bool Census { get; init; }

    /// <summary>Count remote calls / custom fields per "class.field" on every channel (research).</summary>
    public bool RpcCensus { get; init; }

    /// <summary>Decode the parameters of remote calls whose "class.function" matches (research: damage cues...).</summary>
    public Regex? RpcCapture { get; init; }

    /// <summary>Record everything about map-placed actors whose own path matches (research: one building...).</summary>
    public Regex? Actors { get; init; }

    /// <summary>Record spawned (non map-placed) actors whose archetype path matches: when and where (research).</summary>
    public Regex? Spawns { get; init; }

    /// <summary>Record player positions, at most one per pawn per second (research: how far away events are seen).</summary>
    public bool Movement { get; init; }
}

/// <summary>
/// One replicated field in an update. Raw holds up to 64 bits exactly as sent; longer fields keep their bytes in
/// Data. Note is a decoded, human readable form when the reader knows the layout.
/// </summary>
public readonly record struct TraceField(string Name, int Bits, ulong Raw, byte[]? Data = null, string? Note = null)
{
    /// <summary>The value read as an object reference (a packed network id), or null if it isn't shaped like one.</summary>
    public uint? AsNetGuid()
    {
        if (Bits is < 8 or > 40 || Bits % 8 != 0) return null;
        uint value = 0;
        var raw = Raw;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = (uint)(raw & 0xFF);
            raw >>= 8;
            value |= (b >> 1) << shift;
            if ((b & 1) == 0) return value;
        }
        return null;
    }
}

public sealed record TraceUpdate(float Time, uint Channel, uint Actor, string Group, TraceField[] Fields);

/// <param name="Kind">"open" or "close".</param>
/// <param name="Reason">For a close: Destroyed, Dormancy, LevelUnloaded, Relevancy or TearOff.</param>
/// <param name="Bare">The channel carried no container state at all between opening and closing.</param>
public sealed record TraceChannelEvent(float Time, uint Channel, uint Actor, string Kind, string? Reason, string? Class, string? Key,
    double? X, double? Y, double? Z, bool Bare = false);

public sealed record TraceRpc(float Time, uint Channel, string Cache, string Field, int Bits);

public sealed record TraceSpawn(float Time, uint Channel, uint Actor, string Archetype, double X, double Y, double Z);

public readonly record struct TracePosition(float Time, uint Actor, double X, double Y, double Z);

public sealed class ReplayTrace
{
    public List<TraceUpdate> Updates { get; } = new();
    public List<TraceChannelEvent> Channels { get; } = new();
    public List<TraceRpc> Rpcs { get; } = new();
    public List<TraceSpawn> Spawns { get; } = new();
    public List<TracePosition> Positions { get; } = new();
    public Dictionary<string, string[]> GroupFields { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> KeywordPaths { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Census { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SpawnCensus { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> RpcCensus { get; } = new(StringComparer.Ordinal);
    /// <summary>Network id -> object path (map-placed actors) or archetype path (spawned actors).</summary>
    public Dictionary<uint, string> Paths { get; } = new();
}
