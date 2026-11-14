namespace Tokenify.ReplayReader;

/// <summary>How strongly a replay supports "the recording player opened this chest".</summary>
public enum Evidence
{
    /// <summary>The game itself named the recording player on the chest at the moment it opened or broke. Counted.</summary>
    Confirmed,
    /// <summary>
    /// The game named nobody, but two separate records agree: the chest opened or broke in view, and the player's
    /// own hit on that very chest is recorded just before (and nobody else's). Counted, flagged for review.
    /// </summary>
    Corroborated,
    /// <summary>Something happened to a chest during the player's activity, but the replay does not say who did it.
    /// Never counted automatically; listed so the player can decide.</summary>
    Ambiguous,
    /// <summary>The replay only shows that a chest is gone (or shows nothing at all). Never counted, reported as a
    /// number only.</summary>
    Unobservable,
}

public enum ChestAction
{
    /// <summary>Opened (by hand or by a hit): its "already searched" state turned on.</summary>
    Searched,
    /// <summary>Broken so that its loot spilled: its "destroyed" state turned on before it was ever opened.</summary>
    Destroyed,
    /// <summary>It disappeared; the replay holds no opened or destroyed state for it.</summary>
    Removed,
}

/// <summary>The observations behind a <see cref="ChestEvent"/>. Stable ids: stored, exported and shown in the audit.</summary>
public static class Signal
{
    /// <summary>The chest's own record names the recording player (SearchingPawn).</summary>
    public const string NamedYou = "named-you";
    /// <summary>The chest's record names nobody.</summary>
    public const string NobodyNamed = "nobody-named";
    public const string OpenedState = "opened-state";
    public const string DestroyedState = "destroyed-state";
    /// <summary>The change was seen as it happened, on a chest already in view.</summary>
    public const string SeenLive = "seen-live";
    /// <summary>The chest was not being replicated before; its state arrived together with it.</summary>
    public const string ArrivedWithChest = "arrived-with-chest";
    /// <summary>The loot animation counter went up in the same update.</summary>
    public const string SearchAnimation = "search-animation";
    /// <summary>Destroyed in one go (for example because what it stood on was destroyed).</summary>
    public const string Instant = "instant";
    /// <summary>Destroyed by damage from an ability or effect (the chest carries the damage record).</summary>
    public const string DamageRecord = "damage-record";
    /// <summary>A damage cue from the recording player's own character names this chest just before.</summary>
    public const string YourHit = "your-hit";
    /// <summary>The recording player was attacking with an area weapon or power at that moment.</summary>
    public const string AbilityActive = "ability-active";
    /// <summary>The replay holds no state for the chest at all, only that it is gone.</summary>
    public const string NoDetail = "no-detail";
    /// <summary>The chest had been seen intact (closed) earlier in this replay.</summary>
    public const string SeenIntact = "seen-intact";
}

/// <summary>One chest interaction found in a replay, with the evidence for it.</summary>
/// <param name="Time">Seconds since the replay started recording.</param>
/// <param name="Kind">Registry kind: regular, rare, buried, poster, relic or other.</param>
/// <param name="ClassName">Short class name, e.g. "Tiered_Chest_Athena".</param>
/// <param name="ChestId">Identity of the physical chest within this match.</param>
/// <param name="Counted">True when it adds to the player's chest total.</param>
/// <param name="Signals">Ids from <see cref="Signal"/>.</param>
/// <param name="Holding">What the player had in hand (label or internal class name), when known.</param>
/// <param name="Reason">Plain-language reason for events that are not counted.</param>
/// <param name="Reopened">Opened again after a refill.</param>
/// <param name="Unverified">Its class is not in the chest registry yet.</param>
public sealed record ChestEvent(
    float Time,
    string Kind,
    string ClassName,
    string ChestId,
    ChestAction Action,
    Evidence Evidence,
    bool Counted,
    string[] Signals,
    string? Holding,
    string? Reason,
    bool Reopened = false,
    bool Unverified = false);

/// <summary>A map-placed chest whose replication ended with reason "destroyed" without any opened/destroyed state.</summary>
/// <param name="SeenIntact">Its closed state had been replicated before it disappeared.</param>
public sealed record ContainerRemoval(float Time, string Key, string ClassPath, bool SeenIntact);

/// <summary>A damage cue naming a container: who hit it, with what.</summary>
public readonly record struct ContainerHit(float Time, uint Pawn, string Key, string? Weapon, float Magnitude, bool Fatal);

/// <summary>A character switched to another item (null = nothing in hand).</summary>
public readonly record struct WeaponChange(float Time, uint Pawn, string? Weapon);

/// <summary>An ability/effect cue fired on a character (used only as "was attacking at that moment").</summary>
public readonly record struct AbilityCue(float Time, uint Pawn);

