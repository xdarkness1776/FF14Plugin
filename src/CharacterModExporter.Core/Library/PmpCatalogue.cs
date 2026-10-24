using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CharacterModExporter.Core.Library;

/// <summary>Names the algorithm behind a hash so hashes of different algorithms are never compared.</summary>
public enum HashAlgorithmKind
{
    /// <summary>SHA-1 of a file's content: what PlayerSync names its cache files by and sends to other clients.</summary>
    Sha1,
    /// <summary>SHA-256, used by this plugin for whole packages and copy verification.</summary>
    Sha256,
}

/// <summary>A hash together with its algorithm. Uppercase hex.</summary>
public readonly record struct ContentHash(HashAlgorithmKind Algorithm, string Hex)
{
    /// <summary>True only for the same algorithm and the same value.</summary>
    public bool SameAs(ContentHash other)
        => Algorithm == other.Algorithm && string.Equals(Hex, other.Hex, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Algorithm}:{Hex}";
}

/// <summary>One group of a Penumbra mod (a <c>group_XXX_name.json</c>).</summary>
public sealed class PmpGroup
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Single, Multi, Imc or Combining, as written in the file.</summary>
    public string Type { get; set; } = string.Empty;

    public int Priority { get; set; }
    public long DefaultSettings { get; set; }
    public List<string> Options { get; set; } = [];

    /// <summary>The group file inside the archive.</summary>
    public string SourceEntry { get; set; } = string.Empty;
}

/// <summary>
/// One container of redirections: the default option, an option of a Single or Multi group, or one container of a
/// Combining group. Imc groups change meta data only and have no file containers.
/// </summary>
public sealed class PmpContainer
{
    /// <summary>Null for the default option (<c>default_mod.json</c>).</summary>
    public string? Group { get; set; }

    /// <summary>Option name, or the container name of a Combining group (which may be empty).</summary>
    public string? Option { get; set; }

    public int Priority { get; set; }
    public int ManipulationCount { get; set; }

    [JsonIgnore]
    public string Label => Group == null ? "Default" : string.IsNullOrEmpty(Option) ? Group : $"{Group} / {Option}";
}

/// <summary>One game path a container redirects to a file inside the package.</summary>
public sealed class PmpPayload
{
    public int Container { get; set; }

    /// <summary>Normalized game path (lowercase, forward slashes).</summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>The relative file path as the package writes it.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The archive entry that holds the file; null when the package does not contain it.</summary>
    public string? Entry { get; set; }

    /// <summary>SHA-1 of the file content (comparable with PlayerSync's hashes). Null when the file is absent.</summary>
    public string? Sha1 { get; set; }

    /// <summary>SHA-256 of the file content.</summary>
    public string? Sha256 { get; set; }

    public long Size { get; set; }

    [JsonIgnore]
    public ContentHash? ContentSha1 => Sha1 == null ? null : new ContentHash(HashAlgorithmKind.Sha1, Sha1);
}

/// <summary>A game path a container swaps to another game path.</summary>
public sealed class PmpSwap
{
    public int Container { get; set; }
    public string GamePath { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
}

/// <summary>Everything the catalogue knows about one original <c>.pmp</c> file. Read only from the file itself.</summary>
public sealed class PmpPackage
{
    /// <summary>Path relative to the library folder, with forward slashes.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>The number at the start of the file name, exactly as written (leading zeros kept); null if there is none.</summary>
    public string? NumberText { get; set; }

