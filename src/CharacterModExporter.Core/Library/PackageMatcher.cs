using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core.Library;

/// <summary>
/// How well an original package corresponds to what the character has applied. There are deliberately no
/// percentages: each status is defined by what was compared.
/// </summary>
public enum MatchStatus
{
    /// <summary>
    /// Every file and swap of each option the character uses (plus the default option) was found on the character with the
    /// same content hash, nothing contradicts it, and no other package explains the same files.
    /// </summary>
    Exact,
    /// <summary>
    /// Some files match by content hash and none contradicts, but not every file of the used options was seen on the
    /// character (the game only loads what is on screen: other races, other slots, unplayed animations).
    /// </summary>
    Strong,
    /// <summary>
    /// Some files match, but at other paths of the same options the character has different content (another version of
    /// the mod, or another mod overriding those paths).
    /// </summary>
    Partial,
    /// <summary>Every file that matches is also matched by another package, so the files cannot tell which one is used.</summary>
    Ambiguous,
    /// <summary>A character resource with a content hash that no package in the library contains.</summary>
    Unmatched,
    /// <summary>A character resource that cannot be compared: no file, no hash, or a compressed copy whose original hash is unknown.</summary>
    Unresolvable,
    /// <summary>The user confirmed this package for the character. The computed status is kept next to it.</summary>
    UserConfirmed,
}

/// <summary>A character resource reduced to what matching needs.</summary>
public sealed record CharacterResource(
    IReadOnlyList<string> GamePaths,
    ContentHash? SourceHash,
    bool SourceHashKnown,
    string? FileSwapTarget,
    ResourceAvailability Availability)
{
    /// <summary>
    /// Builds the matching view of a resource from the export state.
    /// </summary>
    /// <param name="sourceHashKnown">
    /// False when the hash may be of a compressed copy rather than of the original file (the stock source cannot tell
    /// whether PlayerSync substituted a compressed texture). The PlayerSync export IPC always reports the original hash.
    /// </param>
    public static CharacterResource From(ResourceDto r, bool sourceHashKnown)
    {
        var paths = r.GamePaths.Select(PenumbraPackageBuilder.NormalizeGamePath).Where(p => p != null).Select(p => p!).ToList();
        var hash = ExportPathPolicy.IsSha1Hex(r.Hash) ? new ContentHash(HashAlgorithmKind.Sha1, r.Hash!.ToUpperInvariant()) : (ContentHash?)null;
        var swap = PenumbraPackageBuilder.NormalizeGamePath(r.FileSwapTarget);
        return new CharacterResource(paths, hash, sourceHashKnown, swap, r.Availability);
    }
}

public sealed class PackageMatch
{
    public required PmpPackage Package { get; init; }
    public MatchStatus Status { get; set; }

    /// <summary>The status the evidence gives, kept when the user overrides it.</summary>
    public MatchStatus ComputedStatus { get; set; }

    /// <summary>Game paths whose content hash equals the package's file.</summary>
    public List<string> MatchedPaths { get; } = [];

    /// <summary>File swaps of the package that the character also has.</summary>
    public List<string> MatchedSwaps { get; } = [];

    /// <summary>Game paths where the character has different content than this package's used options provide.</summary>
    public List<string> ConflictPaths { get; } = [];

    /// <summary>Texture paths that differ but may be a compressed copy of this package's file; neither proof nor disproof.</summary>
    public List<string> CompressedUnverifiedPaths { get; } = [];

    /// <summary>Files and swaps of the used options that the character did not have (not loaded, or not applied).</summary>
    public int UnseenCount { get; set; }

    /// <summary>The options (containers) that have at least one match, plus the default option when it has content.</summary>
    public List<string> ActiveOptions { get; } = [];

    /// <summary>Other packages that match the same files.</summary>
    public List<string> SharedWith { get; } = [];

    /// <summary>Other packages with the same SHA-256 (byte-identical copies).</summary>
    public List<string> IdenticalCopies { get; } = [];

    public List<string> Reasons { get; } = [];
}

public sealed class ResourceMatch
{
    public required CharacterResource Resource { get; init; }
    public MatchStatus Status { get; set; }
    public List<string> Packages { get; } = [];
    public string? Reason { get; set; }
}

public sealed class MatchReport
{
    public List<PackageMatch> Packages { get; } = [];
    public List<ResourceMatch> Resources { get; } = [];
}

