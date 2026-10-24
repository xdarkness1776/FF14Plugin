using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CharacterModExporter.Core;
using CharacterModExporter.Core.Library;
using K4os.Compression.LZ4.Legacy;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

/// <summary>MCDF v1 edge cases, read from the decompressed stream so each case controls every byte.</summary>
public class McdfHardeningTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static object Entry(string[] paths, string content, string? hash = null, long? length = null)
        => new { GamePaths = paths, Length = length ?? Encoding.UTF8.GetByteCount(content), Hash = hash ?? TestPmp.Sha1(content) };

    private static object Swap(string[] paths, string target) => new { GamePaths = paths, FileSwapPath = target };

    /// <summary>The decompressed layout: "MCDF", version, int32 length, JSON, payloads.</summary>
    private static byte[] Raw(object header, IEnumerable<byte[]> payloads, int? lengthOverride = null, byte version = 1)
    {
        var json = B(JsonSerializer.Serialize(header));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("MCDF"u8);
            w.Write(version);
            w.Write(lengthOverride ?? json.Length);
            w.Write(json);
            foreach (var p in payloads) w.Write(p);
        }

        return ms.ToArray();
    }

    private static McdfSnapshot Read(byte[] raw, Stream? wrapper = null)
        => McdfReader.ReadFrom(wrapper ?? new MemoryStream(raw), verifyContents: true);

    [Fact]
    public void EmptyAndTruncatedHeadersAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Read([]));
        Assert.Throws<InvalidDataException>(() => Read("MCDF\u0001"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => Read("MCDF\u0001\u0010\0"u8.ToArray()));
    }

    [Fact]
    public void HeaderLongerThanTheFileIsRejected()
    {
        // Claims 30 MB of JSON, provides 20 bytes: rejected as an early end, not a 30 MB allocation followed by a parse.
        var raw = Raw(new { Files = Array.Empty<object>() }, [], lengthOverride: 30 * 1024 * 1024);
        Assert.Contains("ends inside its header", Assert.Throws<InvalidDataException>(() => Read(raw)).Message);
    }

    [Fact]
    public void TooManyFilesAreRejected()
    {
        var entries = Enumerable.Range(0, McdfReader.MaxFiles + 1).Select(i => Entry([$"chara/{i}.tex"], "x")).ToArray();
        Assert.Contains("more than", Assert.Throws<InvalidDataException>(() => Read(Raw(new { Files = entries }, []))).Message);
    }

    [Fact]
    public void ZeroLengthEntryIsNotEvidenceAndDoesNotShiftTheNext()
    {
        var raw = Raw(new { Files = new[] { Entry(["chara/empty.tex"], ""), Entry(["chara/a.tex"], "model") } }, [B("model")]);
        var snap = Read(raw);
        Assert.Equal([McdfFileStatus.EmptyPayload, McdfFileStatus.Verified], snap.Files.Select(f => f.Status));
        Assert.Contains(snap.Warnings, w => w.Contains("is empty"));
    }

    [Fact]
    public void PayloadsInTheWrongOrderFailVerification()
    {
        var raw = Raw(new { Files = new[] { Entry(["chara/a.tex"], "aaaa"), Entry(["chara/b.tex"], "bbbb") } }, [B("bbbb"), B("aaaa")]);
        Assert.All(Read(raw).Files, f => Assert.Equal(McdfFileStatus.HashMismatch, f.Status));
    }

    [Fact]
    public void TruncatedFinalEntryIsMissingAndEarlierOnesStayVerified()
    {
        var raw = Raw(new { Files = new[] { Entry(["chara/a.tex"], "aaaa"), Entry(["chara/b.tex"], "bbbb") } }, [B("aaaa"), B("bbb")]);
        Assert.Equal([McdfFileStatus.Verified, McdfFileStatus.MissingPayload], Read(raw).Files.Select(f => f.Status));
    }

    [Fact]
    public void ShortReadsAreHandled()
    {
        var raw = Raw(new { Files = new[] { Entry(["chara/a.tex"], new string('a', 5000)), Entry(["chara/b.tex"], "bbbb") } },
            [B(new string('a', 5000)), B("bbbb")]);
        var snap = Read(raw, new ShortReadStream(raw, maxPerRead: 3));
        Assert.True(snap.AllVerified);
    }

    [Fact]
    public void MixedCaseDuplicatePathsAreOnePath()
    {
        var same = Read(Raw(new { Files = new[] { Entry(["chara/A.tex"], "x"), Entry(["chara/a.TEX"], "x") } }, [B("x"), B("x")]));
        Assert.True(same.AllVerified);

        var different = Read(Raw(new { Files = new[] { Entry(["chara/A.tex"], "x"), Entry(["chara/a.TEX"], "y") } }, [B("x"), B("y")]));
        Assert.All(different.Files, f => Assert.Equal(McdfFileStatus.ConflictingGamePath, f.Status));
    }

    [Fact]
    public void SwapsAreCheckedBeforeTheyAreUsed()
    {
        var raw = Raw(new
        {
            Files = new[] { Entry(["chara/equipment/e0001/texture/a.tex"], "stored") },
            FileSwaps = new[]
            {
                Swap(["chara/ok_from.tex"], "chara/ok_to.tex"),
                Swap(["chara/self.tex"], "chara/self.tex"),
                Swap(["chara/model.mdl"], "chara/texture.tex"),
                Swap(["chara/x.tex"], "C:/Windows/x.tex"),
                Swap(["../escape.tex"], "chara/y.tex"),
                Swap(["chara/equipment/e0001/texture/A.tex"], "chara/other.tex"),
                Swap(["chara/dup.tex"], "chara/one.tex"),
                Swap(["chara/DUP.tex"], "chara/two.tex"),
                Swap(["chara/same.tex"], "chara/target.tex"),
                Swap(["chara/same.tex"], "chara/target.tex"),
                Swap([], "chara/z.tex"),
            },
        }, [B("stored")]);

        var snap = Read(raw);

        Assert.Equal(
            [
                McdfSwapStatus.Valid, McdfSwapStatus.Invalid, McdfSwapStatus.Invalid, McdfSwapStatus.Invalid, McdfSwapStatus.Invalid,
                McdfSwapStatus.OverridesStoredFile, McdfSwapStatus.ConflictingTargets, McdfSwapStatus.ConflictingTargets,
                McdfSwapStatus.Valid, McdfSwapStatus.Valid, McdfSwapStatus.Invalid,
            ],
            snap.FileSwaps.Select(s => s.Status));
        Assert.All(snap.FileSwaps.Where(s => s.Status != McdfSwapStatus.Valid), s => Assert.False(string.IsNullOrEmpty(s.Problem)));

        var swapResources = McdfReader.ToResources(snap).Where(r => r.Availability == ResourceAvailability.FileSwap).ToList();
        Assert.Equal(3, swapResources.Count);
        Assert.True(snap.Files.Single().Verified); // the stored file is unaffected by the swap that would override it
    }

    /// <summary>
    /// A file written byte for byte the way PlayerSync 1.15.5.9 writes it (MareCharaFileHeader.WriteToStream:
    /// BinaryWriter.Write(char) four times, the version byte, the int32 length and System.Text.Json's PascalCase
    /// MareCharaFileData; then the files in order, inside one K4os LZ4 legacy HighCompression stream).
    /// </summary>
    [Fact]
    public void PlayerSyncWrittenFileIsReadUnchanged()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "playersync.mcdf");
        var a = new string('m', 70_000);
        var data = new
        {
            Description = "Look",
            GlamourerData = "glam64",
            CustomizePlusData = "cplus64",
            ManipulationData = "manip64",
            Files = new[]
            {
                new { GamePaths = (IEnumerable<string>)new[] { "chara/equipment/e0001/model/c0101e0001_top.mdl" }, Length = a.Length, Hash = TestPmp.Sha1(a) },
                new { GamePaths = (IEnumerable<string>)new[] { "chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex", "chara/equipment/e0001/texture/v02_c0101e0001_top_n.tex" }, Length = 3, Hash = TestPmp.Sha1("tex") },
            },
            FileSwaps = new[] { new { GamePaths = (IEnumerable<string>)new[] { "chara/equipment/e0002/texture/a.tex" }, FileSwapPath = "chara/equipment/e0003/texture/a.tex" } },
        };
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var lz4 = new LZ4Stream(fs, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression))
        using (var writer = new BinaryWriter(lz4))
        {
            writer.Write('M');
            writer.Write('C');
            writer.Write('D');
            writer.Write('F');
            writer.Write((byte)1);
            var json = B(JsonSerializer.Serialize(data));
            writer.Write(json.Length);
            writer.Write(json);
            writer.Write(B(a));
            writer.Write(B("tex"));
        }

        var snap = McdfReader.Read(path);

        Assert.Equal(1, snap.Version);
        Assert.True(snap.AllVerified);
        Assert.True(snap.HasGlamourerData && snap.HasCustomizePlusData && snap.HasManipulationData);
        Assert.Equal(2, snap.Files[1].GamePaths.Count);
        Assert.Equal(McdfSwapStatus.Valid, Assert.Single(snap.FileSwaps).Status);
        Assert.Empty(snap.Warnings);
    }

    /// <summary>Returns at most a few bytes per Read even though more are available.</summary>
    private sealed class ShortReadStream(byte[] data, int maxPerRead) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, maxPerRead), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Packaging only from the verified staged copies, and the cache files that are never exported.</summary>
