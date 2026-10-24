using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

/// <summary>How a tracked redirection was first seen.</summary>
public enum ObservationSource
{
    /// <summary>In the first resource tree read for the character.</summary>
    InitialSnapshot,
    /// <summary>Penumbra's per-resource "resolved" event, as the game loaded the file.</summary>
    Event,
    /// <summary>In a later resource tree (for example after a redraw).</summary>
    LaterSnapshot,
}

/// <summary>One local file of the character with what the tracker knows about it.</summary>
public sealed record TrackedFile(List<string> GamePaths, ResourceActivity Activity, ObservationSource FirstSource,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc);

/// <summary>
/// The files Penumbra has redirected for one character: what stock PlayerSync applied, as far as the game
/// has loaded it. Fed by Penumbra's on-screen resource tree and by its per-resource "resolved" event, so
/// animations, VFX and sounds are added once they play. Thread-safe; the event arrives on game threads.
/// <para>
/// The tree says what is on the character now, so a file that was in an earlier tree and is missing from the latest
/// one, or whose game path now points at another file, is kept as no longer active rather than forgotten or offered
/// as current. Files only ever seen through the event are kept as observed: the tree never lists them, so their
/// absence from it proves nothing.
/// </para>
/// </summary>
public sealed class LoadedResourceTracker
{
    /// <summary>Upper bound of current redirections; a character has a few thousand at most.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>Upper bound of remembered replaced redirections; the oldest are dropped first.</summary>
    public const int MaxReplaced = 2_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byGamePath = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _replaced = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _snapshots;

