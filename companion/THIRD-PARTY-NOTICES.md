# Third-party notices

TOKENIFY Companion (GPL-3.0-or-later, see `LICENSE`) includes or is built with the following. Licence texts for
the MIT-licensed replay projects are in `lib/`.

| Component | Use | Licence |
|---|---|---|
| Unreal.Core, Unreal.Encryption — [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) (commit 76f74c1) | Unreal Engine replay reader | MIT, © 2020-2026 Shiqan (`lib/LICENSE-FortniteReplayDecompressor.txt`) |
| Patches to the above — [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter) (commit in `lib/.ledn-commit`) | fixes listed in `lib/PATCHES-Fortnite-ChestCounter.md`; opening rules adapted in `OpeningResolver.cs`; `EcoQos` | MIT, © 2026 LED-N (`lib/LICENSE-Fortnite-ChestCounter.txt`) |
| [rossalv/fortnite-chest-counter](https://github.com/rossalv/fortnite-chest-counter) (commit in `lib/.rossalv-commit`) | parts of `ChestReader.cs`, `ReplayWorker.cs`, `ReplayProbe.cs` | MIT, © 2026 rfalv (`lib/LICENSE-fortnite-chest-counter.txt`) |
| OozSharp (in FortniteReplayDecompressor), a C# port by SL-x-TnT of [powzix/ooz](https://github.com/powzix/ooz) | Oodle Kraken decompression of replay data | GPL-3.0-or-later, © 2016 Powzix |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 10.0.14 | local database | MIT, © .NET Foundation |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) 2.1.14 | SQLite native bundle | Apache-2.0, © Eric Sink |
| [SQLite](https://sqlite.org) | database engine | Public domain |
| [.NET runtime and Windows Forms](https://github.com/dotnet) 10 (bundled in the self-contained exe) | runtime | MIT, © .NET Foundation and contributors |
| Microsoft.Extensions.Logging.Abstractions 10 | logging interfaces used by Unreal.Core | MIT, © .NET Foundation |

"Fortnite" and "Unreal Engine" are trademarks of Epic Games, Inc. This project is not affiliated with or endorsed by
Epic Games.
