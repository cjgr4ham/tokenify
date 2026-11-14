using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tokenify.ReplayReader;

/// <summary>What a replicated class is, as far as chest counting is concerned.</summary>
public enum ContainerRole
{
    /// <summary>Not a container we care about.</summary>
    None,
    /// <summary>A chest class listed in the registry: counted.</summary>
    KnownChest,
    /// <summary>Its name says chest but no registry rule recognises it (a new season's chest type?).</summary>
    UnknownChest,
    /// <summary>Ammo boxes, food boxes, loot cannons...: never counted, reported separately.</summary>
    OtherContainer,
}

public readonly record struct ContainerClass(ContainerRole Role, string Kind);

/// <summary>
/// Versioned rules for which Fortnite classes count as chests (registry/chest-registry.json, embedded in the
/// build). The rules live in data, not code, so they can be updated after a Fortnite patch without touching
/// the parser.
/// </summary>
public sealed class ChestRegistry
{
    public int Version { get; private init; }
    public bool CountDestroyed { get; private init; } = true;
    public bool CountReopenedAfterRefill { get; private init; } = true;
    public float StaleWindowSeconds { get; private init; } = 0.5f;
    public double SameSpawnToleranceCm { get; private init; } = 150;
    /// <summary>"count-and-flag" (counted, listed as unverified) or "ignore" (not counted, still reported).</summary>
    public string UnknownChestPolicy { get; private init; } = "count-and-flag";

    private Regex _chestClass = null!;
    private Regex[] _exclude = [];
    private (string Kind, Regex Pattern, HashSet<string> Known)[] _kinds = [];
    private (string Kind, Regex Pattern)[] _others = [];
    private (Regex Pattern, string? Label, bool Area)[] _weapons = [];
    private readonly Dictionary<string, (string? Label, bool Area)> _weaponCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContainerClass> _cache = new(StringComparer.Ordinal);

    public bool CountsUnknownChests => UnknownChestPolicy != "ignore";

    private static ChestRegistry? _default;

    /// <summary>The registry built into this version of the companion.</summary>
    public static ChestRegistry Default => _default ??= LoadEmbedded();

    private static ChestRegistry LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tokenify.ReplayReader.chest-registry.json")
            ?? throw new InvalidOperationException("chest-registry.json is missing from the build");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static ChestRegistry Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<RegistryDoc>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException("empty chest registry");
        const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        var reg = new ChestRegistry
        {
            Version = doc.Version,
            CountDestroyed = doc.Counting?.CountDestroyed ?? true,
            CountReopenedAfterRefill = doc.Counting?.CountReopenedAfterRefill ?? true,
            StaleWindowSeconds = doc.Counting?.StaleWindowSeconds ?? 0.5f,
            SameSpawnToleranceCm = doc.Counting?.SameSpawnToleranceCm ?? 150,
            UnknownChestPolicy = doc.Counting?.UnknownChestPolicy ?? "count-and-flag",
        };
        if (reg.Version <= 0) throw new InvalidDataException("chest registry needs a positive version");
        reg._chestClass = new Regex(string.IsNullOrWhiteSpace(doc.ChestClassPattern) ? "chest" : doc.ChestClassPattern, opts);
        reg._exclude = (doc.Exclude ?? []).Select(p => new Regex(p, opts)).ToArray();
        reg._kinds = (doc.Kinds ?? []).Select(k => (k.Kind, new Regex(k.Pattern, opts), new HashSet<string>(k.Known ?? [], StringComparer.OrdinalIgnoreCase))).ToArray();
        reg._others = (doc.OtherContainers ?? []).Select(k => (k.Kind, new Regex(k.Pattern, opts))).ToArray();
        reg._weapons = (doc.Weapons ?? []).Select(w => (new Regex(w.Pattern, opts), w.Label, w.Area)).ToArray();
        return reg;
    }

    /// <summary>Class name without path, instance suffix or trailing "_C" (e.g. "Tiered_Chest_Athena").</summary>
    public static string ShortClassName(string path)
    {
        var name = path;
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        if (name.StartsWith("Default__", StringComparison.Ordinal)) name = name["Default__".Length..];
        var uaid = name.IndexOf("_UAID_", StringComparison.Ordinal);
        if (uaid > 0) name = name[..uaid];
        return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
    }

    /// <summary>Decides what a replicated class is. Cached per class name.</summary>
    public ContainerClass Classify(string classPath)
    {
        var name = ShortClassName(classPath);
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out var known)) return known;
        }

        ContainerClass result;
        var looksLikeChest = _chestClass.IsMatch(name);
        var other = _others.FirstOrDefault(o => o.Pattern.IsMatch(name));
        if (looksLikeChest && !_exclude.Any(e => e.IsMatch(name)))
        {
            var kind = _kinds.FirstOrDefault(k => k.Known.Contains(name) || k.Pattern.IsMatch(name));
            result = kind.Kind is null ? new ContainerClass(ContainerRole.UnknownChest, "other") : new ContainerClass(ContainerRole.KnownChest, kind.Kind);
        }
        else if (other.Kind is not null)
        {
            result = new ContainerClass(ContainerRole.OtherContainer, other.Kind);
        }
        else
        {
            result = new ContainerClass(ContainerRole.None, "");
        }

        lock (_cache)
        {
            _cache[name] = result;
        }
        return result;
    }

    private (string? Label, bool Area) Weapon(string? className)
    {
        if (string.IsNullOrEmpty(className)) return (null, false);
        lock (_weaponCache)
        {
            if (_weaponCache.TryGetValue(className, out var known)) return known;
        }
        var match = _weapons.FirstOrDefault(w => w.Pattern.IsMatch(className));
        var result = match.Pattern is null ? ((string?)null, false) : (match.Label, match.Area);
        lock (_weaponCache)
        {
            _weaponCache[className] = result;
        }
        return result;
    }

    /// <summary>
    /// A readable name for what a player holds: the registry's label when it has one, otherwise the game's
    /// internal class name (shown as-is rather than guessed at). Null when nothing is held.
    /// </summary>
    public string? WeaponLabel(string? className) => string.IsNullOrEmpty(className) ? null : Weapon(className).Label ?? className;

    /// <summary>Weapons and powers that can break several things, at range or through walls, in one use.</summary>
    public bool IsAreaWeapon(string? className) => Weapon(className).Area;

    /// <summary>True when an opening of this class adds to the chest total.</summary>
    public bool Counts(ContainerClass c) => c.Role == ContainerRole.KnownChest || (c.Role == ContainerRole.UnknownChest && CountsUnknownChests);

    private sealed class RegistryDoc
    {
        public int Version { get; set; }
        public CountingDoc? Counting { get; set; }
        public string? ChestClassPattern { get; set; }
        public string[]? Exclude { get; set; }
        public KindDoc[]? Kinds { get; set; }
        public KindDoc[]? OtherContainers { get; set; }
        public WeaponDoc[]? Weapons { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class CountingDoc
    {
        public bool? CountDestroyed { get; set; }
        public bool? CountReopenedAfterRefill { get; set; }
        public float? StaleWindowSeconds { get; set; }
        public double? SameSpawnToleranceCm { get; set; }
        public string? UnknownChestPolicy { get; set; }
    }

    private sealed class WeaponDoc
    {
        public string Pattern { get; set; } = "$^";
        public string? Label { get; set; }
        public bool Area { get; set; }
    }

    private sealed class KindDoc
    {
        public string Kind { get; set; } = "";
        public string? Label { get; set; }
        public string Pattern { get; set; } = "$^";
        public string[]? Known { get; set; }
    }
}
