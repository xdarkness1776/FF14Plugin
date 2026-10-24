using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Legacy;

namespace CharacterModExporter.Core.Library;

/// <summary>One file stored in an MCDF: the game paths it was used for, its length, and the SHA-1 the file claims.</summary>
public sealed class McdfFile
{
    public List<string> GamePaths { get; set; } = [];
    public int Length { get; set; }

    /// <summary>The SHA-1 written in the file (PlayerSync's cache hash).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>SHA-1 of the stored bytes, computed while reading; null when they were not read.</summary>
    public string? ComputedSha1 { get; set; }

    public bool Verified => ComputedSha1 != null && string.Equals(ComputedSha1, Hash, StringComparison.OrdinalIgnoreCase);
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

    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Reads the MCDF format as PlayerSync 1.15.5.9 writes it (verified against its MareCharaFileHeader and
/// CharaDataFileHandler): the whole file is a K4os LZ4 legacy stream containing "MCDF", a version byte (only 1 exists),
/// an int32 length, that many bytes of UTF-8 JSON, and then the files' bytes in the order the JSON lists them.
/// </summary>
public static class McdfReader
{
    public const int MaxHeaderBytes = 64 * 1024 * 1024;
    public const int MaxFiles = 100_000;

    /// <param name="verifyContents">Reads every stored file and checks its SHA-1. Without it only the header is read.</param>
    public static McdfSnapshot Read(string path, bool verifyContents = true, CancellationToken token = default)
    {
        var fi = new FileInfo(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var lz4 = new LZ4Stream(file, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        var snapshot = ReadFrom(lz4, verifyContents, token);
        snapshot.FilePath = fi.FullName;
        snapshot.FileLastWriteUtc = fi.LastWriteTimeUtc;
        return snapshot;
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

        using var doc = JsonDocument.Parse(ReadExactly(decompressed, length));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The MCDF header is not a JSON object.");
        snapshot.Description = Str(root, "Description") ?? string.Empty;
        snapshot.HasGlamourerData = !string.IsNullOrEmpty(Str(root, "GlamourerData"));
        snapshot.HasCustomizePlusData = !string.IsNullOrEmpty(Str(root, "CustomizePlusData"));
        snapshot.HasManipulationData = !string.IsNullOrEmpty(Str(root, "ManipulationData"));

        if (root.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            if (files.GetArrayLength() > MaxFiles) throw new InvalidDataException("The MCDF lists too many files.");
            foreach (var f in files.EnumerateArray())
            {
                var entry = new McdfFile
                {
                    GamePaths = Paths(f),
                    Length = f.TryGetProperty("Length", out var l) && l.TryGetInt32(out var li) ? li : -1,
                    Hash = (Str(f, "Hash") ?? string.Empty).ToUpperInvariant(),
                };
                if (entry.Length < 0) throw new InvalidDataException("An MCDF file entry has no valid length.");
                snapshot.Files.Add(entry);
            }
        }

        if (root.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Array)
            foreach (var s in swaps.EnumerateArray())
                snapshot.FileSwaps.Add(new McdfSwap { GamePaths = Paths(s), Target = Str(s, "FileSwapPath") ?? string.Empty });

        if (!verifyContents) return snapshot;

        var buffer = new byte[1024 * 256];
        foreach (var f in snapshot.Files)
        {
            token.ThrowIfCancellationRequested();
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var remaining = f.Length;
            while (remaining > 0)
            {
                var read = decompressed.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    snapshot.Warnings.Add("The file ends before all stored files were read; the rest is unverified.");
                    return snapshot;
                }

                sha1.AppendData(buffer, 0, read);
                remaining -= read;
            }

            f.ComputedSha1 = Convert.ToHexString(sha1.GetHashAndReset());
            if (!f.Verified) snapshot.Warnings.Add($"Stored file for {f.GamePaths.FirstOrDefault()} does not match its hash {f.Hash}.");
        }

        return snapshot;
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
                f.Verified ? PlayerSyncExport.Contracts.ResourceAvailability.Available : PlayerSyncExport.Contracts.ResourceAvailability.Missing));
        }

        foreach (var s in snapshot.FileSwaps)
        {
            var paths = s.GamePaths.Select(PenumbraPackageBuilder.NormalizeGamePath).Where(p => p != null).Select(p => p!).ToList();
            list.Add(new CharacterResource(paths, null, true, PenumbraPackageBuilder.NormalizeGamePath(s.Target),
                PlayerSyncExport.Contracts.ResourceAvailability.FileSwap));
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
