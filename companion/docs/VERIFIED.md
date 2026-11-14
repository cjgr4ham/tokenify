# What has been verified, and what has not

TOKENIFY Chest Tracker 2.0.0, counting rules 2.0.0, chest registry v2, Fortnite 43.00 (November 2026).

This page separates three things that are easy to blur: what was **tested and passed**, what is **built but could
not be tested here**, and what the **replay simply does not contain**. Nothing below is an estimate presented as a
fact.

How things were tested:

| Name used below | What it is |
|---|---|
| **unit** | `Tokenify.Tests.exe test`: 138 automated checks of the engine, the evidence rules, statistics, the database, exports, updates, overlay placement and drawing |
| **reader check** | `TOKENIFY-Tracker.exe --check-replay`: the real program reading a replay in its separate reader process, without any window |
| **real replays** | the same run, on 14 matches of the current season (one of them hand-counted) and 4 public sample replays of older seasons |
| **self-test** | `TOKENIFY-Tracker.exe --selftest`: the final single-file exe with real windows on a test data folder, 35 checks, at 100%, 125% and 150% display scaling |
| **backend** | `node dev/test-backend.mjs`: 373 checks of the website's server rules in a local database |
| **end-to-end** | `node dev/e2e-tracker.mjs`: the tracker against the local test backend, 31 checks plus 4 for last season's replays. For 2.1 it ran with the final single-file exe. For 2.0 it ran with the debug build of the same source, because the antivirus on the test PC removed the single-file exe part-way through that run (see Known limits) |
| **by eye** | pictures of every window and overlay layout, drawn by the app itself and looked at |

## Counting

| Behaviour | Status | Evidence |
|---|---|---|
| Chests the replay holds a record for (buried, Sprite, and ordinary ones when recorded) | **Agrees with two independent parsers; not hand-counted** | real replays: 60 chest records in 14 current-season matches; the 11 older baselines give the same totals as the rossalv and LED-N parsers; public samples agree across three parsers. Agreement only shows that three programs read the same fields, not that the fields are complete |
| Ordinary chests the replay holds **no** record for | **Checked against one hand-counted match: 7 of 8 found. Otherwise unverified** | Version 2.0 claimed ordinary chests were verified. That was wrong. In most of this player's 23 replays the game wrote no ordinary chest, no opened state and no loot into the replay at all, and the count was far too low. Since 2.1 the player's own character is used instead: it carries a "started searching" flag for as long as a search lasts. In the hand-counted match (7 ordinary chests and 1 Sprite chest reported by the player) the replay holds one chest record and seven searches; 2.0 counted 1, 2.1 counts 7. Across the 23 replays, 106 searches are followed within 2.5 s by a chest record naming the player (counted once, from the record, as before) and 201 by none (now counted as type "not recorded") |
| What an unrecorded search opened | **Not recorded: known limit** | the flag is the same for a chest, an ammo box, a cooler or anything else that is searched. When the replay does hold the ammo box's record it is left out, as before; when it does not, the search counts. A search you let go of early cannot be told from a finished one |
| Searches on the warm-up island, held too long, or with no visible end | **Verified in the rules** | unit: before the bus drop they are ignored; held over 3 s, never ending, or at the same moment as another player's recorded opening they are listed for review and not counted (3 such cases in the 14 current-season matches) |
| Time after a reboot | **Not recorded in some matches: known limit, reported** | in 7 of the 23 replays the game wrote two or three updates of the player's new character in several minutes, so nothing they did can be seen. Each such stretch is listed with its times under the match, and the count is called a minimum. Nothing is added for it |
| Destroyed chests count when the game names you | **Verified on 3 real cases** | real replays: 3 destroyed chests (2 with the Gum-Gum Fruit, 1 broken with its structure), each recorded by the game as destroyed with the recording player named |
| Destroyed chests you never had on screen | **Verified** | the chest's state arrives together with the chest when it was not being tracked before; counted like any other (unit: dormant chest; real replays: "arrived with the chest" signal) |
| Several chests destroyed at once are separate events | **Verified in the rules; not verified in a real match** | unit: simultaneous events are kept per chest; in the one real case (below) four simultaneous removals are recorded as four events, not one. No real sample shows several chests *named* in one attack |
| Duplicate updates of one chest do not count twice | **Verified** | unit (repeated state, chest leaving and re-entering view, searched-then-destroyed counts once); real replays read twice give identical results |
| A refilled chest opened again counts again | **Verified in the rules** | unit; no current-season sample contains one |
| Other players' chests are never yours | **Verified** | unit (another opener, another opener seen again later); real replays hold other players' chests and totals still match the independent parsers |
| Ammo boxes, food boxes, loot cannons are not chests | **Verified when the replay records them** | unit (registry); real replays. See the row on unrecorded searches for when it does not |
| Unknown chest-like class | **Verified** | unit: counted and flagged "unverified" |
| Unsupported, cut-off, damaged, still-recording replays are recognised | **Verified** | unit: header checks, a truncated real replay is never reported complete, damaged bytes never raise the count, an empty file is never zero chests |
| Nothing is invented where the replay is silent | **Verified** | real replays: 29 chest removals with no record are noted as a number only; 4 ambiguous events are stored and not counted |
| Only this season counts | **Verified** | backend and end-to-end (older replays are skipped without being read; the server refuses them too) |