/// <summary>Everything the reader collected that bears on chest interactions.</summary>
public sealed class ReplayObservations
{
    public IReadOnlyList<ContainerOpening> Openings { get; init; } = [];
    public IReadOnlyList<ContainerRemoval> Removals { get; init; } = [];
    public IReadOnlyList<ContainerHit> Hits { get; init; } = [];
    public IReadOnlyList<WeaponChange> Weapons { get; init; } = [];
    public IReadOnlyList<AbilityCue> Cues { get; init; } = [];
    /// <summary>Every character the recording player controlled in this match (one per life).</summary>
    public IReadOnlySet<uint> MyPawns { get; init; } = new HashSet<uint>();
}

public sealed class EvidenceReport
{
    /// <summary>Counted and ambiguous events, in time order.</summary>
    public List<ChestEvent> Events { get; } = new();
    /// <summary>Chests that vanished with no record and no link to the player's actions.</summary>
    public int Unobservable { get; set; }
    public Dictionary<string, int> OtherContainers { get; } = new();
    public SortedSet<string> UnknownChestClasses { get; } = new(StringComparer.Ordinal);
    /// <summary>Chest openings by anyone in view: a plausibility check only.</summary>
    public int OpenedByAnyone { get; set; }
}

/// <summary>
/// Decides, from the observations of one replay, which chest interactions belong to the recording player and how
/// sure that is. Pure logic (no replay reading), so every rule is unit-tested without files.
///
/// What real replays showed (Fortnite 43.00, see docs/REPLAY-EVIDENCE.md): whenever a chest's loot comes out
/// because of a player - opened by hand, opened by a hit, smashed with a pickaxe, a vehicle, an ability, or lost
/// with the structure it stood on - the game writes that player on the chest (SearchingPawn). That is the
/// accepted pattern for <see cref="Evidence.Confirmed"/>. The other classes exist for the cases where that
/// record is missing; they never add to the total unless a second, independent record points at the player.
/// </summary>
public static class EvidenceAnalyzer
{
    /// <summary>How long before a chest opens a hit on it still counts as "just before".</summary>
    public const float HitWindowSeconds = 8f;
    /// <summary>Slack after the event: cues and chest state travel in separate packets.</summary>
    public const float HitSlackSeconds = 1.5f;
    /// <summary>Window around a disappearance in which the player must have been attacking.</summary>
    public const float AbilityWindowSeconds = 3f;
    public const int AbilityCuesNeeded = 3;
    /// <summary>A named record this soon after an unnamed one for the same chest replaces it.</summary>
    public const float SupersedeSeconds = 10f;

