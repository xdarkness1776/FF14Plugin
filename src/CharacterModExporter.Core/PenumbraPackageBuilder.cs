using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

/// <summary>One file redirection in a package: the game paths it replaces and the exported file that provides them.</summary>
public sealed record PackageFile(string SourcePath, string FileName, IReadOnlyList<string> GamePaths);

public sealed record PackageResult(string? PackagePath, List<string> Problems, List<string> Omitted, int FileCount, int RedirectCount, int SwapCount)
{
    public bool Created => PackagePath != null;
}

/// <summary>
/// Builds a Penumbra mod package (<c>.pmp</c>, a zip archive) with a single default option.
///
/// Format (Penumbra mod file version 3, which current Penumbra still loads and migrates):
/// <c>meta.json</c> with the mod metadata and <c>default_mod.json</c> with <c>Files</c>
/// (game path to relative file), <c>FileSwaps</c> (game path to game path) and <c>Manipulations</c>.
/// Meta manipulations are not included because PlayerSync only carries them in Penumbra's compressed
/// binary form; the package says so in its description.
/// </summary>
public static class PenumbraPackageBuilder
{
    public const int ModFileVersion = 3;
    public const int MaxGamePathLength = 260;
    private const string FilesFolder = "files";

    /// <summary>
    /// Normalizes a game path the way Penumbra stores it (lowercase, forward slashes, no leading slash).
    /// Returns null if Penumbra would reject it.
    /// </summary>
    public static string? NormalizeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var p = gamePath.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (p.Length == 0 || p.Length > MaxGamePathLength) return null;
        if (p.Contains("//", StringComparison.Ordinal) || p.Split('/').Any(s => s is "." or "..")) return null;
        if (p.Any(c => c > 0x7F || char.IsControl(c) || c == ':')) return null;
        if (p.EndsWith('/')) return null;
        return p;
    }

    public static PackageResult Build(string outputDirectory, string modName, string description, string version,
        IReadOnlyList<PackageFile> files, IReadOnlyDictionary<string, string> fileSwaps, IReadOnlyList<string>? tags = null)
    {
        var problems = new List<string>();
        var omitted = new List<string>();
        var redirects = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var swaps = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var usedFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!File.Exists(file.SourcePath))
            {
                omitted.Add($"{file.FileName}: exported file is missing.");
                continue;
            }

            var relative = FilesFolder + "\\" + file.FileName;
            foreach (var raw in file.GamePaths)
            {
                var gamePath = NormalizeGamePath(raw);
                if (gamePath == null)
                {
                    omitted.Add($"{raw}: not a valid game path for Penumbra.");
                    continue;
                }

                if (redirects.TryGetValue(gamePath, out var existing) && !string.Equals(existing, relative, StringComparison.Ordinal))
                {
                    omitted.Add($"{gamePath}: mapped to two different files ({existing}, {relative}); kept the first.");
                    continue;
                }

                redirects[gamePath] = relative;
                usedFiles[file.FileName] = file.SourcePath;
            }
        }

        foreach (var (rawFrom, rawTo) in fileSwaps)
        {
            var from = NormalizeGamePath(rawFrom);
            var to = NormalizeGamePath(rawTo);
            if (from == null || to == null)
            {
                omitted.Add($"{rawFrom} -> {rawTo}: not a valid file swap.");
                continue;
            }

            if (redirects.ContainsKey(from))
            {
                omitted.Add($"{from}: has both a file and a swap; kept the file.");
                continue;
            }

            swaps[from] = to;
        }

        if (redirects.Count == 0 && swaps.Count == 0)
        {
            problems.Add("No redirections could be built, so no package was created.");
            return new(null, problems, omitted, 0, 0, 0);
        }

        var meta = new JsonObject
        {
            ["FileVersion"] = ModFileVersion,
            ["Name"] = modName,
            ["Author"] = "Character Mod Exporter",
            ["Description"] = description,
            ["Version"] = version,
            ["Website"] = string.Empty,
            ["ModTags"] = new JsonArray((tags ?? []).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        };
        var defaultMod = new JsonObject
        {
            ["Name"] = string.Empty,
            ["Description"] = string.Empty,
            ["Files"] = ToJson(redirects),
            ["FileSwaps"] = ToJson(swaps),
            ["Manipulations"] = new JsonArray(),
        };

        var createdDirectory = !Directory.Exists(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var baseName = ExportPathPolicy.SanitizeSegment(modName, "Character", 80);
        var packagePath = ExportPathPolicy.CombineWithin(outputDirectory, baseName + ".pmp");
        for (var i = 2; File.Exists(packagePath); i++)
            packagePath = ExportPathPolicy.CombineWithin(outputDirectory, $"{baseName} ({i}).pmp");

        var tempPath = packagePath + VerifiedFileCopier.TempSuffix;
        try
        {
            using (var zipStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                WriteJson(zip, "meta.json", meta);
                WriteJson(zip, "default_mod.json", defaultMod);
                foreach (var (fileName, source) in usedFiles)
                {
                    // Game resources are mostly already compressed; Fastest keeps packaging quick.
                    zip.CreateEntryFromFile(source, FilesFolder + "/" + fileName, CompressionLevel.Fastest);
                }
            }

            var validation = Validate(tempPath);
            if (validation.Count > 0)
            {
                File.Delete(tempPath);
                RemoveIfEmpty(outputDirectory, createdDirectory);
                problems.AddRange(validation);
                return new(null, problems, omitted, 0, 0, 0);
            }

            File.Move(tempPath, packagePath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch (Exception) { /* best effort */ }
            RemoveIfEmpty(outputDirectory, createdDirectory);
            problems.Add("Could not write the package: " + ex.Message);
            return new(null, problems, omitted, 0, 0, 0);
        }

        return new(packagePath, problems, omitted, usedFiles.Count, redirects.Count, swaps.Count);
    }

    /// <summary>
    /// Re-opens a package and checks it the way Penumbra's loader reads it: both JSON files parse,
    /// the file version is supported, every game path is valid and every redirected file is in the archive.
    /// Returns the problems found (empty when valid).
    /// </summary>
    public static List<string> Validate(string packagePath)
    {
        var problems = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var entries = zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

            if (!entries.TryGetValue("meta.json", out var metaEntry))
            {
                problems.Add("meta.json is missing at the archive root.");
                return problems;
            }

            using (var metaStream = metaEntry.Open())
            {
                var meta = JsonNode.Parse(metaStream)?.AsObject();
                var version = meta?["FileVersion"]?.GetValue<int>();
                if (version is null or < 0 or > 4) problems.Add($"meta.json FileVersion {version} is not supported.");
                if (string.IsNullOrWhiteSpace(meta?["Name"]?.GetValue<string>())) problems.Add("meta.json has no Name.");
            }

            if (!entries.TryGetValue("default_mod.json", out var defaultEntry))
            {
                problems.Add("default_mod.json is missing.");
                return problems;
            }

            using var defaultStream = defaultEntry.Open();
            var defaultMod = JsonNode.Parse(defaultStream)?.AsObject();
            if (defaultMod?["Files"] is JsonObject filesNode)
            {
                foreach (var (gamePath, value) in filesNode)
                {
                    if (NormalizeGamePath(gamePath) != gamePath) problems.Add($"Invalid game path '{gamePath}'.");
                    var rel = value?.GetValue<string>()?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(rel) || !entries.ContainsKey(rel)) problems.Add($"'{gamePath}' points to '{rel}', which is not in the archive.");
                }
            }
            else
            {
                problems.Add("default_mod.json has no Files object.");
            }

            if (defaultMod?["FileSwaps"] is JsonObject swapsNode)
            {
                foreach (var (from, to) in swapsNode)
                {
                    if (NormalizeGamePath(from) != from || NormalizeGamePath(to?.GetValue<string>()) == null)
                        problems.Add($"Invalid file swap '{from}'.");
                }
            }

            if (defaultMod?["Manipulations"] is not (null or JsonArray)) problems.Add("Manipulations must be an array.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            problems.Add("The package could not be read: " + ex.Message);
        }

        return problems;
    }

    /// <summary>The Files map of a package's default option, sorted, or null if it cannot be read.</summary>
    public static SortedDictionary<string, string>? ReadRedirects(string packagePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var entry = zip.GetEntry("default_mod.json");
            if (entry == null) return null;
            using var stream = entry.Open();
            if (JsonNode.Parse(stream)?["Files"] is not JsonObject files) return null;
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in files) map[k] = v?.GetValue<string>() ?? string.Empty;
            return map;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void RemoveIfEmpty(string directory, bool createdByUs)
    {
        try
        {
            if (createdByUs && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception)
        {
            // Leaving an empty folder behind is harmless.
        }
    }

    private static JsonObject ToJson(SortedDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map) obj[k] = v;
        return obj;
    }

    private static void WriteJson(ZipArchive zip, string name, JsonNode node)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
