# Replay fixtures

Replay files are large (5–20 MB each) and contain other players' match data, so they are **not committed**.
The test runner takes a folder of `.replay` files and checks each one listed in `expected.json`.

## Public samples used today

The sample replays shipped with [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor)
(MIT) in `src/ConsoleReader/Replays/`, Fortnite 11.00 – 32.00:

```
git clone --depth 1 https://github.com/Shiqan/FortniteReplayDecompressor.git
companion\tests\Tokenify.Tests\bin\Release\net10.0\Tokenify.Tests.exe test FortniteReplayDecompressor\src\ConsoleReader\Replays
```

`expected.json` records what three independent parsers (TOKENIFY, rossalv/fortnite-chest-counter and
LED-N/Fortnite-ChestCounter) agree on for those files. Agreement between parsers is evidence, not proof.

## Still needed: current-season replays (Fortnite 42.x)

None of the public samples is from the current season. To validate a new Fortnite version:

1. On a Windows PC, turn on *Settings → Game → Replays → Record Replays*.
2. Play a match and **write down every chest you open** (and whether you broke any instead).
3. Copy the newest file from `%LOCALAPPDATA%\FortniteGame\Saved\Demos` into this folder.
4. Add an entry to `expected.json` with your hand count, then run the tests.

Record any difference as a false positive (counted, but not a chest you opened) or a false negative (a chest
you opened that was missed), with the time in the match, so the chest registry or parser can be fixed.
