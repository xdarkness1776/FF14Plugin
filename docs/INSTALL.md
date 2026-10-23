# Installing Character Mod Exporter

Two plugins are needed:

1. **PlayerSync with export IPC** (`PlayerSync-ExportIPC.zip`, built from `playersync-fork/`). It replaces your
   normal PlayerSync and is loaded as a dev plugin (it is deliberately not listed in `repo.json`, because it uses
   the same plugin identity as official PlayerSync).
2. **Character Mod Exporter**, installed from this project's custom repository.

Both zips are attached to every GitHub release of this repository (the Release workflow builds, tests and attaches
them when a tag `v<version>` is pushed, or when a release with a new tag `v<version>` is published from GitHub's
Releases page). CI runs also keep them as the `packages` artifact.

## 1. Patched PlayerSync (dev plugin)

1. Download `PlayerSync-ExportIPC.zip` from the latest release and extract it, for example to
   `%AppData%\XIVLauncher\devPlugins\MareSempiterne\`.
2. Launch FFXIV through XIVLauncher with Dalamud enabled.
3. `/xlplugins` → Installed Plugins → **disable** the official PlayerSync (both use the identity `MareSempiterne`
   and cannot load together). Your PlayerSync settings and cache stay where they are.
4. `/xlsettings` → **Experimental** → **Dev Plugin Locations**: add the full path of `MareSempiterne.dll`, press
   **+**, save.
5. `/xlplugins` → Dev Tools → enable it. `/xllog` shows `Export IPC v1 registered`.

To go back to stock PlayerSync: disable the dev build, remove its Dev Plugin Location, re-enable PlayerSync.

## 2. Character Mod Exporter (custom repository)

1. `/xlsettings` → **Experimental** → **Custom Plugin Repositories**: add
   `https://raw.githubusercontent.com/xdarkness1776/FF14Plugin/main/repo.json`, tick **Enabled**, save.
   (This file is created by the first release; until then it returns 404.)
2. `/xlplugins` → All Plugins → search **Character Mod Exporter** → Install.
3. Later releases show up as updates in `/xlplugins`.

Alternatively load `CharacterModExporter.dll` from the release zip as a dev plugin, like step 1.

## Use it

1. Have ZoneSync (or a syncshell / pair) active in PlayerSync, and let the other character load.
2. Right-click the character → **Export Character Mods**.
3. In the window: **Refresh Character Resources** reads the current mapping; **Scan Character Resources** asks
   PlayerSync to re-run its normal sync for that character (cached files reused, only missing ones downloaded,
   the character is redrawn).
4. Pick files (all available files are preselected) → **Export Selected**. The window waits for PlayerSync to
   finish synchronizing, copies and verifies the files, and writes `Manifest.json` and `README.txt`.
5. **Open Character Folder** shows the result:

```
<parent>\FFXIV Character Mod Exports\<Name>_<World>\
    Assets\<sha1>.<ext>          raw game resources, PlayerSync cache names
    Installable\<Name> (exported).pmp   only when every resource was exported
    Manifest.json
    README.txt
```

6. To use a `.pmp`: Penumbra → Mods → Import Mod → pick the file → enable it in a collection. Nothing is
   installed or enabled automatically.

Settings (`/cmexport` or the cog in `/xlplugins`): parent folder (folder picker or typed path, validated),
export folder name, reuse vs. timestamped folders, shared pool for files used by several characters, building the
Penumbra package, sync wait time, and whether the menu entry shows only on PlayerSync-synced characters.
