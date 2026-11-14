using System.Text.RegularExpressions;
using Tokenify.ReplayReader;

namespace Tokenify.Tests;

/// <summary>
/// The evidence rules: which chest interactions are counted for the recording player, and with what certainty.
/// These are logic tests with constructed observations, so the expected counts are known exactly. Whether a real
/// replay contains those observations is a separate question, answered by the replay fixtures and by
/// docs/REPLAY-EVIDENCE.md.
/// </summary>
public static partial class Tests
{
    private static IEnumerable<(string, Action)> EvidenceTests()
    {
        // ---- several chests from one attack (each chest is its own event) ----
        yield return ("one attack destroys one chest", () => AttackDestroys(1));
        yield return ("one attack destroys three chests", () => AttackDestroys(3));
        yield return ("one attack destroys ten chests", () => AttackDestroys(10));
        yield return ("chests destroyed on the same tick are not merged", SameTickNotMerged);
        yield return ("chests never seen before count (camera and line of sight play no part)", NeverSeenBefore);
        yield return ("one in view, several not: all counted", OneSeenSeveralNot);
        yield return ("a building with several chests comes down", BuildingCollapse);
        yield return ("nearby things that are not chests are not counted", NearbyNonChests);
        yield return ("another player destroys a different chest at the same moment", OtherPlayerSameMoment);
        yield return ("a teammate's area attack is never yours", TeammateAttack);

        // ---- one chest, several records ----
        yield return ("one chest with several related records counts once", OneChestManyRecords);
        yield return ("opened by you, destroyed later by someone else: once", OpenedThenDestroyedByOther);
        yield return ("a chest you opened vanishing later is not a second event", VanishAfterOpening);
        yield return ("a refilled chest opened again is flagged as reopened", ReopenedFlag);

        // ---- nobody named ----
        yield return ("destroyed in view, nobody named, your hit on record: corroborated and counted", UnnamedWithMyHit);
        yield return ("destroyed in view, nobody named, you and another player hit it: ambiguous", UnnamedBothHit);
        yield return ("destroyed in view, nobody named, no hit on record: ambiguous, not counted", UnnamedNoHit);
        yield return ("already destroyed when first seen, nobody named: not an event", UnnamedStale);
        yield return ("a named record following an unnamed one is a single confirmed event", NamedFollowsUnnamed);
        yield return ("your hit long before does not corroborate", OldHitDoesNotCount);

        // ---- chests that vanish without any record ----
        yield return ("four chests vanish while you attack with an area power: four ambiguous, none counted", VanishWhileAttacking);
        yield return ("chests vanishing while you are not attacking are only tallied", VanishNotAttacking);
        yield return ("holding an area power without attacking does not flag vanished chests", VanishHoldingOnly);
        yield return ("a vanished chest you hit is ambiguous", VanishAfterMyHit);
        yield return ("vanished ammo boxes are ignored", VanishOtherContainer);

        // ---- reporting ----
        yield return ("events are in time order with searched/destroyed totals", TotalsAndOrder);
        yield return ("what you were holding is labelled from the registry", HoldingLabels);
        yield return ("registry v2: weapons, hammocks and cannons", RegistryV2);

        // ---- the app must never touch the game ----
        yield return ("no game-touching code anywhere in the source", SafetyScan);
    }

    private const string RelicCls = "/RelicChest/Gameplay/B_TieredChest_Relics.B_TieredChest_Relics_C";
    private const string AmmoCls = "/Game/Building/ActorBlueprints/Containers/Tiered_Ammo_Athena.Tiered_Ammo_Athena_C";
    private const string FoodCls = "/Game/Building/ActorBlueprints/Containers/FoodBox_Produce_Athena.FoodBox_Produce_Athena_C";
    private const string GumGum = "B_TackyGoofinessWeapon";

    private static ContainerUpdate D(float t, uint ch, string key, uint? pawn, string cls = ChestCls, bool? instant = null, bool context = false) =>
        new(t, ch, key, true, cls, pawn, null, true, 1, instant, context);

    private static EvidenceReport Analyze(OpeningResolver r, IEnumerable<ContainerRemoval>? removals = null, IEnumerable<ContainerHit>? hits = null,
        IEnumerable<WeaponChange>? weapons = null, IEnumerable<AbilityCue>? cues = null, ChestRegistry? reg = null) =>
        EvidenceAnalyzer.Analyze(new ReplayObservations
        {
            Openings = r.Openings,
            Removals = (removals ?? []).ToList(),
            Hits = (hits ?? []).ToList(),
            Weapons = (weapons ?? []).ToList(),
            Cues = (cues ?? []).ToList(),
            MyPawns = MyPawns,
        }, reg ?? ChestRegistry.Default);

