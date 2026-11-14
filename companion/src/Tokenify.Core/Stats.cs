namespace Tokenify.Core;

/// <summary>One valid completed game, as the statistics see it.</summary>
public sealed record GameStat(long Id, DateTime StartedUtc, DateTime EndedUtc, int Chests, bool Corrected = false)
{
    public TimeSpan Length => EndedUtc > StartedUtc ? EndedUtc - StartedUtc : TimeSpan.Zero;
}

/// <summary>Average over the most recent games. With fewer games than the window, it is the average of those: "LAST 6".</summary>
public sealed record RecentCpg(int Window, int Games, double? Cpg)
{
    public string Label => $"LAST {(Games is > 0 && Games < Window ? Games : Window)}";
}

/// <param name="Active">A session is running now.</param>
/// <param name="Inferred">Its boundaries were worked out from the gaps between matches, not marked by the player.</param>
/// <param name="MatchTime">Sum of the lengths of its valid matches.</param>
/// <param name="Span">First match start to last match end (or to now, for a session started by hand that is still open).</param>
public sealed record SessionStats(bool Active, bool Inferred, int Games, int Chests, double? Cpg, TimeSpan MatchTime, TimeSpan Span,
    DateTime? StartedUtc, DateTime? EndedUtc)
{
    public static readonly SessionStats None = new(false, true, 0, 0, null, TimeSpan.Zero, TimeSpan.Zero, null, null);
}

/// <summary>
/// Theoretical Pirate Token figures. Each chest is an independent 1-in-137,500 try: chests already opened do not
/// make the next one any more likely, so "average chests per token" is the same number however many were opened.
/// It assumes every counted chest can drop the token; the replay does not say which chest types can.
/// </summary>
public sealed record TokenOdds(double Chance, double? ChestsPerHour, TimeSpan? TimePerToken)
{
    public const double PerChest = 1.0 / 137_500;
    public const int AverageChestsPerToken = 137_500;

    /// <summary>Chance of at least one token in <paramref name="chests"/> independent tries.</summary>
    public static double ChanceIn(long chests) => chests <= 0 ? 0 : -double.ExpM1(chests * double.LogP1(-PerChest));
}

public sealed record StatsOptions(int StartingTotal = 0, string SessionMode = "auto", int SessionGapMinutes = 30, bool MatchInProgress = false);

/// <summary>
/// Every number the overlay and the windows show, computed from the valid games only. A game is valid when it was
/// read completely (or the player vouched for its count), belongs to the player and was not set aside. A replay
/// that could not be read is simply absent: it is never a zero-chest game. A real game with zero chests is a game.
/// </summary>
public sealed class Stats
{
    /// <summary>Chests in valid games plus the starting total the player entered.</summary>
    public int TotalChests { get; private init; }
    /// <summary>Chests in valid games on this PC.</summary>
    public int TrackedChests { get; private init; }
    public int StartingTotal { get; private init; }
    public int Games { get; private init; }
    public double? Cpg { get; private init; }
    public int? Best { get; private init; }
    public GameStat? LastGame { get; private init; }
    public RecentCpg Last10 { get; private init; } = new(10, 0, null);
    public RecentCpg Last25 { get; private init; } = new(25, 0, null);
    public RecentCpg Last50 { get; private init; } = new(50, 0, null);
    /// <summary>+1 / 0 / -1: recent form against the all-time average. Null until there are enough games to compare.</summary>
    public int? Trend { get; private init; }
    public SessionStats Session { get; private init; } = SessionStats.None;
    public TokenOdds Odds { get; private init; } = new(0, null, null);

    public static readonly Stats Empty = new();

    public static Stats Compute(IEnumerable<GameStat> validGames, StatsOptions options, DateTime nowUtc, IReadOnlyList<SessionRow>? manualSessions = null)
    {
        var games = validGames.OrderBy(g => g.StartedUtc).ThenBy(g => g.Id).ToList();
        var tracked = games.Sum(g => g.Chests);
        var cpg = games.Count > 0 ? (double)tracked / games.Count : (double?)null;
        var last10 = Recent(games, 10);
        var session = options.SessionMode == "manual"
            ? ManualSession(games, manualSessions ?? [], nowUtc)
            : AutoSession(games, TimeSpan.FromMinutes(options.SessionGapMinutes), nowUtc, options.MatchInProgress);

        return new Stats
        {
            TotalChests = tracked + options.StartingTotal,
            TrackedChests = tracked,
            StartingTotal = options.StartingTotal,
            Games = games.Count,
            Cpg = cpg,
            Best = games.Count > 0 ? games.Max(g => g.Chests) : null,
            LastGame = games.LastOrDefault(),
            Last10 = last10,
            Last25 = Recent(games, 25),
            Last50 = Recent(games, 50),
            Trend = TrendOf(games.Count, cpg, last10),
            Session = session,
            Odds = Odds_(tracked + options.StartingTotal, games, session),
        };
    }

    private static RecentCpg Recent(List<GameStat> games, int window)
    {
        var n = Math.Min(window, games.Count);
        return n == 0 ? new RecentCpg(window, 0, null) : new RecentCpg(window, n, games.Skip(games.Count - n).Average(g => (double)g.Chests));
    }

    private static int? TrendOf(int games, double? cpg, RecentCpg last10)
    {
        // Comparing the last 10 with an average made of mostly the same games says nothing.
        if (games < 15 || cpg is not double all || last10.Cpg is not double recent) return null;
        if (all <= 0) return recent > 0 ? 1 : 0;
        var change = (recent - all) / all;
        return change > 0.05 ? 1 : change < -0.05 ? -1 : 0;
    }