public class StagedExportIntegrityTests
{
    [Fact]
    public void FileChangedAfterExportIsNeverPackaged()
    {
        using var dir = new TempDir();
        var staged = Path.Combine(dir.Sub("assets"), "a.tex");
        File.WriteAllText(staged, "verified bytes");
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified bytes")));
        File.WriteAllText(staged, "replaced later");

        var result = PenumbraPackageBuilder.Build(dir.Sub("out"), "Test", "d", "1", [new PackageFile(staged, "a.tex", ["chara/a.tex"], sha256)],
            new Dictionary<string, string>());

        Assert.False(result.Created);
        Assert.Contains(result.Problems, p => p.Contains("changed after it was exported"));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "out")));
    }

    [Fact]
    public void PackageRoundTripKeepsBytesMappingsAndSwaps()
    {
        using var dir = new TempDir();
        var staged = Path.Combine(dir.Sub("assets"), "a.tex");
        File.WriteAllText(staged, "texture");
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("texture")));
        var swaps = new Dictionary<string, string>
        {
            ["chara/s.tex"] = "chara/t.tex",
            ["chara/self.tex"] = "chara/self.tex",
            ["chara/m.mdl"] = "chara/t.tex",
        };

        var result = PenumbraPackageBuilder.Build(dir.Sub("out"), "Test", "d", "1",
            [new PackageFile(staged, "a.tex", ["chara/a.tex", "chara/A2.tex"], sha256)], swaps);

        Assert.True(result.Created, string.Join("; ", result.Problems));
        Assert.Equal(2, result.Omitted.Count(o => o.Contains("not a valid file swap")));
        using var zip = ZipFile.OpenRead(result.PackagePath!);
        using (var entry = zip.GetEntry("files/a.tex")!.Open())
            Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(entry)));
        using var defaults = JsonDocument.Parse(zip.GetEntry("default_mod.json")!.Open());
        var files = defaults.RootElement.GetProperty("Files");
        Assert.Equal("files\\a.tex", files.GetProperty("chara/a.tex").GetString());
        Assert.Equal("files\\a.tex", files.GetProperty("chara/a2.tex").GetString());
        Assert.Equal("chara/t.tex", Assert.Single(defaults.RootElement.GetProperty("FileSwaps").EnumerateObject()).Value.GetString());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567.bin", true)]
    [InlineData("mare_12.tmp", true)]
    [InlineData("x.tex.ab12cd34.cmx-partial", true)]
    [InlineData("0123456789abcdef0123456789abcdef01234567.tex", false)]
    public void TemporaryFilesAreRecognized(string name, bool temporary)
        => Assert.Equal(temporary, ExportPathPolicy.IsTemporaryFile(name));

    [Fact]
    public void DownloadLeftoverInTheCacheIsNeverOffered()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        // A crash-leftover still LZ4-wrapped, named after the hash it was meant to become.
        var bin = Path.Combine(src.CacheFolder, TestPmp.Sha1("real").ToLowerInvariant() + ".bin");
        File.WriteAllText(bin, "wrapped bytes");
        src.Tracker.Record("chara/a.tex", bin);
        src.Tracker.Record("chara/b.tex", src.AddCacheFile("tex", "fine"));
        src.Settle();

        var resolution = src.Resolve(1);

        Assert.Equal(ResourceAvailability.Missing, resolution.State.Resources.Single(r => r.GamePaths.Contains("chara/a.tex")).Availability);
        Assert.DoesNotContain(resolution.Files.Values, f => f.Path == bin);
        Assert.Contains(resolution.State.Notes, n => n.Contains("temporary download"));
    }

    [Fact]
    public void ExportIpcNeverOffersALeftoverEither()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var hash = TestPmp.Sha1("real");
        var bin = Path.Combine(ps.CacheFolder, hash.ToLowerInvariant() + ".bin");
        File.WriteAllText(bin, "wrapped bytes");
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/a.tex"], hash, null));
        pair.Modded[("chara/a.tex", hash)] = bin;

        var resolution = ps.Resolve(1);

        Assert.Equal(ResourceAvailability.Missing, Assert.Single(resolution.State.Resources).Availability);
        Assert.Empty(resolution.Files);
    }

    [Fact]
    public async Task FileEvictedDuringTheExportIsReportedAndNoPackageIsBuilt()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        var b = ps.AddCacheFile("tex", "b");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(a, "Player", "chara/a.tex");
        pair.AddResolved(b, "Player", "chara/b.tex");
        var dest = dir.Sub("out");
        var checks = 0;
        // The destination is checked once at the start and once before each file: evict b right before its copy.
        using var registry = new ExportJobRegistry(ps.Resolve, () =>
        {
            if (++checks == 3) File.Delete(b.Path);
            return [ps.CacheFolder];
        }, delay: (_, t) => Task.Delay(5, t));

        var start = registry.Start(new ExportRequestDto { EntityId = 1, ExpectedPlayerName = "A B", DestinationDirectory = dest, CacheHashes = [a.Hash, b.Hash], SyncWaitSeconds = 2 });
        await registry.LastJobTask.WaitAsync(TimeSpan.FromSeconds(30));
        var status = registry.GetStatus(start.JobId);

        Assert.Equal(ExportJobPhase.CompletedWithIssues, status.Phase);
        Assert.Equal(FileExportOutcome.SourceMissing, status.Files.Single(f => f.CacheHash == b.Hash).Outcome);
        Assert.Equal([a.Hash.ToLowerInvariant() + ".tex"], Directory.GetFiles(dest).Select(Path.GetFileName));
    }

    [Fact]
    public async Task MissingCachedFileMeansNoPackage()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        var b = ps.AddCacheFile("tex", "b");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(a, "Player", "chara/a.tex");
        pair.AddResolved(b, "Player", "chara/b.tex");
        File.Delete(b.Path); // evicted before the export started
        var folders = new ExportFolderService(() => [ps.CacheFolder]);
        var coordinator = new ExportCoordinator(ps, folders, "t", (_, _) => Task.Delay(5));
        var parent = dir.Sub("parent");

        var outcome = await coordinator.ExportAsync(new ExportTarget(1, "A B", null), Settings.For(parent), [a.Hash, b.Hash], null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Layout);
        Assert.False(Directory.Exists(outcome.Layout!.InstallableFolder) && Directory.EnumerateFiles(outcome.Layout.InstallableFolder).Any());
        var manifest = ManifestWriter.FromJson(File.ReadAllText(outcome.Layout.ManifestPath))!;
        Assert.Contains(manifest.Notes, n => n.Contains("No Penumbra package was built"));
    }
}
