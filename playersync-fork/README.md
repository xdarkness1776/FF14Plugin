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

## IPC (version 1)

| Gate | Signature | Purpose |
| --- | --- | --- |
| `PlayerSync.Export.GetApiVersion` | `Func<int>` | Returns 1. |
| `PlayerSync.Export.GetCharacterState` | `Func<uint, string>` | EntityId to `CharacterStateDto` JSON. |
| `PlayerSync.Export.RequestResync` | `Func<uint, bool>` | Runs PlayerSync's "reapply last received data". |
| `PlayerSync.Export.StartExport` | `Func<string, string>` | `ExportRequestDto` JSON to `ExportStartResultDto` JSON. |
| `PlayerSync.Export.GetExportStatus` | `Func<string, string>` | Job id to `ExportJobStatusDto` JSON. |
| `PlayerSync.Export.CancelExport` | `Func<string, bool>` | Cancels a job. |

Only files in the requested character's resolved mapping can be exported, only into an existing folder outside
PlayerSync's cache, Penumbra's mod folder and the Dalamud plugin folders, and every copy is SHA-1 verified.

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
   `git diff -- PlayerSync ':!PlayerSync/Interop/Ipc/Export/Contracts' > playersync-fork/patches/0001-export-ipc.patch`
   (add new files with `git add -N` first).
3. CI builds the patched PlayerSync on every push.

If the patched build is not loaded (for example after an official PlayerSync update), the exporter falls back to
reading through Penumbra.
