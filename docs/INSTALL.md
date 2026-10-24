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
   * Only modded files are offered. Mods often ship unchanged copies of game files, and PlayerSync syncs those too;
     a file whose bytes are identical to the game's own file at every path it replaces is left out (the window's
     notes say how many).
4. Pick files (all available files are preselected) → **Export Selected**. The window waits until no new files have
   appeared for about 5 seconds, copies and verifies the files, and writes `Manifest.json` and `README.txt`.
5. **Open Character Folder** shows the result:

```
<parent>\FFXIV Character Mod Exports\<Name>_<World>\
    Assets\<sha1>.<ext>          raw game resources, PlayerSync cache names
    Mods\<Group>\<Category>\<Item> (<supports>) [<Name>].pmp   one Penumbra mod per item
    Installable\<Name> (exported).pmp   the whole character, only when every listed file was exported
    Original packages\...\<your file>.pmp   your original packages, copied unchanged (only when you export them)
    Manifest.json
    README.txt
```

6. `Mods\` holds one mod per item, sorted into category folders:

   | Folder | What goes there |
   | --- | --- |
   | `Gear\Head`, `Body`, `Hands`, `Legs`, `Feet` (`Other gear`) | one mod per gear piece, with its skeleton/physics files |
   | `Accessories\Earrings`, `Necklace`, `Bracelets`, `Rings` | one mod per accessory |
   | `Weapons` | one mod per weapon model |
   | `Character\Body` | body mods (Bibo+, Gen3, TBSE...), with the base skeleton |
   | `Character\Hair`, `Face`, `Ears`, `Tail` | one mod per hairstyle, face, ears or tail, with its physics |
   | `Character\Eyes`, `Face paint`, `Skin` | eye, face paint and shared skin textures |
   | `Animations\Emotes` | one mod per emote (start, loop and end together) |
   | `Animations\Idle and movement`, `Battle animations`, `Other animations` | one mod per kind |
   | `VFX`, `Sounds`, `Minions and mounts` | |

   Files stay with the mod that uses them. Materials name their textures, animation timelines (`.tmb`) and
   animations name their effects and sounds, and effects name their textures. The exporter reads those names, so an
   emote keeps its VFX and sounds, gear keeps its own effects, custom textures stay with their material, and the
   body deformer (`human.pbd`) goes with body mods. Only effects and sounds nothing refers to end up in `VFX` and `Sounds`.

   Only real mods get a folder: an item needs a model, material, texture, skeleton or physics file; animations a `.pap` or `.tmb`; VFX an
   `.avfx` or `.atex`; sounds an `.scd`. Files that are not a mod on their own (shaders, loose support files) get no
   mod and no category; the manifest lists them, and they stay in `Assets` and the full-character package.
   * Gear is named after the game item it replaces when the item list knows the model, e.g.
     `Body - <item name> (e0123)`. PlayerSync does not pass on the original mod names, so the
     mods are split by game item instead.
   * Gear, hair and body mods say what they were made for: the races and genders found in the model and texture
     paths, and the body type (Bibo+, Gen3, TBSE, HRBody, YAB, Tight&Firm) when the paths show it. The same appears in
     the mod's description and as Penumbra tags. A mod made for another race or body may not fit yours.
   * An item whose files were not all exported gets no mod (the manifest says which). Exporting the same character
     again keeps identical item mods instead of adding copies. Settings can turn item mods off.
7. To use a `.pmp`: Penumbra → Mods → Import Mod → pick the file → enable it in a collection. Nothing is
   installed or enabled automatically. Because only loaded files are listed, a package may miss emotes or effects
   that never played; meta manipulations are never included.

8. **Original Packages** (button in the export window): if you keep your original mods as `.pmp` files, choose that
   folder under Settings → Original Package Library and index it once. Then **Match Live Character** lists the
   packages the character's files come from (Exact, Strong, Partial, Ambiguous), and **Export Selected Originals**
   copies them unchanged into `Original packages\` next to the other exports, with `OriginalPackages.json`.
   **Open MCDF Snapshot...** compares a saved `.mcdf` file instead, without applying it. Details:
   [ORIGINAL_PACKAGES.md](ORIGINAL_PACKAGES.md).

Settings (`/cmexport` or the cog in `/xlplugins`): parent folder (folder picker or typed path, validated),
export folder name, reuse vs. timestamped folders, shared pool for files used by several characters, building the
Penumbra package, wait time, and whether the menu entry shows only on PlayerSync-synced characters.

## Optional: PlayerSync build with the export IPC

`playersync-fork/` still contains a small PlayerSync patch that exposes the complete applied mapping and the real
sync state over IPC. It is not needed. If a PlayerSync build with that IPC is loaded, the exporter uses it
automatically (the window's Source line says which source is in use). Release zips only include it when the
repository variable `PUBLISH_PATCHED_PLAYERSYNC` is `true`; see `playersync-fork/README.md`.
