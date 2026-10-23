# Installing Character Mod Exporter

Character Mod Exporter is an add-on for PlayerSync. You need:

* the official **PlayerSync**, installed and logged in as usual,
* **Penumbra** (PlayerSync needs it anyway),
* **Character Mod Exporter**, from this project's custom repository.

Nothing in PlayerSync has to be replaced or reconfigured.

## Install

1. `/xlsettings` → **Experimental** → **Custom Plugin Repositories**: add
   `https://raw.githubusercontent.com/xdarkness1776/FF14Plugin/main/repo.json`, tick **Enabled**, save.
2. `/xlplugins` → All Plugins → search **Character Mod Exporter** → Install.
3. Later releases show up as updates in `/xlplugins`.

Every GitHub release also has `CharacterModExporter.zip` attached (the Release workflow builds, tests and attaches it
when a tag `v<version>` is pushed, or when a release with a new tag `v<version>` is published from GitHub's Releases
page). CI runs keep it as the `packages` artifact.

## Use it

1. Have ZoneSync (or a syncshell / pair) active in PlayerSync, and let the other character load.
2. Right-click the character → **Export Character Mods**.
3. The window lists the files PlayerSync applied to that character, as Penumbra reports them:
   * Only files the game has loaded are listed. Animations, VFX and sounds are added once they play while the window
     is open (press **Refresh Character Resources** to update the list).
   * **Scan Character Resources** redraws the character so the game loads its gear again. The exporter never
     downloads anything; files PlayerSync has not downloaded yet cannot be listed.
   * Only files from PlayerSync's cache folder (read from PlayerSync's settings, never changed) or from your own
     Penumbra mod folder are offered.
4. Pick files (all available files are preselected) → **Export Selected**. The window waits until no new files have
   appeared for about 5 seconds, copies and verifies the files, and writes `Manifest.json` and `README.txt`.
5. **Open Character Folder** shows the result:

```
<parent>\FFXIV Character Mod Exports\<Name>_<World>\
    Assets\<sha1>.<ext>          raw game resources, PlayerSync cache names
    Installable\<Name> (exported).pmp   only when every listed file was exported
    Manifest.json
    README.txt
```

6. To use a `.pmp`: Penumbra → Mods → Import Mod → pick the file → enable it in a collection. Nothing is
   installed or enabled automatically. Because only loaded files are listed, a package may miss emotes or effects
   that never played; meta manipulations are never included.

Settings (`/cmexport` or the cog in `/xlplugins`): parent folder (folder picker or typed path, validated),
export folder name, reuse vs. timestamped folders, shared pool for files used by several characters, building the
Penumbra package, wait time, and whether the menu entry shows only on PlayerSync-synced characters.

## Optional: PlayerSync build with the export IPC

`playersync-fork/` still contains a small PlayerSync patch that exposes the complete applied mapping and the real
sync state over IPC. It is not needed. If a PlayerSync build with that IPC is loaded, the exporter uses it
automatically (the window's Source line says which source is in use). Release zips only include it when the
repository variable `PUBLISH_PATCHED_PLAYERSYNC` is `true`; see `playersync-fork/README.md`.
