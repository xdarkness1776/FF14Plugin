# Capture reliability audit (0.5)

This is the 0.5 audit of how the exporter finds a character, tracks its files and compares them with saved data.
Everything here was checked against source code: PlayerSync 1.15.5.9 (upstream `b2f8ce8`), Penumbra `9bb7931` and
Penumbra.Api 5.15.1. **Nothing in this document has been verified in game yet.** The live checklist at the end of
`MANUAL_TEST_PLAN.md` (rows 30 to 49) is where the in-game results go.

## PlayerSync IPC that exists

PlayerSync registers these, and nothing else, in `IpcProvider`. The `MareSynchronos.*` names are aliases of the
`PlayerSync.*` names.

| Name | Signature | What it does | Use in the exporter |
|---|---|---|---|
| `PlayerSync.LoadMcdf` | `Func<string, IGameObject, bool>` | Starts applying an MCDF to an object in the background and returns true right away. | **Not used.** It applies data to a character, and the exporter never applies anything. |
| `PlayerSync.LoadMcdfAsync` | `Func<string, IGameObject, Task<bool>>` | Same thing, but waits for it to finish. | **Not used**, for the same reason. |
| `PlayerSync.GetHandledAddresses` | `Func<List<nint>>` | The addresses of every object PlayerSync applies data to right now, excluding your own. | Used to decide whether a character is synced. |

What `GetHandledAddresses` returns comes from `GameObjectHandlerCreated`/`Destroyed` messages. It lists only
non-owned handlers whose address is not zero, without duplicates. It includes minions, pets and companions of synced
players, not only the players themselves. The exporter only checks a player character's own address against it.
Addresses are not identities: the list says "something PlayerSync handles is at this address now", nothing more.

There is no IPC for a character's file list, its pairing state or its ZoneSync membership. The patched PlayerSync
(`playersync-fork/`) adds the versioned export IPC for that. Stock PlayerSync is read through Penumbra (below).

## Penumbra calls and their semantics

| Call | Thread | Meaning used by the exporter |
|---|---|---|
| `GetGameObjectResourcePaths(ushort index)` | framework (called from `RunOnFrameworkThread`) | Penumbra's resource tree for the object: the files on the character right now (models, materials, textures, skeleton and physics files). It does not list animations, sounds or VFX that are not part of the tree. Returns null when the object is not a character. |
| `GameObjectResourcePathResolved(nint gameObject, string gamePath, string fullPath)` | any thread the game loads resources on | Raised in `GameStateApi.OnResourceLoaded` and `OnPapRequested` for every resource load that Penumbra associates with a game object (`ResolveData.AssociatedGameObject != 0`). `fullPath` is the redirected path, or the game path when nothing redirects it. It carries an address, not an identity. |
| `RedrawObject(index, Redraw)` | framework | Makes the game load the character's files again. The exporter uses it for **Scan (Redraw)**. |
| `GetModDirectory()` | any | The user's Penumbra mod folder, used to recognise their own mod files. |
| `ApiVersion()` | any | Breaking version 5 is required. Any other version is refused with a message. |

## Identity, addresses and stale data

- A watched character is keyed by its **entity id**. Its address is only used to attribute Penumbra events, and the
  name and (since 0.6) the home world are checked on every read.
- **New in 0.6: a capture that starts over gets a new tracker** (`CharacterWatchList`). Before, the tracker was reset
  in place, so a Penumbra event that had already looked up the old address could still land in the fresh capture.
  Now such an event goes to the discarded tracker. A change of address, name or home world, a despawn, a zone change
  and closing the window all start over this way.
- **Every frame** (0.4.1 checked once a second), the source checks that each watched character is still at the
  address it recorded. When the character despawned or moved, its tracker is reset and the old address stops
  attributing events. So an object that takes over the address can be misattributed for at most one frame, and since
  0.6 only into a capture that is then discarded.