    /// <summary>
    /// Sessions worked out from the matches themselves: a new one starts when more than the gap passes between
    /// one match ending and the next starting. The current session is the last one, while it is still within
    /// the gap (or a match is being played).
    /// </summary>
    public static SessionStats AutoSession(List<GameStat> games, TimeSpan gap, DateTime nowUtc, bool matchInProgress)
    {
        if (games.Count == 0) return SessionStats.None with { Active = matchInProgress };
        var start = games.Count - 1;
        while (start > 0 && games[start].StartedUtc - games[start - 1].EndedUtc <= gap) start--;
        var current = games.Skip(start).ToList();
        var lastEnd = current[^1].EndedUtc;
        var stillGoing = nowUtc - lastEnd <= gap;
        if (!stillGoing)
        {
            // The last session is over. A match being played now belongs to a new one that has no finished game yet.
            return SessionStats.None with { Active = matchInProgress };
        }
        return Describe(current, active: true, inferred: true, current[0].StartedUtc, lastEnd);
    }

    private static SessionStats ManualSession(List<GameStat> games, IReadOnlyList<SessionRow> sessions, DateTime nowUtc)
    {
        var open = sessions.LastOrDefault(s => s.Ended is null);
        if (open is null) return SessionStats.None with { Inferred = false };
        var from = DateTimeOffset.FromUnixTimeMilliseconds(open.Started).UtcDateTime;
        var mine = games.Where(g => g.StartedUtc >= from).ToList();
        return Describe(mine, active: true, inferred: false, from, nowUtc);
    }

    private static SessionStats Describe(List<GameStat> games, bool active, bool inferred, DateTime from, DateTime to)
    {
        var chests = games.Sum(g => g.Chests);
        var matchTime = TimeSpan.FromTicks(games.Sum(g => g.Length.Ticks));
        return new SessionStats(active, inferred, games.Count, chests, games.Count > 0 ? (double)chests / games.Count : null,
            matchTime, to > from ? to - from : TimeSpan.Zero, from, to);
    }

    /// <summary>Every inferred session, oldest first (for the history view).</summary>
    public static List<SessionStats> AllAutoSessions(IEnumerable<GameStat> validGames, TimeSpan gap)
    {
        var games = validGames.OrderBy(g => g.StartedUtc).ToList();
        var result = new List<SessionStats>();
        var group = new List<GameStat>();
        foreach (var g in games)
        {
            if (group.Count > 0 && g.StartedUtc - group[^1].EndedUtc > gap)
            {
                result.Add(Describe(group, false, true, group[0].StartedUtc, group[^1].EndedUtc));
                group = new List<GameStat>();
            }
            group.Add(g);
        }
        if (group.Count > 0) result.Add(Describe(group, false, true, group[0].StartedUtc, group[^1].EndedUtc));
        return result;
    }

    private static TokenOdds Odds_(int totalChests, List<GameStat> games, SessionStats session)
    {
        // Pace: time actually spent in matches, from this session when it has enough, otherwise the last 10 games.
        double? perHour = null;
        if (session.Active && session.MatchTime >= TimeSpan.FromMinutes(20) && session.Chests > 0)
        {
            perHour = session.Chests / session.MatchTime.TotalHours;
        }
        else
        {
            var recent = games.Skip(Math.Max(0, games.Count - 10)).ToList();
            var hours = recent.Sum(g => g.Length.TotalHours);
            var chests = recent.Sum(g => g.Chests);
            if (recent.Count >= 3 && hours >= 0.25 && chests > 0) perHour = chests / hours;
        }
        TimeSpan? perToken = perHour is double p && p > 0 ? TimeSpan.FromHours(Math.Min(TokenOdds.AverageChestsPerToken / p, 1_000_000)) : null;
        return new TokenOdds(TokenOdds.ChanceIn(totalChests), perHour, perToken);
    }
}

/// <summary>Number formatting shared by the overlay, the windows, the OBS page and exports.</summary>
public static class Fmt
{
    private static readonly System.Globalization.CultureInfo En = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    public static string Int(int n) => n.ToString("N0", En);
    public static string Cpg(double? v) => v is double d ? d.ToString("0.0", En) : "—";
    public static string Count(int? n) => n is int v ? Int(v) : "—";

    /// <summary>"0.93%", "12.4%", or "under 0.01%".</summary>
    public static string Percent(double fraction)
    {
        var pct = fraction * 100;
        if (pct <= 0) return "0%";
        if (pct < 0.01) return "<0.01%";
        return pct < 10 ? pct.ToString("0.00", En) + "%" : pct.ToString("0.0", En) + "%";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "0m";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 100) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{(int)t.TotalHours:N0}h";
    }

    /// <summary>"about 310 h", "about 2 years": deliberately rough, it is an average and not a countdown.</summary>
    public static string RoughTime(TimeSpan? t)
    {
        if (t is not TimeSpan v) return "—";
        if (v.TotalHours < 1) return "under 1 h";
        if (v.TotalHours < 1000) return $"~{Math.Round(v.TotalHours / (v.TotalHours < 100 ? 1 : 10)) * (v.TotalHours < 100 ? 1 : 10):N0} h";
        return $"~{v.TotalHours / 1000:0.#}k h";
    }
}