    public LoadedResourceTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LastChangeUtc = _clock();
    }

    /// <summary>When a game path was last added or redirected to a different file, or a redraw was requested.</summary>
    public DateTimeOffset LastChangeUtc { get; private set; }

    /// <summary>Set when <see cref="MaxEntries"/> was reached and further redirections were ignored.</summary>
    public bool Truncated { get; private set; }

    /// <summary>How many resource trees were merged since the last reset.</summary>
    public int SnapshotCount
    {
        get
        {
            lock (_lock) return _snapshots;
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _byGamePath.Count;
        }
    }

    /// <summary>Records one redirection reported by Penumbra's event. Vanilla and swapped resources (no file on disk) are ignored.</summary>
    public void Record(string? gamePath, string? resolvedPath)
    {
        lock (_lock) RecordCore(gamePath, resolvedPath, ObservationSource.Event, _clock());
    }

    private bool RecordCore(string? gamePath, string? resolvedPath, ObservationSource source, DateTimeOffset now)
    {
        var gp = NormalizeGamePath(gamePath);
        var fp = NormalizeResolvedPath(resolvedPath);
        if (gp == null || fp == null) return false;
        var fromTree = source != ObservationSource.Event;

        if (_byGamePath.TryGetValue(gp, out var existing))
        {
            if (string.Equals(existing.File, fp, StringComparison.OrdinalIgnoreCase))
            {
                existing.LastSeenUtc = now;
                if (fromTree) existing.InTree = existing.EverInTree = true;
                else existing.LastEventSnapshot = _snapshots;
                return true;
            }

            // The game path now points at another file: the old one is no longer what the character uses.
            _replaced.AddLast(existing);
            if (_replaced.Count > MaxReplaced) _replaced.RemoveFirst();
        }
        else if (_byGamePath.Count >= MaxEntries)
        {
            Truncated = true;
            return false;
        }

        _byGamePath[gp] = new Entry(gp, fp, source, now)
        {
            InTree = fromTree, EverInTree = fromTree, LastEventSnapshot = fromTree ? -1 : _snapshots,
        };
        LastChangeUtc = now;
        return true;
    }

    /// <summary>
    /// Merges Penumbra's GetGameObjectResourcePaths result (actual path to game paths): what is on the character now.
    /// Null means Penumbra had no tree for it, which says nothing, so nothing changes.
    /// </summary>
    public void Merge(IReadOnlyDictionary<string, HashSet<string>>? actualToGamePaths)
    {
        if (actualToGamePaths == null) return;
        lock (_lock)
        {
            var now = _clock();
            var source = _snapshots == 0 ? ObservationSource.InitialSnapshot : ObservationSource.LaterSnapshot;
            _snapshots++;
            var wasInTree = _byGamePath.Values.Where(e => e.InTree).ToList();
            foreach (var e in wasInTree) e.InTree = false;
            foreach (var (actual, gamePaths) in actualToGamePaths)
            {
                foreach (var gp in gamePaths) RecordCore(gp, actual, source, now);
            }

            // A file leaving the tree is a change too: the set is still settling.
            if (wasInTree.Exists(e => !e.InTree)) LastChangeUtc = now;
        }
    }

    /// <summary>Marks that the character is being redrawn, so the set counts as changing until it settles again.</summary>
    public void MarkChanging()
    {
        lock (_lock) LastChangeUtc = _clock();
    }

    /// <summary>Forgets everything, for example when the game object now belongs to another character or the zone changed.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _byGamePath.Clear();
            _replaced.Clear();
            _snapshots = 0;
            Truncated = false;
            LastChangeUtc = _clock();
        }
    }

    /// <summary>
    /// Local file to its game paths and activity. A file used for several game paths takes the most current activity
    /// of them; files that were only replaced keep the game paths they used to serve, as no longer active.
    /// </summary>
    public Dictionary<string, TrackedFile> Snapshot()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, TrackedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _byGamePath.Values.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
                result[group.Key] = Combine(group.ToList());

            foreach (var group in _replaced.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
            {
                if (result.ContainsKey(group.Key)) continue;
                var old = Combine(group.ToList());
                result[group.Key] = old with { Activity = ResourceActivity.NoLongerActive };
            }

            return result;
        }
    }

    private TrackedFile Combine(List<Entry> entries)
    {
        var activity = entries.Select(ActivityOf).Min();
        var first = entries.MinBy(e => e.FirstSeenUtc)!;
        return new TrackedFile(entries.Select(e => e.GamePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            activity, first.FirstSource, first.FirstSeenUtc, entries.Max(e => e.LastSeenUtc));
    }

    private ResourceActivity ActivityOf(Entry e)
    {
        if (e.InTree) return ResourceActivity.Active;
        // Seen loading after the latest tree was read: observed, whatever the tree said before.
        if (!e.EverInTree || e.LastEventSnapshot >= _snapshots) return ResourceActivity.ObservedLoaded;
        return ResourceActivity.NoLongerActive;
    }

    private sealed class Entry(string gamePath, string file, ObservationSource source, DateTimeOffset now)
    {
        public string GamePath { get; } = gamePath;
        public string File { get; } = file;
        public ObservationSource FirstSource { get; } = source;
        public DateTimeOffset FirstSeenUtc { get; } = now;
        public DateTimeOffset LastSeenUtc { get; set; } = now;
        public bool InTree { get; set; }
        public bool EverInTree { get; set; }

        /// <summary>The number of trees merged when the event last reported it; -1 when it never did.</summary>
        public int LastEventSnapshot { get; set; }
    }

    public static string? NormalizeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var gp = gamePath.Trim().Replace('\\', '/').ToLowerInvariant();
        return Path.IsPathRooted(gp) || gp.Contains(':') ? null : gp;
    }

    /// <summary>
    /// The file on disk behind a Penumbra resolved path, or null for game paths (vanilla or swapped resources).
    /// Penumbra prefixes some paths with "|collection_id|" to keep them apart per collection.
    /// </summary>
    public static string? NormalizeResolvedPath(string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath)) return null;
        var path = resolvedPath.Trim();
        if (path.StartsWith('|'))
        {
            var parts = path.Split('|');
            if (parts.Length < 3) return null;
            path = parts[2];
        }

        // Rooted on Windows (C:\...) or Unix (/...), but never a game path such as "chara/...".
        var windowsRooted = path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        if (!windowsRooted && !(Path.IsPathRooted(path) && path.StartsWith('/'))) return null;
        return path;
    }
}

