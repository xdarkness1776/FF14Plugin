# PlayerSync source audit

Audited revision: `universalconquistador/MareSynchronosClient` @ `b2f8ce894edbaafda6fbda3c84b5561b878e513c`
(PlayerSync 1.15.5.9, Dalamud.NET.Sdk 15.0.0, `net10.0-windows7.0`). Line numbers refer to that revision.

## How a ZoneSync character gets its mods

1. **ZoneSync is a server-managed syncshell.** `PlayerData/Pairs/GroupZoneSyncManager.cs` only sends the
   player's location (`GroupZoneJoin`, line 208) after a debounce, and leaves when the area is forbidden
   (`IsTerritoryForbidden`, line 142) or the user's area toggles (field/town/residential/dungeon/PvP) are off.
   The server answers with a group whose GID starts with `Constants.GroupZoneSyncPrefix` (`"ZONE"`). Everyone in
   it becomes an ordinary `Pair` through group membership, so no direct pairing is involved.
   `Pair.IsZoneSyncOnlyPair` (`Pair.cs:52`) is true when every shared group is a ZoneSync group.
2. **Character data arrives per pair.** `Pair.ApplyDataAsync` stores `LastReceivedCharacterData` and calls
   `ApplyLastReceivedDataAsync` (`Pair.cs:180`), which runs `RemoveNotSyncedFiles` (`Pair.cs:306`): the user's
   and the other player's sound/animation/VFX permissions, global filters, and the ZoneSync-specific filters
   remove entries before anything is resolved. Files removed here are never downloaded.
3. **`PairHandler.ApplyCharacterDataAsync` (`PairHandler.cs:238`)** defers in combat, cutscenes, GPose, when the
   character object is invalid, or when the data hash is unchanged.
4. **`DownloadAndApplyCharacterAsync` (`PairHandler.cs:366`)** calls `TryCalculateModdedDictionary` (line 740),
   which for every `FileReplacementData` looks up `FileCacheManager.GetFileCacheByHash(hash)`; present files go
   into `moddedPaths[(gamePath, hash)] = localPath`, missing ones are returned. Compressed alternate textures may
   replace the source hash (`ActiveCompressionRedirects`). Missing hashes go to
   `FileDownloadManager.InitiateDownloadList` (line 403) and `DownloadFiles` (line 417). The loop retries up to 10
   times (line 379) and stops early when every remaining hash is a forbidden transfer (line 431).
5. **`ApplyDataApplicationAsync` (`PairHandler.cs:490`)** runs PlayerSync's file validation (crash-risk files are
   removed from `moddedPaths`), then `Penumbra.SetTemporaryModsAsync` (line 574) with the final mapping, then
   Glamourer/Customize+/etc. `_cachedData = charaData` (line 603) is the only "application finished" signal.

**Conclusion:** the authoritative live mapping for a character is the `moddedPaths` dictionary handed to Penumbra
in step 5. It exists only as a local variable; nothing stores it, and there is no completion flag beyond
`_cachedData` and the private download/application tasks.

## Cache

* Folder: `MareConfig.CacheFolder` (user-configured), index in `<plugin config>/FileCache.csv` (`FileCacheManager.cs:42`).
* File name: `GetCacheFilePath` (line 179) = `hash.ToLowerInvariant() + "." + extension`, extension taken from the
  first game path. Downloads are written to `<hash>.bin`, LZ4-unwrapped, and checked by `PersistFileToStorage`
  (`FileDownloadManager.cs:537`), which deletes the file if its hash differs.
* Hash: `Crypto.GetFileHash` (`Utils/Crypto.cs:14`) is **SHA-1 of the file content**, uppercase hex. So the
  40-character names (for example `00cb686fd0b07b186eadca644610f945355e5f3c.avfx`) are verified SHA-1 content hashes;
  the exporter re-checks SHA-1 after copying and records SHA-256 separately. (The supplied example files were
  not available in the workspace, so only their names were used.)
