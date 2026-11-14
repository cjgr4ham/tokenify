# What a Fortnite replay records about chests

Findings from 11 real matches recorded on one PC with Fortnite `++Fortnite+Release-43.00` (10–11 November 2026),
read with `Tokenify.Tests.exe probe` and `tokenify-analyze`. Everything below was observed in those files. Where
something was **not** observed, or could not be settled, it says so. Nothing here was read from the game itself:
only from the replay files Fortnite saves.

Sample size matters: 48 chests counted for the recording player, 3 of them destroyed rather than opened, 2 of
those with the Gum-Gum Fruit. That is enough to describe how the format works, not enough to promise it never
behaves differently.

## Summary

| Question | Answer from the data |
|---|---|
| Does the replay name who opened a chest? | Yes. `SearchingPawn` on the chest, set together with `bAlreadySearched`. |
| What about a chest destroyed instead of opened? | The chest gets `bDestroyed` (and `bHidden`), `SearchAnimationCount` goes up, and `SearchingPawn` names the destroyer. `bAlreadySearched` is not set. Seen 5 times (3 by the recorder), named every time. |
| Destroyed with the Gum-Gum Fruit? | Seen twice for the recorder, both named and counted (a relic chest, a regular chest). |
| Destroyed indirectly (what it stood on, a blast)? | Seen once: `bInstantDeath` is set and the recorder is still named. |
| Chests the recorder never looked at / that were not being replicated? | The chest's whole state arrives with it, including who destroyed it. Seen for the recorder (one Gum-Gum chest) and for others. The camera plays no part. |
| Several chests from one attack? | **Not observed.** No match contains one attack opening or destroying two chests. Each chest is its own actor with its own record, so nothing suggests they would be merged, but this is untested against a known count. |
| Does "channel closed: destroyed" mean a chest was destroyed? | No. In these client-recorded replays every close reason is `Destroyed`, including chests that just went out of range. |
| Chests that vanish with no state at all? | 29 times in 11 matches. They carry no name, no loot record and no time of destruction. In one match four vanish at the same instant while the recorder attacks with the Gum-Gum Fruit: see "The open question". |
| Loot as evidence? | Weak. Pickups appear with a time and position but no reference to their chest; relic chests produce none. Not used for counting. |
| Damage instigator? | Yes for weapons: damage cues name the hit actor and the weapon. No for the Gum-Gum Fruit: its hits produce ability cues that carry a position but not the hit actor. |

## The records

### The chest itself

Every searchable container replicates, on its own actor channel:

- `bAlreadySearched` – turns on when it is opened.
- `SearchingPawn` – the character that opened or destroyed it. Cleared about 80 seconds later.
- `SearchAnimationCount` – goes up with each opening.
- `bDestroyed`, `bHidden`, `bInstantDeath`, `Health`, `MaxHealth` – only sent once they differ from the default.
- `EffectContext` – present when it was destroyed by damage from an ability or effect. Its start decodes (flags,
  then object references) but only names an "effect causer" that was the same object for different players, so
  it is not usable for attribution. Recorded only as "destroyed by ability damage".
- Buried chests add `TimesDug`, `DugUp`, `ChestCurrentLoc`.

Map-placed chests (`Tiered_Chest_Athena_C`, `AlwaysSpawn_NormalChest_C`, `AlwaysSpawn_RareChest_C`) have a stable
name for the whole match. Spawned chests (`B_Athena_BuriedChest_Chest_C`, `B_TieredChest_Relics_C`,
`B_SpritePosters_Booty_Chest_C`) get a new network id every time they come back into range; they are identified
by class and spawn position.

A chest's record reaches the recording client in one of two ways:

1. **Live**: the chest was already being replicated (closed), then its state changes. The time is exact.
2. **With the chest**: the chest was not being replicated; a channel opens with its full current state. This is
   how a chest the player never approached slowly shows up, and also how old openings by other players appear
   when the player walks into an area. An opening by the recorder that arrives this way still names the recorder.

Because of (2), the same opening is delivered again every time the chest comes back into range (31 times in one
match). The counting rules treat state that arrives with a chest already recorded as opened as a re-sighting.

### The recording player

