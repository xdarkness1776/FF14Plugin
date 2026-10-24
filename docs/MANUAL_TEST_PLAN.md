# Manual acceptance test plan

None of these have been run yet (status: **not run** for every row): the development environment has no game
client, Dalamud runtime or PlayerSync server. Automated tests that did run are listed at the end. Use two test characters you control (A runs the exporter, B is the remote character) in a ZoneSync-enabled
area (for example a town), with no direct pair between them. Record pass/fail and `/xllog` excerpts.

| # | Check | Steps | Expected |
|---|---|---|---|
| 1 | Correct character | Right-click B in a crowd; also right-click B's name in chat | Window title and Character line show B, not your target. Chat names without a nearby object get no entry. |
| 2 | No direct pairing | Before/after export, PlayerSync main UI | No new direct pair or pair request. Source line reads "Official PlayerSync, files read through Penumbra". |
| 3 | Live mapping | B wears a mod; open window | Game paths match B's gear; they match Penumbra's On-Screen tab for B (files from PlayerSync's cache). |
| 3a | Transient files | B uses a modded emote while the window is open, then Refresh | The emote's animation/VFX files are added to the list. |
| 4 | Waits for loading | B switches gear, immediately Export Selected | Phase "Waiting"; the export starts once no new files appeared for about 5 seconds. |
| 5 | Scan | Note the PlayerSync log and a network monitor (for example Resource Monitor filtered to the game process); press Refresh, then Scan Character Resources | Stock: B is redrawn; state shows "still loading" and then "loaded". Patched: the list is read again. Both: **zero** download entries in PlayerSync's log and no new transfer to the file server caused by the button. |
| 6 | Cache reuse | Export B twice; Scan in between | Second export: AlreadyPresentIdentical; PlayerSync log shows no download for cached hashes. |
| 7 | Only B's files | Export B with C nearby | Every Assets file is in B's manifest with game paths; none of C's. |
| 8 | Cache untouched | Hash/list PlayerSync cache, FileCache.csv and PlayerSync's config.json before and after a Refresh, a Scan and an export | Identical (file list, sizes, times, hashes). |
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
| 23 | Library index | Settings → Original Package Library → choose your `.pmp` folder → Save and Index Library | Progress bar, then "Indexed: N package(s)". Library folder unchanged (compare a file listing with sizes and times before/after). |
| 24 | Re-index | Index again without changes; then add, rename and delete one package and index again | First: everything "unchanged". Second: 1 read per new/renamed file, removed ones counted. |
| 25 | Live match | Character B wears a mod you have as a numbered `.pmp`; Original Packages → Match Live Character | That package is listed as Exact or Strong with the option B uses; unrelated packages are not listed. |
| 25a | Variants | Your package has colour options; B uses one | The used option is shown under "Options used"; the package is not Partial because of the other colours. |
| 25b | Compressed textures | Turn on PlayerSync's compressed textures for downloads; match again | Textures show as "Cannot compare" (stock source) instead of making the package Partial. |
| 26 | Export originals | Select the matched packages → Export Selected Originals | `Original packages\` holds byte-identical copies (same file name and number, same SHA-256 as the library); `OriginalPackages.json` lists them. |
| 26a | No overwrite | Put a different file with the same name in `Original packages\`, export again | Reported as a conflict; the file is untouched. |
| 27 | Overrides | Change a package's category and tick Confirmed; restart the game | Both are kept; the package file is unchanged; `package-overrides.json` is in the plugin settings folder. |
| 28 | MCDF | Save your own character as `.mcdf` in PlayerSync; Open MCDF Snapshot... in an export window | Shown as "Saved snapshot (not live)"; matching runs; nothing is applied to any character. |
| 29 | Library safety | Choose the export folder (or a folder inside it) as the library | Refused with a message. |

## Live acceptance checklist (0.6 reliability fixes)

Status: **not run** for every row. Record the actual outcome next to each row when it is run.

| # | Check | Steps | Expected | Outcome |
|---|---|---|---|---|
| 50 | Scan makes no transfers | Patched build; B has a missing file; snapshot cache, FileCache.csv and config.json; Scan | Missing file still listed as missing; no download in the log; snapshot identical | not run |
| 51 | Reapply is explicit | Patched build; press Reapply PlayerSync Data..., then Cancel; then again and confirm | Cancel: nothing happens. Confirm: PlayerSync reapplies (the log may show downloads); the list updates | not run |
| 52 | Stale data | Patched build; export while B just changed outfit and PlayerSync has not applied it yet | Data line says stale; the export fails with a message naming the reapply action; nothing reapplied automatically | not run |
| 53 | Junction refused | Windows: `mklink /J D:\Exports\link <PlayerSync cache>`; choose `D:\Exports\link` as parent | Refused: "goes through a link or junction"; nothing written into the cache | not run |
| 54 | Game folder refused | Choose a folder inside the game installation as parent | Refused | not run |
| 55 | Other job's partial file | Put `x.tex.abcdef12.cmx-partial` in Assets; export | That file is still there afterwards; the export's own partial files are gone | not run |
| 56 | Same name, other world | If available: two characters with the same name on different worlds | The export window for one never lists or exports the other's files | not run |
| 57 | ZoneSync line | PlayerSync: global ZoneSync joining off, per-character on for your current character | Window: "ZoneSync joining for you: on (set for this character)" | not run |
| 58 | v1 build ignored | Load a PlayerSync build with the v1 export IPC | Source line says the v1 IPC is not compatible and is not used; files are read through Penumbra | not run |

## Live acceptance checklist (0.5 capture reliability)

Status: **not run** for every row. Record the actual outcome and an `/xllog` excerpt next to each row when it is run.
Do not mark a row passed from these expectations.

| # | Check | Steps | Expected | Outcome |
|---|---|---|---|---|
| 30 | Synced detection | Open the window for a ZoneSync character after the join delay | Lookup Found; files listed | not run |
| 31 | Not yet joined | Open the window within the ZoneSync join delay after zoning | "not synced"; Refresh after the delay lists files | not run |
| 32 | Unsynced player | Open the window for a player PlayerSync does not handle | "not synced"; nothing exportable | not run |
| 33 | Initial snapshot | Open the window, compare with Penumbra's On-Screen tab | Same files; notes say nothing is "no longer in use" | not run |
| 34 | Seen loading | B plays a modded emote with the window open | Its .pap/.avfx/.scd appear with "seen loading" | not run |
| 35 | Outfit change | B changes a modded top, then Refresh | Old top files "no longer in use" and unselected; new ones selected | not run |
| 36 | Package after change | Export after row 35 | Package and item mods contain only the new top; manifest `Activity` says NoLongerActive for the old | not run |
| 37 | PlayerSync reapply | B changes mods so PlayerSync sends new data; Refresh | Replaced files "no longer in use"; state settles to loaded | not run |
| 38 | Despawn | B teleports away with the window open | Export fails "no longer nearby"; no files of whoever appears later | not run |
| 39 | Respawn | B comes back | Files recorded again from scratch | not run |
| 40 | Own zone change | You teleport to another zone where B also is | Earlier files gone; B's files recorded again in the new zone | not run |
| 41 | Instance change | Change instance in a field zone | Same as row 40 | not run |
| 42 | Crowd | Export B in a busy town | Only B's files; nothing from players around B | not run |
| 43 | Permissions | Disable VFX for the zone group in PlayerSync | B's VFX files are not listed (PlayerSync does not apply them) | not run |
| 44 | Duty or PvP | Repeat row 30 inside a duty (dungeon sync on) | Same as row 30 | not run |
| 45 | MCDF valid | Open your own fresh MCDF | All entries Verified; coverage partial; packages with animations at most Strong | not run |
| 46 | MCDF damaged | Open a truncated copy (cut the file in half) | A readable error, or MissingPayload entries; the original file unchanged | not run |
| 47 | Library match evidence | Match Live; hover a package | Tooltip shows matched, only-in-this-package, not seen and not comparable counts | not run |
| 48 | Responsiveness | Open the window for a character with many mods; Refresh repeatedly | No visible frame hitch; hashing runs in the background | not run |
| 49 | Unload | Disable the exporter while a window is open and B is tracked | No errors in `/xllog`; no event handlers left (re-enable works) | not run |

## Automated tests (run in the development environment)

Last run for 0.6.0.0 on 2026-10-24 with `dotnet test` (Release): `CharacterModExporter.Tests` 242 passed, 0 failed;
`CharacterModExporter.PenumbraCompatTests` 2 passed, 0 failed. `playersync-fork/apply.sh` on a fresh checkout of
upstream b2f8ce8 applied the v2 patch and the patched PlayerSync built with 0 errors. New in 0.6: destination checks
through symbolic links (Linux symlinks; Windows junctions use the same .NET link API but were not exercised),
traversal, files in the path, an unavailable drive (simulated), the common forbidden roots, and a direct IPC request
into the cache; per-job temporary files, cleanup that throws, another job's partial file, cancellation and the slot
being released for the next job; the frozen mapping, the per-file destination re-check, a same-name character from
another world and an unconfirmed world; Refresh, Scan and Export never reapplying; stale data; mapping freshness;
target identity; the effective ZoneSync setting; and the Penumbra watch list (replacement, despawn, zone change,
forget, stale events). The earlier history follows.

Last run for 0.5.0.0 on 2026-10-24 with `dotnet test` (Release): `CharacterModExporter.Tests` 193 passed, 0 failed;
`CharacterModExporter.PenumbraCompatTests` 2 passed, 0 failed. The patched PlayerSync (`playersync-fork/apply.sh`,
upstream b2f8ce8) built successfully with the 0.5 contracts. New in 0.5: tracker lifecycle (tree and event sources,
outfit change, replacement, out-of-order and duplicate events, reset, size bounds, concurrency), activity in the
state, selection and packages, malformed MCDF files (truncated, trailing data, invalid hash or path, conflicting
paths, header limits, invalid JSON, wrong version, damaged LZ4), PlayerSync's MCDF writer rules, and match evidence
(source, coverage, unique matches, files the evidence cannot contain). These cover the resolver, the package reader for every
group type, the index cache, matching statuses, original export (verification, conflicts, changed packages,
library/export overlap), categories and overrides, and the MCDF reader, but not the game, Dalamud or Penumbra UI.