- **New in 0.5: zone and instance changes reset every tracker** (`IClientState.TerritoryChanged` and
  `InstanceChanged`). Every object despawns on a zone change, and the game reuses addresses for the next objects that
  load. The character is found again by entity id on the next read, and its files are recorded afresh.
- **New in 0.5: closing a character's window stops watching it** and drops what was recorded. Before, every character
  ever opened stayed watched (checked every second, events recorded) until the plugin unloaded. Watching is now
  bounded by the open windows. A running export still reads the character on its own.
- Penumbra's tree is read on the framework thread inside the same call that checks the object, so a tree is never
  merged into another character's tracker. Events are matched against a concurrent address table. The tracker locks
  on every update.
- An export session belongs to one target. It checks that the target is present before every operation, and refuses
  when the provider reports another name, or another home world when both sides know it (`TargetIdentity`; a
  missing world is "world unconfirmed", not a match failure, and an export job records that in its notes). The copy step verifies every file's SHA-1 again, so a file
  that changed after it was listed fails rather than being exported under the wrong identity.

## Capture lifecycle

The capture states are the ones the window already shows. The 0.5 tracker adds what each file's state is within them.

| Stage | Where | States and limits |
|---|---|---|
| Discovery | Context menu → `CharacterSelectionService` | Only an object in the object table with a player name. Chat names without a nearby object get no entry. |
| Validation | `Probe`, `GetHandledAddresses` | `Lookup`: NotConnected (Penumbra or PlayerSync missing), NotSynced (address not handled), Found. Penumbra API version checked. |
| Initial snapshot | First `GetGameObjectResourcePaths` | Files recorded as **InitialSnapshot**. |
| Change tracking | Penumbra event | New files recorded as **Event**. Duplicates only refresh `LastSeenUtc`. |
| Reconciliation | Every later tree (each Refresh, after a Scan) | Files in the tree are **Active**. Files that were in an earlier tree but are not in this one, or whose game path now points to another file, become **NoLongerActive**. Files only ever seen through the event stay **ObservedLoaded**, because the tree never lists them. |
| Finalization | `SyncState` | NotApplied, InProgress (something changed in the last 5 seconds), Complete, CompleteWithMissing. Files no longer in use do not count towards either. |
| Export | `ExportCoordinator` | Waits at most `SyncWaitSeconds` for InProgress to end. Cancellable. Every copy is verified. Dispose waits at most 5 seconds for a running job. |

The bounds are a maximum of 20,000 current redirections per character, plus 2,000 remembered replaced ones (the
oldest are dropped first). When the limit is reached, the window says the list may be incomplete.

What changes for the user:

- Files **no longer in use** are listed with that note. They start unselected, and are unselected when they stop
  being used. They are left out of the live library match, of the whole-character package and of the item mods,
  where they would compete with the current file for the same game path. They can still be exported as single files
  on purpose.
- Files **seen loading** are listed with that note and treated like current files.
- `Manifest.json` (now version 2) records `Activity`, `FirstSeenUtc` and `LastSeenUtc` for each asset when the
  source tracks them. The export IPC source does not track them, because PlayerSync's applied mapping is current by
  definition.

## ZoneSync and permissions

ZoneSync is decided entirely by PlayerSync and its server (`GroupZoneSyncManager`). When a zone change ends,
PlayerSync waits `ZoneJoinDelayTime` seconds (default 10, restarted on every further zone change), then sends a join
for the world, territory, ward, house and room. In instanced content the room is the instance and the server is the
data centre. It joins with the user's default group permissions (sounds, animations, VFX). Forbidden territories, and
areas the user disabled (field, residential, town, dungeon, PvP), leave every zone group instead. The exporter does
not influence any of this. It only sees the result: an address in `GetHandledAddresses` and files in Penumbra.