    public long? Number { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>SHA-256 of the whole package file. A package fingerprint, never compared with asset hashes.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Website { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    public int? FileVersion { get; set; }

    public List<PmpGroup> Groups { get; set; } = [];
    public List<PmpContainer> Containers { get; set; } = [];
    public List<PmpPayload> Payloads { get; set; } = [];
    public List<PmpSwap> Swaps { get; set; } = [];

    /// <summary>Archive entries no container references (previews, leftovers).</summary>
    public int UnreferencedEntries { get; set; }

    /// <summary>Problems found while reading. A package with problems is still listed.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Set when the package could not be read at all.</summary>
    public string? Error { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(FileName) : Name!;
}

/// <summary>
/// Reads an original Penumbra mod package without changing or extracting it. Covers the default option and every
/// group type Penumbra writes (Single, Multi, Imc, Combining), following Penumbra's own schema files.
/// </summary>
public static class PmpReader
{
    /// <summary>JSON files larger than this are not parsed.</summary>
    public const long MaxJsonBytes = 32L * 1024 * 1024;

    /// <summary>Archives with more entries than this are rejected.</summary>
    public const int MaxEntries = 200_000;

    /// <summary>Single payload entries larger than this (uncompressed) are not hashed.</summary>
    public const long MaxPayloadBytes = 4L * 1024 * 1024 * 1024;

    private static readonly Regex LeadingNumber = new(@"^\s*[\[(#]?\s*(\d{1,15})(?=$|[\s._\-)\]#,])", RegexOptions.CultureInvariant);

    /// <summary>The number a file name starts with, e.g. "0042 Outfit.pmp" gives "0042". Null when it starts otherwise.</summary>
    public static string? ParseNumber(string fileName)
    {
        var m = LeadingNumber.Match(Path.GetFileNameWithoutExtension(fileName));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Normalizes a relative file path or archive entry name for lookups.</summary>
    public static string NormalizeEntry(string path)
        => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    public static PmpPackage Read(string fullPath, string relativePath, CancellationToken token = default)
    {
        var fi = new FileInfo(fullPath);
        var package = new PmpPackage
        {
            RelativePath = relativePath.Replace('\\', '/'),
            FileName = fi.Name,
            NumberText = ParseNumber(fi.Name),
            Size = fi.Exists ? fi.Length : 0,
            LastWriteUtc = fi.Exists ? fi.LastWriteTimeUtc : default,
        };
        if (package.NumberText != null && long.TryParse(package.NumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            package.Number = number;

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            package.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            ReadArchive(zip, package, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
        {
            package.Error = ex is InvalidDataException ? "Not a readable zip archive: " + ex.Message : ex.Message;
        }

        return package;
    }

    private static void ReadArchive(ZipArchive zip, PmpPackage package, CancellationToken token)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            package.Error = $"The archive has {zip.Entries.Count} entries, more than the {MaxEntries} this reader accepts.";
            return;
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            entries.TryAdd(NormalizeEntry(e.FullName), e);
        }

        if (entries.TryGetValue("meta.json", out var metaEntry) && ParseJson(metaEntry, package) is { } meta)
        {
            using (meta)
            {
                var root = meta.RootElement;
                package.Name = Str(root, "Name");
                package.Author = Str(root, "Author");
                package.Version = Str(root, "Version");
                package.Website = Str(root, "Website");
                package.Description = Str(root, "Description");
                if (root.TryGetProperty("FileVersion", out var fv) && fv.TryGetInt32(out var v)) package.FileVersion = v;
                if (root.TryGetProperty("ModTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    package.Tags = [.. tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Where(t => t.Length > 0)];
            }
        }
        else
        {
            package.Warnings.Add("meta.json is missing or unreadable.");
        }

        if (entries.TryGetValue("default_mod.json", out var defaultEntry) && ParseJson(defaultEntry, package) is { } def)
        {
            using (def)
                AddContainer(package, def.RootElement, null, null, 0);
        }

        foreach (var (name, entry) in entries.Where(kv => kv.Key.IndexOf('/') < 0 && kv.Key.StartsWith("group_", StringComparison.Ordinal)
                     && kv.Key.EndsWith(".json", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (ParseJson(entry, package) is not { } doc) continue;
            using (doc)
                ReadGroup(package, doc.RootElement, entry.FullName);
        }

        if (package.Containers.Count == 0) package.Warnings.Add("No default option or groups were found.");

        // Hash every referenced file once, however many game paths or options use it.
        var hashed = new Dictionary<string, (string? Sha1, string? Sha256, long Size, string? Entry)>(StringComparer.Ordinal);
        foreach (var payload in package.Payloads)
        {
            token.ThrowIfCancellationRequested();
            var key = NormalizeEntry(payload.RelativePath);
            if (!hashed.TryGetValue(key, out var h))
            {
                h = (null, null, 0, null);
                if (entries.TryGetValue(key, out var entry))
                {
                    if (entry.Length > MaxPayloadBytes)
                    {
                        package.Warnings.Add($"{entry.FullName} is larger than {MaxPayloadBytes / (1024 * 1024)} MB and was not hashed.");
                    }
                    else
                    {
                        using var es = entry.Open();
                        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[1024 * 256];
                        long total = 0;
                        int read;
                        while ((read = es.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read;
                            if (total > MaxPayloadBytes) throw new InvalidDataException($"{entry.FullName} expands beyond its declared size.");
                            sha1.AppendData(buffer, 0, read);
                            sha256.AppendData(buffer, 0, read);
                        }

                        h = (Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha256.GetHashAndReset()), total, entry.FullName);
                    }
                }
                else
                {
                    package.Warnings.Add($"{payload.RelativePath} is referenced but not in the archive.");
                }

                hashed[key] = h;
            }

            (payload.Sha1, payload.Sha256, payload.Size, payload.Entry) = h;
        }

        var referenced = hashed.Keys.ToHashSet(StringComparer.Ordinal);
        package.UnreferencedEntries = entries.Keys.Count(k => !referenced.Contains(k) && k != "meta.json" && k != "default_mod.json"
                                                              && !(k.StartsWith("group_", StringComparison.Ordinal) && k.EndsWith(".json", StringComparison.Ordinal)));
    }

    private static void ReadGroup(PmpPackage package, JsonElement root, string entryName)
    {
        var group = new PmpGroup
        {
            Name = Str(root, "Name") ?? Path.GetFileNameWithoutExtension(entryName),
            Type = Str(root, "Type") ?? string.Empty,
            Priority = Int(root, "Priority"),
            SourceEntry = entryName,
        };
        if (root.TryGetProperty("DefaultSettings", out var ds) && ds.TryGetInt64(out var dsv)) group.DefaultSettings = dsv;
        if (root.TryGetProperty("Options", out var options) && options.ValueKind == JsonValueKind.Array)
            group.Options = [.. options.EnumerateArray().Select(o => Str(o, "Name") ?? string.Empty)];
        package.Groups.Add(group);

        switch (group.Type)
        {
            case "Single":
            case "Multi":
                if (options.ValueKind != JsonValueKind.Array) break;
                foreach (var option in options.EnumerateArray())
                    AddContainer(package, option, group.Name, Str(option, "Name"), group.Type == "Multi" ? Int(option, "Priority") : group.Priority);
                break;
            case "Combining":
                if (root.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
                {
                    // Unnamed containers are labelled by their position, which is what selects them (a bit mask of the options).
                    var i = 0;
                    foreach (var container in containers.EnumerateArray())
                    {
                        AddContainer(package, container, group.Name, Str(container, "Name") ?? $"#{i}", group.Priority);
                        i++;
                    }
                }

                break;
            case "Imc":
                // Imc groups only change meta data (attribute masks); they redirect no files.
                break;
            default:
                package.Warnings.Add($"{entryName}: unknown group type \"{group.Type}\".");
                break;
        }
    }

    private static void AddContainer(PmpPackage package, JsonElement element, string? group, string? option, int priority)
    {
        var index = package.Containers.Count;
        var container = new PmpContainer { Group = group, Option = option, Priority = priority };
        if (element.TryGetProperty("Manipulations", out var manips) && manips.ValueKind == JsonValueKind.Array)
            container.ManipulationCount = manips.GetArrayLength();
        package.Containers.Add(container);

        if (element.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in files.EnumerateObject())
            {
                var gamePath = PenumbraPackageBuilder.NormalizeGamePath(file.Name);
                var relative = file.Value.ValueKind == JsonValueKind.String ? file.Value.GetString() : null;
                if (gamePath == null || string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    package.Warnings.Add($"{container.Label}: skipped invalid redirection \"{file.Name}\".");
                    continue;
                }

                package.Payloads.Add(new PmpPayload { Container = index, GamePath = gamePath, RelativePath = relative });
            }
        }

        if (element.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Object)
        {
            foreach (var swap in swaps.EnumerateObject())
            {
                var from = PenumbraPackageBuilder.NormalizeGamePath(swap.Name);
                var to = swap.Value.ValueKind == JsonValueKind.String ? PenumbraPackageBuilder.NormalizeGamePath(swap.Value.GetString()) : null;
                if (from == null || to == null)
                {
                    package.Warnings.Add($"{container.Label}: skipped invalid file swap \"{swap.Name}\".");
                    continue;
                }

                package.Swaps.Add(new PmpSwap { Container = index, GamePath = from, Target = to });
            }
        }
    }

    private static JsonDocument? ParseJson(ZipArchiveEntry entry, PmpPackage package)
    {
        if (entry.Length > MaxJsonBytes)
        {
            package.Warnings.Add($"{entry.FullName} is too large to read.");
            return null;
        }

        try
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            if (ms.Length > MaxJsonBytes) throw new InvalidDataException($"{entry.FullName} expands beyond its declared size.");
            return JsonDocument.Parse(ms.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            package.Warnings.Add($"{entry.FullName} is not valid JSON: {ex.Message}");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
}
