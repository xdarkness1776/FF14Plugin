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
    /// <param name="expectedSha256">When given, the copy is accepted only if the SHA-256 of the data matches.</param>
    /// <param name="ledger">Records the temporary file while it exists, so its owner can remove it if this method is interrupted.</param>
    public static async Task<CopyResult> CopyAsync(string sourcePath, string destinationDirectory, string fileName,
        string? expectedSha1, IProgress<long>? progress, CancellationToken token, string? expectedSha256 = null, TempFileLedger? ledger = null)
    {
        var finalPath = ExportPathPolicy.CombineWithin(destinationDirectory, fileName);
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix;
        ledger?.Add(tempPath);
        Func<string, bool> tryDelete = ledger != null ? ledger.TryDelete : TryDelete;
        try
        {
            return await CopyCoreAsync(sourcePath, finalPath, tempPath, fileName, expectedSha1, progress, token, expectedSha256, tryDelete)
                .ConfigureAwait(false);
        }
        finally
        {
            // Every path below either moved the file to its final name or tried to delete it. A file still here is ours;
            // if it cannot be removed now it stays in the ledger for the job's final cleanup. Never throws, so a cleanup
            // problem never replaces the copy's own result.
            if (!File.Exists(tempPath)) ledger?.Remove(tempPath);
            else tryDelete(tempPath);
        }
    }

    private static async Task<CopyResult> CopyCoreAsync(string sourcePath, string finalPath, string tempPath, string fileName,
        string? expectedSha1, IProgress<long>? progress, CancellationToken token, string? expectedSha256, Func<string, bool> tryDelete)
    {
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
            tryDelete(tempPath);
            return new(FileExportOutcome.Cancelled, length, false, null, "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tryDelete(tempPath);
            var outcome = File.Exists(sourcePath) ? FileExportOutcome.Failed : FileExportOutcome.SourceMissing;
            return new(outcome, length, false, null, ex.Message);
        }

        var verified = expectedSha1 != null && string.Equals(sha1Hex, expectedSha1, StringComparison.OrdinalIgnoreCase);
        if (expectedSha1 != null && !verified)
        {
            tryDelete(tempPath);
            return new(FileExportOutcome.IntegrityMismatch, length, false, sha256Hex,
                $"SHA-1 of the copied data is {sha1Hex}, expected {expectedSha1.ToUpperInvariant()}. The cache file may be incomplete or was changed.");
        }

        if (expectedSha256 != null && !string.Equals(sha256Hex, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            tryDelete(tempPath);
            return new(FileExportOutcome.IntegrityMismatch, length, verified, sha256Hex,
                $"SHA-256 of the copied data is {sha256Hex}, expected {expectedSha256.ToUpperInvariant()}. The file changed since it was indexed.");
        }

        try
        {
            if (File.Exists(finalPath))
            {
                var existing = await HashFileAsync(finalPath, HashAlgorithmName.SHA256, token).ConfigureAwait(false);
                tryDelete(tempPath);
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
            tryDelete(tempPath);
            return new(FileExportOutcome.Cancelled, length, false, null, "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tryDelete(tempPath);
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

/// <summary>
/// The temporary files one export job created and has not finished with yet. Cleanup deletes exactly these, never
/// other files that happen to have the same suffix (another job's, or a user's). Thread-safe.
/// </summary>
public sealed class TempFileLedger
{
    private readonly object _lock = new();
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly Action<string> _delete;

    /// <param name="delete">Deletes one file (tests replace it); defaults to <see cref="File.Delete"/>.</param>
    public TempFileLedger(Action<string>? delete = null) => _delete = delete ?? File.Delete;

    public void Add(string path)
    {
        lock (_lock) _paths.Add(path);
    }

    public void Remove(string path)
    {
        lock (_lock) _paths.Remove(path);
    }

    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_lock) return [.. _paths];
        }
    }

    /// <summary>Deletes one recorded file if it still exists. On success it is no longer recorded; never throws.</summary>
    public bool TryDelete(string path) => TryDelete(path, out _);

    private bool TryDelete(string path, out string? error)
    {
        error = null;
        try
        {
            if (!path.EndsWith(VerifiedFileCopier.TempSuffix, StringComparison.Ordinal))
            {
                error = "Not a temporary export file; left untouched.";
                return false;
            }

            if (File.Exists(path)) _delete(path);
            Remove(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Deletes every recorded file that still exists, and only those. Returns the ones that could not be deleted, with
    /// the reason; they stay recorded. Never throws.
    /// </summary>
    public List<(string Path, string Error)> DeleteAll()
    {
        var failed = new List<(string, string)>();
        foreach (var path in Pending)
            if (!TryDelete(path, out var error)) failed.Add((path, error ?? "Unknown error."));
        return failed;
    }
}
