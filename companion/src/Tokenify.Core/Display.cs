using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tokenify.Core;

public enum StatusKind { Idle, Live, Busy, Good, Warn, Off }

/// <param name="Trend">+1 / 0 / -1 when the value comes with an "against your average" marker.</param>
/// <param name="Dim">Shown, but not complete or not available right now.</param>
public sealed record MetricItem(string Id, string Label, string Value, int? Trend = null, bool Dim = false);

/// <summary>
/// What the overlay (and the OBS page) shows right now, as plain text: one big number, a few labelled rows and a
/// status. Built from the tracker's snapshot only, so both displays always agree with the windows.
/// </summary>
public sealed record OverlayContent(MetricItem Hero, IReadOnlyList<MetricItem> Rows, string? Status, StatusKind StatusKind, string? Toast)
{
    /// <summary>Changes whenever anything visible changes: lets a display skip redrawing when nothing did.</summary>
    public string Signature => string.Join("|", new[] { Hero.Value, Status ?? "", StatusKind.ToString(), Toast ?? "" }
        .Concat(Rows.Select(r => $"{r.Id}={r.Label}:{r.Value}:{r.Trend}:{r.Dim}")));
}

public static class Display
{
    public static OverlayContent Build(TrackerSnapshot s, IReadOnlyList<string> metrics, string? toast = null)
    {
        var stats = s.Stats;
        var rows = new List<MetricItem>();
        var showStatus = false;
        foreach (var id in metrics)
        {
            switch (id)
            {
                case Metric.Total:
                    break; // always the big number
                case Metric.Status:
                    showStatus = true;
                    break;
                default:
                    if (Item(s, id) is { } item) rows.Add(item);
                    break;
            }
        }

        // Party members' chests: one extra row, by itself, only once there are any (the small view stays small).
        if (s.PartyChests > 0 && !metrics.Contains(Metric.SquadTotal) && Item(s, Metric.SquadTotal) is { } squadTotal)
        {
            rows.Add(squadTotal);
        }

        // The player's own running count for the match being played, when they use the manual shortcut.
        if (s.Settings.Shortcuts.ManualEnabled && s.Activity == Activity.InMatch && s.ManualTaps > 0)
        {
            rows.Add(new MetricItem("taps", "YOUR COUNT", Fmt.Int(s.ManualTaps)));
        }

        var kind = s.Activity switch
        {
            Activity.InMatch => StatusKind.Live,
            Activity.Processing => StatusKind.Busy,
            Activity.NeedsAttention or Activity.FolderMissing => StatusKind.Warn,
            Activity.Paused => StatusKind.Off,
            Activity.Ready => StatusKind.Good,
            _ => StatusKind.Idle,
        };
        return new OverlayContent(new MetricItem(Metric.Total, "TOTAL CHESTS", Fmt.Int(stats.TotalChests)), rows,
            showStatus || toast is not null ? s.Status : null, kind, toast);
    }

