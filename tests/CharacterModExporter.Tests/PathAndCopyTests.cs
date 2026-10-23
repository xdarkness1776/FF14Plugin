using System.Security.Cryptography;
using System.Text;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

public class ExportPathPolicyTests
{
    [Theory]
    [InlineData("00cb686fd0b07b186eadca644610f945355e5f3c", true)]
    [InlineData("0A94997F24061CBAE5FBB6CC07D6FCAF8AC3C217", true)]
    [InlineData("0a94997f24061cbae5fbb6cc07d6fcaf8ac3c21", false)]
    [InlineData("../../../../etc/passwd/aaaaaaaaaaaaaaaaaaa", false)]
    [InlineData(null, false)]
    public void Sha1HexDetection(string? value, bool expected) => Assert.Equal(expected, ExportPathPolicy.IsSha1Hex(value));

    [Theory]
    [InlineData("avfx", "avfx")]
    [InlineData(".ATEX", "atex")]
    [InlineData("tex/../x", null)]
    [InlineData("", null)]
    [InlineData("mtrl ", null)]
    public void ExtensionsAreSanitized(string ext, string? expected) => Assert.Equal(expected, ExportPathPolicy.SanitizeExtension(ext));

    [Theory]
    [InlineData("chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex", "tex")]
    [InlineData("vfx/common/eff/abc.avfx", "avfx")]
    [InlineData("chara/no_extension", null)]
    public void ExtensionFromGamePath(string gamePath, string? expected) => Assert.Equal(expected, ExportPathPolicy.ExtensionOfGamePath(gamePath));

    [Fact]
    public void CacheFileNameMatchesPlayerSyncNaming()
        => Assert.Equal("00bbd874d5135762b3d5ff61f1a15224541f7075.atex", ExportPathPolicy.CacheFileName("00BBD874D5135762B3D5FF61F1A15224541F7075", "atex"));

    [Theory]
    [InlineData("..", "Unnamed")]
    [InlineData("../../Windows", ".._.._Windows")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("A<B>C:D", "A_B_C_D")]
    [InlineData("  ", "Unnamed")]
    [InlineData("Y'shtola Rhul", "Y'shtola Rhul")]
    public void SegmentsAreSanitized(string input, string expected) => Assert.Equal(expected, ExportPathPolicy.SanitizeSegment(input));

    [Fact]
    public void CombineWithinRejectsTraversal()
    {
        var root = Path.GetTempPath();
        Assert.Throws<ArgumentException>(() => ExportPathPolicy.CombineWithin(root, ".."));
        Assert.Throws<ArgumentException>(() => ExportPathPolicy.CombineWithin(root, "a/../../b"));
        Assert.Throws<ArgumentException>(() => ExportPathPolicy.CombineWithin(root, Path.GetTempPath()));
        Assert.StartsWith(ExportPathPolicy.NormalizeFull(root), ExportPathPolicy.CombineWithin(root, "ok", "file.tex"));
    }

    [Fact]
    public void IsWithinDoesNotMatchSiblingPrefixes()
    {
        using var dir = new TempDir();
        Assert.True(ExportPathPolicy.IsWithin(dir.Path, Path.Combine(dir.Path, "a")));
        Assert.False(ExportPathPolicy.IsWithin(Path.Combine(dir.Path, "cache"), Path.Combine(dir.Path, "cache2")));
    }

    [Fact]
    public void DestinationMayNotBeInsideForbiddenFolders()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("cache");
        Assert.NotNull(ExportPathPolicy.ValidateDestination(Path.Combine(cache, "exports"), [cache]));
        Assert.NotNull(ExportPathPolicy.ValidateDestination(cache, [cache]));
        Assert.NotNull(ExportPathPolicy.ValidateDestination("relative/folder", []));
        Assert.NotNull(ExportPathPolicy.ValidateDestination("", []));
        Assert.Null(ExportPathPolicy.ValidateDestination(dir.Sub("exports"), [cache, null, ""]));
    }
}