`GameState.RecorderPlayerState` → each `PlayerPawn.PlayerState` → the set of characters the recorder controlled
(one per life). A chest is the recorder's when `SearchingPawn` is one of them. TOKENIFY, rossalv's counter and
LED-N's counter agree on all 48 chests.

### Damage cues

`NetMulticast_Athena_BatchedDamageCues`, a remote call on the attacking character: `HitActor`, `NonPlayerHitActor`,
`SourceWeapon`, `Magnitude`, `bIsFatal`. It names the exact chest for pickaxe and gun hits. Observed: the recorder
hitting a chest twice with a pickaxe (50 damage each) without opening it — correctly not counted.

The Gum-Gum Fruit (internal name `TackyGoofiness`: stretch launch, rocket, grab, falling kick) does **not**
produce these. Its hits produce `NetMulticast_InvokeGameplayCueExecuted_WithParams` bursts whose parameters hold a
position, not the hit actor.

Cues for distant characters arrive in batches every few seconds, so their timestamps are approximate. For the
recorder's own character they are immediate.

### Weapon in hand

`CurrentWeapon` on the character, pointing at a spawned weapon actor whose class is known from its channel.
Used to label events ("holding Gum-Gum Fruit") and for the vanished-chest rule below. Never used to add a chest.

### Chests that vanish

A channel opens for a map-placed chest and closes in the same instant with reason `Destroyed`, with no state. Or
a chest seen closed stops existing with no opened/destroyed state in between. Engine behaviour that matches:
the server tells a client that a map-placed actor no longer exists once the client is within range, whenever it
was actually destroyed. So these mean "this chest is not there", not "this chest was just destroyed", and never
"by you".

They happen whether or not the recorder uses the Gum-Gum Fruit (11 in a match where the recorder never had it).

## The open question

In `season43-01`, at 07:27, six actors of one ship vanish in the same instant with no
state: four `Tiered_Chest_Athena_C`, a `Viking_Ship_Hull_A1_C` and a `Viking_Ship_Rail_A_C`. The recorder is in the
middle of a series of Gum-Gum attacks (impacts at 07:24 and 07:28). Twenty-two seconds later the recorder
destroys a relic chest with the same power and that one is fully recorded and named.

Two readings fit, and the replay cannot tell them apart:

- The ship had already gone (or its chests never spawned) and the client was told when it came into range.
  Supporting this: the hull and rail were never replicated as damaged, which they would be if the recorder had
  been breaking them.
- The attack removed the ship in a way that skips the normal "destroyed" state, taking four real chests with it.

So TOKENIFY lists those four as **ambiguous**, does not count them, and lets the player add them. Settling it
needs one match where a known number of chests is smashed with the Gum-Gum Fruit and counted by hand.

## How TOKENIFY classifies

| Class | Rule | Counted |
|---|---|---|
| Confirmed | The chest's own record shows it opening or breaking and names the recording player. | Yes |
| Corroborated | It opens or breaks in view naming nobody, and the recorder's own damage cue names that chest in the 8 seconds before, and nobody else's does. Never seen in real data; exists so a missing name does not lose a chest the player demonstrably hit. | Yes, flagged |
| Ambiguous | It opens or breaks in view naming nobody without such a cue; or it vanishes with no record while the recorder is attacking with an area weapon, or right after the recorder hit it. | No |
| Unobservable | It vanishes with no record and nothing links it to the recorder. Reported as a number only. | No |

Rules that never apply: nothing is counted from the camera, from proximity alone, from loot alone, from another
player's record, or from a replay that was not read to the end.

## Known limits

- One attack destroying several chests has not been compared with a hand count.
- Timestamps of events far from the recorder can be a few seconds late. The recorder's own are exact.
- Anything the game never sent to the recording client is not in the file. Observed range for chest events was
  at least 80 m live and about 135 m for chests arriving with their state; the Gum-Gum Fruit's reach is far
  shorter than that, but its exact range was not measured.
- The match must finish (or the player must leave it) normally: Fortnite writes the key that makes a replay
  readable only at the end. A crash mid-match leaves a replay that cannot be read, and it is reported as such.
- Replays from versions before container state was replicated this way (seen in 11.11 and 12.00 samples) attribute
  nothing.
- The Gum-Gum Fruit is identified by its internal name. If a later patch renames it, the label and the
  vanished-chest rule stop applying to it until the chest registry is updated; counting is unaffected.