    private static MetricItem? Item(TrackerSnapshot s, string id)
    {
        var st = s.Stats;
        var session = st.Session;
        string SessionValue(string value) => session.Active && (session.Games > 0 || s.Activity == Activity.InMatch) ? value : "—";
        return id switch
        {
            Metric.Matches => new MetricItem(id, "MATCHES", Fmt.Int(st.Games)),
            Metric.Cpg => new MetricItem(id, "AVG CPG", Fmt.Cpg(st.Cpg)),
            Metric.Best => new MetricItem(id, "BEST GAME", Fmt.Count(st.Best)),
            Metric.Last => new MetricItem(id, "LAST GAME", Fmt.Count(st.LastGame?.Chests)),
            Metric.Last10 => new MetricItem(id, st.Last10.Label, Fmt.Cpg(st.Last10.Cpg), st.Trend),
            Metric.Last25 => new MetricItem(id, st.Last25.Label, Fmt.Cpg(st.Last25.Cpg)),
            Metric.Last50 => new MetricItem(id, st.Last50.Label, Fmt.Cpg(st.Last50.Cpg)),
            Metric.SessionChests => new MetricItem(id, "SESSION", SessionValue(Fmt.Int(session.Chests)), Dim: !session.Active),
            Metric.SessionGames => new MetricItem(id, "SESSION GAMES", SessionValue(Fmt.Int(session.Games)), Dim: !session.Active),
            Metric.SessionCpg => new MetricItem(id, "SESSION CPG", SessionValue(Fmt.Cpg(session.Cpg)), Dim: !session.Active),
            Metric.SessionTime => new MetricItem(id, "SESSION TIME", SessionValue(Fmt.Duration(session.MatchTime)), Dim: !session.Active),
            Metric.Chance => new MetricItem(id, "TOKEN CHANCE", Fmt.Percent(st.Odds.Chance)),
            Metric.AverageToToken => new MetricItem(id, "AVG PER TOKEN", Fmt.Int(TokenOdds.AverageChestsPerToken)),
            Metric.Eta => new MetricItem(id, "PER TOKEN AT PACE", Fmt.RoughTime(st.Odds.TimePerToken), Dim: st.Odds.TimePerToken is null),
            Metric.Squad => s.Squad is { } q
                // Shown as final only when every player's count is in. Until then it is "at least this many":
                // "2/4" when counts are still missing, "SO FAR" when the squad's size itself is not confirmed.
                ? new MetricItem(id, q.Complete ? "SQUAD LAST GAME" : q.Members > q.Reported ? $"SQUAD ({q.Reported}/{q.Members})" : "SQUAD SO FAR",
                    Fmt.Int(q.Chests) + (q.Complete ? "" : "+"), Dim: !q.Complete)
                : new MetricItem(id, "SQUAD LAST GAME", "—", Dim: true),
            Metric.SquadTotal => new MetricItem(id, "SQUAD TOTAL", Fmt.Int(s.SquadTotal), Dim: s.PartyChests == 0),
            Metric.Sync => new MetricItem(id, "WEBSITE", s.Sync switch
            {
                SyncState.UpToDate => "SYNCED",
                SyncState.Syncing => "SYNCING",
                SyncState.Queued => $"{s.Queued} WAITING",
                SyncState.Offline => "OFFLINE",
                SyncState.Disconnected => "RECONNECT",
                _ => "NOT LINKED",
            }, Dim: s.Sync is SyncState.NotLinked or SyncState.Offline or SyncState.Disconnected),
            _ => null,
        };
    }

    public static IReadOnlyList<string> ObsMetrics(GeneralSettings g)
    {
        var list = new List<string>(Metric.ForPreset(g.ObsLayout, Metric.Standard));
        if (g.ObsSquad && !list.Contains(Metric.Squad)) list.Add(Metric.Squad);
        return list;
    }
}

/// <summary>
/// The OBS overlay: two files in the tracker's data folder. obs-overlay.html is a page with a transparent
/// background, added to OBS as a Browser source with "Local file". obs-data.js holds the current numbers and is
/// rewritten whenever they change; the page re-reads it every few seconds. Nothing is served over the network
/// and nothing is listening on any port. Like the overlay, it only ever shows finished matches.
/// </summary>
public static class ObsPage
{
    public static string DataPath => Path.Combine(AppPaths.DataFolder, "obs-data.js");
    private static string? _lastData, _lastPage;