/// <summary>Turns tracked Penumbra redirections into the same state and file map the export job uses.</summary>
public static class PenumbraResolution
{
    /// <summary>How long the set must stay unchanged before it counts as fully loaded.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    public const string SourceNote =
        "Read from Penumbra: only files the game has loaded for this character are listed. Animations, VFX and sounds appear once they " +
        "play while this window is open; Scan Character Resources redraws the character to reload the rest.";

    /// <param name="handledByPlayerSync">Whether stock PlayerSync handles this character; null when PlayerSync is not loaded.</param>
    /// <param name="files">Local file to the game paths it replaces (from <see cref="LoadedResourceTracker.Snapshot"/>).</param>
    /// <param name="truncated">Whether the tracker reached its size limit (<see cref="LoadedResourceTracker.Truncated"/>).</param>
    /// <param name="hashes">SHA-1 cache for files whose name is not already their hash (your own Penumbra mods).</param>
    /// <param name="vanilla">Leaves out files identical to the game's own; null keeps them.</param>
    public static CharacterResolution Build(string playerName, bool? handledByPlayerSync, IReadOnlyDictionary<string, TrackedFile> files,
        string? cacheFolder, string? penumbraFolder, FileVerificationCache hashes, DateTimeOffset lastChangeUtc, DateTimeOffset now,
        VanillaFileComparer? vanilla = null, bool truncated = false)
    {
        var map = new Dictionary<string, ResolvedFile>(StringComparer.OrdinalIgnoreCase);
        var state = new CharacterStateDto { PlayerName = playerName, LinkDetailsKnown = false };
        if (handledByPlayerSync == null)
        {
            state.Lookup = CharacterLookup.NotConnected;
            state.Notes.Add("PlayerSync is not loaded, or did not answer.");
            return new(state, map);
        }

        if (handledByPlayerSync == false)
        {
            state.Lookup = CharacterLookup.NotSynced;
            return new(state, map);
        }

        state.Lookup = CharacterLookup.Found;
        state.Notes.Add(SourceNote);
        if (string.IsNullOrEmpty(cacheFolder))
            state.Notes.Add("PlayerSync's cache folder could not be found (is it set in PlayerSync's settings?), so its files cannot be listed.");

        var other = 0;
        var unchanged = 0;
        var inactive = 0;
        foreach (var (path, tracked) in files.OrderBy(kv => kv.Value.GamePaths.FirstOrDefault(), StringComparer.Ordinal))
        {
            var gamePaths = tracked.GamePaths;
            var kind = ResourceResolver.Classify(path, cacheFolder, penumbraFolder);
            if (kind == SourceKind.Other)
            {
                other++;
                continue;
            }

            var dto = new ResourceDto
            {
                ObjectKind = "Player",
                GamePaths = [.. gamePaths],
                SourceKind = kind,
                Extension = ExportPathPolicy.ExtensionOfGamePath(gamePaths.FirstOrDefault()) ?? string.Empty,
                Activity = tracked.Activity,
                FirstSeenUtc = tracked.FirstSeenUtc,
                LastSeenUtc = tracked.LastSeenUtc,
            };
            var fi = new FileInfo(path);
            var ext = ExportPathPolicy.SanitizeExtension(fi.Extension);
            var stem = Path.GetFileNameWithoutExtension(fi.Name);
            // PlayerSync names cache files by the SHA-1 of their content; the copy verifies it again.
            var hash = kind == SourceKind.PlayerSyncCache && ExportPathPolicy.IsSha1Hex(stem) ? stem.ToUpperInvariant()
                : fi.Exists ? hashes.GetSha1(fi.FullName) : null;
            if (hash != null && fi.Exists && vanilla != null && vanilla.IsUnchanged(hash, fi.FullName, gamePaths))
            {
                unchanged++;
                continue;
            }

            dto.Hash = hash;
            dto.CacheHash = hash;
            if (fi.Exists && ext != null && hash != null)
            {
                dto.Size = fi.Length;
                dto.Extension = ext;
                dto.Availability = ResourceAvailability.Available;
                map[hash] = new ResolvedFile(fi.FullName, ext);
            }
            else
            {
                dto.Availability = ResourceAvailability.Missing;
            }

            if (tracked.Activity == ResourceActivity.NoLongerActive) inactive++;
            state.Resources.Add(dto);
        }

        if (inactive > 0)
            state.Notes.Add($"{inactive} file(s) were on this character earlier but are no longer in use (changed outfit, or PlayerSync applied new data). " +
                            "They are listed as no longer active, are not selected for export, and are not compared with your library.");
        if (truncated)
            state.Notes.Add($"More than {LoadedResourceTracker.MaxEntries} redirections were seen for this character; later ones were ignored, so the list may be incomplete.");

        if (unchanged > 0)
            state.Notes.Add($"{unchanged} synced file(s) are identical to the game's own files (mods often ship unchanged copies) and are not offered.");
        if (other > 0)
            state.Notes.Add($"{other} loaded file(s) come from neither PlayerSync's cache nor your Penumbra mod folder and are not offered.");

        state.AppliedAtUtc = state.Resources.Count > 0 ? lastChangeUtc : null;
        // Files the character no longer uses say nothing about whether its current set is complete.
        var current = state.Resources.Where(r => r.Activity != ResourceActivity.NoLongerActive).ToList();
        state.SyncState = current.Count == 0 ? SyncState.NotApplied
            : now - lastChangeUtc < SettleTime ? SyncState.InProgress
            : current.Exists(r => r.Availability != ResourceAvailability.Available) ? SyncState.CompleteWithMissing
            : SyncState.Complete;
        if (state.Resources.Count == 0 && unchanged == 0)
            state.Notes.Add("No PlayerSync files are loaded for this character yet. PlayerSync may still be downloading; try again in a moment.");
        return new(state, map);
    }
}

