# PlayerSync with export IPC

**Optional.** Since 0.2 Character Mod Exporter works with the official PlayerSync by reading the applied files from
Penumbra. A PlayerSync build that carries `patches/0001-export-ipc.patch` gives the exporter the complete applied
mapping (including files the game has not loaded yet) and the real sync state; when one is loaded the exporter uses it
automatically. This is an unofficial, personal-use build; it is not endorsed by the PlayerSync team. PlayerSync is MIT licensed (Copyright (c) 2022 Penumbra-Sync); the license is kept in the checkout.

## What the patch changes

| File | Change |
| --- | --- |
| `PlayerData/Handlers/PairHandler.cs` | Keeps an `ExportSnapshot` of the last applied mapping; adds `IsSyncInProgress`. |
| `PlayerData/Pairs/Pair.cs` | `ExportHandler` and `GetFilteredLastReceivedData()` (read-only accessors). |
| `Interop/Ipc/Export/*.cs` | New `ExportIpcProvider` and snapshot record. |
| `Interop/Ipc/Export/Contracts/*.cs` | Copied by `apply.sh` from `src/PlayerSyncExport.Contracts`. |
| `Plugin.cs` | Registers the provider. |
| `UI/EditProfileUi.cs` | `Rectangle` alias so it compiles against Dalamud 15.0.3.9. |
| `MareSempiterne.json` | Description says it is an unofficial export-IPC build; `RepoUrl` filled in. |

It does not change syncing, downloads, the cache, its index, or the configuration.

## IPC (version 2)

| Gate | Signature | Purpose |
| --- | --- | --- |
| `PlayerSync.Export.GetApiVersion` | `Func<int>` | Returns 2. |
| `PlayerSync.Export.GetCharacterState` | `Func<uint, string>` | EntityId to `CharacterStateDto` JSON. Read-only: never downloads, reapplies or writes. |
| `PlayerSync.Export.RequestReapply` | `Func<uint, bool>` | Runs PlayerSync's "reapply last received data". **May download** missing files; the exporter calls it only from its confirmed "Reapply PlayerSync Data" action. |
| `PlayerSync.Export.StartExport` | `Func<string, string>` | `ExportRequestDto` JSON to `ExportStartResultDto` JSON. |
| `PlayerSync.Export.GetExportStatus` | `Func<string, string>` | Job id to `ExportJobStatusDto` JSON. |
| `PlayerSync.Export.CancelExport` | `Func<string, bool>` | Cancels a job. |

Version 2 (exporter 0.6.0.0) replaced `RequestResync` with `RequestReapply`, so a caller cannot mistake it for a
read-only rescan. `CharacterStateDto` reports `HomeWorld` and the ZoneSync joining setting as
`ZoneSyncJoiningGlobal`, `ZoneSyncJoiningOverride` (your own character's entry in `ZoneSyncEnabledPerCharacter`) and
`ZoneSyncJoiningEffective` instead of `ZoneSyncEnabled`; `ExportRequestDto` carries `ExpectedHomeWorld`, and a job
fails when the character's world differs. An exporter and a build of different versions do not talk to each other:
the exporter shows the loaded version as incompatible and reads through Penumbra instead.

`ResourceDto` gained two optional fields in 0.4.0.0 without a version change (older readers ignore them):
`ExcludedGamePaths` (paths PlayerSync's validation removed) and `MissingGamePaths` (paths of an applied resource that
are not in PlayerSync's mapping). For an available resource, `GamePaths` now lists only the paths PlayerSync handed to
Penumbra. Every game path is looked up, not only the first one.

Only files in the requested character's resolved mapping can be exported, only into an existing folder outside
PlayerSync's cache, Penumbra's mod folder, the game installation, Dalamud, `pluginConfigs` and the installed plugins'
folders, also when the path reaches one of them through a symbolic link or junction. The provider runs these checks
itself (the same `ExportPathPolicy` code), before the job and again before each file, so calling the IPC directly does
not bypass them. Every copy is SHA-1 verified, and a job removes only its own temporary files.

## Proposed upstream fix (not applied)

`proposed/0001-mcdf-import-export-hardening.patch` fixes defects in PlayerSync's own MCDF import and export, found in
the 0.6.1 audit:
- On import, it checks the header length, file count, lengths and hashes before anything is applied, and reads every
  file exactly. It also rejects truncated files, data after the last file, invalid game paths and swap targets, and
  conflicting paths.
- On export, it reads each file exactly and logs files left out because they have no local copy.

The MCDF format is unchanged. It applies cleanly to the pinned commit, independently of the export patch, and the
patched PlayerSync builds with both applied. It has not been run in game. `apply.sh` does not apply it, because it
changes how PlayerSync imports files, which is not what this fork is for. It is meant to be offered to upstream.

## Build

```bash
playersync-fork/apply.sh build/PlayerSync          # pinned upstream commit from UPSTREAM + patch
dotnet build build/PlayerSync/PlayerSync/PlayerSync.csproj -c Release
```

Needs the .NET 10 SDK, the .NET 9 runtime (a code generator in the VFXEditor submodule targets it; or set
`DOTNET_ROLL_FORWARD=Major`) and the Dalamud dev assemblies (`DALAMUD_HOME`, or XIVLauncher's
`%AppData%\XIVLauncher\addon\Hooks\dev`). On Linux add `-p:EnableWindowsTargeting=true`.
Output: `build/PlayerSync/PlayerSync/bin/Release/MareSempiterne/latest.zip`.

## Install (replaces PlayerSync)

The build keeps PlayerSync's plugin identity (`MareSempiterne`) so it can never run next to the official one and
keeps your PlayerSync settings and cache. See `docs/INSTALL.md`.

## Updating to a new PlayerSync version

1. Set `commit` and `version` in `UPSTREAM` to the new upstream revision.
2. Run `apply.sh`; if the patch does not apply, resolve it in the checkout and regenerate with
   `git add -A -- . ':!PlayerSync/Interop/Ipc/Export/Contracts'` and then
   `git diff --cached -- . ':!PlayerSync/Interop/Ipc/Export/Contracts' > playersync-fork/patches/0001-export-ipc.patch`.
3. CI builds the patched PlayerSync on every push.

If the patched build is not loaded (for example after an official PlayerSync update), the exporter falls back to
reading through Penumbra.