    public static EvidenceReport Analyze(ReplayObservations obs, ChestRegistry registry)
    {
        var report = new EvidenceReport();
        var mine = obs.MyPawns;
        var hits = obs.Hits.GroupBy(h => h.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var myWeapons = obs.Weapons.Where(w => mine.Contains(w.Pawn)).OrderBy(w => w.Time).ToList();
        var myCues = obs.Cues.Where(c => mine.Contains(c.Pawn)).Select(c => c.Time).OrderBy(t => t).ToList();

        bool Hit(string key, float t, bool byMe) => hits.TryGetValue(key, out var list)
            && list.Any(h => mine.Contains(h.Pawn) == byMe && h.Time >= t - HitWindowSeconds && h.Time <= t + HitSlackSeconds);

        string? HeldClass(float t)
        {
            string? held = null;
            foreach (var w in myWeapons)
            {
                if (w.Time > t + 0.25f) break;
                held = w.Weapon;
            }
            return held;
        }

        bool Attacking(float t)
        {
            var n = 0;
            foreach (var c in myCues)
            {
                if (c < t - AbilityWindowSeconds) continue;
                if (c > t + AbilityWindowSeconds) break;
                if (++n >= AbilityCuesNeeded) return true;
            }
            return false;
        }

        var countedAt = new Dictionary<string, List<float>>(StringComparer.Ordinal);
        void Counted(string key, float t)
        {
            if (!countedAt.TryGetValue(key, out var list)) countedAt[key] = list = new List<float>();
            list.Add(t);
        }

        // ---- chests whose own record shows them opening or breaking
        foreach (var o in obs.Openings.OrderBy(o => o.Time))
        {
            var cls = registry.Classify(o.ClassPath);
            if (cls.Role == ContainerRole.None) continue;
            var name = ChestRegistry.ShortClassName(o.ClassPath);
            var named = o.Opener is > 0;

            if (named && registry.Counts(cls) && (!o.Stale || o.StableKey)) report.OpenedByAnyone++;

            if (named && mine.Contains(o.Opener!.Value))
            {
                // Without a lasting identity, state that arrives with the chest may be an old opening seen again.
                if (o.Stale && !o.StableKey) continue;

                if (cls.Role == ContainerRole.OtherContainer)
                {
                    report.OtherContainers[cls.Kind] = report.OtherContainers.GetValueOrDefault(cls.Kind) + 1;
                    continue;
                }

                var unverified = cls.Role == ContainerRole.UnknownChest;
                if (unverified)
                {
                    report.UnknownChestClasses.Add(name);
                    if (!registry.CountsUnknownChests) continue;
                }

                var signals = new List<string> { Signal.NamedYou, o.Destroyed ? Signal.DestroyedState : Signal.OpenedState, o.Stale ? Signal.ArrivedWithChest : Signal.SeenLive };
                if (o.AnimCount is > 0) signals.Add(Signal.SearchAnimation);
                if (o.Instant) signals.Add(Signal.Instant);
                if (o.DamageContext) signals.Add(Signal.DamageRecord);
                if (Hit(o.Key, o.Time, byMe: true)) signals.Add(Signal.YourHit);

                report.Events.Add(new ChestEvent(o.Time, cls.Kind, name, o.Key, o.Destroyed ? ChestAction.Destroyed : ChestAction.Searched,
                    Evidence.Confirmed, Counted: true, signals.ToArray(), registry.WeaponLabel(HeldClass(o.Time)), Reason: null, o.Reopened, unverified));
                Counted(o.Key, o.Time);
                continue;
            }

            if (named || !registry.Counts(cls)) continue; // someone else's, or not a chest

            // Nobody named. State that arrives with the chest is an old opening by someone else, long over.
            if (o.Stale) continue;

            var myHit = Hit(o.Key, o.Time, byMe: true);
            var otherHit = Hit(o.Key, o.Time, byMe: false);
            var state = o.Destroyed ? Signal.DestroyedState : Signal.OpenedState;
            var action = o.Destroyed ? ChestAction.Destroyed : ChestAction.Searched;
            if (myHit && !otherHit)
            {
                report.Events.Add(new ChestEvent(o.Time, cls.Kind, name, o.Key, action, Evidence.Corroborated, Counted: true,
                    [Signal.NobodyNamed, state, Signal.SeenLive, Signal.YourHit], registry.WeaponLabel(HeldClass(o.Time)),
                    Reason: null, o.Reopened, cls.Role == ContainerRole.UnknownChest));
                Counted(o.Key, o.Time);
            }
            else
            {
                var why = myHit
                    ? "It opened in view, but both you and another player hit it and the replay names neither."
                    : "It opened in view, but the replay does not name who did it.";
                var signals = new List<string> { Signal.NobodyNamed, state, Signal.SeenLive };
                if (myHit) signals.Add(Signal.YourHit);
                report.Events.Add(new ChestEvent(o.Time, cls.Kind, name, o.Key, action, Evidence.Ambiguous, Counted: false,
                    signals.ToArray(), registry.WeaponLabel(HeldClass(o.Time)), why, o.Reopened, cls.Role == ContainerRole.UnknownChest));
            }
        }

        // A named record that follows an unnamed one for the same chest is the same event, better described.
        report.Events.RemoveAll(e => e.Evidence != Evidence.Confirmed
            && report.Events.Any(c => c.Evidence == Evidence.Confirmed && c.ChestId == e.ChestId && Math.Abs(c.Time - e.Time) <= SupersedeSeconds));

        // ---- chests that simply disappeared
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in obs.Removals.OrderBy(r => r.Time))
        {
            var cls = registry.Classify(r.ClassPath);
            if (!registry.Counts(cls) || !seen.Add(r.Key)) continue;
            // A chest the player already opened disappearing later is not news.
            if (report.Events.Any(e => e.ChestId == r.Key && e.Time <= r.Time + HitSlackSeconds)) continue;

            var name = ChestRegistry.ShortClassName(r.ClassPath);
            var held = HeldClass(r.Time);
            var myHit = Hit(r.Key, r.Time, byMe: true);
            var area = registry.IsAreaWeapon(held) && Attacking(r.Time);
            if (!myHit && !area)
            {
                report.Unobservable++;
                continue;
            }

            var signals = new List<string> { Signal.NoDetail };
            if (r.SeenIntact) signals.Add(Signal.SeenIntact);
            if (myHit) signals.Add(Signal.YourHit);
            if (area) signals.Add(Signal.AbilityActive);
            var why = myHit
                ? "You hit it and it disappeared, but the replay has no record of it opening."
                : $"It disappeared while you were attacking with {registry.WeaponLabel(held) ?? "an area weapon"}. The replay does not say who or what removed it.";
            report.Events.Add(new ChestEvent(r.Time, cls.Kind, name, r.Key, ChestAction.Removed, Evidence.Ambiguous, Counted: false,
                signals.ToArray(), registry.WeaponLabel(held), why, Unverified: cls.Role == ContainerRole.UnknownChest));
        }

        report.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
        return report;
    }
}
