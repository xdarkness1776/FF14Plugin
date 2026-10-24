using System.Text.Json;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core.Library;

/// <summary>
/// The user's library of original packages: its saved index, refreshing it, the user's overrides, and matching
/// characters (live or from a saved MCDF) against it. The library folder is only ever read.
/// </summary>
public sealed class PackageLibrary
{
    private readonly string _indexPath;
    private readonly string _overridesPath;
    private readonly object _gate = new();
    private Task _refresh = Task.CompletedTask;
    private CancellationTokenSource _cts = new();

    /// <param name="configDirectory">The plugin's own configuration folder; the index and overrides are saved there.</param>
    public PackageLibrary(string configDirectory)
    {
        _indexPath = Path.Combine(configDirectory, "package-library-index.json");
        _overridesPath = Path.Combine(configDirectory, "package-overrides.json");
        Index = PmpLibraryIndexer.Load(_indexPath);
        Overrides = PackageOverrideStore.Load(_overridesPath);
    }

    public PmpLibraryIndexFile? Index { get; private set; }
    public PackageOverrideStore Overrides { get; }
    public IndexProgress? Progress { get; private set; }
    public IndexResult? LastResult { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRefreshing => !_refresh.IsCompleted;

    /// <summary>True when the saved index belongs to <paramref name="folder"/>.</summary>
    public bool IsIndexOf(string? folder)
        => Index != null && !string.IsNullOrWhiteSpace(folder)
                         && string.Equals(ExportPathPolicy.NormalizeFull(Index.LibraryFolder), ExportPathPolicy.NormalizeFull(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks a library folder. Returns null when it can be used, otherwise a message.</summary>
    public static string? ValidateFolder(string? folder, IEnumerable<string?> mustNotOverlap)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose the folder that holds your original .pmp files.";
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Not a valid folder path: " + ex.Message;
        }

        if (!Directory.Exists(full)) return $"'{full}' does not exist.";
        foreach (var other in mustNotOverlap)
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            if (ExportPathPolicy.IsWithin(other, full) || ExportPathPolicy.IsWithin(full, other))
                return $"The library folder must not contain or be inside '{other}' (the export folder).";
        }

        return null;
    }

    public Task RefreshAsync(string folder)
    {
        lock (_gate)
        {
            if (!_refresh.IsCompleted) return _refresh;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            LastError = null;
            var previous = Index;
            _refresh = Task.Run(() =>
            {
                try
                {
                    var result = PmpLibraryIndexer.Refresh(folder, previous, new IndexProgressReporter(p => Progress = p), token);
                    PmpLibraryIndexer.Save(_indexPath, result.Index);
                    Index = result.Index;
                    LastResult = result;
                }
                catch (OperationCanceledException)
                {
                    LastError = "Indexing was cancelled; the previous index is kept.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                {
                    LastError = ex.Message;
                }
                finally
                {
                    Progress = null;
                }
            }, CancellationToken.None);
            return _refresh;
        }
    }

    public void CancelRefresh()
    {
        lock (_gate) _cts.Cancel();
    }

    public void SaveOverrides() => Overrides.Save(_overridesPath);

    /// <summary>
    /// Matches a live character. Resources the stock source read from PlayerSync's cache may be compressed copies, and
    /// files the character no longer uses are left out: the live report is about what is applied now.
    /// </summary>
    public MatchReport? MatchLive(CharacterStateDto state, string characterKey)
    {
        if (Index == null) return null;
        var resources = state.Resources.Where(r => r.Activity != ResourceActivity.NoLongerActive)
            .Select(r => CharacterResource.From(r, SourceHashKnown(state, r))).ToList();
        return PackageMatcher.Match(resources, Index.Packages, Overrides.ConfirmedPaths(characterKey, Index.Packages),
            state.LinkDetailsKnown ? MatchContext.ExportIpc : MatchContext.LivePenumbra);
    }

    /// <summary>Matches a saved MCDF snapshot. Its results are a separate report and never mixed with the live one.</summary>
    public MatchReport? MatchSnapshot(McdfSnapshot snapshot)
        => Index == null ? null : PackageMatcher.Match(McdfReader.ToResources(snapshot), Index.Packages, context: MatchContext.Mcdf);

    /// <summary>
    /// The export IPC reports the original hash next to the hash of the compressed copy it used, so its hashes are
    /// always the original's. The stock source only sees the file Penumbra loaded: a texture from PlayerSync's cache
    /// may be a compressed alternate, whose hash says nothing about the original.
    /// </summary>
    internal static bool SourceHashKnown(CharacterStateDto state, ResourceDto r)
        => state.LinkDetailsKnown || r.SourceKind != SourceKind.PlayerSyncCache
                                  || !r.GamePaths.Any(gp => gp.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));

    private sealed class IndexProgressReporter(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }
}

/// <summary>What was exported from the library, written next to the copies as <c>OriginalPackages.json</c>.</summary>
public sealed class OriginalPackagesManifest
{
    public const string FileName = "OriginalPackages.json";

    /// <summary>1: first format. 2: adds EvidenceCoverage, EvidenceDescription and per-package evidence counts.</summary>
    public int ManifestVersion { get; set; } = 2;

    public string ExporterVersion { get; set; } = string.Empty;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string? World { get; set; }

    /// <summary>"Live" for what the character has applied now, or "MCDF snapshot" with <see cref="SnapshotFile"/>.</summary>
    public string Evidence { get; set; } = "Live";

    public string? SnapshotFile { get; set; }

    /// <summary>Complete, Partial or Unknown: whether the evidence could contain every resource of the character.</summary>
    public string EvidenceCoverage { get; set; } = nameof(MatchCoverage.Unknown);

    public string? EvidenceDescription { get; set; }
    public string LibraryFolder { get; set; } = string.Empty;
    public DateTimeOffset LibraryIndexedAtUtc { get; set; }
    public string HashNote { get; set; } =
        "Packages are copied byte for byte. Sha256 values are of whole package files; matching compared SHA-1 content hashes of the files inside packages with PlayerSync's SHA-1 hashes.";

    public List<OriginalPackageCopy> Packages { get; set; } = [];

    /// <summary>Character resources no package in the library contains, or that could not be compared.</summary>
    public List<ManifestUnmatched> NotFoundInLibrary { get; set; } = [];

    /// <summary>
    /// The new copies plus the entries of an earlier manifest in the same folder for packages not copied this time, so
    /// exporting a few more packages later does not drop the record of the ones already there.
    /// </summary>
    public static List<OriginalPackageCopy> KeepEarlier(string folder, List<OriginalPackageCopy> copies)
    {
        var merged = new List<OriginalPackageCopy>(copies);
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return merged;
            var earlier = JsonSerializer.Deserialize<OriginalPackagesManifest>(File.ReadAllText(path));
            if (earlier == null) return merged;
            var now = copies.Select(c => c.LibraryRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            merged.AddRange(earlier.Packages.Where(p => p.ExportedFile != null && !now.Contains(p.LibraryRelativePath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable earlier manifest is replaced; the copies themselves are untouched.
        }

        return merged;
    }

    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record ManifestUnmatched(List<string> GamePaths, string? Sha1, string Status, string? Reason);
