# Replay fixtures

Replay files are large (5–20 MB each) and contain other players' match data, so they are **not committed**.
The test runner takes folders of `.replay` files and checks each file listed in the two expectation files here.

## Public samples (older seasons): `expected.json`

The sample replays shipped with [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor)
(MIT) in `src/ConsoleReader/Replays/`, Fortnite 11.00 – 32.00:

```
git clone --depth 1 https://github.com/Shiqan/FortniteReplayDecompressor.git
companion\tests\Tokenify.Tests\bin\Release\net10.0-windows\Tokenify.Tests.exe test FortniteReplayDecompressor\src\ConsoleReader\Replays
```

`expected.json` records what three independent parsers (TOKENIFY, rossalv/fortnite-chest-counter and
LED-N/Fortnite-ChestCounter) agree on for those files. Agreement between parsers is evidence, not proof.

## Current season (Fortnite 43.00 and 43.10): `expected-current.json`

Fourteen matches recorded in November 2026, kept outside the repository under the neutral names
`season43-01.replay` … `season43-14.replay`. `season43-12` is the only hand-counted one. Point the test runner at the folder that holds them:

```
set TOKENIFY_REPLAY_CURRENT=C:\path\to\the\folder
Tokenify.Tests.exe test <public samples folder>
```

Without that variable these checks are skipped (and reported as skipped, not as passed).

For each file `expected-current.json` gives two groups of numbers:

- from **chest records** (`chests`, `searched`, `destroyed`, `ambiguous`): buried, Sprite, ordinary chests the
  replay recorded, destroyed chests. For the first eleven files these are the same numbers as before the own-search
  rule existed, and the test fails if the rule ever changes one of them. Together: 60 chest records in 14 matches.
- from the **player's own searches** with no chest record (`unrecorded` counted, `unrecordedReview` listed only)
  and the stretches the replay did not record the player's character at all (`gaps`). Together: 90 counted, 3 for
  review, 9 gaps.

The total a match shows is `chests + unrecorded`.

What these baselines are, and are not:

- The **chest-record totals** of the first eleven files are the agreement of three parsers (TOKENIFY, rossalv,
  LED-N). They were **not** hand-counted while playing, and agreement did not catch that ordinary chests were
  missing from the replays altogether.
- `season43-12` was hand-counted by the player right after the match: 7 ordinary chests and 1 Sprite chest. The
  replay gives 1 chest record + 6 unrecorded searches = 7. `unrecorded` numbers of all other files are what the
  rule finds, not a hand count.
- The three destroyed chests are each named by the game (`bDestroyed` + `SearchingPawn` = the recording player).
  Two of them were destroyed with the Gum-Gum Fruit.
- `season43-01` contains the one case the replay does not settle: four chests of a ship vanish in the same instant
  during a Gum-Gum attack with no record of who removed them. They are expected as `ambiguous: 4` and are not in
  the count. See `companion/docs/REPLAY-EVIDENCE.md`.

## Still needed: a hand-counted Gum-Gum match

No sample shows several chests destroyed by one attack **and named individually**, and none was counted by hand.
To close that gap:

1. Turn on *Settings → Game → Replays → Record Replays* in Fortnite.
2. Play a match and destroy a known number of chests with the Gum-Gum Fruit, ideally several with one attack.
   **Write down** how many you destroyed and how many you opened normally.
3. Copy the newest file from `%LOCALAPPDATA%\FortniteGame\Saved\Demos` to the samples folder (copy, never move).
4. Run `tokenify-analyze <file> -events` and compare with your notes.
5. Add an entry to `expected-current.json` with a `"note"` saying it was hand-counted, then run the tests.

Record any difference as a false positive (counted, but not a chest you opened or destroyed) or a false negative
(one of yours that was missed or left ambiguous), with the time in the match, so the rules can be corrected.
`tokenify-analyze -expect truth.json` computes precision and recall against such a hand count.
