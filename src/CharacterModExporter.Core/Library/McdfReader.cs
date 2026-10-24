using PlayerSyncExport.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Legacy;

namespace CharacterModExporter.Core.Library;

/// <summary>What is known about one file entry of an MCDF.</summary>
public enum McdfFileStatus
{
    /// <summary>Only the header was read; the stored bytes were not checked.</summary>
    NotRead,
    /// <summary>The stored bytes were read and their SHA-1 matches the hash the entry claims.</summary>
    Verified,
    /// <summary>The stored bytes were read but their SHA-1 differs from the claimed hash.</summary>
    HashMismatch,
    /// <summary>The file ends before this entry's bytes (truncated or damaged).</summary>
    MissingPayload,
    /// <summary>The claimed hash is not a SHA-1 (40 hex digits), so the entry cannot be verified or compared.</summary>
    InvalidHash,
    /// <summary>None of the entry's game paths is a valid game path.</summary>
    InvalidPath,
    /// <summary>Another entry claims one of this entry's game paths with different content; which one applies is unknown.</summary>
    ConflictingGamePath,
}

/// <summary>One file stored in an MCDF: the game paths it was used for, its length, and the SHA-1 the file claims.</summary>
public sealed class McdfFile
{
    public List<string> GamePaths { get; set; } = [];
    public long Length { get; set; }

    /// <summary>The SHA-1 written in the file (PlayerSync's cache hash).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>SHA-1 of the stored bytes, computed while reading; null when they were not read.</summary>
    public string? ComputedSha1 { get; set; }

    public McdfFileStatus Status { get; set; }

    /// <summary>Game paths of the entry that PlayerSync's MCDF writer would have removed (see <see cref="McdfWriterRules"/>).</summary>
    public List<string> WriterExcludedPaths { get; set; } = [];

    /// <summary>Only verified entries are evidence for matching.</summary>
    public bool Verified => Status == McdfFileStatus.Verified;
}

/// <summary>
/// What PlayerSync's MCDF writer leaves out (PlayerSync 1.15.5.9, CharaDataFileHandler.CreatePlayerData): game paths
/// ending in .pap, .tmb or .scd, and .avfx/.atex paths outside "/weapon/" and "/equipment/". It only writes the local
/// player's own files, never those of minions, pets or companions. An MCDF therefore never shows such resources,
/// and their absence from one says nothing about the character.
/// </summary>
public static class McdfWriterRules
{
    public static bool IsExcluded(string gamePath)
    {
        if (gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) || gamePath.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)
                                                                        || gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!gamePath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) && !gamePath.EndsWith(".atex", StringComparison.OrdinalIgnoreCase))
            return false;
        return !gamePath.Contains("/weapon/", StringComparison.OrdinalIgnoreCase) && !gamePath.Contains("/equipment/", StringComparison.OrdinalIgnoreCase);
    }

    public const string Description =
        "PlayerSync's MCDF export leaves out animations (.pap, .tmb), sounds (.scd) and VFX (.avfx, .atex) other than gear and weapon VFX, " +
        "and contains only the player's own files (no minion, pet or companion files).";
}