| Case | What PlayerSync does | What the exporter shows | Status |
|---|---|---|---|
| Field zone, field sync on | Joins after the delay | Character listed once PlayerSync applies data | not run |
| Town, town sync on | Joins | Same | not run |
| Residential ward or house, residential sync on | Joins with ward, house and room | Same | not run |
| Duty, dungeon sync on | Joins by instance and data centre | Same | not run |
| PvP, PvP sync on | Joins | Same | not run |
| Area type disabled in ZoneSync settings | Leaves all zone groups | "not synced by PlayerSync"; nothing exported | not run |
| Forbidden territory | Leaves all zone groups | Same | not run |
| Within the join delay | Not joined yet | "not synced" until PlayerSync applies data; Refresh later | not run |
| Character disappears or teleports | Handler destroyed | Tracker reset; export fails with "no longer nearby" | not run |
| Your own zone change | All objects despawn | Every tracker reset (new in 0.5); files recorded again | not run |
| Sounds, animations or VFX disabled for the group or pair | PlayerSync does not apply those files | They are not on the character, so they are not listed | not run |
| Paused pair | Data removed | "not synced" | not run |

The exporter never requests pairing, never joins or leaves a group and never changes a permission.

**Effective joining setting (0.6).** `GroupZoneSyncManager` decides whether to join with
`ZoneSyncEnabledPerCharacter.ContainsKey(PlayerName)`: an entry for **your own** current character's name overrides
`EnableGroupZoneSyncJoining`. The export IPC used to report only the global setting; it now reports the global value,
the override and the effective value (`ZoneSyncSettings.Effective`). The window shows "ZoneSync joining for you",
which is your setting, not whether the other character is synced, nor whether its data is complete: those are the
separate Sync link and Data lines. The stock Penumbra source cannot read PlayerSync's settings and shows "unknown".

## MCDF reading (hardened in 0.5)

