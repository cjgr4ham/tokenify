using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tokenify.Core;

/// <summary>Ids of everything the overlay (and the OBS page) can show. Stored in settings, so they never change.</summary>
public static class Metric
{
    public const string Total = "total";
    public const string Matches = "matches";
    public const string Cpg = "cpg";
    public const string Best = "best";
    public const string Last = "last";
    public const string Last10 = "last10";
    public const string Last25 = "last25";
    public const string Last50 = "last50";
    public const string SessionChests = "session_chests";
    public const string SessionGames = "session_games";
    public const string SessionCpg = "session_cpg";
    public const string SessionTime = "session_time";
    public const string Chance = "chance";
    public const string AverageToToken = "avg_token";
    public const string Eta = "eta";
    public const string Squad = "squad";
    public const string Sync = "sync";
    public const string Status = "status";

    /// <summary>Every metric, grouped as in Settings → Layout.</summary>
    public static readonly (string Group, string Id, string Label, string Hint)[] All =
    [
        ("Lifetime", Total, "Total chests", "Your confirmed total. Always the largest number."),
        ("Lifetime", Matches, "Total matches", "Completed matches with a valid count."),
        ("Lifetime", Cpg, "All-time CPG", "Chests per game over every valid match."),
        ("Lifetime", Best, "Best game", "Most chests in one match."),
        ("Recent", Last, "Last game", "Chests in the last completed match."),
        ("Recent", Last10, "Last 10 CPG", "Average of the 10 most recent valid matches (fewer if you have fewer)."),
        ("Recent", Last25, "Last 25 CPG", "Average of the 25 most recent valid matches."),
        ("Recent", Last50, "Last 50 CPG", "Average of the 50 most recent valid matches."),
        ("Session", SessionChests, "Session chests", "Chests in the current session."),
        ("Session", SessionGames, "Session games", "Matches in the current session."),
        ("Session", SessionCpg, "Session CPG", "Chests per game this session."),
        ("Session", SessionTime, "Session time", "Time spent in matches this session."),
        ("Pirate Token", Chance, "Token chance so far", "Theoretical chance of at least one Pirate Token in the chests you have opened."),
        ("Pirate Token", AverageToToken, "Average chests per token", "1 in 137,500 per chest. Past chests do not change the next one."),
        ("Pirate Token", Eta, "Time per token at your pace", "An average at your recent pace, not a countdown."),
        ("Status", Status, "Match status", "Match in progress, reading replay, last match imported..."),
        ("Status", Sync, "Website sync", "Whether everything has reached TOKENIFY."),
        ("Status", Squad, "Squad chests, last game", "Your party's total for the last match, when TOKENIFY has it."),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;

    public static readonly string[] Minimal = [Total, Cpg];
    public static readonly string[] Standard = [Total, Last, Cpg, Last10, Status];
    public static readonly string[] Expanded = [Total, Last, Cpg, Last10, SessionChests, SessionGames, Best, Status];

    public static IReadOnlyList<string> ForPreset(string preset, IReadOnlyList<string> custom) => preset switch
    {
        "minimal" => Minimal,
        "expanded" => Expanded,
        "custom" => custom.Count > 0 ? custom : Standard,
        _ => Standard,
    };
}

/// <summary>Where the overlay sits on one monitor: its top-left corner as a fraction of that monitor's area.</summary>
public sealed class OverlayPlace
{
    public double X { get; set; } = 0.015;
    public double Y { get; set; } = 0.30;
}

public sealed class OverlaySettings
{
    /// <summary>The counter is drawn on top of the game. On by default; setup lets the player pick its style or turn it off.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Shown or hidden with the show/hide shortcut.</summary>
    public bool Visible { get; set; } = true;
    /// <summary>Locked = clicks pass through to the game. Unlocked = it can be dragged and resized.</summary>
    public bool Locked { get; set; } = true;
    /// <summary>always, or fortnite (only while Fortnite is the window in front).</summary>
    public string ShowWhen { get; set; } = "always";

    /// <summary>minimal, standard, expanded or custom.</summary>
    public string Preset { get; set; } = "standard";
    public List<string> CustomMetrics { get; set; } = [.. Metric.Standard];

