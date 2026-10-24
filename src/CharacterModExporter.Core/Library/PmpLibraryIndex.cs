using System.Text.Json;

namespace CharacterModExporter.Core.Library;

/// <summary>The saved catalogue of a library folder. Stored in the plugin's own configuration folder, never in the library.</summary>
public sealed class PmpLibraryIndexFile
{
    /// <summary>Raised whenever what is stored per package changes; an index with another version is rebuilt.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string LibraryFolder { get; set; } = string.Empty;
    public DateTimeOffset BuiltAtUtc { get; set; }
    public List<PmpPackage> Packages { get; set; } = [];
}

public sealed record IndexProgress(int Done, int Total, string? Current);

public sealed record IndexResult(PmpLibraryIndexFile Index, int Read, int Reused, int Removed, List<string> Problems);

/// <summary>
/// Builds and refreshes the catalogue of a user-selected folder of original <c>.pmp</c> packages. Only reads the
/// library; a package is read again when its size or modification time changed, or when the index format changed.
/// </summary>
public static class PmpLibraryIndexer
{
    public static IndexResult Refresh(string libraryFolder, PmpLibraryIndexFile? previous, IProgress<IndexProgress>? progress = null,
        CancellationToken token = default)
    {
        var problems = new List<string>();
        var root = Path.GetFullPath(libraryFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The library folder does not exist: {root}");

        var reusable = previous != null && previous.Format == PmpLibraryIndexFile.CurrentFormat
                                        && string.Equals(Path.GetFullPath(previous.LibraryFolder), root, StringComparison.OrdinalIgnoreCase)
            ? previous.Packages.Where(p => p.Error == null).ToDictionary(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PmpPackage>(StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = FileAttributes.ReparsePoint };
            files.AddRange(Directory.EnumerateFiles(root, "*.pmp", options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add("The library folder could not be listed completely: " + ex.Message);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        var index = new PmpLibraryIndexFile { LibraryFolder = root, BuiltAtUtc = DateTimeOffset.UtcNow };
        int read = 0, reused = 0;
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var full = files[i];
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            progress?.Report(new IndexProgress(i, files.Count, relative));
            var fi = new FileInfo(full);
            if (reusable.TryGetValue(relative, out var cached) && cached.Size == fi.Length && cached.LastWriteUtc == fi.LastWriteTimeUtc)
            {
                index.Packages.Add(cached);
                reused++;
                continue;
            }

            var package = PmpReader.Read(full, relative, token);
            if (package.Error != null) problems.Add($"{relative}: {package.Error}");
            index.Packages.Add(package);
            read++;
        }

        progress?.Report(new IndexProgress(files.Count, files.Count, null));
        var removed = reusable.Keys.Count(k => !index.Packages.Exists(p => string.Equals(p.RelativePath, k, StringComparison.OrdinalIgnoreCase)));
        return new IndexResult(index, read, reused, removed, problems);
    }

    public static PmpLibraryIndexFile? Load(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath)) return null;
            using var s = File.OpenRead(indexPath);
            var index = JsonSerializer.Deserialize<PmpLibraryIndexFile>(s);
            return index?.Format == PmpLibraryIndexFile.CurrentFormat ? index : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the index next to the plugin configuration (write to a temp file, then replace).</summary>
    public static void Save(string indexPath, PmpLibraryIndexFile index)
    {
        if (!string.IsNullOrEmpty(index.LibraryFolder) && PlayerSyncExport.Contracts.ExportPathPolicy.IsWithin(index.LibraryFolder, Path.GetFullPath(indexPath)))
            throw new InvalidOperationException("The index is never written inside the library folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        var temp = indexPath + ".tmp";
        using (var s = File.Create(temp))
            JsonSerializer.Serialize(s, index);
        File.Move(temp, indexPath, overwrite: true);
    }
}
