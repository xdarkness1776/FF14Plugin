# FF14Plugin: Character Mod Exporter

A Dalamud plugin for FINAL FANTASY XIV. Right-click another player that PlayerSync is syncing (through ZoneSync,
a syncshell or a direct pair) and choose **Export Character Mods**. The exporter asks PlayerSync for the
character's live, already-resolved resource mapping, waits until PlayerSync has finished synchronizing that
character, and copies those files from PlayerSync's cache into a folder you choose, together with
`Manifest.json`, `README.txt` and, when the mapping is complete, a Penumbra mod package (`.pmp`) for manual import.

**It requires a patched PlayerSync build.** Stock PlayerSync has no IPC that exposes a character's resolved files
or sync state (see [docs/PLAYERSYNC_AUDIT.md](docs/PLAYERSYNC_AUDIT.md)). [playersync-fork/](playersync-fork/)
contains a small, versioned export IPC patch for PlayerSync 1.15.5.9. The patched build is unofficial and not
endorsed by the PlayerSync team.

What it does not do: it never downloads anything itself, never pairs with anyone, never scans other characters,
never writes to PlayerSync's cache or settings, and never installs or enables mods. Missing files are only ever
fetched by PlayerSync's own pipeline when you press **Scan Character Resources**.

## Status

| Area | State |
| --- | --- |
| Exporter plugin, Release build, DalamudPackager zip (API 15) | builds |
| Patched PlayerSync (Release) | builds from `playersync-fork/apply.sh` |
| Automated tests (76) | pass: path safety, verified copying, cache preservation, mapping, waiting for sync, cancellation, folders, manifest, `.pmp` validated with Penumbra's own path parser |
| In-game behaviour | **not tested yet** (no game client in the development environment); see [docs/MANUAL_TEST_PLAN.md](docs/MANUAL_TEST_PLAN.md) |
| `.pmp` import into Penumbra | structure validated, real import not tested yet |

## Docs

* [docs/INSTALL.md](docs/INSTALL.md): install, usage and output layout
* [docs/PLAYERSYNC_AUDIT.md](docs/PLAYERSYNC_AUDIT.md): how PlayerSync's ZoneSync pipeline works and why the IPC is needed
* [playersync-fork/README.md](playersync-fork/README.md): the patch, the IPC contract, rebuilding for new PlayerSync versions
* [docs/MANUAL_TEST_PLAN.md](docs/MANUAL_TEST_PLAN.md): in-game acceptance tests

## Layout

```
src/CharacterModExporter/        Dalamud plugin: context menu, export window, settings
src/CharacterModExporter.Core/   game-independent logic: folders, inventory, export flow, manifest, .pmp builder
src/PlayerSyncExport.Contracts/  IPC names and DTOs, path rules, verified copier, job runner (also compiled into the patched PlayerSync)
tests/                           xUnit tests (+ Penumbra path compatibility tests)
playersync-fork/                 pinned upstream revision, patch, apply script
tools/                           package and repo.json checks used by CI
```

## Build

```bash
dotnet build FF14Plugin.slnx -c Release          # add -p:EnableWindowsTargeting=true on Linux/macOS
dotnet test tests/CharacterModExporter.Tests -c Release
```

Needs the .NET 10 SDK and the Dalamud dev assemblies (XIVLauncher's `%AppData%\XIVLauncher\addon\Hooks\dev`, or
`DALAMUD_HOME`). The version lives in `Directory.Build.props` (`ExporterVersion`); pushing the tag
`v<ExporterVersion>` runs the Release workflow.

## Note on content

Exported files are other creators' work. Keep exports for personal use unless the creators' terms allow sharing.
