using System.Text.Json;

namespace CharacterModExporter.Core.Library;

/// <summary>A category worked out from a package's own game paths, with the counts behind it.</summary>
public sealed record PackageCategory(string Category, string Evidence, IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// Sorts original packages into the same folders the exporter uses for items (Gear/Body, Character/Hair, VFX, ...),
/// using only the game paths the package itself redirects or swaps. Nothing is guessed from names.
/// </summary>
public static class PackageCategorizer
{
    public const string Uncategorized = "Uncategorized";
    public const string Mixed = "Mixed";

    public static PackageCategory Categorize(PmpPackage package)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gp in package.Payloads.Select(p => p.GamePath).Concat(package.Swaps.Select(s => s.GamePath)).Distinct(StringComparer.Ordinal))
        {
            var key = ItemModGrouper.Classify(gp);
            // Helper files (shaders, skeleton helpers, shared material textures, unknown paths) do not decide a category.
            if (key.Key is "shared:other" || key.Key.StartsWith("common:", StringComparison.Ordinal) && key.Key != "common:deform") continue;
            var folder = key.Key == "common:deform" ? "Character/Body" : key.Folder;
            counts[folder] = counts.GetValueOrDefault(folder) + 1;
        }

        if (counts.Count == 0)
            return new PackageCategory(Uncategorized, "None of the package's game paths belong to a known category.", counts);

        var total = counts.Values.Sum();
        var ordered = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (ordered.Count == 1)
            return new PackageCategory(ordered[0].Key, $"All {total} categorized path(s) are {ordered[0].Key}.", counts);

        // Only one category may win, and only clearly (more than half of the categorized paths); otherwise it is mixed.
        if (ordered[0].Value * 2 > total)
            return new PackageCategory(ordered[0].Key, $"{ordered[0].Value} of {total} categorized path(s) are {ordered[0].Key}; also " +
                                                       string.Join(", ", ordered.Skip(1).Select(kv => $"{kv.Key} ({kv.Value})")) + ".", counts);
        return new PackageCategory(Mixed, "Paths of several categories: " + string.Join(", ", ordered.Select(kv => $"{kv.Key} ({kv.Value})")) + ".", counts);
    }
}

/// <summary>A category or note the user set for a package.</summary>
public sealed class PackageOverride
{
    public string? Category { get; set; }
    public string? Note { get; set; }

    /// <summary>The library path when the override was saved, for display only; the key is the package SHA-256.</summary>
    public string? LastKnownPath { get; set; }
}

/// <summary>
/// The user's own decisions about original packages, kept in the plugin's configuration folder and never written into
/// the packages. Keyed by the package's SHA-256 so renaming or moving a package keeps them.
/// </summary>
public sealed class PackageOverrideStore
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    /// <summary>Package SHA-256 to the user's override.</summary>
    public Dictionary<string, PackageOverride> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Character key ("Name@World") to the SHA-256 of packages the user confirmed for that character.</summary>
    public Dictionary<string, HashSet<string>> Confirmed { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Set when the saved file could not be read; saving then keeps a backup of it instead of losing it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadError { get; private set; }

    public static string CharacterKey(string name, string? world) => string.IsNullOrEmpty(world) ? name : $"{name}@{world}";

    // The window asks for categories every frame and exports read them on a background thread.
    private readonly object _lock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PackageCategory> _detected = new(StringComparer.OrdinalIgnoreCase);

    public (string Category, bool FromUser, string Evidence) CategoryOf(PmpPackage package)
    {
        lock (_lock)
        {
            if (Packages.TryGetValue(package.Sha256, out var o) && !string.IsNullOrWhiteSpace(o.Category))
                return (o.Category!, true, "Set by you.");
        }

        // A package's SHA-256 fixes its content, so its detected category never changes.
        var c = _detected.GetOrAdd(package.Sha256, _ => PackageCategorizer.Categorize(package));
        return (c.Category, false, c.Evidence);
    }

    public void SetCategory(PmpPackage package, string? category)
    {
        lock (_lock)
            SetCategoryCore(package, category);
    }

    private void SetCategoryCore(PmpPackage package, string? category)
    {
        if (!Packages.TryGetValue(package.Sha256, out var o)) Packages[package.Sha256] = o = new PackageOverride();
        o.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        o.LastKnownPath = package.RelativePath;
        if (o.Category == null && o.Note == null) Packages.Remove(package.Sha256);
    }

    public IReadOnlySet<string> ConfirmedPaths(string characterKey, IEnumerable<PmpPackage> packages)
    {
        lock (_lock)
        {
            if (!Confirmed.TryGetValue(characterKey, out var hashes)) return new HashSet<string>();
            return packages.Where(p => hashes.Contains(p.Sha256)).Select(p => p.RelativePath).ToHashSet(StringComparer.Ordinal);
        }
    }

    public void SetConfirmed(string characterKey, PmpPackage package, bool confirmed)
    {
        lock (_lock)
            SetConfirmedCore(characterKey, package, confirmed);
    }

    private void SetConfirmedCore(string characterKey, PmpPackage package, bool confirmed)
    {
        if (!Confirmed.TryGetValue(characterKey, out var set)) Confirmed[characterKey] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (confirmed) set.Add(package.Sha256);
        else set.Remove(package.Sha256);
        if (set.Count == 0) Confirmed.Remove(characterKey);
    }

    public static PackageOverrideStore Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PackageOverrideStore();
            using var s = File.OpenRead(path);
            var store = JsonSerializer.Deserialize<PackageOverrideStore>(s) ?? new PackageOverrideStore();
            // Re-create the dictionaries with the intended comparers.
            store.Packages = new Dictionary<string, PackageOverride>(store.Packages, StringComparer.OrdinalIgnoreCase);
            store.Confirmed = store.Confirmed.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PackageOverrideStore { LoadError = ex.Message };
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (LoadError != null && File.Exists(path))
        {
            File.Copy(path, path + $".unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}.bak", overwrite: false);
            LoadError = null;
        }

        var temp = path + ".tmp";
        lock (_lock)
        {
            using var s = File.Create(temp);
            JsonSerializer.Serialize(s, this, new JsonSerializerOptions { WriteIndented = true });
        }

        File.Move(temp, path, overwrite: true);
    }
}