public class VerifiedFileCopierTests
{
    private static (string Path, string Sha1, byte[] Bytes) MakeSource(TempDir dir, string content, string ext = "tex")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes));
        var path = Path.Combine(dir.Sub("cache"), sha1.ToLowerInvariant() + "." + ext);
        File.WriteAllBytes(path, bytes);
        return (path, sha1, bytes);
    }

    [Fact]
    public async Task CopiesVerifiesAndLeavesSourceUntouched()
    {
        using var dir = new TempDir();
        var (src, sha1, bytes) = MakeSource(dir, "texture data");
        var before = Snapshot.Of(Path.GetDirectoryName(src)!);
        var dest = dir.Sub("out");

        var result = await VerifiedFileCopier.CopyAsync(src, dest, "x.tex", sha1, null, CancellationToken.None);

        Assert.Equal(FileExportOutcome.Copied, result.Outcome);
        Assert.True(result.Sha1Verified);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dest, "x.tex")));
        Assert.Equal(before, Snapshot.Of(Path.GetDirectoryName(src)!));
        Assert.Empty(Directory.GetFiles(dest, "*" + VerifiedFileCopier.TempSuffix));
    }

    [Fact]
    public async Task IntegrityMismatchLeavesNothingBehind()
    {
        using var dir = new TempDir();
        var (src, _, _) = MakeSource(dir, "partial");
        var dest = dir.Sub("out");

        var result = await VerifiedFileCopier.CopyAsync(src, dest, "x.tex", new string('A', 40), null, CancellationToken.None);

        Assert.Equal(FileExportOutcome.IntegrityMismatch, result.Outcome);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task IdenticalExistingFileIsReused()
    {
        using var dir = new TempDir();
        var (src, sha1, bytes) = MakeSource(dir, "same");
        var dest = dir.Sub("out");
        File.WriteAllBytes(Path.Combine(dest, "x.tex"), bytes);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(dest, "x.tex"));

        var result = await VerifiedFileCopier.CopyAsync(src, dest, "x.tex", sha1, null, CancellationToken.None);

        Assert.Equal(FileExportOutcome.AlreadyPresentIdentical, result.Outcome);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dest, "x.tex")));
        Assert.Single(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task DifferentExistingFileIsNeverOverwritten()
    {
        using var dir = new TempDir();
        var (src, sha1, _) = MakeSource(dir, "new content");
        var dest = dir.Sub("out");
        var unrelated = Path.Combine(dest, "x.tex");
        File.WriteAllText(unrelated, "user's own file");

        var result = await VerifiedFileCopier.CopyAsync(src, dest, "x.tex", sha1, null, CancellationToken.None);

        Assert.Equal(FileExportOutcome.DestinationConflict, result.Outcome);
        Assert.Equal("user's own file", File.ReadAllText(unrelated));
        Assert.Single(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task CancellationRemovesPartialFile()
    {
        using var dir = new TempDir();
        var (src, sha1, _) = MakeSource(dir, new string('x', 3 * 1024 * 1024));
        var dest = dir.Sub("out");
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirstChunk(cts);

        var result = await VerifiedFileCopier.CopyAsync(src, dest, "x.tex", sha1, progress, cts.Token);

        Assert.Equal(FileExportOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task MissingSourceIsReported()
    {
        using var dir = new TempDir();
        var result = await VerifiedFileCopier.CopyAsync(Path.Combine(dir.Path, "gone.tex"), dir.Sub("out"), "x.tex", null, null, CancellationToken.None);
        Assert.Equal(FileExportOutcome.SourceMissing, result.Outcome);
    }

    [Fact]
    public void CleanupOnlyRemovesOwnTempFiles()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "keep.tex"), "a");
        File.WriteAllText(Path.Combine(dir.Path, "x.tex.1234" + VerifiedFileCopier.TempSuffix), "b");

        Assert.Equal(1, VerifiedFileCopier.CleanupTempFiles(dir.Path));
        Assert.True(File.Exists(Path.Combine(dir.Path, "keep.tex")));
    }

    private sealed class CancelAfterFirstChunk(CancellationTokenSource cts) : IProgress<long>
    {
        public void Report(long value) => cts.Cancel();
    }
}
