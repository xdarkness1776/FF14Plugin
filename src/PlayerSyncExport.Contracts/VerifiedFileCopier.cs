using System.Security.Cryptography;

namespace PlayerSyncExport.Contracts;

/// <summary>
/// Copies one file into a destination directory without ever modifying the source and without
/// overwriting anything at the destination. The data is streamed into a temporary file, hashed
/// while copying, verified, and only then moved to its final name.
/// </summary>
public static class VerifiedFileCopier
{
    public const string TempSuffix = ".cmx-partial";
    private const int BufferSize = 1024 * 1024;

    public sealed record CopyResult(FileExportOutcome Outcome, long Size, bool Sha1Verified, string? Sha256, string? Error);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <c>destinationDirectory/fileName</c>.
    /// </summary>
    /// <param name="expectedSha1">When given, the copy is accepted only if the SHA-1 of the data matches.</param>
    public static async Task<CopyResult> CopyAsync(string sourcePath, string destinationDirectory, string fileName,
        string? expectedSha1, IProgress<long>? progress, CancellationToken token)
    {
        var finalPath = ExportPathPolicy.CombineWithin(destinationDirectory, fileName);
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix;

        if (!File.Exists(sourcePath))
            return new(FileExportOutcome.SourceMissing, 0, false, null, "The source file no longer exists.");

        string sha1Hex;
        string sha256Hex;
        long length = 0;
        try
        {
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // FileShare.Read: if PlayerSync is still writing the file, opening fails instead of copying partial data.
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                             FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    sha256.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    length += read;
                    progress?.Report(read);
                }

                await target.FlushAsync(token).ConfigureAwait(false);
            }

            sha1Hex = Convert.ToHexString(sha1.GetHashAndReset());
            sha256Hex = Convert.ToHexString(sha256.GetHashAndReset());
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return new(FileExportOutcome.Cancelled, length, false, null, "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            var outcome = File.Exists(sourcePath) ? FileExportOutcome.Failed : FileExportOutcome.SourceMissing;
            return new(outcome, length, false, null, ex.Message);
        }

        var verified = expectedSha1 != null && string.Equals(sha1Hex, expectedSha1, StringComparison.OrdinalIgnoreCase);
        if (expectedSha1 != null && !verified)
        {
            TryDelete(tempPath);
            return new(FileExportOutcome.IntegrityMismatch, length, false, sha256Hex,
                $"SHA-1 of the copied data is {sha1Hex}, expected {expectedSha1.ToUpperInvariant()}. The cache file may be incomplete or was changed.");
        }

        try
        {
            if (File.Exists(finalPath))
            {
                var existing = await HashFileAsync(finalPath, HashAlgorithmName.SHA256, token).ConfigureAwait(false);
                TryDelete(tempPath);
                return string.Equals(existing, sha256Hex, StringComparison.OrdinalIgnoreCase)
                    ? new(FileExportOutcome.AlreadyPresentIdentical, length, verified, sha256Hex, null)
                    : new(FileExportOutcome.DestinationConflict, length, verified, sha256Hex,
                        $"'{fileName}' already exists with different content and was left untouched.");
            }

            File.Move(tempPath, finalPath, overwrite: false);
            return new(FileExportOutcome.Copied, length, verified, sha256Hex, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return new(FileExportOutcome.Cancelled, length, false, null, "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            // A concurrent writer may have created the final file between the check and the move.
            return File.Exists(finalPath)
                ? new(FileExportOutcome.DestinationConflict, length, verified, sha256Hex, ex.Message)
                : new(FileExportOutcome.Failed, length, verified, sha256Hex, ex.Message);
        }
    }

    public static async Task<string> HashFileAsync(string path, HashAlgorithmName algorithm, CancellationToken token)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = algorithm == HashAlgorithmName.SHA1
            ? await SHA1.HashDataAsync(fs, token).ConfigureAwait(false)
            : algorithm == HashAlgorithmName.SHA256
                ? await SHA256.HashDataAsync(fs, token).ConfigureAwait(false)
                : throw new ArgumentOutOfRangeException(nameof(algorithm));
        return Convert.ToHexString(hash);
    }

    /// <summary>Deletes temporary files this copier left behind (only files ending in <see cref="TempSuffix"/>).</summary>
    public static int CleanupTempFiles(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + TempSuffix, SearchOption.TopDirectoryOnly))
        {
            if (TryDelete(file)) removed++;
        }
        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!path.EndsWith(TempSuffix, StringComparison.Ordinal)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