/// <summary>
/// Matches the character's applied resources against the catalogue. Only SHA-1 is compared with SHA-1 (PlayerSync's
/// content hashes against the content hash of each file inside a package); whole-package SHA-256 values are used only
/// to recognize identical copies. A matching resource is evidence for a package, never proof that the whole package is
/// what the character uses.
/// </summary>
public static class PackageMatcher
{
    /// <param name="confirmed">Relative paths of packages the user confirmed for this character.</param>
    public static MatchReport Match(IReadOnlyList<CharacterResource> resources, IReadOnlyList<PmpPackage> packages,
        IReadOnlySet<string>? confirmed = null)
    {
        var report = new MatchReport();
        var byPath = new Dictionary<string, CharacterResource>(StringComparer.Ordinal);
        foreach (var r in resources)
        foreach (var gp in r.GamePaths)
            byPath.TryAdd(gp, r);

        // (game path, SHA-1) to the packages providing it with that content, to find files more than one package explains.
        var providers = new Dictionary<(string, string), HashSet<string>>();
        var swapProviders = new Dictionary<(string, string), HashSet<string>>();
        var candidates = new List<(PackageMatch Match, HashSet<(string, string)> Keys, HashSet<(string, string)> SwapKeys)>();

        foreach (var package in packages)
        {
            if (package.Error != null) continue;
            var match = new PackageMatch { Package = package };
            var keys = new HashSet<(string, string)>();
            var swapKeys = new HashSet<(string, string)>();
            var active = new HashSet<int>();

            foreach (var p in package.Payloads)
            {
                if (p.Sha1 == null || !byPath.TryGetValue(p.GamePath, out var r) || r.SourceHash is not { } h) continue;
                if (h.SameAs(new ContentHash(HashAlgorithmKind.Sha1, p.Sha1))) active.Add(p.Container);
            }

            foreach (var s in package.Swaps)
                if (byPath.TryGetValue(s.GamePath, out var r) && r.FileSwapTarget == s.Target)
                    active.Add(s.Container);

            if (active.Count == 0) continue;
            // The default option is always applied with the mod.
            var defaultIndex = package.Containers.FindIndex(c => c.Group == null);
            if (defaultIndex >= 0 && (package.Payloads.Exists(p => p.Container == defaultIndex) || package.Swaps.Exists(s => s.Container == defaultIndex)))
                active.Add(defaultIndex);

            // A path can be provided by several used options; it only counts against the package when none of them matches.
            var payloadsByPath = package.Payloads.Where(p => active.Contains(p.Container)).GroupBy(p => p.GamePath, StringComparer.Ordinal);
            foreach (var group in payloadsByPath)
            {
                var gp = group.Key;
                if (!byPath.TryGetValue(gp, out var r) || r.SourceHash is not { } h)
                {
                    match.UnseenCount++;
                    continue;
                }

                var hit = group.FirstOrDefault(p => p.Sha1 != null && h.SameAs(new ContentHash(HashAlgorithmKind.Sha1, p.Sha1)));
                if (hit != null)
                {
                    match.MatchedPaths.Add(gp);
                    var key = (gp, hit.Sha1!.ToUpperInvariant());
                    keys.Add(key);
                    Add(providers, key, package.RelativePath);
                }
                else if (!r.SourceHashKnown && gp.EndsWith(".tex", StringComparison.Ordinal))
                {
                    match.CompressedUnverifiedPaths.Add(gp);
                }
                else
                {
                    match.ConflictPaths.Add(gp);
                }
            }

            foreach (var group in package.Swaps.Where(s => active.Contains(s.Container)).GroupBy(s => s.GamePath, StringComparer.Ordinal))
            {
                if (byPath.TryGetValue(group.Key, out var r) && group.Any(s => s.Target == r.FileSwapTarget))
                {
                    match.MatchedSwaps.Add(group.Key);
                    var key = (group.Key, r.FileSwapTarget!);
                    swapKeys.Add(key);
                    Add(swapProviders, key, package.RelativePath);
                }
                else if (byPath.TryGetValue(group.Key, out var other) && (other.SourceHash != null || other.FileSwapTarget != null))
                {
                    match.ConflictPaths.Add(group.Key);
                }
                else
                {
                    match.UnseenCount++;
                }
            }

            match.ActiveOptions.AddRange(active.Order().Select(i => package.Containers[i].Label));
            candidates.Add((match, keys, swapKeys));
        }

        foreach (var (match, keys, swapKeys) in candidates)
        {
            var self = match.Package.RelativePath;
            var unique = keys.Any(k => providers[k].Count == 1) || swapKeys.Any(k => swapProviders[k].Count == 1);
            var shared = keys.SelectMany(k => providers[k]).Concat(swapKeys.SelectMany(k => swapProviders[k]))
                .Where(p => p != self).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            match.SharedWith.AddRange(shared);
            match.IdenticalCopies.AddRange(candidates.Where(c => c.Match.Package.RelativePath != self && c.Match.Package.Sha256 == match.Package.Sha256)
                .Select(c => c.Match.Package.RelativePath));

            MatchStatus status;
            if (match.ConflictPaths.Count > 0)
            {
                status = MatchStatus.Partial;
                match.Reasons.Add($"{match.ConflictPaths.Count} path(s) of the used options have different content on the character.");
            }
            else if (!unique)
            {
                status = MatchStatus.Ambiguous;
                match.Reasons.Add(match.IdenticalCopies.Count > 0
                    ? "Byte-identical copies of this package are in the library."
                    : "Every matching file is also in another package.");
            }
            else if (match.UnseenCount == 0 && match.CompressedUnverifiedPaths.Count == 0)
            {
                status = MatchStatus.Exact;
            }
            else
            {
                status = MatchStatus.Strong;
                if (match.UnseenCount > 0) match.Reasons.Add($"{match.UnseenCount} file(s) or swap(s) of the used options were not seen on the character.");
                if (match.CompressedUnverifiedPaths.Count > 0)
                    match.Reasons.Add($"{match.CompressedUnverifiedPaths.Count} texture(s) differ but may be compressed copies; they are not counted against the package.");
            }

            match.ComputedStatus = status;
            match.Status = confirmed != null && confirmed.Contains(self) ? MatchStatus.UserConfirmed : status;
            report.Packages.Add(match);
        }

        // One sort (List.Sort is not stable): user-confirmed first, then by status, most matched files, path.
        static int Rank(MatchStatus s) => s == MatchStatus.UserConfirmed ? -1 : (int)s;
        report.Packages.Sort((a, b) => Rank(a.Status) != Rank(b.Status) ? Rank(a.Status).CompareTo(Rank(b.Status))
            : b.MatchedPaths.Count != a.MatchedPaths.Count ? b.MatchedPaths.Count.CompareTo(a.MatchedPaths.Count)
            : string.Compare(a.Package.RelativePath, b.Package.RelativePath, StringComparison.OrdinalIgnoreCase));

        foreach (var r in resources)
        {
            var rm = new ResourceMatch { Resource = r };
            if (r.FileSwapTarget != null)
            {
                rm.Packages.AddRange(r.GamePaths.SelectMany(gp => swapProviders.TryGetValue((gp, r.FileSwapTarget), out var s) ? s : []).Distinct(StringComparer.Ordinal));
                rm.Status = rm.Packages.Count > 0 ? MatchStatus.Exact : MatchStatus.Unmatched;
            }
            else if (r.SourceHash is not { } h || r.Availability is ResourceAvailability.Missing or ResourceAvailability.ExcludedByValidation)
            {
                rm.Status = MatchStatus.Unresolvable;
                rm.Reason = r.SourceHash == null ? "No content hash to compare." : "PlayerSync did not apply this file.";
                if (r.SourceHash is { } hh) rm.Packages.AddRange(Providers(providers, r.GamePaths, hh));
                if (rm.Packages.Count > 0) rm.Status = MatchStatus.Exact;
            }
            else
            {
                rm.Packages.AddRange(Providers(providers, r.GamePaths, h));
                rm.Status = rm.Packages.Count switch
                {
                    0 when !r.SourceHashKnown && r.GamePaths.Any(gp => gp.EndsWith(".tex", StringComparison.Ordinal)) => MatchStatus.Unresolvable,
                    0 => MatchStatus.Unmatched,
                    1 => MatchStatus.Exact,
                    _ => MatchStatus.Ambiguous,
                };
                if (rm.Status == MatchStatus.Unresolvable) rm.Reason = "May be a compressed copy; its original hash is unknown.";
            }

            report.Resources.Add(rm);
        }

        return report;
    }

    private static IEnumerable<string> Providers(Dictionary<(string, string), HashSet<string>> providers, IEnumerable<string> paths, ContentHash h)
        => h.Algorithm != HashAlgorithmKind.Sha1 ? []
            : paths.SelectMany(gp => providers.TryGetValue((gp, h.Hex.ToUpperInvariant()), out var s) ? s : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    private static void Add<TKey>(Dictionary<TKey, HashSet<string>> map, TKey key, string value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }
}
