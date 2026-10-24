# Original packages: library, matching, export, categories, MCDF

Since 0.4.0.0 the exporter can find the **original** Penumbra packages (`.pmp`) in your own library that a character's
synced files come from, and copy those packages unchanged. The rebuilt per-item mods (`Mods\`) and the raw files
(`Assets\`) are still written, but they are reconstructions and are labelled that way. They are not originals.

Nothing in this feature downloads anything or writes into your library, PlayerSync's cache, Penumbra's mod folder or
the Dalamud plugin folders. Only packages from the folder you choose are used.

## 1. The library and its index

Settings → **Original Package Library** → choose the folder → **Save and Index Library**.

* Every `*.pmp` below the folder is read (subfolders included, links skipped). Packages are only opened for reading:
  the reader hashes the archive and the files inside it in memory and extracts nothing.
* For each package the index stores:
  * the path below the library, the file name, and the number at the start of the name exactly as written
    (`0042 - Outfit.pmp` gives `0042`; names that do not start with a number have none);
  * size, modification time, and the **SHA-256 of the whole file** (the package fingerprint);
  * `meta.json`: name, author, version, website, description and tags;
  * every option container: the default option (`default_mod.json`), every option of Single and Multi groups (with
    Multi option priorities), every container of Combining groups, and Imc groups (which change only meta data and
    redirect no files). The group formats follow Penumbra's own schema files (`schemas/group.json` and
    `schemas/structs/*`, Penumbra 9bb7931);
  * for every file redirection: the game path (normalized the way Penumbra stores it), the relative file, the
    archive entry, its size, its **SHA-1** (comparable with PlayerSync's hashes) and its SHA-256;
  * file swaps, the number of meta manipulations per container, entries nothing references, and warnings.
* The index is saved as `package-library-index.json` in the plugin's settings folder (`pluginConfigs\CharacterModExporter`).
  Indexing again rereads only packages whose size or modification time changed, drops removed ones and adds new
  ones. Raising the index format rebuilds it completely. Saving the index inside the library is refused.
* Limits: JSON files over 32 MB are not parsed, archives with more than 200,000 entries are rejected, and a single
  file over 4 GB is not hashed. Broken archives are listed with their error and skipped by matching.

## 2. Matching

In the export window, **Original Packages** → **Match Live Character**.

What is compared: the character's game paths and **SHA-1 content hashes** (what PlayerSync sends and names its
cache files by) against the game paths and SHA-1 of the files inside each package. Hashes of different algorithms are
never compared (the code carries the algorithm with every hash), and a whole-package SHA-256 is never compared with
a file hash. Package SHA-256 values are only used to recognize byte-identical copies.

Only the options a character evidently uses are judged: a container counts as **used** when at least one of its files
(or swaps) matches, and the default option always counts when it has content. Other options of a Single group are
ignored, so a red and a blue variant of the same mod are told apart.

| Status | Meaning |
| --- | --- |
| **Exact** | Every file and swap of the used options was found on the character with the same content, nothing contradicts it, and at least one matching file is in no other package. |
| **Strong** | Files match and none contradicts, but not every file of the used options was seen on the character. This is normal: the game only loads what is on screen (other races, other slots, animations that did not play). |
| **Partial** | Some files match, but at other paths of the same options the character has different content (another version of the mod, or another mod overriding those paths). |
| **Ambiguous** | Every matching file is also in another package, so the files cannot tell which one is used (for example a mod and a pack that contains it, or two copies). |
| **Not in library** (Unmatched) | A character file with a hash that no package contains. |
| **Cannot compare** (Unresolvable) | A character file without a comparable hash: not downloaded, excluded by PlayerSync's validation, or a texture that may be a compressed copy. |
| **Confirmed by you** (User-confirmed) | You ticked *Confirmed* for this character. The computed status is kept and written to the manifest. |

There are no percentages. A matching file is evidence for a package, never proof that the whole package is in use.

### Compressed textures

PlayerSync can replace a texture with a compressed alternate (`CompressedAlternateUsage`; the source hash maps to
the alternate in `ActiveCompressionRedirects`). A compressed copy has other bytes and therefore another hash, and it
says nothing about whether the original package is related.

* With the PlayerSync export IPC, each resource carries the **original** hash (`Hash`) next to the hash of the copy
  that was used (`CacheHash`). Matching uses the original hash, so compressed textures still match exactly.
* The stock source only sees the file Penumbra loaded. A `.tex` from PlayerSync's cache whose hash matches nothing is
  therefore reported as *Cannot compare*, and inside a package it is listed as "may be a compressed copy" instead of
  as a contradiction. A texture that does match is still exact evidence (equal hashes mean equal bytes).

## 3. Exporting originals

Select packages (**Select Exact and Confirmed** helps) → **Export Selected Originals**.

* Each package is copied byte for byte into `<character folder>\Original packages\<its folder below the library>\<its
  file name>`. File names and numbers are kept as they are.
* The copy is written to a temporary file, hashed, and accepted only when its **SHA-256 equals the value from the
  index**. A package that changed since it was indexed is not exported (index the library again).
* An existing file at the destination is never replaced: the same content is reported as *already present*, other
  content as a *conflict* and left untouched.
* The export folder and the library may not contain each other.
* `Original packages\OriginalPackages.json` (manifest version 1) records, for each package: library path, number,
  name, match status (and the computed status if you confirmed it), options used, number of matched files, reasons,
  category and whether you set it, the indexed SHA-256, the SHA-256 of the copy, and the outcome. It also lists the
  character's files that are in no package or could not be compared, says whether the evidence was the live character
  or an MCDF snapshot, and when the library was indexed.

The exported originals are other creators' work. Their own licences and terms apply.

## 4. Categories and your overrides

Each package gets a category from its own game paths, using the same folders as the item mods (Gear/Body,
Character/Hair, Animations/Emotes, VFX, ...). Helper files (shaders, shared textures, skeleton helpers) do not count.
One category wins only with more than half of the categorized paths; otherwise the package is **Mixed**, and a package
with no known paths is **Uncategorized**. The tooltip shows the counts behind it. Nothing is guessed from names.

Click a category to set your own. Your categories and confirmations are saved in `package-overrides.json` in the
plugin's settings folder, keyed by the package's SHA-256 (so renaming or moving a package keeps them), and are never
written into a package. If that file cannot be read, it is backed up as `package-overrides.json.unreadable-<time>.bak`
before it is replaced.

## 5. MCDF snapshots

**Open MCDF Snapshot...** reads a saved PlayerSync character file without applying or extracting it and compares it
with the library as a separate report (switch between *Live* and *Snapshot*). Snapshot results are never mixed with
the live character, and confirmations apply to the live character only.

Format, verified against PlayerSync 1.15.5.9 (`MareCharaFileHeader`, `MareCharaFileData`, `CharaDataFileHandler`):
the whole file is a K4os LZ4 legacy stream; inside are `MCDF`, a version byte (only 1 exists), an int32 length, that
many bytes of UTF-8 JSON (`Description`, `GlamourerData`, `CustomizePlusData`, `ManipulationData`,
`Files[{GamePaths, Length, Hash}]`, `FileSwaps[{GamePaths, FileSwapPath}]`) and then the files' bytes in that order.
The reader checks the magic, version and lengths, hashes every stored file, and only files whose bytes match their
SHA-1 count as evidence.

PlayerSync writes MCDF files only from **your own** character, and they contain loose files, not packages.

## 6. Penumbra

All Penumbra access goes through the public Penumbra.Api (5.15.1, breaking version 5); nothing uses reflection into
Penumbra. The stock source uses `GetGameObjectResourcePaths`, `GameObjectResourcePathResolved`, `GetModDirectory` and
`RedrawObject`. The package catalogue does not need Penumbra at all.

Checked but not used for attribution: `GetCollectionForObject`, `GetAllModSettings`, `GetCurrentModSettingsWithTemp`,
`GetModList`, `GetModPath`, `GetGameObjectResourceTrees` and `GetGameObjectResourcesOfType` exist in this API version.
For a PlayerSync character they cannot name the original mods: PlayerSync applies everything through a temporary
collection with one temporary mod, so Penumbra only knows PlayerSync's flat file list. They would help for your own
character or for files PlayerSync resolved from your own Penumbra mod folder; that attribution is not implemented yet.

## 7. Snappy

Studied read-only at `beslightly/snappy` 5c44b2c7eb012aefb1b768d113bc8041aaeb146a. The repository has no licence
file, so no code was copied. Snappy builds snapshots from `GetGameObjectResourcePaths` and file hashes and exports
reconstructed packages of changed items, which is the same kind of output as this exporter's `Mods\` folder. It does
not identify original packages, so it confirmed the approach here rather than supplying one.

## 8. Limits

* Only loaded files can be compared with the stock source. Unplayed emotes and effects stay unseen, which makes
  Strong more common than Exact. The PlayerSync export IPC reports the full applied mapping.
* Meta manipulations (Imc, Eqp, ...) are not compared: PlayerSync sends them only as one compressed blob.
* A package that the character uses only through file swaps or meta changes can only be found through its swaps.
* Matching cannot tell apart two packages that contain the same files; both are Ambiguous until you confirm one.
* Large libraries take time to index the first time (every package is read in full). Later runs reuse the index.
