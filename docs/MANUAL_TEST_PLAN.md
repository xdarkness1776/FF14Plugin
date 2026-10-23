# Manual acceptance test plan

None of these have been run yet: the development environment has no game client, Dalamud runtime or PlayerSync
server. Use two test characters you control (A runs the exporter, B is the remote character) in a ZoneSync-enabled
area (for example a town), with no direct pair between them. Record pass/fail and `/xllog` excerpts.

| # | Check | Steps | Expected |
|---|---|---|---|
| 1 | Correct character | Right-click B in a crowd; also right-click B's name in chat | Window title and Character line show B, not your target. Chat names without a nearby object get no entry. |
| 2 | No direct pairing | Before/after export, PlayerSync main UI | No new direct pair or pair request. Sync link shows "ZoneSync". |
| 3 | Live mapping | B wears a mod; open window | Game paths match B's mod; count roughly matches PlayerSync's "Pair Data" for B. |
| 4 | Waits for sync | B switches gear, immediately Export Selected | Phase "Waiting for PlayerSync"; export starts after B finishes loading; manifest AppliedDataHash is the new one. |
| 5 | Missing files via PlayerSync | Clear one hash from a *copy* scenario: B adds a new mod; Scan | PlayerSync log shows its normal download; exporter itself makes no HTTP requests. |
| 6 | Cache reuse | Export B twice; Scan in between | Second export: AlreadyPresentIdentical; PlayerSync log shows no download for cached hashes. |
| 7 | Only B's files | Export B with C nearby | Every Assets file is in B's manifest with game paths; none of C's. |
| 8 | Cache untouched | Hash/list PlayerSync cache + FileCache.csv before and after an export (not Scan) | Identical. |
| 9 | Custom parent | Settings → Choose Parent Folder `D:\My FFXIV Mods` → Apply | Export creates `D:\My FFXIV Mods\FFXIV Character Mod Exports\B_World\...`. |
| 10 | Auto-creation | Delete the export root, export | Root, character and Assets folders are created. |
| 11 | Persistence | Restart game | Settings show the chosen folder. |
| 12 | Unavailable folder | Unplug/rename the drive folder, open Settings, export | Red warning; export fails with message; nothing written elsewhere. |
| 13 | Existing files intact | Put `notes.txt` and a same-named different file in Assets, export | Both untouched; the conflict is reported, Retry works after removing it. |
| 14 | No false success | Export while B is in combat or paused | Failed "Synchronization did not finish" or "not available"; no package. |
| 15 | Raw vs package | Open README.txt | Assets described as individual resources; package listed only if built. |
| 16 | Package import | Penumbra → Import Mod the .pmp; enable on A in a test collection | Import succeeds; items look like B's (minus meta manipulations). |
| 17 | Leaving / despawn | Start export, B teleports away | Job fails "no longer available"; window still usable. |
| 18 | Cancel | Export a large character, Cancel Export | Phase Cancelled; no `.cmx-partial` files left. |
| 19 | Unload | `/xlplugins` disable exporter mid-export | No exception spam; partial files cleaned. |
| 20 | Stock PlayerSync | Load official PlayerSync instead | Window says export IPC not available. |
| 21 | Release build | Install zips from a release as dev plugins | Both load; manifest versions match. |
| 22 | Repo install/update | Only if hosting is public | Installs from repo.json; bumping the version shows an update. |