* Lookups mutate the index: `GetFileCacheByHash` (line 192) calls `Validate` (line 474), which removes entries for
  missing files and rehashes changed ones; `MigrateFileHashToExtension` (line 396) renames extension-less files.
  **The export IPC never calls these.** It reads the paths PlayerSync already resolved during application and only
  opens files for reading (`FileShare.Read`).
* A hash may resolve to a file in the user's own Penumbra mod folder (entries with the `{penumbra}` prefix win in
  `GetFileCacheByHash`). The export reports this as `SourceKind = PenumbraModDirectory`.

## Saved-character (MCDF) path is different

`Services/CharaData/CharaDataFileHandler.SaveCharaFileAsync` (line 259) builds an MCDF from `CreatePlayerData()`
(line 80), which reads the **local player** through PlayerSync's own data factory. `CharaDataManager` and
`IpcProvider`'s `LoadMcdf` only load MCDF files onto a target. None of this exports a remote character.

## Existing IPC

`Interop/Ipc/IpcProvider.cs` registers only: `MareSynchronos.LoadMcdf`/`PlayerSync.LoadMcdf` (+Async) and
`MareSynchronos.GetHandledAddresses`/`PlayerSync.GetHandledAddresses` (lines 52-62). There is no IPC for a
character's resource mapping, sync completion, or cache file resolution. Option A is therefore not possible.

## Context menu

`PlayerData/Handlers/PairContextMenuHandler.cs` subscribes `IContextMenu.OnMenuOpened` and matches the pair with
`MenuTargetDefault.TargetObjectId == pair.PlayerCharacterId` (line 114), the game object's EntityId. The exporter
uses the same identity (EntityId captured from `MenuTargetDefault.TargetObject`) and the same lookup on the
PlayerSync side.

## Decision: Option B, a minimal versioned export IPC

See `playersync-fork/README.md`. The patch (about 260 added lines, nothing removed):

* `PairHandler`: after a successful application, keeps a read-only `ExportSnapshot` (applied data, the final
  `moddedPaths`, compression redirects, validation exclusions) and exposes `IsSyncInProgress`.
* `Pair`: exposes the handler and the filtered last received data (same filter PlayerSync applies).
* `ExportIpcProvider`: registers `PlayerSync.Export.*` gates (version, state, resync, start/status/cancel export).
  Validation, waiting and copying are in the shared `PlayerSyncExport.Contracts` code, which is unit tested.
* Resync uses `Pair.ApplyLastReceivedData(forced: true)`, the same call as PlayerSync's "Reapply last data" menu
  item, so cached files are reused and only missing files go through PlayerSync's download manager.
* `EditProfileUi.cs`: one `using` alias, because the current Dalamud 15.0.3.9 assemblies add an ambiguous
  `Rectangle` type and upstream does not compile against them otherwise.

Option C (exporter inside the fork) was not chosen: it would mix a large UI into a plugin that must be rebased on
every PlayerSync update, while the IPC keeps the fork diff small and the exporter independently versioned.

## Update (0.2): working with stock PlayerSync

Replacing PlayerSync with a patched build is not acceptable for an add-on, so the exporter now reads what stock
PlayerSync applied from Penumbra instead:

* PlayerSync applies `moddedPaths` through a Penumbra temporary collection assigned to the character (step 5 above).
  Penumbra's `GetGameObjectResourcePaths` (the same call PlayerSync uses for the local player) returns local file to
  game paths for everything the character currently has loaded, and its `GameObjectResourcePathResolved` event reports
  each further redirection (animations, VFX, sounds) as it loads.
* `PlayerSync.GetHandledAddresses` (stock IPC) confirms the character is handled by PlayerSync.
* The cache folder is read from PlayerSync's `pluginConfigs/MareSempiterne/config.json` (`CacheFolder`), read-only.
  Files there are named by their SHA-1, which the copy verifies; files from the user's own Penumbra mod folder are
  hashed by the exporter. Anything else is not offered.
