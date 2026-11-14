namespace Tokenify.ReplayReader;

/// <summary>How strongly a replay supports "the recording player opened this chest".</summary>
public enum Evidence
{
    /// <summary>The game itself named the recording player on the chest at the moment it opened or broke. Counted.</summary>
    Confirmed,
    /// <summary>
    /// No chest record names the player, but a record of the player's own action does the job instead. Either the
    /// chest opened or broke in view and the player's own hit on that very chest is recorded just before (and
    /// nobody else's), or the player's own character is recorded searching a container and the replay holds no
    /// chest record for that moment at all (many matches do not record ordinary chests). Counted, flagged for review.
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
    /// <summary>The recording player's own character is recorded searching a container at that moment.</summary>
    public const string YourSearch = "your-search";
    /// <summary>The replay holds no chest record for that search: what was searched is not recorded.</summary>
    public const string NoChestRecord = "no-chest-record";
    /// <summary>For a stretch of the match the replay holds next to nothing about the player's own character.</summary>
    public const string CharacterNotRecorded = "character-not-recorded";
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

/// <summary>
/// One search action of a character: the game sets a flag on the character itself while it searches a container
/// (chest, ammo box...) and clears it when the search is over. It does not say what was searched.
/// </summary>
/// <param name="End">When the flag was cleared; null when the replay never shows that.</param>
public readonly record struct OwnSearch(float Start, float? End, uint Pawn);

/// <summary>How much the replay holds about one character: its first and last update and how many there were.</summary>
public readonly record struct PawnLife(uint Pawn, float First, float Last, int Updates);

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
    /// <summary>Search actions of the recording player's own characters.</summary>
    public IReadOnlyList<OwnSearch> Searches { get; init; } = [];
    /// <summary>When the recording player jumped from the bus (the match proper begins); null when never seen.</summary>
    public float? DroppedAt { get; init; }
    /// <summary>One entry per character the recording player controlled.</summary>
    public IReadOnlyList<PawnLife> Lives { get; init; } = [];
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
    /// <summary>A chest's record may arrive this long after the search that opened it ended (measured: up to 2.5 s).</summary>
    public const float SearchRecordLagSeconds = 3f;
    /// <summary>...and, because the two travel separately, this much before the search is seen starting.</summary>
    public const float SearchLeadSeconds = 0.3f;
    /// <summary>Searching a chest takes about 0.8 s (measured 0.2 - 2.2 s). Anything held longer is something else.</summary>
    public const float MaxSearchSeconds = 3f;
    /// <summary>Another player's chest opening this close to the end of a search may be the same chest.</summary>
    public const float SearchRaceSeconds = 1.5f;
    /// <summary>Kind of a counted search whose container the replay does not record.</summary>
    public const string UnrecordedKind = "unrecorded";
    /// <summary>A life this long with at most <see cref="SilentLifeUpdates"/> updates was not recorded
    /// (a recorded character gets several updates every second).</summary>
    public const float SilentLifeSeconds = 30f;
    public const int SilentLifeUpdates = 10;

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

        AddUnrecordedSearches(report, obs, registry, HeldClass);
        AddSilentLives(report, obs);