public sealed class McdfSwap
{
    public List<string> GamePaths { get; set; } = [];
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// A saved PlayerSync character data file (MCDF), read without being applied or extracted. This is a snapshot of
/// one moment and is kept apart from what a character has applied now.
/// </summary>
public sealed class McdfSnapshot
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime FileLastWriteUtc { get; set; }
    public byte Version { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool HasGlamourerData { get; set; }
    public bool HasCustomizePlusData { get; set; }
    public bool HasManipulationData { get; set; }
    public List<McdfFile> Files { get; set; } = [];
    public List<McdfSwap> FileSwaps { get; set; } = [];

    /// <summary>True when every stored file was read and its SHA-1 matched the hash it claims.</summary>
    public bool AllVerified => Files.Count > 0 && Files.TrueForAll(f => f.Verified);

    /// <summary>How many entries have each status.</summary>
    public IReadOnlyDictionary<McdfFileStatus, int> StatusCounts => Files.GroupBy(f => f.Status).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Entries whose game paths the writer would have removed: the file was not written by PlayerSync as is.</summary>
    public int WriterExcludedCount => Files.Count(f => f.WriterExcludedPaths.Count > 0);

    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Reads the MCDF format as PlayerSync 1.15.5.9 writes it (verified against its MareCharaFileHeader and
/// CharaDataFileHandler): the whole file is a K4os LZ4 legacy stream containing "MCDF", a version byte (only 1 exists),
/// an int32 length, that many bytes of UTF-8 JSON, and then the files' bytes in the order the JSON lists them.
/// </summary>
public static class McdfReader
{
    /// <summary>Real headers are a few hundred KB (Glamourer, Customize+ and manipulation data are base64 text).</summary>
    public const int MaxHeaderBytes = 32 * 1024 * 1024;

    public const int MaxFiles = 100_000;

    /// <summary>Largest single stored file; PlayerSync's own files are far smaller.</summary>
    public const long MaxFileBytes = 1L << 30;

    /// <summary>Largest total of stored files; reading is streamed, so this only bounds the time spent.</summary>
    public const long MaxTotalBytes = 16L << 30;

    /// <summary>How far past the stored files the reader looks for extra data.</summary>
    public const int TrailingProbeBytes = 64 * 1024;

    /// <param name="verifyContents">Reads every stored file and checks its SHA-1. Without it only the header is read.</param>
    /// <exception cref="InvalidDataException">The file is not a readable MCDF; the message says why.</exception>
    public static McdfSnapshot Read(string path, bool verifyContents = true, CancellationToken token = default)
    {
        var fi = new FileInfo(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var lz4 = new LZ4Stream(file, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
            var snapshot = ReadFrom(lz4, verifyContents, token);
            snapshot.FilePath = fi.FullName;
            snapshot.FileLastWriteUtc = fi.LastWriteTimeUtc;
            return snapshot;
        }
        catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            // The LZ4 decoder reports damaged blocks with its own exception types.
            throw new InvalidDataException("The file is not a valid compressed MCDF: " + ex.Message, ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>Reads the decompressed MCDF stream.</summary>
    public static McdfSnapshot ReadFrom(Stream decompressed, bool verifyContents, CancellationToken token = default)
    {
        var head = ReadExactly(decompressed, 9);
        if (head[0] != 'M' || head[1] != 'C' || head[2] != 'D' || head[3] != 'F') throw new InvalidDataException("Not an MCDF file.");
        var snapshot = new McdfSnapshot { Version = head[4] };
        if (snapshot.Version != 1) throw new InvalidDataException($"MCDF version {snapshot.Version} is not supported (only version 1 exists).");
        var length = BitConverter.ToInt32(head, 5);
        if (length <= 0 || length > MaxHeaderBytes) throw new InvalidDataException($"MCDF header length {length} is not plausible.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(ReadExactly(decompressed, length));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The MCDF header is not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The MCDF header is not a JSON object.");
            snapshot.Description = Str(root, "Description") ?? string.Empty;
            snapshot.HasGlamourerData = !string.IsNullOrEmpty(Str(root, "GlamourerData"));
            snapshot.HasCustomizePlusData = !string.IsNullOrEmpty(Str(root, "CustomizePlusData"));
            snapshot.HasManipulationData = !string.IsNullOrEmpty(Str(root, "ManipulationData"));

            long total = 0;
            if (root.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                if (files.GetArrayLength() > MaxFiles) throw new InvalidDataException($"The MCDF lists more than {MaxFiles} files.");
                foreach (var f in files.EnumerateArray())
                {
                    var entry = new McdfFile
                    {
                        GamePaths = Paths(f),
                        Length = f.TryGetProperty("Length", out var l) && l.TryGetInt64(out var li) ? li : -1,
                        Hash = (Str(f, "Hash") ?? string.Empty).ToUpperInvariant(),
                    };
                    // Every later entry's position depends on this length, so a bad one makes the rest unreadable.
                    if (entry.Length < 0 || entry.Length > MaxFileBytes)
                        throw new InvalidDataException($"An MCDF file entry has an invalid length ({entry.Length}).");
                    total += entry.Length;
                    if (total > MaxTotalBytes) throw new InvalidDataException($"The MCDF declares more than {MaxTotalBytes >> 30} GB of stored files.");
                    snapshot.Files.Add(entry);
                }
            }

            if (root.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Array)
                foreach (var s in swaps.EnumerateArray())
                    snapshot.FileSwaps.Add(new McdfSwap { GamePaths = Paths(s), Target = Str(s, "FileSwapPath") ?? string.Empty });
        }

        ValidateEntries(snapshot);
        if (!verifyContents) return snapshot;

        var buffer = new byte[1024 * 256];
        for (var i = 0; i < snapshot.Files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var f = snapshot.Files[i];
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var remaining = f.Length;
            while (remaining > 0)
            {
                var read = decompressed.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    var missing = snapshot.Files.Count - i;
                    foreach (var rest in snapshot.Files.Skip(i)) rest.Status = McdfFileStatus.MissingPayload;
                    snapshot.Warnings.Add($"The file ends before all stored files were read; {missing} file(s) have no stored bytes and are not used as evidence.");
                    return snapshot;
                }

                sha1.AppendData(buffer, 0, read);
                remaining -= read;
            }

            f.ComputedSha1 = Convert.ToHexString(sha1.GetHashAndReset());
            // An entry already found unusable keeps that reason; its bytes were still read to reach the next entry.
            if (f.Status != McdfFileStatus.NotRead) continue;
            f.Status = string.Equals(f.ComputedSha1, f.Hash, StringComparison.OrdinalIgnoreCase) ? McdfFileStatus.Verified : McdfFileStatus.HashMismatch;
            if (f.Status == McdfFileStatus.HashMismatch) snapshot.Warnings.Add($"Stored file for {f.GamePaths.FirstOrDefault()} does not match its hash {f.Hash}.");
        }

        var extra = 0;
        while (extra < TrailingProbeBytes)
        {
            var read = decompressed.Read(buffer, 0, Math.Min(buffer.Length, TrailingProbeBytes - extra));
            if (read <= 0) break;
            extra += read;
        }

        if (extra > 0)
            snapshot.Warnings.Add($"The file has {(extra >= TrailingProbeBytes ? "at least " : string.Empty)}{extra} byte(s) after the stored files. They are ignored; the file may have been modified.");
        return snapshot;
    }

    /// <summary>Marks entries that cannot be evidence whatever their bytes are, and notes what the writer leaves out.</summary>
    private static void ValidateEntries(McdfSnapshot snapshot)
    {
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapshot.Files)
        {
            if (!ExportPathPolicy.IsSha1Hex(f.Hash))
            {
                f.Status = McdfFileStatus.InvalidHash;
                snapshot.Warnings.Add($"A stored file for {f.GamePaths.FirstOrDefault() ?? "(no game path)"} has no valid SHA-1 hash and is not used as evidence.");
            }

            var valid = f.GamePaths.Select(PenumbraPackageBuilder.NormalizeGamePath).Where(p => p != null).Select(p => p!).Distinct(StringComparer.Ordinal).ToList();
            if (valid.Count == 0 && f.Status == McdfFileStatus.NotRead)
            {
                f.Status = McdfFileStatus.InvalidPath;
                snapshot.Warnings.Add($"A stored file ({f.Hash}) has no valid game path and is not used as evidence.");
            }

            f.WriterExcludedPaths = valid.Where(McdfWriterRules.IsExcluded).ToList();
            foreach (var gp in valid)
            {
                if (claims.TryGetValue(gp, out var other) && !string.Equals(other, f.Hash, StringComparison.OrdinalIgnoreCase)) conflicting.Add(gp);
                else claims.TryAdd(gp, f.Hash);
            }
        }

        if (conflicting.Count == 0) return;
        foreach (var f in snapshot.Files.Where(f => f.Status == McdfFileStatus.NotRead))
        {
            if (f.GamePaths.Select(PenumbraPackageBuilder.NormalizeGamePath).Any(p => p != null && conflicting.Contains(p)))
                f.Status = McdfFileStatus.ConflictingGamePath;
        }

        snapshot.Warnings.Add($"{conflicting.Count} game path(s) are claimed by stored files with different content (for example {conflicting.First()}); " +
                              "those files are not used as evidence.");
    }

    /// <summary>The snapshot as matching input. Only files whose bytes matched their hash count as evidence.</summary>
    public static List<CharacterResource> ToResources(McdfSnapshot snapshot)
    {
        var list = new List<CharacterResource>();
        foreach (var f in snapshot.Files)
        {
            var paths = f.GamePaths.Select(PenumbraPackageBuilder.NormalizeGamePath).Where(p => p != null).Select(p => p!).ToList();
            var hash = f.Verified ? new ContentHash(HashAlgorithmKind.Sha1, f.Hash) : (ContentHash?)null;
            list.Add(new CharacterResource(paths, hash, true, null,
                f.Verified ? ResourceAvailability.Available : ResourceAvailability.Missing));
        }

        foreach (var s in snapshot.FileSwaps)
        {
            var paths = s.GamePaths.Select(PenumbraPackageBuilder.NormalizeGamePath).Where(p => p != null).Select(p => p!).ToList();
            list.Add(new CharacterResource(paths, null, true, PenumbraPackageBuilder.NormalizeGamePath(s.Target),
                ResourceAvailability.FileSwap));
        }

        return list;
    }

    private static List<string> Paths(JsonElement e)
        => e.TryGetProperty("GamePaths", out var gp) && gp.ValueKind == JsonValueKind.Array
            ? [.. gp.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)]
            : [];

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static byte[] ReadExactly(Stream s, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = s.Read(buffer, offset, count - offset);
            if (read <= 0) throw new EndOfStreamException("The MCDF file ends early.");
            offset += read;
        }

        return buffer;
    }
}
