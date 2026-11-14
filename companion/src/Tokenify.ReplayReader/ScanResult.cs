namespace Tokenify.ReplayReader;

/// <summary>Why a replay could or could not be counted. Stored with every result.</summary>
public enum ScanStatus
{
    /// <summary>Read to the end; the chest count is final.</summary>
    Complete,
    /// <summary>Reading stopped early on damaged data; chests up to that point were found. Not uploaded as final.</summary>
    Partial,
    /// <summary>Fortnite is still recording this match (or it crashed before finishing): nothing can be read yet.</summary>
    NotReady,
    /// <summary>Not a Fortnite replay, or damaged beyond reading.</summary>
    Corrupt,
    /// <summary>A replay format or build this parser does not understand. Kept for a retry after an update.</summary>
    Unsupported,
    /// <summary>The replay could be read but the recording player could not be identified, so nothing is attributed.</summary>
    NoRecorder,
}

/// <summary>A chest the recording player opened.</summary>
/// <param name="Time">Seconds since the replay started recording.</param>
/// <param name="Kind">Registry kind: regular, rare, buried, poster, relic or other.</param>
/// <param name="ClassName">Short class name, e.g. "Tiered_Chest_Athena".</param>
/// <param name="Destroyed">Broken instead of searched.</param>
/// <param name="Reopened">Opened again after a refill.</param>
/// <param name="Unverified">Its class is not in the chest registry yet (counted only under "count-and-flag").</param>
public sealed record ChestRecord(float Time, string Kind, string ClassName, bool Destroyed, bool Reopened, bool Unverified);

/// <summary>Everything TOKENIFY learns from one replay file. Contains nothing about other players.</summary>
public sealed class ScanResult
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime FileWriteUtc { get; set; }

    public ScanStatus Status { get; set; }
    /// <summary>Short technical reason when <see cref="Status"/> is not Complete (shown only in diagnostics).</summary>
    public string? Error { get; set; }

    public string ParserVersion { get; set; } = ParserInfo.Version;
    public int RegistryVersion { get; set; }

    // ---- replay header ----
    /// <summary>Unique id of this recording (survives renames and copies). Not shared between players.</summary>
    public string? ReplayGuid { get; set; }
    public string? Branch { get; set; }
    public uint Changelist { get; set; }
    public int FileVersion { get; set; }
    public int EngineNetworkVersion { get; set; }
    public int NetworkVersion { get; set; }
    public bool IsLive { get; set; }
    public DateTime? StartedUtc { get; set; }
    public uint LengthMs { get; set; }
    public DateTime? EndedUtc => StartedUtc?.AddMilliseconds(LengthMs);

    // ---- match ----
    /// <summary>The Fortnite match's session id (GameState.GameSessionId): the same for everyone in that match.</summary>
    public string? MatchSessionId { get; set; }
    public string? Playlist { get; set; }

    // ---- recording player ----
    public bool RecorderFound { get; set; }
    /// <summary>Epic account id of the recording player, as replicated in the replay. Kept on this PC only.</summary>
    public string? RecorderAccountId { get; set; }
    public string? RecorderName { get; set; }
    public int? RecorderTeam { get; set; }

    // ---- chests ----
    /// <summary>The counted chests (confirmed or corroborated), one per opening.</summary>
    public List<ChestRecord> Chests { get; set; } = new();
    public int ChestsOpened => Chests.Count;

    /// <summary>Every chest interaction found, with its evidence: the counted ones and the ambiguous ones.</summary>
    public List<ChestEvent> Events { get; set; } = new();
    /// <summary>Counted chests that were opened (by hand or by a hit).</summary>
    public int Searched => Events.Count(e => e.Counted && e.Action == ChestAction.Searched);
    /// <summary>Counted chests that were broken so their loot spilled.</summary>
    public int Destroyed => Events.Count(e => e.Counted && e.Action == ChestAction.Destroyed);
    public int Confirmed => Events.Count(e => e.Counted && e.Evidence == Evidence.Confirmed);
    public int Corroborated => Events.Count(e => e.Counted && e.Evidence == Evidence.Corroborated);
    /// <summary>Interactions the replay cannot attribute. Never part of the total; shown for review.</summary>
    public int Ambiguous => Events.Count(e => e.Evidence == Evidence.Ambiguous);
    /// <summary>Chests that vanished with no record of who or why, unrelated to the player's actions.</summary>
    public int Unobservable { get; set; }
    /// <summary>Chest openings by anyone in view (you, teammates, opponents): a plausibility check only.</summary>
    public int ChestsOpenedByAnyone { get; set; }
    /// <summary>Ammo boxes and other containers the recording player opened (never counted as chests).</summary>
    public Dictionary<string, int> OtherContainers { get; set; } = new();
    /// <summary>Chest-looking classes the registry does not know yet (for registry updates).</summary>
    public List<string> UnknownChestClasses { get; set; } = new();
    public int CorruptBlocksSkipped { get; set; }

    public Dictionary<string, int> ChestsByKind() => Chests.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
}