    private static int CountedTotal(EvidenceReport report) => report.Events.Count(e => e.Counted);

    /// <summary>A chest that was in view (closed) and is then destroyed by <paramref name="pawn"/> at <paramref name="t"/>.</summary>
    private static void SeenThenDestroyed(OpeningResolver r, uint ch, string key, float t, uint? pawn, string cls = ChestCls, bool? instant = null)
    {
        r.ChannelOpened(ch, 1f);
        r.Observe(U(1f, ch, key, searched: false, cls: cls));
        r.Observe(D(t, ch, key, pawn, cls, instant));
    }

    /// <summary>A chest that was not being replicated: its channel opens already destroyed by <paramref name="pawn"/>.</summary>
    private static void ArrivesDestroyed(OpeningResolver r, uint ch, string key, float t, uint? pawn, string cls = ChestCls)
    {
        r.ChannelOpened(ch, t);
        r.Observe(D(t, ch, key, pawn, cls));
    }

    private static IEnumerable<AbilityCue> Burst(float from, float to, uint pawn = Me)
    {
        for (var t = from; t <= to; t += 0.1f) yield return new AbilityCue(t, pawn);
    }

    // ------------------------------------------------------------------ several chests from one attack

    private static void AttackDestroys(int chests)
    {
        var r = Resolver();
        for (uint i = 0; i < chests; i++) SeenThenDestroyed(r, 10 + i, "chest-" + i, 50f, Me);
        var report = Analyze(r);
        Eq(chests, CountedTotal(report), "counted");
        Eq(chests, report.Events.Select(e => e.ChestId).Distinct().Count(), "distinct chests");
        True(report.Events.All(e => e.Evidence == Evidence.Confirmed && e.Action == ChestAction.Destroyed), "all confirmed destructions");
        True(report.Events.All(e => e.Signals.Contains(Signal.NamedYou) && e.Signals.Contains(Signal.DestroyedState) && e.Signals.Contains(Signal.SeenLive)), "signals");
    }