    /// <summary>frosted, solid, hud or gold.</summary>
    public string Style { get; set; } = "frosted";
    /// <summary>0-90. Only the panel behind the numbers; the numbers stay crisp.</summary>
    public int BackgroundOpacity { get; set; } = 70;
    /// <summary>40-100. Everything, numbers included.</summary>
    public int OverallOpacity { get; set; } = 100;
    /// <summary>50-200 percent.</summary>
    public int Scale { get; set; } = 100;
    /// <summary>80-130 percent, on top of the scale.</summary>
    public int FontScale { get; set; } = 100;
    public string Accent { get; set; } = "#F6C24A";
    public int CornerRadius { get; set; } = 12;
    public bool Shadow { get; set; } = true;
    public int Padding { get; set; } = 12;
    /// <summary>left or center.</summary>
    public string Align { get; set; } = "left";
    public bool SnapToEdges { get; set; } = true;

    /// <summary>The monitor the overlay belongs to (Windows device name), and its place on each monitor it has been on.</summary>
    public string? Monitor { get; set; }
    public Dictionary<string, OverlayPlace> Places { get; set; } = new();

    public IReadOnlyList<string> Metrics() => Metric.ForPreset(Preset, CustomMetrics);
}

public sealed class TrackingSettings
{
    /// <summary>Null = Fortnite's own replay folder.</summary>
    public string? ReplayFolder { get; set; }
    public bool Paused { get; set; }
    public bool CountDestroyed { get; set; } = true;
    /// <summary>auto (grouped by a gap between matches) or manual (you start and end them).</summary>
    public string SessionMode { get; set; } = "auto";
    public int SessionGapMinutes { get; set; } = 30;
    /// <summary>Chests you had before this PC started counting. Added to the total only, never to CPG.</summary>
    public int StartingTotal { get; set; }
    /// <summary>Matches before this local date are left out of every statistic (they stay in the history).</summary>
    public DateTime? StartDate { get; set; }
    /// <summary>auto = the Fortnite account that played most recently on this PC; otherwise that account's id.</summary>
    public string Profile { get; set; } = "auto";
}

public sealed class ShortcutSettings
{
    public string ToggleLock { get; set; } = "Ctrl+Shift+L";
    public string ToggleVisible { get; set; } = "Ctrl+Shift+O";
    /// <summary>
    /// Off unless turned on. It is an ordinary registered Windows shortcut, so while it is on the key is taken by
    /// the tracker and does not reach the game: choose one that is not bound to anything in Fortnite.
    /// </summary>
    public bool ManualEnabled { get; set; }
    public string ManualIncrement { get; set; } = "K";
}

public sealed class GeneralSettings
{
    public bool SetupDone { get; set; }
    /// <summary>none, or windows (a normal per-user "start with Windows" entry).</summary>
    public string Startup { get; set; } = "none";
    public bool NotifyOnImport { get; set; } = true;
    /// <summary>offer (tell me, I click) or off (never contact GitHub).</summary>
    public string Updates { get; set; } = "offer";
    public bool ObsPage { get; set; }
    /// <summary>minimal, standard or expanded.</summary>
    public string ObsLayout { get; set; } = "standard";
    public bool ObsSquad { get; set; }
    public bool ShowTokenChance { get; set; }
}

/// <summary>Every preference of the tracker. Saved as one JSON document; unknown or missing fields fall back to defaults.</summary>
public sealed class Settings
{
    public OverlaySettings Overlay { get; set; } = new();
    public TrackingSettings Tracking { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();
    public GeneralSettings General { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A position that is not a number must not stop settings from being saved; it is replaced on load.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Settings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Settings();
        try
        {
            return (JsonSerializer.Deserialize<Settings>(json, Json) ?? new Settings()).Sanitized();
        }
        catch (JsonException)
        {
            return new Settings();
        }
    }

    public Settings Clone() => FromJson(ToJson());

    /// <summary>Clamps every value into its supported range (settings files get edited by hand, and versions change).</summary>
    public Settings Sanitized()
    {
        Overlay ??= new();
        Tracking ??= new();
        Shortcuts ??= new();
        General ??= new();
        var o = Overlay;
        o.Preset = OneOf(o.Preset, "standard", "minimal", "expanded", "custom");
        o.Style = OneOf(o.Style, "frosted", "solid", "hud", "gold");
        o.ShowWhen = OneOf(o.ShowWhen, "always", "fortnite");
        o.Align = OneOf(o.Align, "left", "center");
        o.BackgroundOpacity = Math.Clamp(o.BackgroundOpacity, 0, 90);
        o.OverallOpacity = Math.Clamp(o.OverallOpacity, 40, 100);
        o.Scale = Math.Clamp(o.Scale, 50, 200);
        o.FontScale = Math.Clamp(o.FontScale, 80, 130);
        o.CornerRadius = Math.Clamp(o.CornerRadius, 0, 24);
        o.Padding = Math.Clamp(o.Padding, 6, 24);
        o.Accent = IsColor(o.Accent) ? o.Accent : "#F6C24A";
        o.CustomMetrics = (o.CustomMetrics ?? []).Where(m => Metric.All.Any(a => a.Id == m)).Distinct().ToList();
        if (!o.CustomMetrics.Contains(Metric.Total)) o.CustomMetrics.Insert(0, Metric.Total);
        o.Places ??= new();
        foreach (var p in o.Places.Values)
        {
            p.X = double.IsFinite(p.X) ? Math.Clamp(p.X, -0.2, 1.2) : 0.015;
            p.Y = double.IsFinite(p.Y) ? Math.Clamp(p.Y, -0.2, 1.2) : 0.30;
        }
        Tracking.SessionMode = OneOf(Tracking.SessionMode, "auto", "manual");
        Tracking.SessionGapMinutes = Math.Clamp(Tracking.SessionGapMinutes, 5, 240);
        Tracking.StartingTotal = Math.Clamp(Tracking.StartingTotal, 0, 10_000_000);
        Tracking.Profile = string.IsNullOrWhiteSpace(Tracking.Profile) ? "auto" : Tracking.Profile;
        if (string.IsNullOrWhiteSpace(Tracking.ReplayFolder)) Tracking.ReplayFolder = null;
        Shortcuts.ToggleLock = Hotkey.Parse(Shortcuts.ToggleLock)?.ToString() ?? "Ctrl+Shift+L";
        Shortcuts.ToggleVisible = Hotkey.Parse(Shortcuts.ToggleVisible)?.ToString() ?? "Ctrl+Shift+O";
        Shortcuts.ManualIncrement = Hotkey.Parse(Shortcuts.ManualIncrement)?.ToString() ?? "K";
        General.Updates = OneOf(General.Updates, "offer", "off");
        General.Startup = OneOf(General.Startup, "none", "windows");
        General.ObsLayout = OneOf(General.ObsLayout, "standard", "minimal", "expanded");
        return this;
    }

    private static string OneOf(string? value, string fallback, params string[] others) =>
        value is not null && (value == fallback || others.Contains(value)) ? value : fallback;

    public static bool IsColor(string? hex) => hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>A keyboard shortcut as text ("Ctrl+Shift+L"), independent of any window toolkit.</summary>
public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    /// <summary>Windows virtual-key code of <see cref="Key"/>.</summary>
    public int VirtualKey => KeyCode(Key) ?? 0;
    public bool HasModifier => Ctrl || Alt || Win || Shift;

    public override string ToString() => string.Join("+", new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Win ? "Win" : null, Key }.Where(p => p is not null));

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (key is not null) return null;
                    key = Normalise(raw);
                    break;
            }
        }
        return key is null || KeyCode(key) is null ? null : new Hotkey(ctrl, alt, shift, win, key);
    }

    private static string Normalise(string key)
    {
        if (key.Length == 1) return key.ToUpperInvariant();
        var upper = key.ToUpperInvariant();
        if (upper.Length is 2 or 3 && upper[0] == 'F' && int.TryParse(upper[1..], out var n) && n is >= 1 and <= 24) return upper;
        return Named.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
    }

    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Pause"] = 0x13, ["ScrollLock"] = 0x91,
        ["Num0"] = 0x60, ["Num1"] = 0x61, ["Num2"] = 0x62, ["Num3"] = 0x63, ["Num4"] = 0x64, ["Num5"] = 0x65, ["Num6"] = 0x66,
        ["Num7"] = 0x67, ["Num8"] = 0x68, ["Num9"] = 0x69, ["NumMultiply"] = 0x6A, ["NumAdd"] = 0x6B, ["NumSubtract"] = 0x6D,
        ["NumDecimal"] = 0x6E, ["NumDivide"] = 0x6F, ["Minus"] = 0xBD, ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Period"] = 0xBE,
        ["Semicolon"] = 0xBA, ["Slash"] = 0xBF, ["Backslash"] = 0xDC, ["Tilde"] = 0xC0, ["LeftBracket"] = 0xDB, ["RightBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    private static int? KeyCode(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return null;
        }
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var n) && n is >= 1 and <= 24) return 0x70 + n - 1;
        return Named.TryGetValue(key, out var vk) ? vk : null;
    }

    /// <summary>The name of a virtual-key code, for the "press a shortcut" box.</summary>
    public static string? NameOf(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return "F" + (virtualKey - 0x70 + 1);
        return Named.FirstOrDefault(kv => kv.Value == virtualKey).Key;
    }
}
