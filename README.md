# FF14Plugin: Character Mod Exporter

A Dalamud add-on for PlayerSync in FINAL FANTASY XIV. Right-click another player that PlayerSync is syncing (through
ZoneSync, a syncshell or a direct pair) and choose **Export Character Mods**. The exporter asks Penumbra which files
PlayerSync applied to that character, keeps the ones from PlayerSync's cache (or your own Penumbra mod folder), waits
until loading has settled, and copies them into a folder you choose, together with `Manifest.json`, `README.txt` and,
when the set is complete, a Penumbra mod package (`.pmp`) for manual import. Each item (gear piece, hairstyle,
animations, ...) is also written as its own `.pmp` under `Mods\<Category>\`, with the races and body type it was made for.

**Finds your original packages.** Point the exporter at the folder with your own numbered `.pmp` files and it shows
which of them the character's files come from (matched by SHA-1 content hashes, with explicit statuses), then copies
the chosen originals unchanged and SHA-256 verified. See [docs/ORIGINAL_PACKAGES.md](docs/ORIGINAL_PACKAGES.md).
The rebuilt per-item mods are reconstructions and are labelled as such.

**Works with the official PlayerSync.** Nothing in PlayerSync is replaced or changed. Stock PlayerSync has no IPC
for a character's files (see [docs/PLAYERSYNC_AUDIT.md](docs/PLAYERSYNC_AUDIT.md)), but it applies them through a
Penumbra temporary collection, and Penumbra reports which local file stands behind each game path the character
loads. The limit: only files the game has loaded are listed, so animations, VFX and sounds appear once they have
played, and "finished" means no new files appeared for a few seconds.

What it does not do: it never downloads anything, never pairs with anyone, never scans the whole cache, never writes
to PlayerSync's cache or settings, and never installs or enables mods.

[playersync-fork/](playersync-fork/) keeps an optional PlayerSync patch with a versioned export IPC (complete mapping,
real sync state). If a PlayerSync build with it is loaded, the exporter uses it automatically. It is unofficial,
not endorsed by the PlayerSync team, and not required.

## Status

| Area | State |
| --- | --- |
| Exporter plugin, Release build, DalamudPackager zip (API 15) | builds |
| Automated tests | pass: path safety, verified copying, cache preservation, Penumbra source (path handling, settling, filtering), waiting, cancellation, folders, manifest, `.pmp` validated with Penumbra's own path parser |
| Optional patched PlayerSync (Release) | builds from `playersync-fork/apply.sh` |
| In-game behaviour | version 0.1 loaded in game and opened the export window from the context menu; the Penumbra source (0.2) is **not tested in game yet**; see [docs/MANUAL_TEST_PLAN.md](docs/MANUAL_TEST_PLAN.md) |
| `.pmp` import into Penumbra (full and per item) | structure validated, real import not tested yet |
| Original package library, matching, original export, MCDF reader | unit tested (synthetic packages for every group type, a real 156 MB package); **not tested in game yet** |

## Docs

* [docs/INSTALL.md](docs/INSTALL.md): install, usage and output layout
* [docs/ORIGINAL_PACKAGES.md](docs/ORIGINAL_PACKAGES.md): package library, matching statuses, exporting originals, categories, MCDF, Penumbra and Snappy notes
* [docs/PLAYERSYNC_AUDIT.md](docs/PLAYERSYNC_AUDIT.md): how PlayerSync's ZoneSync pipeline works and why the IPC is needed
* [playersync-fork/README.md](playersync-fork/README.md): the optional patch, the IPC contract, rebuilding for new PlayerSync versions
* [docs/MANUAL_TEST_PLAN.md](docs/MANUAL_TEST_PLAN.md): in-game acceptance tests

## Layout

```
src/CharacterModExporter/        Dalamud plugin: context menu, export window, settings
src/CharacterModExporter.Core/   game-independent logic: Penumbra resource tracking, folders, inventory, export flow, manifest, .pmp builder;
                                 Library/: .pmp catalogue and index, matcher, original export, categories, MCDF reader
src/PlayerSyncExport.Contracts/  DTOs, path rules, verified copier, job runner (also compiled into the optional patched PlayerSync)
tests/                           xUnit tests (+ Penumbra path compatibility tests)
playersync-fork/                 optional: pinned upstream revision, export IPC patch, apply script
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
