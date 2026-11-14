# TOKENIFY Chest Tracker

A small Windows app that counts the chests you open **and destroy** in Fortnite, from the replay Fortnite saves on
your PC after every match, and shows your total and your chests per game (CPG) on a small overlay while you play.
Nothing to press, nothing to Alt+Tab to.

It replaces *TOKENIFY Companion* (version 1): run the tracker once and it takes over that app's history and its
link to your TOKENIFY account.

It only reads replay files. It never touches the Fortnite program, its memory, its files or your input. It is an
independent fan tool, not made, reviewed or endorsed by Epic Games.

- [Set it up](#set-it-up-about-two-minutes)
- [The overlay](#the-overlay)
- [How chests are counted](#how-chests-are-counted)
- [When the replay is not sure](#when-the-replay-is-not-sure-ambiguous-chests)
- [Statistics](#statistics)
- [TOKENIFY website, OBS, updates](#tokenify-website-optional)
- [What it does not do](#what-it-does-not-do)
- [Troubleshooting](#troubleshooting)
- [For developers](#for-developers)

What has actually been verified, and what has not, is listed item by item in
[`docs/VERIFIED.md`](docs/VERIFIED.md). The replay research behind the counting rules is in
[`docs/REPLAY-EVIDENCE.md`](docs/REPLAY-EVIDENCE.md).

## Set it up (about two minutes)

1. **Turn on replays in Fortnite:** Settings → Game → Replays → **Record Replays → On** (and *Record Large Team
   Replays* if you play those modes).
2. **Download `TOKENIFY-Tracker.exe`** from the TOKENIFY website (Settings → Automatic PC tracking → *Download the
   tracker*) or the [latest release](https://github.com/cjgr4ham/tokenify/releases/latest), and run it. It is one
   file: no installer, no administrator rights, no .NET to install.
   Windows may say *"Windows protected your PC"* because the app is not code-signed. Choose **More info → Run
   anyway** only for a file you downloaded from TOKENIFY's own link; the release page lists its SHA-256 checksum.
3. **Follow the six setup steps.** It finds Fortnite's replay folder, shows the overlay so you can pick a layout
   and style, asks whether to start with Windows, and offers to link your TOKENIFY account (optional).
4. **Set Fortnite to Windowed Fullscreen** (Settings → Video → Window Mode) so the overlay can be seen over it.
5. **Play.** A few seconds after each match ends, the overlay shows the new total.

The tracker lives in the notification area next to the clock (the compass icon). Closing its window keeps it
running; right-click the icon and choose **Exit** to stop it. A match played while the tracker was not running is
still counted the next time it starts, as long as Fortnite has not deleted that replay (it keeps roughly the last
100).

**Removing it:** exit it from the tray menu and delete `TOKENIFY-Tracker.exe`. Your history and settings stay in
`%LOCALAPPDATA%\TOKENIFY Companion` (the folder keeps its old name so version 1's data carries over) until you
delete that folder yourself. Inside the app, *Advanced → Start over* has separate buttons for resetting settings
and for deleting history.

## The overlay

A small always-on-top panel. The big number is your **confirmed total**; CPG comes next.

| | |
|---|---|
| **Layouts** | *Minimal* (compass, total, average CPG on one line), *Standard* (total, last game, average CPG, last 10 CPG, a small status), *Expanded* (adds session, best game), *Custom* (tick any of the metrics and order them) |
| **Styles** | Frosted Dark, Solid Dark, HUD Only (no panel, outlined numbers), Gold Accent |
| **Background opacity** | 0–90%, independent of the numbers: they stay solid and get a thin dark edge when the panel is faint |
| **Size** | 50–200%, by slider or by dragging the corner while unlocked; text size 80–130%; accent colour; corner radius; padding |
| **Position** | drag it anywhere while unlocked; it snaps to edges and the centre; it remembers its place on each display and comes back into view if that place no longer exists |
| **Locked** (the default) | clicks go straight through it to the game |

Shortcuts work while Fortnite has the keyboard, and can be changed under *Shortcuts*:

- **Ctrl+Shift+L** lock / unlock (unlocking always brings the overlay back if it was hidden or lost)
- **Ctrl+Shift+O** show / hide

During a match the overlay says **MATCH IN PROGRESS** and keeps showing the totals of finished matches: a match in
progress cannot be read (Fortnite only makes the replay readable when the match ends), so no number is ever shown
for it. When the replay has been read, **+42 CHESTS** appears for a moment and the total updates.

The overlay is an ordinary Windows window (layered, topmost, never activated). It cannot draw over a game running
in **exclusive Fullscreen**: Windows does not allow that, and the tracker does not try to force it. Counting works
either way.

*Optional manual count:* under *Shortcuts* you can turn on a key (default **K**, off by default) to count by hand
as you play. It is shown as YOUR COUNT next to the replay's number afterwards, and never adds to your total by
itself. While it is on, that key is taken by the tracker and does not reach the game, so pick one you do not use.

## How chests are counted

Fortnite's replay records, for each chest, which player's character searched it and whether it was opened or
destroyed, and it records which player made the recording. The tracker counts a chest when the game itself named
**you** on it.

- **Opened** (searched) and **destroyed** (broken by your pickaxe, explosives, a Gum-Gum Fruit attack, or the floor
  under it, with the game naming you) both count, **once** each. *Tracking → What counts* can switch destroyed
  chests off.
- **Each opening counts once.** A chest that goes out of view and comes back, or whose state is repeated in the
  replay, is the same opening. A chest that was **refilled** and opened again counts again (a new opening).
- **Other players' chests are never yours**, including teammates'.
- Ammo boxes, food boxes, coolers, safes and loot cannons are not chests. Which classes are chests is a versioned
  list (`registry/chest-registry.json`); a chest-like class the list does not know yet is counted and marked
  *unverified*.
- **Only this season counts** (from Nov 1, 2026, 12:00 AM Eastern): the Pirate Token did not exist before, so
  older replays are never read.
- It works for chests you never had on screen: the replay is recorded by the game, not by your camera.

Every chest event carries one of four evidence classes, shown in *Matches* for each event with the signals it was
based on:

| Class | Meaning | Counted |
|---|---|---|
| **Confirmed** | the game named you on the chest and recorded its new state | yes |
| **Corroborated** | nobody was named, but the chest was seen intact, your own hit on it is recorded within seconds, and nobody else's | yes |
| **Ambiguous** | something happened to the chest while you were attacking, but the replay does not say who did it | **no**, unless you add it |
| **No record** | the chest left the replay with no information at all | no (only the number is noted) |

## When the replay is not sure (ambiguous chests)

The tracker never guesses. In 11 real replays from this season (48 chests, 3 destroyed, 2 with the Gum-Gum Fruit)
every destroyed chest was named by the game and counted. One situation was different: four chests and parts of a
ship vanished in the same instant during a Gum-Gum attack, with no record of who removed them. Those four are
listed as **ambiguous** and are **not** in the total.

So after a match with something like that, *Matches* shows it with "4 to review". Open it and choose:

- **The 4 ambiguous chests were mine: add them** (recorded as your correction), or
- **The count is right**, or
- **Correct count…** to enter the number you saw.

The replay's own count and every event stay on record next to your correction, and a later re-read of the replay
never overwrites it. Whether several chests smashed at once are always named individually is **not yet verified**:
it needs a match where a known number of chests was destroyed that way.

## Statistics

- **CPG** = chests in valid finished games ÷ valid finished games.
  A real finished game with zero chests counts as a game. A replay that is damaged, cut off, from an unsupported
  Fortnite version, or never recorded is **not** a zero-chest game: it is left out and listed separately.
- **Last 10 CPG** uses the ten most recent valid games; with fewer it says *Last 6* and divides by six.
- Best game, last game, total matches, a starting total (chests you had before the tracker), an optional start date.
- **Sessions:** automatic (matches less than 30 minutes apart belong together; the gap is adjustable) or manual
  (start and end them yourself). Automatic session times are estimates and are labelled as such; session time is
  time in matches, not queues.
- **Pirate Token figures** (optional metrics): chance of at least one in *n* chests = `1 − (1 − 1/137,500)^n`. They
  are theoretical and assume every counted chest can drop the token.
- **Several Fortnite accounts on one PC** each keep their own numbers. The first account seen is taken to be
  yours; any other is asked about once before anything of it is counted.
- **Export** (Advanced): the whole history as CSV or JSON, with every correction. A match's chest events can be
  saved as a diagnostic file from *Matches*.

History stays after Fortnite deletes the replay files.

## TOKENIFY website (optional)

The tracker works fully on its own. Linked (Account → type the code the website shows; you never enter your
website password), each finished match goes to your TOKENIFY account and your parties, and corrections travel both
ways.

**Sent per finished match:** its match id, start and end time, your chest count with the opened/destroyed split and
the time of each chest, the Fortnite version, the counting-rules version, whether the replay was read completely,
and a one-way hash of your Epic account id (plus your Fortnite display name once, so you recognise it on the
website). **Never sent:** the replay file, other players, your Epic account id itself, your PC's name, or anything
else from the PC. Matches wait in a queue while you are offline and are sent later; the same match is never counted
twice.

**OBS:** *Advanced → OBS overlay* writes a small page (`obs-overlay.html`) next to the app's data. Add it in OBS as
a Browser source with *Local file*. It has a transparent background and updates after each match. It is a file on
your PC: nothing is put online and nothing listens on your network.

**Updates:** by default the tracker asks GitHub once per start whether a newer release exists and tells you. An
update is downloaded only when you click, and installed only if its signature matches TOKENIFY's release key;
otherwise the download is deleted. *Advanced → Updates → Never check* turns the check off.

## What it does not do

- It does not read or write Fortnite's memory, inject anything, hook rendering or input, send key presses to the
  game, change game files, or look at network traffic. An automated test scans the source for those Windows APIs.
- It does not show anything about a match before that match is over, and nothing about other players.
- It does not upload replay files.
- It does not promise to be risk-free. Epic's terms forbid cheats and tampering with the game, and Epic says it
  cannot tell players whether a particular third-party program may lead to a ban; such programs are used at your
  own risk. This tracker is a passive statistics tool that only reads files Fortnite wrote for you, and it is
  built to stay well away from anything those terms describe, but nobody outside Epic can guarantee how they
  judge any tool.

## Troubleshooting

- **Overview** says what the tracker is doing and what needs your attention. **Advanced → Health check** lists the
  replay folder, the replays found, whether they could be read, the Fortnite account, and the website link.
- **The program disappeared or will not start:** an antivirus program may have removed it. It is new and not
  code-signed, which some of them distrust (McAfee did this once during testing). Restore it from the antivirus's
  quarantine if you trust the download, and check the SHA-256 on the release page.
- **Nothing is counted:** replays are off in Fortnite, or the folder is elsewhere (*Tracking → Choose folder…*).
- **The overlay is not visible over the game:** Fortnite is in exclusive Fullscreen (use Windowed Fullscreen), or
  the overlay is hidden (Ctrl+Shift+O) or switched off (*Position*). Ctrl+Shift+L always brings it back.
- **A match shows "Unsupported replay":** Fortnite changed its replay format. Update the tracker; the replay is
  kept and read afterwards. It is never counted as zero.
- **A count looks wrong:** open the match in *Matches*, look at the events, correct it, and use *Save diagnostic
  file…* if you want to report it.
- Command line, for support: `TOKENIFY-Tracker.exe --report report.txt` writes a diagnostics report with no
  secrets; `TOKENIFY-Tracker.exe --check-replay <replay> result.txt` reads one replay the way the running tracker
  does and writes what it found (nothing is stored); `tokenify-analyze.exe <replay or folder> -events` (a separate small program, see below) prints what a
  replay holds without the app.

## For developers

Requires the .NET 10 SDK on Windows.

```
dotnet build companion\tests\Tokenify.Tests\Tokenify.Tests.csproj -c Release
companion\tests\Tokenify.Tests\bin\Release\net10.0-windows\Tokenify.Tests.exe test <folder with sample replays>

dotnet build companion\src\Tokenify.Analyze\Tokenify.Analyze.csproj -c Release
companion\src\Tokenify.Analyze\bin\Release\net10.0\tokenify-analyze.exe <replay or folder> [-events] [-json] [-expect truth.json]

dotnet publish companion\src\Tokenify.Tracker\Tokenify.Tracker.csproj -c Release -o publish
```

The release build is one self-contained `TOKENIFY-Tracker.exe` (it carries the .NET runtime).

| Folder | What |
|---|---|
| `src/Tokenify.ReplayReader` | replay parsing: `ChestReader` (decodes only chest, pawn, player-state, game-state and damage-cue fields), `OpeningResolver` (what is one opening), `Evidence` (the four evidence classes and the rules between them), `ReplayProbe` (is a replay finished?), `ChestRegistry` |
| `src/Tokenify.Analyze` | `tokenify-analyze`, the command-line analyser: replay id, Fortnite version, recorder, searched, destroyed, confirmed total, ambiguous count, completeness |
| `src/Tokenify.Core` | the engine without any window: replay folder watcher, child-process replay reading, SQLite history, statistics, sessions, corrections, sync with retries, exports, the OBS page, signed updates |
| `src/Tokenify.Overlay` | the overlay: renderer (GDI+ into a per-pixel-alpha layered window), placement arithmetic, global shortcuts |
| `src/Tokenify.Tracker` | the app: tray icon, main window, setup, self-test |
| `registry/chest-registry.json` | versioned chest and weapon rules (embedded in the build) |
| `lib/` | vendored replay library (see below) |
| `tests/` | `Tokenify.Tests` (engine, evidence rules, statistics, database, overlay placement and drawing, a scan for forbidden Windows APIs) and replay fixtures (`tests/fixtures/README.md`) |

Tests that need real windows run inside the app: `TOKENIFY-Tracker.exe --selftest <output folder>` with
`TOKENIFY_DATA` set to a throwaway folder (it refuses to run on a real install). It checks the overlay's window
styles, that nothing takes the keyboard, dragging, resizing, position memory and off-screen recovery, and draws
every page to a PNG. `TOKENIFY_UI_SCALE=150` builds the windows for another display scale.

Backend and end-to-end tests live in `dev/`: `node test-backend.mjs` (all server rules, including pairing, imports,
corrections from the tracker, squads and the season cutoff) and `node e2e-tracker.mjs <exe> <replays>` (the real
exe against the local backend; start `node local-supabase.mjs --season-start=2019-01-01` first so the old sample
replays count).

**When Fortnite changes:** if a new build breaks parsing, update `lib/` from the upstream projects, re-run the
tests on a fresh replay, and bump `ParserInfo.Version` (cached replays are read again; a player's own correction is
never overwritten). If only chest or weapon classes changed, edit the registry and bump its `version`. Add the new
replays' expected counts to `tests/fixtures/expected-current.json`.

**New season:** change `tkp.season_start()` in `supabase/accounts-v2.sql` (and `DefaultSeasonStartUtc` in
`Tracker.cs`), then re-run the SQL. Trackers pick up the new date at their next check-in.

**Releases:** tag `tracker-vX.Y.Z` with three files: `TOKENIFY-Tracker.exe`, `TOKENIFY-Tracker.exe.sig` (ECDSA
P-256 signature made with the release key: `Tokenify.Tests.exe sign <exe> <private key file>`) and `SHA256SUMS.txt`.
The public key is in `Updater.cs`; the private key is not in the repository and must never be.

## Licence and credits

TOKENIFY Chest Tracker is free software under the **GNU General Public License v3.0 or later** (`LICENSE`): it
includes OozSharp, a GPL-3.0 port of powzix/ooz used to decompress replays. The rest of TOKENIFY (the website) is
separate.

It is built on, and adapts code from, three MIT-licensed projects. Thank you:
- [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) (Unreal.Core,
  Unreal.Encryption, OozSharp): the replay reader.
- [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter): the patched copy of that library
  in `lib/` (see `lib/PATCHES-Fortnite-ChestCounter.md`) and the opening rules (refills, re-seen chests, broken chests).
- [rossalv/fortnite-chest-counter](https://github.com/rossalv/fortnite-chest-counter): field-level chest decoding,
  chest identities, the corrupt-block guard and child-process reading.

The overlay uses Bahnschrift, a typeface that ships with Windows; no font is bundled.

Full notices: `THIRD-PARTY-NOTICES.md`. Fortnite is a trademark of Epic Games, Inc.