/// <summary>Reads (never writes) the cache folder from stock PlayerSync's configuration file.</summary>
public static class PlayerSyncConfigReader
{
    /// <summary>PlayerSync keeps the internal name of the Mare client it is based on.</summary>
    public const string InternalName = "MareSempiterne";

    /// <param name="pluginConfigsDirectory">Dalamud's pluginConfigs folder (the parent of this plugin's config directory).</param>
    public static string? ReadCacheFolder(string? pluginConfigsDirectory)
    {
        if (string.IsNullOrEmpty(pluginConfigsDirectory)) return null;
        var configPath = Path.Combine(pluginConfigsDirectory, InternalName, "config.json");
        try
        {
            if (!File.Exists(configPath)) return null;
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("CacheFolder", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            var folder = value.GetString();
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? Path.GetFullPath(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Recognises files that only look modded: many mods ship unchanged copies of game files next to the ones they
/// change, and PlayerSync syncs those too. A file counts as unchanged when, for every game path it replaces, the
/// game's own file has exactly the same bytes.
/// </summary>
public sealed class VanillaFileComparer(Func<string, byte[]?> readGameFile)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="sha1">Upper-case SHA-1 of <paramref name="localFile"/>.</param>
    public bool IsUnchanged(string sha1, string localFile, IReadOnlyCollection<string> gamePaths)
    {
        if (gamePaths.Count == 0) return false;
        return _results.GetOrAdd(sha1 + "|" + string.Join("|", gamePaths.Order(StringComparer.Ordinal)), _ => Compare(sha1, localFile, gamePaths));
    }

    private bool Compare(string sha1, string localFile, IEnumerable<string> gamePaths)
    {
        try
        {
            var length = new FileInfo(localFile).Length;
            foreach (var gamePath in gamePaths)
            {
                var original = readGameFile(gamePath);
                if (original == null || original.Length != length) return false;
                if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(original)), sha1, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (Exception)
        {
            // Unreadable on either side: keep the file rather than drop something that may be modded.
            return false;
        }
    }
}
