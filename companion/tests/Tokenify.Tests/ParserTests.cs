using System.Text;
using Tokenify.ReplayReader;

namespace Tokenify.Tests;

public static partial class Tests
{
    private static partial IEnumerable<(string, Action)> All(string? fixtures)
    {
        // ---- opening rules (pure logic, no replay files needed) ----
        yield return ("one chest opened once counts once", OneChestOnce);
        yield return ("several chests in quick succession", SeveralChestsQuickly);
        yield return ("repeated replication of the same opened chest", RepeatedUpdates);
        yield return ("chest leaves view and comes back (same channel identity)", StreamOutAndBack);
        yield return ("teammate opens a chest next to you", TeammateOpens);
        yield return ("visible but unopened chests count zero", ZeroOpenings);
        yield return ("ammo boxes, coolers and golden prize chests are not chests", NotChests);
        yield return ("several chest types are classified", ChestTypes);
        yield return ("chest destroyed instead of searched", DestroyedChest);
        yield return ("destroyed chest not counted when the rule is off", DestroyedRuleOff);
        yield return ("chest searched then destroyed counts once", SearchedThenDestroyed);
        yield return ("refilled chest opened again", RefillReopen);
        yield return ("refill reopen not counted when the rule is off", RefillRuleOff);
        yield return ("dormant chest first seen already searched by you", DormantChest);
        yield return ("stale opening without a stable identity is not counted", StaleUnstable);
        yield return ("someone else's opening seen again later is not yours", OtherOpenerSeenAgain);
        yield return ("unknown chest classes are flagged (count-and-flag / ignore)", UnknownPolicy);
        yield return ("registry short class names", ShortNames);
        yield return ("player hash is stable and case-insensitive", PlayerHashStable);

        // ---- header checks ----
        yield return ("recording replay (live, no key) is not ready", HeaderRecording);
        yield return ("finished replay with a zeroed key is not ready", HeaderZeroKey);
        yield return ("newer replay file version is unsupported", HeaderUnsupported);
        yield return ("random bytes are corrupt", HeaderCorrupt);
        yield return ("empty file is corrupt, never zero chests", EmptyFile);

        // ---- real replay fixtures ----
        if (string.IsNullOrEmpty(fixtures) || !Directory.Exists(fixtures))
        {
            yield return ("fixture replays", () => throw new SkipException("no fixtures folder given (see tests/fixtures/README.md)"));
            yield break;
        }

        var expectedPath = Path.Combine(AppContext.BaseDirectory, "fixtures-expected.json");
        var expected = File.Exists(expectedPath) ? FixtureExpectations.Load(expectedPath) : new();
        foreach (var e in expected)
        {
            var file = Path.Combine(fixtures, e.File);
            yield return ($"fixture {e.File}: {e.Chests} chests, recorder and match id", () => FixtureMatches(file, e));
        }

        var c5 = Path.Combine(fixtures, "chapter5season5.replay");
        yield return ("same replay parsed twice gives identical results", () => Deterministic(c5));
        yield return ("renamed copy of a replay is recognised as the same match", () => DuplicateFile(c5));
        yield return ("replay marked as still recording is not read", () => LiveCopy(c5));
        yield return ("truncated replay is never reported as complete", () => Truncated(c5));
        yield return ("damaged bytes mid-file never inflate the count", () => Damaged(c5));
        yield return ("replays from two players have different identities", () => TwoRecorders(fixtures));
    }

    // ------------------------------------------------------------------------------ helpers

    private const uint Me = 100, MyOtherPawn = 101, Mate = 200, Enemy = 300;
    private static readonly HashSet<uint> MyPawns = [Me, MyOtherPawn];
    private const string ChestCls = "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C";

    private static ContainerUpdate U(float t, uint ch, string key, uint? pawn = null, bool? searched = null, bool? destroyed = null,
        bool stable = true, string cls = ChestCls) => new(t, ch, key, stable, cls, pawn, searched, destroyed, null);