### The Gum-Gum Fruit: what the replay shows

Details and field names are in [`REPLAY-EVIDENCE.md`](REPLAY-EVIDENCE.md).

- **Single chest destroyed by a Gum-Gum attack: counted.** Two real cases. The game records the chest as destroyed
  and names the player, whether or not the chest was on screen.
- **Several chests removed at once with nothing recorded: not counted, flagged.** One real case (sample
  `season43-01`, 07:27 into the match): four chests and two parts of a ship leave the replay in the same instant
  while the player is attacking with the Gum-Gum Fruit. The replay names nobody and records no damage on those
  chests. They are shown as four **ambiguous** events for the player to confirm.
- **Several chests destroyed at once and each named: not seen yet.** It may well happen and would then be counted
  chest by chest, but there is no sample of it. **This is the main open question.** It needs one match where a
  known number of chests is destroyed with the fruit and written down.
- The Gum-Gum attacks themselves are not recorded as hits on a target (ordinary weapons are), only as ability
  effects with a position. That is why a Gum-Gum removal without a name cannot be upgraded to "corroborated" the
  way a pickaxe hit can.

### What the replay does not contain

- Which chest a piece of loot came from.
- Who destroyed a chest when the game did not write a name on it.
- Anything about the match while it is still being played: the replay becomes readable only when the match ends.
- Whether a counted chest type can drop the Pirate Token. The token figures assume every counted chest can.

## Overlay

Tested on one 1920×1080 display at 125% scaling, with real windows, without Fortnite running.

| Behaviour | Status | Evidence |
|---|---|---|
| Appears above Fortnite in Windowed Fullscreen | **Built; not tested with the game on this PC** | self-test: the window is layered, topmost, non-activating and a tool window, and is kept topmost; that is how any always-on-top window shows over a borderless game. Needs one look with the game running |
| Exclusive Fullscreen | **Not supported, and said so** | Windows does not composite other windows over an exclusive-fullscreen game; the app and its help say to use Windowed Fullscreen |
| Never takes the keyboard | **Verified** | self-test: the window in front before the test is still in front after every operation |
| Drag, resize by the corner, 50–200% limits | **Verified** | self-test |
| Lock / unlock, click-through when locked | **Verified** | self-test (window style changes); unlocking always shows the overlay again |
| Shortcuts | **Registered with Windows; key presses not simulated** | self-test: `RegisterHotKey` succeeds for both; the actions they call are exercised directly |
| Background opacity independent of the numbers | **Verified** | unit: the panel's transparency changes, the digits' solid pixels do not |
| Layouts and styles | **Verified** | unit (minimal smallest, expanded tallest); by eye |
| No clipped text | **Verified** | unit: 324 combinations of layout, style, size, text size and padding, plus seven-digit totals |
| Position and size are remembered | **Verified** | self-test; unit (per display, as a fraction of the display) |
| Off-screen saved position is recovered | **Verified** | self-test; unit |
| Unplugged display | **Verified in arithmetic and by simulation** | unit; self-test with a display name that does not exist |
| Several displays, mixed scaling | **Arithmetic verified; not tested on real hardware** | unit: displays left of, right of and above the main one, different resolutions. Only one display was available |
| Settings windows at other display scales | **Verified at 100%, 125%, 150%** | self-test builds every window at each scale; by eye. Moving a window between displays with different scaling rebuilds it; not tested on real hardware |

## Statistics

| Behaviour | Status | Evidence |
|---|---|---|
| All-time CPG | **Verified** | unit; real replays: 48 chests ÷ 11 games = 4.36 |
| Last 10 CPG, and "Last N" with fewer than ten games | **Verified** | unit |
| A real zero-chest game counts as a game | **Verified** | unit; 4 of the 11 real matches |
| Damaged, cut-off, unsupported, left-out and other players' matches are not games | **Verified** | unit; engine end-to-end on real replays |
| History survives restart and deleted replay files | **Verified** | unit (engine end-to-end) |
| Corrections change every derived number, never the replay's own count | **Verified** | unit; end-to-end |
| Re-reading or rescanning never duplicates a match (also a renamed copy) | **Verified** | unit; backend; end-to-end |
| Version 1's history is carried over | **Verified** | unit (database migration) |
| Sessions: automatic by gap, manual, time in matches | **Verified** | unit |
| Pirate Token chance formula | **Verified** | unit |
| CSV / JSON export, per-match diagnostic file | **Verified** | unit |

## Reliability and sync

