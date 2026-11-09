# TOKENIFY Companion

A small Windows app that counts the chests you open in Fortnite **from the replays Fortnite saves on your PC**,
and adds them to your TOKENIFY account after each match. No buttons to press while you play.

It only reads replay files. It never touches the Fortnite program, its memory, its files or your input, and it is
an independent fan tool, not made or endorsed by Epic Games.

## For players: set it up once

1. **Turn on replays in Fortnite:** Settings → Game → Replays → **Record Replays → On**.
2. **Download** `TOKENIFY-Companion.exe` (TOKENIFY → Settings → Automatic PC tracking → *Download companion*,
   or the [latest release](https://github.com/cjgr4ham/tokenify/releases/latest)) and run it. It's one file: no
   installer, no administrator rights, no .NET to install.
   Windows may say *"Windows protected your PC"* because the app isn't code-signed yet. Choose **More info → Run
   anyway** only for a file you downloaded from TOKENIFY's own link; the release page lists its SHA-256 checksum.
3. **Connect your account:** on the TOKENIFY website press **LINK DEVICE** and type the 8-character code into the
   companion. You never type your TOKENIFY password into the app.
4. **Play Fortnite normally.**
5. **Your chest totals update after each match:** personal total, CPG, Last 10, token chance, and your party's
   squad total.

The companion then lives in the notification area (next to the clock). *Start with Windows* (on by default)
means matches are never missed. Closing its window keeps it running; use *Exit* in the tray menu to stop it.

## How chests are counted

- A chest counts when **you** opened it: Fortnite's replay records which player's character searched each chest
  (`SearchingPawn`) and which player made the recording. Teammates' and opponents' chests are never yours.
- **Searched or broken open** (its loot spills, and the replay names who broke it) both count, once.
- **Each opening counts once.** A chest that streams out of view and back in, or whose state is repeated in the
  replay, is the same opening. A chest that was **refilled** during the match and opened again counts again (it's
  a new opening, a new roll), and is marked as such.
- Ammo boxes, food boxes, coolers, cash registers, safes, loot cannons and the golden prize chest animation are not
  chests. Which classes are chests lives in a versioned registry (`registry/chest-registry.json`); a chest-like
  class it doesn't know yet is counted and flagged as *new chest type* so the registry can be checked.
- **Only this season counts** (since Nov 1, 2026, 12:00 AM Eastern): the Pirate Token didn't exist before, so older
  replays are never read or sent. TOKENIFY enforces the same date on its side.
- Counts arrive **after each match**, never during it: Fortnite only writes a replay's decryption key when the match
  ends, so a match in progress can't be read (the companion shows *Match in progress* and waits).

## What TOKENIFY receives

Per finished match: the chest count and when each chest was opened (seconds into the match), chest types, match
start and end, Fortnite's match id (so squad-mates' results line up), the game build and parser version, and a
one-way SHA-256 hash of your Epic account id (plus your Fortnite display name once, so you recognise it on the
website). The PC itself is listed as "Windows PC" with its status (waiting, in a match, processing, syncing).

**Never** the replay file, other players' names or data, your passwords, or anything else from the PC. Replays and
the local database stay on your PC (`%LOCALAPPDATA%\TOKENIFY Companion`).

## Several Fortnite accounts on one PC

The first Fortnite account the companion sees is taken to be yours (it tells you, and you can change it). Any other
account is asked about once ("Is this yours?") before anything of it is sent. A Fortnite account can only ever
count for one TOKENIFY account; the website lists yours under *Fortnite accounts*, where you can unlink one.

## Good to know

- **Fortnite updates** can change the replay format. If matches stop arriving, update the companion. Unreadable
  replays are kept and retried automatically after an update, never recorded as 0 chests.
- Your replay only covers **your own** chests. In a squad, each PC player adds their own; Switch and console
  players keep counting on the website. Results are tied to the squad game by Fortnite's shared match id.
- Fortnite keeps roughly the last 100 replays. Every match the companion has read stays in its database and on
  TOKENIFY even after Fortnite deletes the replay.
- Counts are **replay-derived**: they come from files on your PC, not from Epic, so they are not cryptographically
  verified gameplay.

## Troubleshooting

- **Status tab** (double-click the tray icon) says what the companion is doing and what needs your attention.
- **Help & diagnostics → Export diagnostics…** saves a zip (log, versions, match list) with no secrets, no
  passwords, no Epic account id and no replay files: attach it when asking for help.
- Command line, for support:
  `TOKENIFY-Companion.exe --report report.txt` writes the diagnostics report;
  `TOKENIFY-Companion.exe --pair XXXX-XXXX` links without the window.

## For developers

Requires the .NET 10 SDK on Windows.

```
dotnet build companion\tests\Tokenify.Tests\Tokenify.Tests.csproj -c Release
companion\tests\Tokenify.Tests\bin\Release\net10.0\Tokenify.Tests.exe test <folder with sample replays>
companion\tests\Tokenify.Tests\bin\Release\net10.0\Tokenify.Tests.exe scan <replay or folder> -v

dotnet publish companion\src\Tokenify.Companion\Tokenify.Companion.csproj -c Release -o publish
```

The release build is one self-contained `TOKENIFY-Companion.exe` (about 50 MB: it carries the .NET runtime).

| Folder | What |
|---|---|
| `src/Tokenify.ReplayReader` | replay parsing: `ChestReader` (decodes only chest, pawn, player-state and game-state fields), `OpeningResolver` (what counts as one opening), `ReplayProbe` (is a replay finished?), `ChestRegistry` |
| `src/Tokenify.Companion` | the tray app: folder watcher + scanner, child-process replay reader, SQLite store, DPAPI-protected device secret, sync with retries, setup and main windows |
| `registry/chest-registry.json` | versioned chest rules (embedded in the build) |
| `lib/` | vendored replay library (see below) |
| `tests/` | parser tests (`Tokenify.Tests`) and replay fixtures (`tests/fixtures/README.md`) |

Backend and end-to-end tests live in `dev/`: `node test-backend.mjs` (all server rules, including pairing, imports,
squads and the season cutoff) and `node e2e-companion.mjs <exe> <replays>` (the real exe against the local backend;
start `node local-supabase.mjs --season-start=2019-01-01` first so the old sample replays count).

**When Fortnite changes:** if a new build breaks parsing, update `lib/` from the upstream projects, re-run the
tests on a fresh replay, and bump `ParserInfo.Version` (re-reads cached replays; the server keeps earlier results
for audit and never overwrites a player's own correction). If only chest classes changed, edit the registry and
bump its `version`.

**New season:** change `tkp.season_start()` in `supabase/accounts-v2.sql` (and `DefaultSeasonStartUtc` in
`Engine.cs`), then re-run the SQL. Companions pick up the new date at their next check-in.

## Licence and credits

TOKENIFY Companion is free software under the **GNU General Public License v3.0 or later** (`LICENSE`): it includes
OozSharp, a GPL-3.0 port of powzix/ooz used to decompress replays. The rest of TOKENIFY (the website) is separate.

It is built on, and adapts code from, three MIT-licensed projects. Thank you:
- [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) (Unreal.Core,
  Unreal.Encryption, OozSharp): the replay reader.
- [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter): the patched copy of that library
  in `lib/` (see `lib/PATCHES-Fortnite-ChestCounter.md`) and the opening rules (refills, re-seen chests, broken chests).
- [rossalv/fortnite-chest-counter](https://github.com/rossalv/fortnite-chest-counter): field-level chest decoding,
  chest identities, the corrupt-block guard and child-process reading.

Full notices: `THIRD-PARTY-NOTICES.md`. Fortnite is a trademark of Epic Games, Inc.
