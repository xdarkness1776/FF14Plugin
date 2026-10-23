# Manual acceptance test plan

None of these have been run yet: the development environment has no game client, Dalamud runtime or PlayerSync
server. Use two test characters you control (A runs the exporter, B is the remote character) in a ZoneSync-enabled
area (for example a town), with no direct pair between them. Record pass/fail and `/xllog` excerpts.

| # | Check | Steps | Expected |
|---|---|---|---|
| 1 | Correct character | Right-click B in a crowd; also right-click B's name in chat | Window title and Character line show B, not your target. Chat names without a nearby object get no entry. |
| 2 | No direct pairing | Before/after export, PlayerSync main UI | No new direct pair or pair request. Source line reads "Official PlayerSync, files read through Penumbra". |
| 3 | Live mapping | B wears a mod; open window | Game paths match B's gear; they match Penumbra's On-Screen tab for B (files from PlayerSync's cache). |
| 3a | Transient files | B uses a modded emote while the window is open, then Refresh | The emote's animation/VFX files are added to the list. |
| 4 | Waits for loading | B switches gear, immediately Export Selected | Phase "Waiting"; the export starts once no new files appeared for about 5 seconds. |
| 5 | Scan | Press Scan Character Resources | B is redrawn; state shows "still loading" and then "loaded"; the exporter makes no network requests. |
| 6 | Cache reuse | Export B twice; Scan in between | Second export: AlreadyPresentIdentical; PlayerSync log shows no download for cached hashes. |
| 7 | Only B's files | Export B with C nearby | Every Assets file is in B's manifest with game paths; none of C's. |
| 8 | Cache untouched | Hash/list PlayerSync cache, FileCache.csv and PlayerSync's config.json before and after an export and a Scan | Identical. |
| 9 | Custom parent | Settings → Choose Parent Folder `D:\My FFXIV Mods` → Apply | Export creates `D:\My FFXIV Mods\FFXIV Character Mod Exports\B_World\...`. |
| 10 | Auto-creation | Delete the export root, export | Root, character and Assets folders are created. |
| 11 | Persistence | Restart game | Settings show the chosen folder. |
| 12 | Unavailable folder | Unplug/rename the drive folder, open Settings, export | Red warning; export fails with message; nothing written elsewhere. |
| 13 | Existing files intact | Put `notes.txt` and a same-named different file in Assets, export | Both untouched; the conflict is reported, Retry works after removing it. |
| 14 | No false success | Pause B in PlayerSync, then export | "not synced by PlayerSync"; nothing exported; no package. |
| 15 | Raw vs package | Open README.txt | Assets described as individual resources; package listed only if built. |
| 16 | Package import | Penumbra → Import Mod the .pmp; enable on A in a test collection | Import succeeds; items look like B's (minus meta manipulations). |
| 16a | Item mods | Open `Mods\` after an export; import one gear `.pmp` and one hair `.pmp` | One folder per category; names show the item and what it supports; each imports and changes only that item. |
| 16b | Supports | Export a character wearing a body mod made for Bibo+/Gen3 | Body mod name and description name the body type and races; tags show them in Penumbra. |
| 16c | Only modded files | Export a character wearing a mod that ships unchanged game files | Notes say N files are identical to the game's own; those are not in Assets or any .pmp. |
| 16d | Categories | Export a character with gear, a hairstyle and a custom emote | `Mods\Gear\...`, `Mods\Character\Hair`, `Mods\Animations\Emotes\Emote - <name>`; no `Other files` mod; manifest lists files that are not a mod. |
| 16e | Linked files | Export a character using a custom emote with effects and sounds | The emote's .pmp contains its .pap/.tmb, .avfx, .atex and .scd; Penumbra shows it working on import. |
| 17 | Leaving / despawn | Start export, B teleports away | Job fails "no longer available"; window still usable. |
| 18 | Cancel | Export a large character, Cancel Export | Phase Cancelled; no `.cmx-partial` files left. |
| 19 | Unload | `/xlplugins` disable exporter mid-export | No exception spam; partial files cleaned. |
| 20 | Without Penumbra / PlayerSync | Disable Penumbra, then PlayerSync, and open the window each time | A red Source line says which one is not loaded; nothing is exported. |
| 21 | Release build | Install from the custom repository | Loads next to the official PlayerSync; version matches the release. |
| 22 | Repo install/update | Only if hosting is public | Installs from repo.json; bumping the version shows an update. |