    public static void Update(TrackerSnapshot s)
    {
        if (!s.Settings.General.ObsPage) return;
        try
        {
            var content = Display.Build(s, Display.ObsMetrics(s.Settings.General));
            var data = new JsonObject
            {
                ["hero"] = new JsonObject { ["label"] = content.Hero.Label, ["value"] = content.Hero.Value },
                ["rows"] = new JsonArray(content.Rows.Select(r => (JsonNode)new JsonObject { ["label"] = r.Label, ["value"] = r.Value, ["trend"] = r.Trend, ["dim"] = r.Dim }).ToArray()),
                ["status"] = content.Status,
                ["kind"] = content.StatusKind.ToString().ToLowerInvariant(),
                ["accent"] = s.Settings.Overlay.Accent,
                ["layout"] = s.Settings.General.ObsLayout,
            }.ToJsonString();
            var js = "window.TOKENIFY_DATA = " + data + ";";
            if (js != _lastData || !File.Exists(DataPath))
            {
                Exporter.Write(DataPath, js);
                _lastData = js;
            }
            if (_lastPage != Page || !File.Exists(AppPaths.ObsPage))
            {
                Exporter.Write(AppPaths.ObsPage, Page);
                _lastPage = Page;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("OBS page could not be written: " + ex.Message);
        }
    }

    private const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>TOKENIFY chest overlay</title>
        <style>
          :root { --accent: #F6C24A; }
          html, body { margin: 0; background: transparent; overflow: hidden; }
          body { font-family: "Bahnschrift SemiCondensed", "Bahnschrift", "Barlow Condensed", "Arial Narrow", "Segoe UI", sans-serif; color: #F3F5FA; }
          .card { display: inline-block; min-width: 210px; margin: 8px; padding: 12px 16px 13px; border-radius: 12px;
                  background: linear-gradient(160deg, rgba(16,26,47,.86), rgba(8,13,25,.90)); border: 1px solid rgba(246,194,74,.28);
                  box-shadow: 0 6px 22px rgba(0,0,0,.45); }
          .head { display: flex; align-items: center; justify-content: space-between; gap: 18px; font-size: 13px; letter-spacing: .14em; font-weight: 600; color: var(--accent); }
          .status { display: flex; align-items: center; gap: 6px; font-size: 11px; letter-spacing: .12em; color: #9AA6BF; }
          .dot { width: 7px; height: 7px; border-radius: 50%; background: #5d6a85; }
          .live .dot { background: #57d98a; box-shadow: 0 0 8px rgba(87,217,138,.8); }
          .busy .dot, .warn .dot { background: var(--accent); }
          .hero { margin-top: 6px; font-family: "Bahnschrift SemiBold Condensed", "Bahnschrift", "Barlow Condensed", Impact, sans-serif;
                  font-size: 58px; line-height: 1; font-weight: 700; font-variant-numeric: tabular-nums; text-shadow: 0 2px 10px rgba(0,0,0,.55); }
          .herolabel { margin-top: 2px; font-size: 12px; letter-spacing: .2em; color: var(--accent); }
          .rows { margin-top: 10px; border-top: 1px solid rgba(255,255,255,.10); padding-top: 8px; }
          .row { display: flex; justify-content: space-between; gap: 22px; font-size: 16px; line-height: 1.45; }
          .row .l { letter-spacing: .1em; color: #9AA6BF; font-size: 13px; padding-top: 2px; }
          .row .v { font-weight: 600; font-variant-numeric: tabular-nums; }
          .row.dim .v { color: #9AA6BF; }
          .up::after { content: " \25B2"; color: #57d98a; font-size: 10px; } .down::after { content: " \25BC"; color: #e9785f; font-size: 10px; }
          .minimal .rows, .minimal .herolabel { display: none; } .minimal .hero { font-size: 44px; }
          .changed { animation: pop .9s ease-out; } @keyframes pop { 0% { color: var(--accent); transform: scale(1.06); } 100% { transform: none; } }
        </style>
        </head>
        <body>
        <div class="card" id="card">
          <div class="head"><span>&#9672; TOKENIFY</span><span class="status" id="status"><span class="dot"></span><span id="statustext"></span></span></div>
          <div class="hero" id="hero">0</div>
          <div class="herolabel" id="herolabel">TOTAL CHESTS</div>
          <div class="rows" id="rows"></div>
        </div>
        <script>
          // The numbers live in obs-data.js next to this file. It is re-read every 3 seconds; nothing is fetched
          // from the internet and nothing is shown for a match until its replay has been read.
          var last = "";
          function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]; }); }
          function render(d) {
            var key = JSON.stringify(d);
            if (key === last) return;
            var first = last === "";
            last = key;
            document.documentElement.style.setProperty("--accent", /^#[0-9a-f]{6}$/i.test(d.accent) ? d.accent : "#F6C24A");
            var card = document.getElementById("card");
            card.className = "card " + (d.layout === "minimal" ? "minimal" : "");
            var hero = document.getElementById("hero");
            if (hero.textContent !== d.hero.value) { hero.textContent = d.hero.value; if (!first) { hero.classList.remove("changed"); void hero.offsetWidth; hero.classList.add("changed"); } }
            document.getElementById("herolabel").textContent = d.hero.label;
            document.getElementById("rows").innerHTML = d.rows.map(function (r) {
              return '<div class="row' + (r.dim ? " dim" : "") + '"><span class="l">' + esc(r.label) + '</span><span class="v ' + (r.trend > 0 ? "up" : r.trend < 0 ? "down" : "") + '">' + esc(r.value) + "</span></div>";
            }).join("");
            var st = document.getElementById("status");
            st.className = "status " + (d.kind || "");
            st.style.display = d.status ? "" : "none";
            document.getElementById("statustext").textContent = d.status || "";
          }
          function load() {
            var s = document.createElement("script");
            s.src = "obs-data.js?t=" + Date.now();
            s.onload = function () { if (window.TOKENIFY_DATA) render(window.TOKENIFY_DATA); s.remove(); };
            s.onerror = function () { s.remove(); };
            document.head.appendChild(s);
          }
          load();
          setInterval(load, 3000);
        </script>
        </body>
        </html>
        """;
}
