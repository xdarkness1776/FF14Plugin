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
