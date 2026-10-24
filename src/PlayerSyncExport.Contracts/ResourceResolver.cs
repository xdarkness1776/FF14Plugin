namespace PlayerSyncExport.Contracts;

/// <summary>One entry of PlayerSync's character data: the game paths a hash (or a file swap) replaces.</summary>
public sealed record ReplacementInput(string ObjectKind, IReadOnlyList<string> GamePaths, string Hash, string? FileSwapPath);

/// <summary>The local file behind a cache hash.</summary>
public sealed record ResolvedFile(string Path, string Extension);

/// <summary>
/// Turns what a PlayerSync pair handler applied (its game path to file mapping) into export DTOs.
/// Pure function so it is shared by the PlayerSync provider and the exporter's tests.
/// </summary>
public static class ResourceResolver
{
    /// <param name="applied">The character data that was applied (after PlayerSync's filters).</param>
    /// <param name="moddedPaths">PlayerSync's resolved mapping: (game path, hash used) to local file. File swaps use a null hash.</param>
    /// <param name="compressionRedirects">Source hash to compressed alternate hash, for substituted textures.</param>
    /// <param name="excludedByValidation">Entries PlayerSync's file validation removed before applying.</param>
    /// <param name="files">Receives cache hash to local file for every available resource.</param>
    public static List<ResourceDto> Resolve(IEnumerable<ReplacementInput> applied,
        IReadOnlyDictionary<(string GamePath, string? Hash), string> moddedPaths,
        IReadOnlyDictionary<string, string> compressionRedirects,
        IReadOnlySet<(string GamePath, string? Hash)> excludedByValidation,
        string? cacheFolder, string? penumbraFolder,
        IDictionary<string, ResolvedFile> files)
    {
        var result = new List<ResourceDto>();
        // PlayerSync keys its mapping by (game path, hash) with the default comparer. Look up without regard to case so a
        // differently cased hash or path in the snapshot never turns an applied file into a "missing" one.
        var mapping = new Dictionary<(string GamePath, string? Hash), string>(PathHashComparer.Instance);
        foreach (var (key, value) in moddedPaths) mapping.TryAdd(key, value);
        var excluded = new HashSet<(string GamePath, string? Hash)>(excludedByValidation, PathHashComparer.Instance);

        foreach (var item in applied)
        {
            var dto = new ResourceDto
            {
                ObjectKind = item.ObjectKind,
                GamePaths = [.. item.GamePaths],
                Extension = ExportPathPolicy.ExtensionOfGamePath(item.GamePaths.FirstOrDefault()) ?? string.Empty,
            };

            if (!string.IsNullOrEmpty(item.FileSwapPath))
            {
                dto.FileSwapTarget = item.FileSwapPath;
                // A swap is applied per game path as (path, null). Validation can drop it (e.g. a target from an
                // expansion that is not installed); report those paths instead of claiming the swap was applied.
                var swapApplied = item.GamePaths.Where(gp => mapping.ContainsKey((gp, null))).ToList();
                var swapExcluded = item.GamePaths.Where(gp => !mapping.ContainsKey((gp, null)) && excluded.Contains((gp, null))).ToList();
                dto.ExcludedGamePaths = swapExcluded.Count > 0 ? swapExcluded : null;
                dto.Availability = swapApplied.Count == 0 && swapExcluded.Count > 0 ? ResourceAvailability.ExcludedByValidation : ResourceAvailability.FileSwap;
                if (swapApplied.Count > 0 && swapApplied.Count < item.GamePaths.Count) dto.GamePaths = swapApplied;
                result.Add(dto);
                continue;
            }

            dto.Hash = item.Hash;
            var cacheHash = compressionRedirects.TryGetValue(item.Hash, out var alt) && !string.IsNullOrEmpty(alt) ? alt : item.Hash;
            dto.CacheHash = cacheHash;
            dto.IsCompressedAlternate = !string.Equals(cacheHash, item.Hash, StringComparison.OrdinalIgnoreCase);

            // Every game path is checked, not only the first: validation removes single (path, hash) pairs, and the
            // first path being dropped or absent says nothing about the others.
            string? localPath = null;
            var appliedPaths = new List<string>();
            var excludedPaths = new List<string>();
            var missingPaths = new List<string>();
            foreach (var gp in item.GamePaths)
            {
                if (mapping.TryGetValue((gp, cacheHash), out var lp))
                {
                    appliedPaths.Add(gp);
                    localPath ??= lp;
                }
                else if (excluded.Contains((gp, cacheHash)) || excluded.Contains((gp, item.Hash)))
                {
                    excludedPaths.Add(gp);
                }
                else
                {
                    missingPaths.Add(gp);
                }
            }

            dto.ExcludedGamePaths = excludedPaths.Count > 0 ? excludedPaths : null;
            dto.MissingGamePaths = missingPaths.Count > 0 && appliedPaths.Count > 0 ? missingPaths : null;

            if (localPath != null)
            {
                var fi = new FileInfo(localPath);
                dto.SourceKind = Classify(fi.FullName, cacheFolder, penumbraFolder);
                var ext = ExportPathPolicy.SanitizeExtension(fi.Extension);
                // Only files inside PlayerSync's cache or Penumbra's mod folder are ever offered for export.
                if (fi.Exists && dto.SourceKind != SourceKind.Other && ext != null && ExportPathPolicy.IsSha1Hex(cacheHash)
                    && !ExportPathPolicy.IsTemporaryFile(fi.Name))
                {
                    dto.Size = fi.Length;
                    dto.Extension = ext;
                    dto.Availability = ResourceAvailability.Available;
                    // Only the paths Penumbra actually received belong to the exported resource.
                    if (appliedPaths.Count < item.GamePaths.Count) dto.GamePaths = appliedPaths;
                    files[cacheHash] = new ResolvedFile(fi.FullName, ext);
                }
                else
                {
                    dto.Availability = ResourceAvailability.Missing;
                }
            }
            else
            {
                dto.Availability = excludedPaths.Count > 0 && missingPaths.Count == 0
                    ? ResourceAvailability.ExcludedByValidation
                    : ResourceAvailability.Missing;
            }

            result.Add(dto);
        }

        return result;
    }