The format was verified against `MareCharaFileHeader` and `CharaDataFileHandler`. It is an LZ4 legacy stream
(HighCompression) holding `MCDF`, version byte 1, an int32 header length, the UTF-8 JSON header and then the stored
files in header order. Each entry is `GamePaths`, `Length` (the cache file's size) and `Hash` (SHA-1).

**What PlayerSync's writer leaves out** (`CreatePlayerData`): game paths ending in `.pap`, `.tmb` or `.scd`, and
`.avfx`/`.atex` paths that contain neither `/weapon/` nor `/equipment/`. Entries left with no game path are dropped.
It also writes only the local player's own files, never those of minions, pets or companions. The reader implements
this rule as `McdfWriterRules.IsExcluded`. Because of it, a package with animations or sounds can never be **Exact**
from an MCDF (see below).

Limits and validation:

- Header: at most 32 MB, and it must be a JSON object. Invalid JSON is rejected with a readable reason. Since 0.6.1
  the header is read in 64 KB chunks, so a header length the file does not back up is found after one chunk instead
  of after allocating the full claimed size.
- Entries: at most 100,000. Each must be between 0 and 1 GB, and the total at most 16 GB. A negative or oversized
  length rejects the file, because every later entry's position depends on it.
- LZ4 errors, a missing magic, version ≠ 1 and a file that ends inside the header all become a readable error. The
  original file is never written.
- Contents are streamed and hashed, never stored in memory or extracted.
- After the last entry, up to 64 KB of extra data is looked for and reported as "trailing data". **Policy:** trailing
  data is a warning, not a rejection. It cannot shift any entry, because every entry was already read at its declared
  position and verified by its own hash; it only means the file was modified. (PlayerSync's own importer, with the
  proposed patch below, rejects it, because it applies the file.)
- Every read loops until the declared count is read or the stream ends; one short `Read` never truncates an entry
  (tested with a stream that returns at most 3 bytes per call).

Status of each entry:

| Status | Meaning | Evidence for matching |
|---|---|---|
| Verified | Bytes read, SHA-1 matches the claimed hash | yes |
| HashMismatch | Bytes read, SHA-1 differs | no |
| MissingPayload | The file ends before this entry | no |
| InvalidHash | The claimed hash is not 40 hex digits | no |
| InvalidPath | No valid game path | no |
| ConflictingGamePath | Another entry claims one of its game paths with other content (compared case-insensitively, as Penumbra does) | no |
| EmptyPayload | The entry stores zero bytes; no game file is empty, and PlayerSync's importer stops at it (0.6.1) | no |
| NotRead | Only the header was read | no |

**File swaps (0.6.1).** Each swap now has a status as well. PlayerSync's importer writes stored files and then swaps
into one dictionary keyed by the exact game path (`CharaDataManager.McdfApplyToTarget`, lines 485-490), so a swap for a
path that also has a stored file replaces it, and of two swaps for one path the last one wins.

| Swap status | Meaning | Used for matching |
|---|---|---|
| Valid | Both sides valid game paths, not pointing at itself, same file type | yes |
| Invalid | Invalid path or target, a swap to itself, different file types, or no game path | no |
| OverridesStoredFile | A stored file claims the same path; which one the character would show depends on the importer | no |
| ConflictingTargets | Another swap sends the same path elsewhere | no |

Entries of a kind the writer excludes are counted separately (`WriterExcludedCount`): an MCDF that has them was not
written by PlayerSync as it stands.

Categories stay separate: files and swaps are compared, while Glamourer, Customize+ and manipulation data are only
reported as present or absent. They are never compared or applied.

## Matching evidence (0.5)

The labels and their meaning are unchanged (see `ORIGINAL_PACKAGES.md`). There are still no percentages. Each
comparison now states what it is based on:

| Source | Coverage | Why |
|---|---|---|
| ExportIpc (patched PlayerSync) | Complete | PlayerSync's full applied mapping with original hashes |
| LivePenumbra (stock PlayerSync) | Partial | Only loaded files; textures may be compressed copies |
| McdfSnapshot | Partial | The writer's exclusions, player files only, verified entries only |

Per package, the report now also gives:

- `UniqueMatchedCount`: matched files no other package provides with the same content. Ambiguous means zero.
- `NotCapturableCount`: files of the used options that this evidence can never contain. It is neither for nor
  against the package, but it keeps the package from being Exact, because those files were not compared.

This is alongside the existing matched files, swaps, conflicts, files not seen, possible compressed copies, options
used, shared-with and identical copies. `OriginalPackages.json` (now version 2) records the coverage, the
description of the evidence and the counts for each package.

## Supplemental plugin data

PlayerSync syncs the following besides files (`CharacterData`). None of it reaches the exporter through stock
PlayerSync. Penumbra knows only files and manipulations, and the export IPC sends only whether manipulations exist.

| Plugin | Synced by PlayerSync | Visible to the exporter | Decision |
|---|---|---|---|
| Glamourer | Yes, per object | Only "present" in an MCDF header | Not exported: it is a full appearance state, and applying it is out of scope |
| Customize+ | Yes, per object | Only "present" in an MCDF header | Same |
| Penumbra manipulations | Yes | Flag only (IPC); "present" in an MCDF | Packages say they omit them |
| Heels (Simple Heels) | Yes | No | Not implemented: no reliable read path without PlayerSync's data |
| Honorific | Yes | No | Same |
| Moodles | Yes | No | Same |
| PetNicknames | Yes | No | Same |
| Loci | Yes, per object | No | Same |
| Brio, Stagehand, Lifestream | Called by PlayerSync, not synced character data | No | Not relevant to export |

Exporting any of these would need PlayerSync to hand them over (an addition to the export IPC), and the user would
have to apply them by hand. That is a possible follow-up, not something the stock path can do.

## Remaining limits

- The stock path can only see files the game loaded while the window was open. Animations, VFX and sounds need to
  play, or a Scan.
- Any file in an earlier tree counts as "no longer in use" when the latest tree lacks it. If Penumbra's tree ever
  missed a file for one read, that file would be marked no longer in use until the next tree or the next time it
  loads. It would still be listed, just unselected.
- Event attribution is by address and can be wrong for up to one frame after a despawn; since 0.6 such events land
  only in a capture that is discarded.
- None of this has run in game.