        report.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
        return report;
    }

    /// <summary>
    /// The player's own search actions that no chest record explains.
    ///
    /// Why this exists: in many Fortnite 43 matches ordinary chests are not written to the replay at all - no
    /// chest, no opened state, no loot - while buried and relic chests are. A hand-counted match (seven ordinary
    /// chests, one relic chest) held exactly one chest record. What the replay does always hold is the player's own
    /// character: it carries a "started searching" flag for as long as a search lasts. In the same replays every
    /// buried and relic chest opening has such a search right before its record (measured on 106 openings), so the
    /// two are the same act seen twice, and a search without a record is an opening the replay did not describe.
    ///
    /// Rules, in this order, so that nothing is counted twice and none of the chest-record rules above change:
    /// 1. A chest record naming the player, seen as it happened, explains the search that ended just before it.
    /// 2. A record naming the player that arrived together with its chest explains the latest unexplained search
    ///    before it (the search was earlier; the chest only came into the replay afterwards).
    /// 3. A chest that opened in view with nobody named, at the moment of a search, is that search's chest.
    /// 4. Every search left over is counted once as "unrecorded" - unless it happened before the bus drop, was held
    ///    longer than a search takes, never ended, or coincides with another player's recorded opening; those are
    ///    listed as ambiguous and never counted automatically.
    /// The flag does not say what was searched: an ammo box or a cooler looks the same. That is stated wherever the
    /// number is shown.
    /// </summary>
    /// <summary>
    /// After a reboot some replays hold next to nothing about the player's new character: two or three updates in
    /// several minutes, so no searches and no chest records can be seen. That is reported, with the times, as a
    /// stretch with no record. Nothing is counted for it and nothing is guessed.
    /// </summary>
    private static bool IsSilent(PawnLife life, float? droppedAt) =>
        droppedAt is float dropped && life.First >= dropped && life.Last - life.First >= SilentLifeSeconds && life.Updates <= SilentLifeUpdates;

    private static void AddSilentLives(EvidenceReport report, ReplayObservations obs)
    {
        foreach (var life in obs.Lives.Where(l => obs.MyPawns.Contains(l.Pawn)).OrderBy(l => l.First))
        {
            if (!IsSilent(life, obs.DroppedAt)) continue;
            static string Clock(float s) => $"{(int)s / 60:00}:{(int)s % 60:00}";
            report.Events.Add(new ChestEvent(life.First, UnrecordedKind, "Character not recorded", FormattableString.Invariant($"gap@{life.First:0}"),
                ChestAction.Removed, Evidence.Unobservable, Counted: false, [Signal.CharacterNotRecorded], null,
                $"From {Clock(life.First)} to {Clock(life.Last)} the replay holds almost nothing about your character (this happens after a reboot). "
                + "Chests you opened in that time cannot be seen, so this match's count is a minimum."));
        }
    }

    private static void AddUnrecordedSearches(EvidenceReport report, ReplayObservations obs, ChestRegistry registry, Func<float, string?> heldClass)
    {
        if (!registry.CountUnrecordedSearches) return;
        var mine = obs.MyPawns;
        var searches = obs.Searches.Where(s => mine.Contains(s.Pawn)).OrderBy(s => s.Start).ToList();
        if (searches.Count == 0) return;

        var explained = new bool[searches.Count];
        int Latest(Func<OwnSearch, bool> fits)
        {
            for (var i = searches.Count - 1; i >= 0; i--)
            {
                if (!explained[i] && fits(searches[i])) return i;
            }
            return -1;
        }
        bool Around(OwnSearch s, float t) => s.Start <= t + SearchLeadSeconds && t <= (s.End ?? s.Start) + SearchRecordLagSeconds;

        // 1 and 2: records that name the player. A destroyed chest was broken, not searched: it explains no search.
        var named = obs.Openings
            .Where(o => o.Opener is uint p && mine.Contains(p) && !o.Destroyed && registry.Classify(o.ClassPath).Role != ContainerRole.None)
            .OrderBy(o => o.Time).ToList();
        foreach (var o in named.Where(o => !o.Stale))
        {
            if (Latest(s => Around(s, o.Time)) is >= 0 and var i) explained[i] = true;
        }
        // ...unless a stretch with no character data lies in between: then the chest was opened in that stretch,
        // where no search could be seen, and it explains none of the searches before it.
        var silent = obs.Lives.Where(l => mine.Contains(l.Pawn) && IsSilent(l, obs.DroppedAt)).ToList();
        foreach (var o in named.Where(o => o.Stale && o.StableKey))
        {
            if (Latest(s => s.Start <= o.Time && !silent.Any(g => g.First >= s.Start && g.First <= o.Time)) is >= 0 and var i) explained[i] = true;
        }

        // 3: a chest that opened in view with nobody named. Already counted (your hit on record): nothing to add.
        // Listed as ambiguous: the player's own search at that moment is the second record it was missing.
        for (var n = 0; n < report.Events.Count; n++)
        {
            var e = report.Events[n];
            if (e.Action != ChestAction.Searched || !e.Signals.Contains(Signal.NobodyNamed)) continue;
            if (Latest(s => Around(s, e.Time)) is not (>= 0 and var i)) continue;
            explained[i] = true;
            if (!e.Counted)
            {
                report.Events[n] = e with { Evidence = Evidence.Corroborated, Counted = true, Reason = null, Signals = [.. e.Signals.Where(x => x != Signal.YourHit), Signal.YourSearch] };
            }
        }

        // 4: what is left.
        var othersOpened = obs.Openings
            .Where(o => o.Opener is uint p && p != 0 && !mine.Contains(p) && !o.Stale && !o.Destroyed && registry.Counts(registry.Classify(o.ClassPath)))
            .Select(o => o.Time).ToList();
        for (var i = 0; i < searches.Count; i++)
        {
            if (explained[i]) continue;
            var s = searches[i];
            // The warm-up island before the bus is not part of the match.
            if (obs.DroppedAt is float dropped && s.Start < dropped) continue;

            string? why = null;
            if (obs.DroppedAt is null)
            {
                why = "You searched something, but the replay never shows you leaving the bus, so it cannot be placed in the match.";
            }
            else if (s.End is not float end)
            {
                why = "You started searching something, but the replay does not show the search ending.";
            }
            else if (end - s.Start > MaxSearchSeconds)
            {
                why = $"You held a search for {end - s.Start:0.#} seconds, longer than a chest takes. The replay does not record what it was.";
            }
            else if (othersOpened.Any(t => t >= end - 0.5f && t <= end + SearchRaceSeconds))
            {
                why = "You searched something, and at the same moment the replay records another player opening a chest and none for you. It may have been the same chest.";
            }

            var time = s.End ?? s.Start;
            report.Events.Add(new ChestEvent(time, UnrecordedKind, "Search (chest not in replay)", FormattableString.Invariant($"search@{s.Start:0.0}"),
                ChestAction.Searched, why is null ? Evidence.Corroborated : Evidence.Ambiguous, Counted: why is null,
                [Signal.YourSearch, Signal.NoChestRecord], registry.WeaponLabel(heldClass(time)), why));
        }
    }
}