* Stock PlayerSync has no completion signal, so the state counts as settled once no new file appeared for 5 seconds.
  "Scan" uses Penumbra's `RedrawObject`, which makes the game reload the character's resources; it downloads nothing.
* Limits: resources that never loaded (an emote that never played) are not visible; meta manipulations are not
  available (as before); file swaps resolve to game paths and are not listed.

The export IPC patch stays available as an optional source with the complete mapping and the real sync state.

## Update (0.5): capture reliability

See [CAPTURE_RELIABILITY.md](CAPTURE_RELIABILITY.md). It covers the PlayerSync IPC that exists (`LoadMcdf`,
`LoadMcdfAsync`, `GetHandledAddresses`), Penumbra's event semantics, identity and zone-change handling, how each
file's activity is tracked, the ZoneSync matrix, MCDF validation and the supplemental plugin data. Upstream is still
`b2f8ce8`.

## Update (0.4): re-audit for original-package matching

Upstream `origin/main` was fetched again on 2026-10-24 and is still `b2f8ce894edbaafda6fbda3c84b5561b878e513c`, so the
pinned revision is the latest one. Findings that matter for matching and for the export IPC:

* **Filters (`Pair.RemoveNotSyncedFiles`).** An entry is dropped when *any* of its game paths ends in `scd` (sounds),
  `tmb`/`pap` (animations) or `atex`/`avfx` (VFX), according to the pair's and your own permissions, the global
  filters (unless the UID/alias is in `UIDsToOverrideFilter`, or the pair is a bidirectional direct pair and
  `DoFilteringBidirectionDirectPairs` is off) and the ZoneSync filters for ZoneSync-only pairs. Minion/mount and
  pet/companion objects can be emptied completely. Filtered files are never downloaded, so they can never be
  matched; the export IPC's `GetFilteredLastReceivedData` applies the same function.
* **Replacements vs swaps (`TryCalculateModdedDictionary`).** Entries with a hash become `(gamePath, hash) → local
  file` for every game path; entries with a `FileSwapPath` become `(gamePath, null) → target game path`.
* **Compressed alternates.** `CompressedAlternateUsage` is `AlwaysSourceQuality` (never substitute),
  `CompressedNewDownloads` (substitute only when the original is not cached) or `AlwaysCompressed`. A substitution
  records `ActiveCompressionRedirects[sourceHash] = alternateHash` and keys the mapping by the alternate hash. The map
  is cleared at the start of each download pass. The IPC therefore reports both hashes, and matching uses the source
  hash.
* **Validation (`ApplyDataApplicationAsync`).** With `EnableValidationChecks`, every `(gamePath, hash)` entry is
  checked on its own: crash-level files are removed for that path, and swaps whose target belongs to an expansion that
  is not installed are removed. A resource can thus survive for some of its game paths and not for others.
* **Deferrals and freshness.** Application is deferred in combat, cutscenes and GPose, for invalid objects and for an
  unchanged data hash; the IPC's snapshot is replaced only after an application finished, and only changes its file
  mapping when the files were re-applied (`updateModdedPaths`).

### Resolver defects fixed in 0.4

`ResourceResolver.Resolve` looked up only the **first** game path of each resource. If PlayerSync's validation
removed that one path, or the first path was absent from the mapping, the whole resource was reported as missing or
excluded even though other paths were applied; validation exclusions of file swaps were not reported at all, and
lookups depended on the case of hashes and paths. It now checks every path (case-insensitively), lists only the
applied paths in `GamePaths`, reports dropped ones in `ExcludedGamePaths` / `MissingGamePaths`, reports a swap that
validation removed as `ExcludedByValidation`, and recognizes exclusions recorded under either the source or the
compressed hash. Regression tests: `ResourceResolverTests` (multi-path, missing first path, per-path validation,
redirect on every path, case, swaps, empty path list). The stock Penumbra source and the read-only resync are unchanged.