| Behaviour | Status | Evidence |
|---|---|---|
| Finished replays are picked up automatically | **Verified** | unit (engine end-to-end); end-to-end |
| A damaged replay cannot freeze the app | **Verified** | replays are read in a separate short-lived process with a memory limit and a time limit, which also ends by itself when the tracker is gone; unit with damaged files; reader check (cut-off file, missing file, tracker gone) |
| Settings survive restart | **Verified** | unit |
| Works with no account at all | **Verified** | unit (engine end-to-end) |
| Linking with a one-time code; the same code never links twice | **Verified** | backend; end-to-end |
| Offline: matches wait and are sent later, once | **Verified** | end-to-end (tracker started while the server cannot be reached) |
| The same match from a reinstalled copy or a second PC is not counted twice | **Verified** | backend; end-to-end |
| Corrections travel both ways | **Verified** | backend; end-to-end |
| Another Fortnite account on the PC is asked about, never sent | **Verified** | end-to-end |
| A disconnected PC stops syncing and says so | **Verified** | end-to-end |
| No key or secret in the log | **Verified** | end-to-end |
| Squad total of the last game | **Verified on the server; display built** | backend; unit (drawing). Not played with a real squad |
| OBS page | **Verified** | self-test (files written, total correct); by eye in a browser |
| Updates only when signed, and only to a newer version | **Verified for the checks; the download path has not run against a real newer release** | unit: a tampered file and a wrong key are rejected; an older signed build offered under a higher release number is refused; odd release entries are skipped. There is no newer release to download yet |
| Start with Windows | **Built; not exercised by the tests** | the tests never write to the Windows start-up list |
| Stays light over hours of play | **Measured briefly, not over hours** | idle in the notification area with 11 matches in its history, optimised build, 30 seconds: 0.7% of one processor core, 68 MB in memory (18 MB private); a replay is read in a separate process that ends afterwards |
| Starts normally from its file | **Verified** | end-to-end. This run caught a start-up failure that only the optimised build had; it is fixed |

## Safety

| | |
|---|---|
| Reads only replay files | **Verified**: unit test scans all source code for process-memory, injection, hooking and input-sending Windows APIs; none are used |
| Overlay is an ordinary window | **Verified**: self-test (window styles) |
| Not a promise | Epic does not say which third-party programs are allowed and says they are used at your own risk. The tracker is built to stay away from everything Epic's terms describe as cheating or tampering. Nobody outside Epic can guarantee how any tool is judged |

## Independent code review

A separate review pass (a reviewer, then a sceptic who tried to refute each finding against the source) has now
covered all five areas.

- Updater, stored credentials, replay-reader process, start-up and single-instance handling: 12 defects confirmed,
  none in counting; all fixed in 2.0.
- Counting engine, tracker engine and storage, overlay and windows, server functions and website scripts: 26
  defects confirmed, 12 refuted. Twelve are fixed in 2.1: a replay with skipped damaged blocks is partial, never
  complete; a match is found again by its file fingerprint; a match whose id changed is sent again; "not mine" also
  takes already sent matches out; resetting settings re-sends what used the old rule; extreme display scales;
  typing a starting total; Enter in the link-code box; several hand-counted games inside one replay match; the
  automatic view without a linked PC; the minus button on replay counts.
- Still open (14): releasing a Fortnite account does not recompute held matches; a re-queue made while an upload is
  in flight can be overwritten; matches of a conflicting account can fill the 25-row upload window; overlay drag
  state when locked or hidden mid-drag, or after a lost mouse-up; replay games attach to any open manual session
  however old; local adjustments on replay games are overwritten; a reused reader stays partial; the analyser
  counts a copied replay twice in totals; `-expect` passes when expected replays are missing; re-reading an
  accepted partial match resets it; a correction always sends both correction and exclusion; a shortcut already
  in use cannot be recorded again; the metrics list is refilled on every refresh.

The review did not find the ordinary-chest gap described under Counting. A player's hand count did.

## Known limits

1. Gum-Gum multi-chest destruction is unverified (see above). Until a hand-counted sample exists, such chests may
   appear as *ambiguous* and need a click to add.
2. Counts are checked against other parsers and against **one** hand-counted match (7 of 8 found). That is thin.
   Chests counted from the player's own search have no type: an ammo box or a search let go of early counts the
   same. In some matches the time after a reboot is not in the replay at all; the tracker says so and does not
   guess.
3. The overlay has not been looked at over the running game on this PC, and never works over exclusive Fullscreen.
4. One display, one scaling factor on the test PC. Other scales were simulated for the windows.
5. A new Fortnite version can change the replay format. Unreadable replays are kept and never counted as zero, but
   counting stops until the tracker is updated.
6. Fortnite keeps about the last 100 replays. A match whose replay was deleted before the tracker ever ran cannot
   be counted.
7. The app is not code-signed, so Windows shows a warning on first run, and antivirus programs may distrust it.
   On the test PC, McAfee quarantined the freshly built exe (and the test's database and log) during an automated
   run that started and stopped it many times; in the other runs it left it alone. This is a false positive, but
   it can happen to players too. The cures are a code-signing certificate or reporting the file to the vendor.
8. Switch and console matches cannot be read: those consoles do not give access to replay files.