    /// <summary>Case-insensitive comparison of PlayerSync's (game path, hash) keys.</summary>
    private sealed class PathHashComparer : IEqualityComparer<(string GamePath, string? Hash)>
    {
        public static readonly PathHashComparer Instance = new();

        public bool Equals((string GamePath, string? Hash) x, (string GamePath, string? Hash) y)
            => string.Equals(x.GamePath, y.GamePath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string GamePath, string? Hash) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GamePath ?? string.Empty),
                obj.Hash == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hash));
    }

    /// <summary>Describes received data that has never been applied: nothing is available yet.</summary>
    public static List<ResourceDto> Unresolved(IEnumerable<ReplacementInput> received)
        => received.Select(item =>
        {
            var swap = !string.IsNullOrEmpty(item.FileSwapPath);
            return new ResourceDto
            {
                ObjectKind = item.ObjectKind,
                GamePaths = [.. item.GamePaths],
                Hash = swap ? null : item.Hash,
                FileSwapTarget = swap ? item.FileSwapPath : null,
                Extension = ExportPathPolicy.ExtensionOfGamePath(item.GamePaths.FirstOrDefault()) ?? string.Empty,
                Availability = swap ? ResourceAvailability.FileSwap : ResourceAvailability.Missing,
            };
        }).ToList();

    /// <summary>
    /// The sync state as the exporter needs it. "Complete" requires that the applied data is the latest
    /// received data and that no pipeline is running, never just that the character is visible.
    /// </summary>
    public static SyncState ComputeState(string? receivedHash, bool inProgress, string? appliedHash, IReadOnlyCollection<ResourceDto> resources)
    {
        if (receivedHash == null) return SyncState.NoData;
        if (inProgress) return SyncState.InProgress;
        if (appliedHash == null) return SyncState.NotApplied;
        if (!string.Equals(appliedHash, receivedHash, StringComparison.Ordinal)) return SyncState.Stale;
        return resources.Any(r => r.Availability is ResourceAvailability.Missing or ResourceAvailability.ExcludedByValidation)
            ? SyncState.CompleteWithMissing
            : SyncState.Complete;
    }

    public static SourceKind Classify(string fullPath, string? cacheFolder, string? penumbraFolder)
    {
        if (!string.IsNullOrEmpty(cacheFolder) && ExportPathPolicy.IsWithin(cacheFolder, fullPath)) return SourceKind.PlayerSyncCache;
        if (!string.IsNullOrEmpty(penumbraFolder) && ExportPathPolicy.IsWithin(penumbraFolder, fullPath)) return SourceKind.PenumbraModDirectory;
        return SourceKind.Other;
    }
}
