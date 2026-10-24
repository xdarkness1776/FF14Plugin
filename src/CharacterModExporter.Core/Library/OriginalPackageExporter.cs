using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core.Library;

/// <summary>The result of copying one original package.</summary>
public sealed class OriginalPackageCopy
{
    public string LibraryRelativePath { get; set; } = string.Empty;
    public string? NumberText { get; set; }
    public string? Name { get; set; }

    /// <summary>The copy, relative to the export session folder; null when nothing was written.</summary>
    public string? ExportedFile { get; set; }

    public string Outcome { get; set; } = string.Empty;
    public string? Error { get; set; }
    public long Size { get; set; }

    /// <summary>SHA-256 recorded when the library was indexed.</summary>
    public string IndexedSha256 { get; set; } = string.Empty;

    /// <summary>SHA-256 of the written copy; equal to <see cref="IndexedSha256"/> whenever a copy was kept.</summary>
    public string? CopySha256 { get; set; }

    public DateTimeOffset ExportedAtUtc { get; set; }

    /// <summary>"Live" or "MCDF snapshot": what the match was based on when this package was exported.</summary>
    public string? Evidence { get; set; }

    public string MatchStatus { get; set; } = string.Empty;
    public string? ComputedMatchStatus { get; set; }
    public string? Category { get; set; }
    public bool CategoryFromUser { get; set; }
    public List<string> ActiveOptions { get; set; } = [];
    public int MatchedFiles { get; set; }
    public List<string> Reasons { get; set; } = [];
}

/// <summary>
/// Copies original packages byte for byte from the library into the export. The original is only read; the copy keeps
/// its file name and its folder below the library, is verified with SHA-256 against the indexed value, and an existing
/// file at the destination is never replaced.
/// </summary>
public static class OriginalPackageExporter
{
    public const string FolderName = "Original packages";

    public static async Task<List<OriginalPackageCopy>> CopyAsync(string libraryFolder, string sessionFolder,
        IReadOnlyList<PackageMatch> selected, Func<PmpPackage, (string? Category, bool FromUser)>? categorize,
        IProgress<long>? progress, CancellationToken token)
    {
        var results = new List<OriginalPackageCopy>();
        var root = Path.GetFullPath(libraryFolder);
        var outRoot = Path.Combine(sessionFolder, FolderName);
        if (ExportPathPolicy.IsWithin(root, sessionFolder) || ExportPathPolicy.IsWithin(sessionFolder, root))
            throw new InvalidOperationException("The export folder and the package library must not contain each other.");
        foreach (var match in selected)
        {
            token.ThrowIfCancellationRequested();
            var p = match.Package;
            var (category, fromUser) = categorize?.Invoke(p) ?? (null, false);
            var result = new OriginalPackageCopy
            {
                LibraryRelativePath = p.RelativePath,
                NumberText = p.NumberText,
                Name = p.Name,
                IndexedSha256 = p.Sha256,
                Size = p.Size,
                MatchStatus = match.Status.ToString(),
                ComputedMatchStatus = match.Status == Library.MatchStatus.UserConfirmed ? match.ComputedStatus.ToString() : null,
                Category = category,
                CategoryFromUser = fromUser,
                ActiveOptions = [.. match.ActiveOptions],
                MatchedFiles = match.MatchedPaths.Count,
                Reasons = [.. match.Reasons],
            };
            results.Add(result);

            var segments = p.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(s => s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                result.Outcome = FileExportOutcome.Failed.ToString();
                result.Error = "The package path cannot be reproduced safely in the export folder.";
                continue;
            }

            var source = Path.GetFullPath(Path.Combine([root, .. segments]));
            if (!ExportPathPolicy.IsWithin(root, source))
            {
                result.Outcome = FileExportOutcome.Failed.ToString();
                result.Error = "The package is not inside the library folder.";
                continue;
            }

            string destinationDir;
            try
            {
                destinationDir = outRoot;
                foreach (var dir in segments[..^1]) destinationDir = Path.Combine(destinationDir, dir);
                if (!ExportPathPolicy.IsWithin(outRoot, destinationDir)) throw new IOException("Unsafe destination.");
                Directory.CreateDirectory(destinationDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                result.Outcome = FileExportOutcome.Failed.ToString();
                result.Error = ex.Message;
                continue;
            }

            var fileName = segments[^1];
            var copy = await VerifiedFileCopier.CopyAsync(source, destinationDir, fileName, null, progress, token, expectedSha256: p.Sha256)
                .ConfigureAwait(false);
            result.Outcome = copy.Outcome.ToString();
            result.Error = copy.Error;
            if (copy.Outcome is FileExportOutcome.Copied or FileExportOutcome.AlreadyPresentIdentical)
            {
                result.CopySha256 = copy.Sha256;
                result.ExportedFile = Path.GetRelativePath(sessionFolder, Path.Combine(destinationDir, fileName)).Replace('\\', '/');
            }
        }

        return results;
    }
}