    private static void SameTickNotMerged()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "a", 50f, Me);
        SeenThenDestroyed(r, 2, "b", 50f, Me);
        var report = Analyze(r);
        Eq(2, CountedTotal(report), "two chests at the same timestamp");
        Eq(report.Events[0].Time, report.Events[1].Time, "same time");
    }

    private static void NeverSeenBefore()
    {
        // Nothing here says where the camera pointed or whether a wall was in the way: no such input exists.
        var r = Resolver();
        ArrivesDestroyed(r, 1, "behind-wall", 50f, Me);
        ArrivesDestroyed(r, 2, "behind-camera", 50f, Me);
        var report = Analyze(r);
        Eq(2, CountedTotal(report), "counted");
        True(report.Events.All(e => e.Signals.Contains(Signal.ArrivedWithChest)), "state arrived with the chest");
    }

    private static void OneSeenSeveralNot()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "in-view", 50f, Me);
        for (uint i = 0; i < 4; i++) ArrivesDestroyed(r, 2 + i, "out-" + i, 50.1f, Me);
        Eq(5, CountedTotal(Analyze(r)), "counted");
    }

    private static void BuildingCollapse()
    {
        var r = Resolver();
        for (uint i = 0; i < 4; i++) SeenThenDestroyed(r, 1 + i, "floor-" + i, 80f, Me, instant: true);
        var report = Analyze(r);
        Eq(4, CountedTotal(report), "counted");
        True(report.Events.All(e => e.Signals.Contains(Signal.Instant)), "instant");
    }

    private static void NearbyNonChests()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "chest", 50f, Me);
        SeenThenDestroyed(r, 2, "ammo", 50f, Me, AmmoCls);
        SeenThenDestroyed(r, 3, "food", 50f, Me, FoodCls);
        SeenThenDestroyed(r, 4, "barrel", 50f, Me, "/Game/Props/Athena_Prop_SlurpPirateBarrel.Athena_Prop_SlurpPirateBarrel_C");
        SeenThenDestroyed(r, 5, "wall", 50f, Me, "/Game/Building/Wall_Wood.Wall_Wood_C");
        var report = Analyze(r);
        Eq(1, CountedTotal(report), "only the chest");
        Eq(1, report.OtherContainers.GetValueOrDefault("ammo"), "ammo listed apart");
        Eq(1, report.OtherContainers.GetValueOrDefault("food"), "food listed apart");
    }

    private static void OtherPlayerSameMoment()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "mine", 50f, Me);
        SeenThenDestroyed(r, 2, "theirs", 50f, Enemy);
        var report = Analyze(r);
        Eq(1, CountedTotal(report), "mine only");
        Eq("mine", report.Events.Single().ChestId, "which");
        Eq(2, report.OpenedByAnyone, "anyone");
    }

    private static void TeammateAttack()
    {
        var r = Resolver();
        for (uint i = 0; i < 5; i++) SeenThenDestroyed(r, 1 + i, "c" + i, 50f, Mate);
        var report = Analyze(r, weapons: [new WeaponChange(10f, Me, GumGum)], cues: Burst(48f, 52f));
        Eq(0, report.Events.Count, "nothing listed for me");
    }

    // ------------------------------------------------------------------ one chest, several records

    private static void OneChestManyRecords()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, Me);
        r.Observe(D(50.2f, 1, "k", Me));           // the same state sent again
        r.Observe(D(51f, 1, "k", null));           // and once more without the name
        r.ChannelClosed(1);
        r.ChannelOpened(7, 90f);                   // seen again later under a new channel
        r.Observe(D(90f, 7, "k", Me));
        var report = Analyze(r, removals: [new ContainerRemoval(120f, "k", ChestCls, false)],
            hits: [new ContainerHit(49f, Me, "k", "B_Athena_Pickaxe_Generic", 50, true), new ContainerHit(49.5f, Me, "k", "B_Athena_Pickaxe_Generic", 50, true)]);
        Eq(1, report.Events.Count, "events");
        Eq(1, CountedTotal(report), "counted once");
        True(report.Events[0].Signals.Contains(Signal.YourHit), "the hits are supporting evidence, not extra chests");
    }

    private static void OpenedThenDestroyedByOther()
    {
        var r = Resolver();
        r.ChannelOpened(1, 1f);
        r.Observe(U(1f, 1, "k", searched: false));
        r.Observe(U(30f, 1, "k", Me, searched: true));
        r.Observe(D(60f, 1, "k", Enemy));
        var report = Analyze(r);
        Eq(1, CountedTotal(report), "counted");
        Eq(ChestAction.Searched, report.Events.Single().Action, "as an opening");
    }

    private static void VanishAfterOpening()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, Me);
        var report = Analyze(r, removals: [new ContainerRemoval(55f, "k", ChestCls, true)],
            weapons: [new WeaponChange(10f, Me, GumGum)], cues: Burst(53f, 57f));
        Eq(1, report.Events.Count, "only the destruction");
        Eq(0, report.Unobservable, "not tallied either");
    }

    private static void ReopenedFlag()
    {
        var r = Resolver();
        r.ChannelOpened(1, 1f);
        r.Observe(U(1f, 1, "k", searched: false));
        r.Observe(U(30f, 1, "k", Me, searched: true));
        r.Observe(U(200f, 1, "k", searched: false));
        r.Observe(U(260f, 1, "k", Me, searched: true));
        var report = Analyze(r);
        Eq(2, CountedTotal(report), "counted");
        True(!report.Events[0].Reopened && report.Events[1].Reopened, "second one is the reopening");
    }

    // ------------------------------------------------------------------ nobody named

    private static void UnnamedWithMyHit()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, null);
        var report = Analyze(r, hits: [new ContainerHit(48f, Me, "k", "B_Athena_Pickaxe_Generic", 50, true)]);
        var e = report.Events.Single();
        Eq(Evidence.Corroborated, e.Evidence, "class");
        True(e.Counted, "counted");
        True(e.Signals.Contains(Signal.NobodyNamed) && e.Signals.Contains(Signal.YourHit), "signals");
    }

    private static void UnnamedBothHit()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, null);
        var report = Analyze(r, hits: [new ContainerHit(48f, Me, "k", null, 50, false), new ContainerHit(49f, Enemy, "k", null, 50, true)]);
        var e = report.Events.Single();
        Eq(Evidence.Ambiguous, e.Evidence, "class");
        True(!e.Counted && e.Reason is not null, "not counted, with a reason");
    }

    private static void UnnamedNoHit()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, null);
        var report = Analyze(r);
        Eq(Evidence.Ambiguous, report.Events.Single().Evidence, "class");
        Eq(0, CountedTotal(report), "counted");
    }

    private static void UnnamedStale()
    {
        var r = Resolver();
        ArrivesDestroyed(r, 1, "k", 50f, null);
        var report = Analyze(r, hits: [new ContainerHit(48f, Me, "k", null, 50, true)]);
        Eq(0, report.Events.Count, "not an event");
    }

    private static void NamedFollowsUnnamed()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, null);
        r.Observe(D(50.4f, 1, "k", Me));
        var report = Analyze(r);
        var e = report.Events.Single();
        Eq(Evidence.Confirmed, e.Evidence, "class");
        Eq(1, CountedTotal(report), "counted once");
    }

    private static void OldHitDoesNotCount()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "k", 50f, null);
        var report = Analyze(r, hits: [new ContainerHit(50f - EvidenceAnalyzer.HitWindowSeconds - 5f, Me, "k", null, 50, false)]);
        Eq(Evidence.Ambiguous, report.Events.Single().Evidence, "class");
    }

    // ------------------------------------------------------------------ vanished without a record

    private static ContainerRemoval[] Ship(float t) =>
        Enumerable.Range(0, 4).Select(i => new ContainerRemoval(t, "ship-chest-" + i, ChestCls, false)).ToArray();

    private static void VanishWhileAttacking()
    {
        var report = Analyze(Resolver(), removals: Ship(447.1f), weapons: [new WeaponChange(417.4f, Me, GumGum)], cues: Burst(444.1f, 448.1f));
        Eq(4, report.Events.Count, "four separate events");
        True(report.Events.All(e => e.Evidence == Evidence.Ambiguous && !e.Counted && e.Action == ChestAction.Removed), "ambiguous, not counted");
        True(report.Events.All(e => e.Signals.Contains(Signal.NoDetail) && e.Signals.Contains(Signal.AbilityActive)), "signals");
        True(report.Events.All(e => e.Reason!.Contains("Gum-Gum Fruit", StringComparison.Ordinal)), "reason names the power");
        Eq(0, CountedTotal(report), "counted");
        Eq(0, report.Unobservable, "tally");
    }

    private static void VanishNotAttacking()
    {
        var report = Analyze(Resolver(), removals: Ship(447.1f), weapons: [new WeaponChange(417.4f, Me, "B_Athena_Pickaxe_Generic")], cues: Burst(444.1f, 448.1f));
        Eq(0, report.Events.Count, "no events");
        Eq(4, report.Unobservable, "tallied");
    }

    private static void VanishHoldingOnly()
    {
        var report = Analyze(Resolver(), removals: Ship(447.1f), weapons: [new WeaponChange(417.4f, Me, GumGum)], cues: Burst(300f, 301f));
        Eq(0, report.Events.Count, "no events");
        Eq(4, report.Unobservable, "tallied");
    }

    private static void VanishAfterMyHit()
    {
        var report = Analyze(Resolver(), removals: [new ContainerRemoval(100f, "k", ChestCls, true)],
            hits: [new ContainerHit(97f, Me, "k", "B_Athena_Pickaxe_Generic", 50, true)]);
        var e = report.Events.Single();
        Eq(Evidence.Ambiguous, e.Evidence, "class");
        True(e.Signals.Contains(Signal.YourHit) && e.Signals.Contains(Signal.SeenIntact), "signals");
        True(!e.Counted, "not counted");
    }

    private static void VanishOtherContainer()
    {
        var report = Analyze(Resolver(), removals: [new ContainerRemoval(100f, "ammo", AmmoCls, false)],
            weapons: [new WeaponChange(10f, Me, GumGum)], cues: Burst(98f, 102f));
        Eq(0, report.Events.Count, "no events");
        Eq(0, report.Unobservable, "not tallied");
    }

    // ------------------------------------------------------------------ reporting

    private static void TotalsAndOrder()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "late", 300f, Me, RelicCls);
        r.ChannelOpened(2, 1f);
        r.Observe(U(1f, 2, "early", searched: false));
        r.Observe(U(40f, 2, "early", Me, searched: true));
        var result = new ScanResult { Events = Analyze(r).Events };
        Eq(1, result.Searched, "searched");
        Eq(1, result.Destroyed, "destroyed");
        Eq(2, result.Confirmed, "confirmed");
        Eq(0, result.Ambiguous, "ambiguous");
        True(result.Events[0].Time < result.Events[1].Time, "time order");
        Eq("relic", result.Events[1].Kind, "kind");
    }

    private static void HoldingLabels()
    {
        var r = Resolver();
        SeenThenDestroyed(r, 1, "a", 50f, Me);
        SeenThenDestroyed(r, 2, "b", 150f, Me);
        SeenThenDestroyed(r, 3, "c", 250f, Me);
        var report = Analyze(r, weapons:
        [
            new WeaponChange(10f, Me, "B_Athena_Pickaxe_Generic"), new WeaponChange(100f, MyOtherPawn, GumGum),
            new WeaponChange(200f, MyOtherPawn, "B_Assault_Flintlock"), new WeaponChange(60f, Enemy, GumGum),
        ]);
        Eq("pickaxe", report.Events[0].Holding, "pickaxe");
        Eq("Gum-Gum Fruit", report.Events[1].Holding, "after respawning as another character");
        Eq("B_Assault_Flintlock", report.Events[2].Holding, "unlabelled items keep their internal name");
    }

    private static void RegistryV2()
    {
        var reg = ChestRegistry.Default;
        True(reg.Version >= 2, "version");
        True(reg.IsAreaWeapon(GumGum) && reg.IsAreaWeapon("B_EnteringWobble_Weapon"), "fruit powers are area weapons");
        True(!reg.IsAreaWeapon("B_Athena_Pickaxe_Generic") && !reg.IsAreaWeapon("B_Assault_Flintlock") && !reg.IsAreaWeapon(null), "ordinary items are not");
        Eq(ContainerRole.None, reg.Classify("/Game/Props/BP_Furniture_Hammock_A.BP_Furniture_Hammock_A_C").Role, "hammock");
        Eq(ContainerRole.None, reg.Classify("/Game/Props/Cannon.Cannon_C").Role, "cannon prop");
        Eq(ContainerRole.None, reg.Classify("/PurpleDrool/Blueprint/PushCannon/PurpleDrool_PushCannon.PurpleDrool_PushCannon_C").Role, "push cannon");
        Eq(ContainerRole.OtherContainer, reg.Classify("/Game/BGA_LootCannon_StandardChest_Pirate.BGA_LootCannon_StandardChest_Pirate_C").Role, "loot cannon");
        Eq(ContainerRole.OtherContainer, reg.Classify(AmmoCls).Role, "ammo box");
        Eq("buried", reg.Classify("/Game/B_Athena_BuriedChest_Chest.B_Athena_BuriedChest_Chest_C").Kind, "buried");
        Eq("regular", reg.Classify("Tiered_Chest_Athena_C_UAID_387C764928FB100C03_4004c97985eed9d3_1121770771").Kind, "map-placed instance name");
    }

    // ------------------------------------------------------------------ safety

    /// <summary>
    /// TOKENIFY only reads replay files. This fails the test run if any call that could touch another process,
    /// fake input, hook the keyboard or capture the screen ever appears in the source (the vendored replay
    /// library included).
    /// </summary>
    private static void SafetyScan()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Tokenify.ReplayReader"))) dir = dir.Parent;
        if (dir is null) throw new SkipException("source folder not found next to the test binary");

        var forbidden = new Regex(
            @"\b(OpenProcess|ReadProcessMemory|WriteProcessMemory|NtReadVirtualMemory|NtWriteVirtualMemory|VirtualAllocEx|VirtualProtectEx|"
            + @"CreateRemoteThread|NtCreateThreadEx|QueueUserAPC|SetWindowsHookEx|SendInput|keybd_event|mouse_event|PostMessage|"
            + @"GetAsyncKeyState|RegisterRawInputDevices|BitBlt|PrintWindow|CopyFromScreen|GraphicsCapture\w*|IDXGIOutputDuplication|"
            + @"DebugActiveProcess|MiniDumpWriteDump|EnumProcessModules|MainModule|ProcessModule|SuspendThread)\b",
            RegexOptions.Compiled);
        var hits = new List<string>();
        var files = 0;
        foreach (var root in new[] { "src", "lib" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(dir.FullName, root), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                files++;
                var n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    var code = line.TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal)) continue;
                    var m = forbidden.Match(line);
                    if (m.Success) hits.Add($"{Path.GetRelativePath(dir.FullName, file)}:{n} {m.Value}");
                }
            }
        }
        True(files > 20, $"scanned only {files} files");
        True(hits.Count == 0, "game-touching calls found: " + string.Join("; ", hits.Take(8)));
    }
}
