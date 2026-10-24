using System.Collections.Concurrent;
using System.Security.Cryptography;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

public enum ExportedFileState
{
    NotExported,
    PresentUnverified,
    PresentVerified,
    PresentMismatch,
}

/// <summary>One exportable file (one cache hash), possibly used for several game paths.</summary>
public sealed class AssetRow
{
    public required string CacheHash { get; init; }
    public string? SourceHash { get; init; }
    public required string Extension { get; init; }
    public long? Size { get; init; }
    public ResourceAvailability Availability { get; init; }
    public SourceKind SourceKind { get; init; }
    public bool IsCompressedAlternate { get; init; }

    /// <summary>The most current activity of the resources behind the file; null when the source does not track it.</summary>
    public ResourceActivity? Activity { get; set; }

    public DateTimeOffset? FirstSeenUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }

    public List<string> GamePaths { get; } = [];
    public SortedSet<string> ObjectKinds { get; } = new(StringComparer.Ordinal);
    public ExportedFileState ExportedState { get; set; }
    public bool Selected { get; set; }

    public string FileName => ExportPathPolicy.IsSha1Hex(CacheHash) && ExportPathPolicy.SanitizeExtension(Extension) != null
        ? ExportPathPolicy.CacheFileName(CacheHash, Extension)
        : CacheHash;

    public bool CanExport => Availability == ResourceAvailability.Available && ExportPathPolicy.IsSha1Hex(CacheHash)
        && ExportPathPolicy.SanitizeExtension(Extension) != null;
}

/// <summary>A resource that has no exportable file: a file swap, or a file PlayerSync could not resolve.</summary>
public sealed record UnresolvedRow(string ObjectKind, IReadOnlyList<string> GamePaths, string? Hash, ResourceAvailability Availability, string? FileSwapTarget);

public sealed class AssetInventory
{
    public List<AssetRow> Assets { get; } = [];
    public List<UnresolvedRow> FileSwaps { get; } = [];
    public List<UnresolvedRow> Unresolved { get; } = [];

    public long KnownSelectedBytes => Assets.Where(a => a.Selected && a.CanExport).Sum(a => a.Size ?? 0);

    /// <summary>
    /// Groups the provider's resources by cache hash. Nothing is guessed: rows come only from the
    /// resources PlayerSync reported for this character.
    /// </summary>
    public static AssetInventory Build(CharacterStateDto state)
    {
        var inventory = new AssetInventory();
        var byHash = new Dictionary<string, AssetRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in state.Resources)
        {
            switch (resource.Availability)
            {
                case ResourceAvailability.FileSwap:
                    inventory.FileSwaps.Add(new(resource.ObjectKind, resource.GamePaths, null, resource.Availability, resource.FileSwapTarget));
                    continue;
                case ResourceAvailability.Missing:
                case ResourceAvailability.ExcludedByValidation:
                    inventory.Unresolved.Add(new(resource.ObjectKind, resource.GamePaths, resource.CacheHash ?? resource.Hash, resource.Availability, null));
                    continue;
            }

            var key = resource.CacheHash ?? resource.Hash;
            if (string.IsNullOrEmpty(key))
            {
                inventory.Unresolved.Add(new(resource.ObjectKind, resource.GamePaths, null, ResourceAvailability.Missing, null));
                continue;
            }

            if (!byHash.TryGetValue(key, out var row))
            {
                row = new AssetRow
                {
                    CacheHash = key.ToUpperInvariant(),
                    SourceHash = resource.Hash?.ToUpperInvariant(),
                    Extension = resource.Extension,
                    Size = resource.Size,
                    Availability = resource.Availability,
                    SourceKind = resource.SourceKind,
                    IsCompressedAlternate = resource.IsCompressedAlternate,
                };
                byHash[key] = row;
                inventory.Assets.Add(row);
                row.Activity = resource.Activity;
                row.FirstSeenUtc = resource.FirstSeenUtc;
                row.LastSeenUtc = resource.LastSeenUtc;
            }
            else if (resource.Activity is { } activity && (row.Activity == null || activity < row.Activity))
            {
                row.Activity = activity;
            }

            foreach (var gamePath in resource.GamePaths)
            {
                if (!row.GamePaths.Contains(gamePath, StringComparer.Ordinal)) row.GamePaths.Add(gamePath);
            }
            row.ObjectKinds.Add(resource.ObjectKind);
        }

        inventory.Assets.Sort((a, b) => string.Compare(a.GamePaths.FirstOrDefault(), b.GamePaths.FirstOrDefault(), StringComparison.Ordinal));
        return inventory;
    }

    /// <summary>Marks which files already exist in <paramref name="assetsFolder"/>, verifying them through the cache.</summary>
    public void CompareWithExport(string? assetsFolder, FileVerificationCache cache, bool verify)
    {
        foreach (var row in Assets)
        {
            row.ExportedState = ExportedFileState.NotExported;
            if (assetsFolder == null || !row.CanExport) continue;
            var path = Path.Combine(assetsFolder, row.FileName);
            if (!File.Exists(path)) continue;
            if (!verify)
            {
                row.ExportedState = cache.TryGetCached(path, out var cached)
                    ? (string.Equals(cached, row.CacheHash, StringComparison.OrdinalIgnoreCase) ? ExportedFileState.PresentVerified : ExportedFileState.PresentMismatch)
                    : ExportedFileState.PresentUnverified;
                continue;
            }

            var sha1 = cache.GetSha1(path);
            row.ExportedState = sha1 == null ? ExportedFileState.PresentUnverified
                : string.Equals(sha1, row.CacheHash, StringComparison.OrdinalIgnoreCase) ? ExportedFileState.PresentVerified
                : ExportedFileState.PresentMismatch;
        }
    }
}

/// <summary>
/// Remembers SHA-1 results per file so the UI never rehashes unchanged files.
/// An entry is reused only while the file's length and last write time are unchanged.
/// </summary>
public sealed class FileVerificationCache
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc, string Sha1)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int ComputeCount { get; private set; }

    public bool TryGetCached(string path, out string? sha1)
    {
        sha1 = null;
        var fi = new FileInfo(path);
        if (!fi.Exists || !_entries.TryGetValue(fi.FullName, out var entry)) return false;
        if (entry.Length != fi.Length || entry.LastWriteUtc != fi.LastWriteTimeUtc) return false;
        sha1 = entry.Sha1;
        return true;
    }

    public string? GetSha1(string path)
    {
        if (TryGetCached(path, out var cached)) return cached;
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var sha1 = Convert.ToHexString(SHA1.HashData(fs));
            ComputeCount++;
            _entries[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, sha1);
            return sha1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Remember(string path, string sha1)
    {
        var fi = new FileInfo(path);
        if (fi.Exists) _entries[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, sha1.ToUpperInvariant());
    }
}