    private static OpeningResolver Resolver(ChestRegistry? reg = null) => new(reg ?? ChestRegistry.Default);

    private static int Mine(OpeningResolver r, ChestRegistry? reg = null)
    {
        reg ??= ChestRegistry.Default;
        return r.OpenedBy(MyPawns).Count(o => reg.Counts(reg.Classify(o.ClassPath)));
    }

    private static ChestRegistry Registry(Func<string, string> edit) =>
        ChestRegistry.Parse(edit(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "chest-registry.json"))));

    // ------------------------------------------------------------------------------ opening rules

    private static void OneChestOnce()
    {
        var r = Resolver();
        r.ChannelOpened(5, 10);
        r.Observe(U(10, 5, "chestA"));                              // came into view, closed
        r.Observe(U(40, 5, "chestA", pawn: Me));                     // search starts
        r.Observe(U(41, 5, "chestA", searched: true));               // searched
        Eq(1, Mine(r), "my chests");
    }

    private static void SeveralChestsQuickly()
    {
        var r = Resolver();
        for (uint i = 0; i < 6; i++)
        {
            r.ChannelOpened(10 + i, 1);
            r.Observe(U(50 + i * 0.3f, 10 + i, "chest" + i, pawn: i % 2 == 0 ? Me : MyOtherPawn, searched: true));
        }
        Eq(6, Mine(r), "my chests");
    }

    private static void RepeatedUpdates()
    {
        var r = Resolver();
        r.ChannelOpened(5, 10);
        r.Observe(U(40, 5, "chestA", pawn: Me, searched: true));
        for (var i = 0; i < 20; i++) r.Observe(U(41 + i, 5, "chestA", pawn: Me, searched: true));
        Eq(1, Mine(r), "my chests");
    }

    private static void StreamOutAndBack()
    {
        var r = Resolver();
        r.ChannelOpened(5, 10);
        r.Observe(U(40, 5, "chestA", pawn: Me, searched: true));
        r.ChannelClosed(5);                                         // walked away: actor no longer relevant
        r.ChannelOpened(9, 120);                                    // came back: new channel, full state re-sent
        r.Observe(U(120, 9, "chestA", pawn: Me, searched: true));
        r.ChannelClosed(9);
        r.ChannelOpened(12, 300);
        r.Observe(U(300, 12, "chestA", pawn: Me, searched: true));
        Eq(1, Mine(r), "my chests");
    }

    private static void TeammateOpens()
    {
        var r = Resolver();
        r.ChannelOpened(5, 10);
        r.Observe(U(40, 5, "chestA", pawn: Mate, searched: true));
        r.ChannelOpened(6, 10);
        r.Observe(U(42, 6, "chestB", pawn: Me, searched: true));
        r.ChannelOpened(7, 10);
        r.Observe(U(43, 7, "chestC", pawn: Enemy, destroyed: true));
        Eq(1, Mine(r), "my chests");
        Eq(3, r.Openings.Count, "openings by anyone");
    }

    private static void ZeroOpenings()
    {
        var r = Resolver();
        for (uint i = 0; i < 10; i++)
        {
            r.ChannelOpened(i, i);
            r.Observe(U(i, i, "chest" + i, searched: false));
            r.Observe(U(i + 5, i, "chest" + i, pawn: Me));           // started a search, walked off before it finished
        }
        Eq(0, Mine(r), "my chests");
    }

    private static void NotChests()
    {
        var reg = ChestRegistry.Default;
        string[] notChests =
        [
            "/Game/Building/ActorBlueprints/Containers/Tiered_Ammo_Athena.Tiered_Ammo_Athena_C",
            "/Game/Athena/Items/EnvironmentalItems/Cooler/B_Cooler_Container.B_Cooler_Container_C",
            "/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C",
            "/Game/Athena/Items/LootCannon/B_LootCannon_Base.B_LootCannon_Base_C",
            "/Game/Athena/Prop_Chest_Sandpile.Prop_Chest_Sandpile_C",
        ];
        foreach (var path in notChests)
        {
            True(!reg.Counts(reg.Classify(path)), "counted as a chest: " + path);
        }

        var r = Resolver();
        r.ChannelOpened(1, 0);
        r.Observe(U(5, 1, "ammo1", pawn: Me, searched: true, cls: notChests[0]));
        Eq(0, Mine(r), "ammo box counted");
    }

    private static void ChestTypes()
    {
        var reg = ChestRegistry.Default;
        Eq("regular", reg.Classify(ChestCls).Kind, "Tiered_Chest_Athena");
        Eq(ContainerRole.KnownChest, reg.Classify(ChestCls).Role, "Tiered_Chest_Athena role");
        Eq("rare", reg.Classify("/Game/X/AlwaysSpawn_RareChest.AlwaysSpawn_RareChest_C").Kind, "rare chest");
        Eq("buried", reg.Classify("/Game/X/Tiered_Chest_Buried.Tiered_Chest_Buried_C").Kind, "buried chest");
        Eq("relic", reg.Classify("/Game/X/B_TieredChest_Relics.B_TieredChest_Relics_C").Kind, "sprite vault chest");
        Eq("regular", reg.Classify("Tiered_Chest_Athena_C_UAID_107C61754444B72302_86b5600c7493c6fd_2045371893").Kind, "level instance name");

        var r = Resolver();
        string[] classes = [ChestCls, "/Game/X/AlwaysSpawn_RareChest.AlwaysSpawn_RareChest_C", "/Game/X/Tiered_Chest_Buried.Tiered_Chest_Buried_C"];
        for (uint i = 0; i < 3; i++)
        {
            r.ChannelOpened(i, 0);
            r.Observe(U(10 + i, i, "c" + i, pawn: Me, searched: true, cls: classes[i]));
        }
        Eq(3, Mine(r), "mixed chest types");
    }

    private static void DestroyedChest()
    {
        var r = Resolver();
        r.ChannelOpened(5, 0);
        r.Observe(U(1, 5, "chestA", searched: false));
        r.Observe(U(30, 5, "chestA", pawn: Me, destroyed: true));
        var mine = r.OpenedBy(MyPawns).ToList();
        Eq(1, mine.Count, "my chests");
        True(mine[0].Destroyed, "flagged as destroyed");
    }

    private static void DestroyedRuleOff()
    {
        var reg = Registry(j => j.Replace("\"countDestroyed\": true", "\"countDestroyed\": false"));
        var r = Resolver(reg);
        r.ChannelOpened(5, 0);
        r.Observe(U(30, 5, "chestA", pawn: Me, destroyed: true));
        Eq(0, Mine(r, reg), "destroyed counted with rule off");
    }

    private static void SearchedThenDestroyed()
    {
        var r = Resolver();
        r.ChannelOpened(5, 0);
        r.Observe(U(30, 5, "chestA", pawn: Me, searched: true));
        r.Observe(U(60, 5, "chestA", pawn: Enemy, destroyed: true));
        r.ChannelOpened(8, 90);
        r.Observe(U(90, 8, "chestA", pawn: Enemy, searched: true, destroyed: true));
        Eq(1, Mine(r), "my chests");
        Eq(1, r.Openings.Count, "openings by anyone");
    }

    private static void RefillReopen()
    {
        var r = Resolver();
        r.ChannelOpened(5, 0);
        r.Observe(U(30, 5, "chestA", pawn: Me, searched: true));
        r.Observe(U(210, 5, "chestA", searched: false));             // refill wave
        r.Observe(U(240, 5, "chestA", pawn: Me, searched: true));
        var mine = r.OpenedBy(MyPawns).ToList();
        Eq(2, mine.Count, "my chests");
        True(mine[1].Reopened && !mine[0].Reopened, "second opening flagged as reopened");
    }

    private static void RefillRuleOff()
    {
        var reg = Registry(j => j.Replace("\"countReopenedAfterRefill\": true", "\"countReopenedAfterRefill\": false"));
        var r = Resolver(reg);
        r.ChannelOpened(5, 0);
        r.Observe(U(30, 5, "chestA", pawn: Me, searched: true));
        r.Observe(U(210, 5, "chestA", searched: false));
        r.Observe(U(240, 5, "chestA", pawn: Me, searched: true));
        Eq(1, Mine(r, reg), "my chests");
    }

    private static void DormantChest()
    {
        var r = Resolver();
        r.ChannelOpened(5, 75);                                     // channel opens at the moment it is searched
        r.Observe(U(75.1f, 5, "chestA", pawn: Me, searched: true));
        Eq(1, Mine(r), "my chests");
    }

    private static void StaleUnstable()
    {
        var r = Resolver();
        r.ChannelOpened(5, 75);
        r.Observe(U(75.1f, 5, "#555", pawn: Me, searched: true, stable: false));
        Eq(0, Mine(r), "stale opening without identity");
    }

    private static void OtherOpenerSeenAgain()
    {
        var r = Resolver();
        r.ChannelOpened(5, 0);
        r.Observe(U(30, 5, "chestA", pawn: Enemy, searched: true));
        r.ChannelClosed(5);
        r.ChannelOpened(6, 100);
        r.Observe(U(100, 6, "chestA", pawn: Enemy, searched: true));
        Eq(0, Mine(r), "my chests");
        Eq(1, r.Openings.Count, "openings by anyone");
    }

    private static void UnknownPolicy()
    {
        const string cls = "/Game/Season99/B_MysteryChest_New.B_MysteryChest_New_C";
        var reg = ChestRegistry.Default;
        Eq(ContainerRole.UnknownChest, reg.Classify(cls).Role, "unknown chest role");
        True(reg.Counts(reg.Classify(cls)), "count-and-flag counts it");
        var ignore = Registry(j => j.Replace("\"count-and-flag\"", "\"ignore\""));
        True(!ignore.Counts(ignore.Classify(cls)), "ignore policy does not count it");
    }

    private static void ShortNames()
    {
        Eq("Tiered_Chest_Athena", ChestRegistry.ShortClassName(ChestCls), "path");
        Eq("Tiered_Chest_Athena", ChestRegistry.ShortClassName("Tiered_Chest_Athena_C_UAID_107C61754444B72302_86b5600c7493c6fd_2045371893"), "instance");
        Eq("Foo", ChestRegistry.ShortClassName("Default__Foo_C"), "default object");
    }

    private static void PlayerHashStable()
    {
        var a = ParserInfo.PlayerHash("8A3FC430B1D24F2CA2B7C1D7E0F11234");
        var b = ParserInfo.PlayerHash("8a3fc430b1d24f2ca2b7c1d7e0f11234");
        Eq(a, b, "case-insensitive");
        Eq(64, a.Length, "hex length");
        True(a != ParserInfo.PlayerHash("00000000000000000000000000000001"), "different players differ");
    }

    // ------------------------------------------------------------------------------ headers

    private static byte[] Header(uint version = 7, bool live = false, bool encrypted = true, byte keyFill = 0x5A, int keyLen = 32)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Unicode);
        w.Write(ReplayProbe.FileMagic);
        w.Write(version);
        if (version >= 7) w.Write(0);                            // custom versions
        w.Write(900_000u);                                      // length
        w.Write(2u);                                            // network version
        w.Write(1234u);                                         // changelist
        var name = Encoding.ASCII.GetBytes("Unsaved Replay\0");
        w.Write(name.Length);
        w.Write(name);
        w.Write(live ? 1u : 0u);
        w.Write(new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Local).ToBinary());
        w.Write(1u);                                            // compressed
        w.Write(encrypted ? 1u : 0u);
        w.Write(keyLen);
        w.Write(Enumerable.Repeat(keyFill, keyLen).ToArray());
        return ms.ToArray();
    }

    private static void HeaderRecording()
    {
        var p = ReplayProbe.Probe(Header(live: true, keyLen: 0));
        Eq(ProbeState.Recording, p.State, "state");
        Eq(ProbeState.Ready, ReplayProbe.Probe(Header()).State, "finished header");
    }

    private static void HeaderZeroKey() => Eq(ProbeState.Recording, ReplayProbe.Probe(Header(keyFill: 0)).State, "state");

    private static void HeaderUnsupported() => Eq(ProbeState.UnsupportedVersion, ReplayProbe.Probe(Header(version: 99)).State, "state");

    private static void HeaderCorrupt()
    {
        var junk = new byte[4096];
        new Random(7).NextBytes(junk);
        Eq(ProbeState.Invalid, ReplayProbe.Probe(junk).State, "random bytes");
        Eq(ProbeState.Invalid, ReplayProbe.Probe(Header()[..30]).State, "truncated header");
    }

    private static void EmptyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "tokenify-empty-" + Guid.NewGuid() + ".replay");
        File.WriteAllBytes(path, []);
        try
        {
            var r = new ChestReader().Read(path);
            True(r.Status != ScanStatus.Complete, "empty file reported complete");
            Eq(0, r.ChestsOpened, "chests");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ fixtures

    private static void FixtureMatches(string file, FixtureExpectations.Entry e)
    {
        if (!File.Exists(file)) throw new SkipException("missing " + Path.GetFileName(file));
        var r = new ChestReader().Read(file);
        Eq(ScanStatus.Complete, r.Status, "status (" + r.Error + ")");
        Eq(e.Chests, r.ChestsOpened, "chests opened by the recording player");
        if (e.RecorderPrefix != null) True(r.RecorderAccountId?.StartsWith(e.RecorderPrefix, StringComparison.OrdinalIgnoreCase) == true, "recorder id " + r.RecorderAccountId);
        if (e.SessionPrefix != null) True(r.MatchSessionId?.StartsWith(e.SessionPrefix, StringComparison.OrdinalIgnoreCase) == true, "match id " + r.MatchSessionId);
        if (e.Times != null) Eq(string.Join(",", e.Times), string.Join(",", r.Chests.Select(c => ((int)c.Time).ToString())), "chest times (s)");
        if (e.Destroyed != null) Eq(e.Destroyed.Value, r.Chests.Count(c => c.Destroyed), "destroyed chests");
    }

    private static string Fingerprint(ScanResult r) =>
        $"{r.Status}|{r.ChestsOpened}|{r.ChestsOpenedByAnyone}|{r.MatchSessionId}|{r.RecorderAccountId}|{r.ReplayGuid}|{string.Join(";", r.Chests.Select(c => $"{c.Time}:{c.Kind}:{c.Destroyed}"))}";

    private static void Deterministic(string file)
    {
        if (!File.Exists(file)) throw new SkipException("missing fixture");
        var reader = new ChestReader();
        var a = Fingerprint(reader.Read(file));
        var b = Fingerprint(reader.Read(file));           // same reader instance reused
        var c = Fingerprint(new ChestReader().Read(file));
        Eq(a, b, "second read with the same reader");
        Eq(a, c, "read with a new reader");
    }

    private static string TempCopy(string file, Func<byte[], byte[]>? edit = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "tokenify-test-" + Guid.NewGuid().ToString("N")[..8] + ".replay");
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(path, edit is null ? bytes : edit(bytes));
        return path;
    }

    private static void DuplicateFile(string file)
    {
        if (!File.Exists(file)) throw new SkipException("missing fixture");
        var copy = TempCopy(file);
        try
        {
            var a = new ChestReader().Read(file);
            var b = new ChestReader().Read(copy);
            Eq(a.ReplayGuid, b.ReplayGuid, "replay guid");
            Eq(a.MatchSessionId, b.MatchSessionId, "match id");
            Eq(a.ChestsOpened, b.ChestsOpened, "chests");
        }
        finally { File.Delete(copy); }
    }

    /// <summary>Byte offset of the bIsLive flag in a replay header.</summary>
    private static int LiveFlagOffset(byte[] b)
    {
        var pos = 8;
        if (BitConverter.ToUInt32(b, 4) >= 7) pos += 4 + BitConverter.ToInt32(b, 8) * 20;
        pos += 12;
        var len = BitConverter.ToInt32(b, pos);
        pos += 4 + (len < 0 ? -len * 2 : len);
        return pos;
    }

    private static void LiveCopy(string file)
    {
        if (!File.Exists(file)) throw new SkipException("missing fixture");
        var copy = TempCopy(file, bytes =>
        {
            var off = LiveFlagOffset(bytes);
            BitConverter.GetBytes(1u).CopyTo(bytes, off);
            return bytes;
        });
        try
        {
            var r = new ChestReader().Read(copy);
            Eq(ScanStatus.NotReady, r.Status, "status");
            Eq(0, r.ChestsOpened, "chests reported for an unreadable match");
        }
        finally { File.Delete(copy); }
    }

    private static void Truncated(string file)
    {
        if (!File.Exists(file)) throw new SkipException("missing fixture");
        var full = new ChestReader().Read(file).ChestsOpened;
        foreach (var fraction in new[] { 0.35, 0.6, 0.9 })
        {
            var copy = TempCopy(file, bytes => bytes[..(int)(bytes.Length * fraction)]);
            try
            {
                var r = new ChestReader().Read(copy);
                True(r.Status != ScanStatus.Complete, $"cut at {fraction:P0} reported complete");
                True(r.ChestsOpened <= full, $"cut at {fraction:P0} found more chests ({r.ChestsOpened}) than the whole file ({full})");
            }
            finally { File.Delete(copy); }
        }
    }

    private static void Damaged(string file)
    {
        if (!File.Exists(file)) throw new SkipException("missing fixture");
        var full = new ChestReader().Read(file).ChestsOpened;
        var copy = TempCopy(file, bytes =>
        {
            var rnd = new Random(42);
            for (var i = 0; i < 64; i++)
            {
                var at = bytes.Length / 2 + rnd.Next(0, bytes.Length / 4);
                bytes[at] ^= (byte)rnd.Next(1, 256);
            }
            return bytes;
        });
        try
        {
            var r = new ChestReader().Read(copy);
            True(r.ChestsOpened <= full + 1, $"damaged copy found {r.ChestsOpened} chests vs {full}");
        }
        finally { File.Delete(copy); }
    }

    private static void TwoRecorders(string fixtures)
    {
        var a = Path.Combine(fixtures, "chapter5season5.replay");
        var b = Path.Combine(fixtures, "season12.replay");
        if (!File.Exists(a) || !File.Exists(b)) throw new SkipException("missing fixtures");
        var ra = new ChestReader().Read(a);
        var rb = new ChestReader().Read(b);
        True(ra.RecorderAccountId != null && rb.RecorderAccountId != null, "both recorders identified");
        True(ParserInfo.PlayerHash(ra.RecorderAccountId!) != ParserInfo.PlayerHash(rb.RecorderAccountId!), "different players share a hash");
        True(ra.MatchSessionId != rb.MatchSessionId, "different matches share an id");
    }
}

internal static class FixtureExpectations
{
    internal sealed class Entry
    {
        public string File { get; set; } = "";
        public int Chests { get; set; }
        public int? Destroyed { get; set; }
        public string? RecorderPrefix { get; set; }
        public string? SessionPrefix { get; set; }
        public int[]? Times { get; set; }
    }

    public static List<Entry> Load(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
}
