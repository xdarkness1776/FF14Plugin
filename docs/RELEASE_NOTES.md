Character Mod Exporter: an add-on for PlayerSync. Right-click a character PlayerSync is syncing (ZoneSync, syncshell
or pair) and export the files PlayerSync applied to them into a folder you choose, with a manifest, a report and,
when complete, a Penumbra package for manual import.

0.6.1.0: MCDF and export integrity. Reading an MCDF snapshot now:
- marks empty entries;
- checks every file swap (invalid targets, swaps to themselves, different file types, swaps that would override a
  stored file, conflicting swaps), and uses only valid swaps for matching;
- reads the header in chunks.

Exports never offer PlayerSync's temporary download files (`.bin`, `.tmp`). A package is built only from the
verified exported copies; a copy that changed after export stops the package. A file swap to itself or to another
file type is left out of packages.

Export window: Character Files, Original Packages and Export Report are now tabs instead of toggle buttons. The
buttons are grouped into a Read row and an Export row. A view menu shows all files, the ones ready to export, the
selected ones, the missing or excluded ones, or the file swaps. Source notes fold away, and missing files explain
why in a tooltip. An opened MCDF snapshot lists its unverified files and unusable swaps. The settings are in
collapsible sections.

A source-backed audit of PlayerSync's MCDF, cache, download and transient code is in docs/PLAYERSYNC_AUDIT.md. A
proposed fix for PlayerSync's own MCDF import and export is in `playersync-fork/proposed/`; it is not applied to the
fork build. Not tested in game yet.

0.6.0.0: reliability and PlayerSync integration fixes. **Refresh and Scan are read-only and never cause downloads.**
With the patched PlayerSync build, Scan used to run PlayerSync's "reapply last data", which can download missing files;
that is now a separate **Reapply PlayerSync Data...** action that asks first and says it may download. The patched
build's export IPC is now version 2 (the reapply gate has a new name); a version 1 build is shown as incompatible and
not used, so update both together or use the official PlayerSync. Export folders are checked through symbolic links
and junctions, and the game folder, Dalamud and every plugin folder are refused too; the PlayerSync IPC runs the same
checks, and the folder is checked again before each file. An export now removes only its own temporary files, so two
exports into one folder no longer delete each other's partial files, and a cleanup failure can no longer block later
exports. Characters are identified by name and home world. The window shows whether the data is current, incomplete,
stale or still loading, and your effective ZoneSync joining setting (the per-character setting wins). Not tested in
game yet.

0.5.0.0: capture reliability. The exporter now knows which files a character still uses. Files that were on the
character earlier but are gone after an outfit change or new PlayerSync data are shown as "no longer in use". They
start unselected, and are kept out of the library match and of every package and item mod. Animations, VFX and
sounds seen while they played are shown as "seen loading". Zone and instance changes, and closing a character's
window, start its capture over, so files can never carry over to whoever loads next at the same memory address. The
address check now runs every frame. The MCDF reader gives every stored file a status (verified, hash mismatch,
missing bytes, invalid hash or path, conflicting paths). It knows which files PlayerSync's MCDF export always leaves
out, reports truncated, padded or damaged files with a readable reason, and enforces size limits. Matching states its
evidence (export IPC, live through Penumbra, or MCDF), whether that evidence is complete, and, for each package, how
many matched files only it provides and how many cannot be compared. `Manifest.json` and `OriginalPackages.json` are
now version 2 with these fields. The full audit is in docs/CAPTURE_RELIABILITY.md. Not tested in game yet.

0.4.1.0: fixes. The Penumbra source now notices when a watched character despawns or respawns at another address, so
files of an object that takes over its old address are never recorded for it. Matching always reads the character
again first instead of reusing an older list, and the package list keeps its order (confirmed, then status, then most
matched files) in large libraries. Exporting more originals into the same folder keeps the earlier entries in
`OriginalPackages.json`; each entry now says when it was exported and from what evidence. Unnamed Combining
containers are labelled by their position. The progress bar counts copied packages, category detection is cached,
and your package choices are safe to change while an export runs.

0.4.0.0: original packages. Choose the folder with your own (numbered) `.pmp` files in Settings and index it. The
export window's **Original Packages** view then shows which of those packages the character's files come from, with
an explicit status (Exact, Strong, Partial, Ambiguous; files in no package or not comparable are listed too), the
options used and a category you can change. **Export Selected Originals** copies the chosen packages byte for byte,
checked with SHA-256, into `Original packages\` with `OriginalPackages.json`; nothing existing is replaced. Saved MCDF
files can be compared as a separate snapshot without being applied. The PlayerSync IPC resolver now checks every game
path of a resource (not only the first), handles validation exclusions per path and for file swaps, and reports
which paths were dropped. Not tested in game yet; see docs/ORIGINAL_PACKAGES.md.

0.3.3.0: mods keep their own files. The exporter reads the file names stored inside materials, animation timelines,
animations and effects, so emotes and battle animations keep their VFX and sounds, gear keeps its own effects,
custom textures stay with the material that uses them, and the body deformer goes with body mods. Skeleton and
physics edits count as mods.

0.3.2.0: proper categories. Item mods are sorted into Gear, Accessories, Weapons, Character (Body, Hair, Face, Eyes,
Face paint, Skin, Ears, Tail), Animations (one mod per emote; idle and movement; battle), VFX, Sounds, and Minions and
mounts. Skeleton and physics files go with the hair, face, body or gear piece they belong to. Files that are not a mod
on their own get no mod and no category.

0.3.1.0: only modded files. Synced files that are byte-for-byte identical to the game's own files (unchanged copies
that many mods ship) are no longer listed, exported or packaged.

0.3.0.0: one Penumbra mod per item. Besides the whole-character package, every gear piece, accessory, weapon,
hairstyle, face, ears, tail, skin, animation set, VFX and sound set becomes its own `.pmp` under
`Mods\<Category>\`, named after the game item where possible. Body and outfit mods state the races/genders and body
type (Bibo+, Gen3, TBSE...) they were made for. Not tested in game yet.

0.2.0.0: works with the official PlayerSync. The exporter reads from Penumbra which of PlayerSync's cached files
are applied to the character. Only files the game has loaded are listed (animations, VFX and sounds once they have
played; Scan redraws the character). The patched PlayerSync build is no longer needed; if one with the export IPC is
loaded, the exporter still uses it. Not tested in game yet; see docs/MANUAL_TEST_PLAN.md. Installation: docs/INSTALL.md.
