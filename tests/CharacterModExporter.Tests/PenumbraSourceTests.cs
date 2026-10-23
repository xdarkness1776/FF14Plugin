using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CharacterModExporter.Core;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

/// <summary>
/// The source used with stock PlayerSync, without the game: the same tracker, state builder and job runner the
/// plugin's PenumbraExportSource wires to Penumbra's IPC. Only the IPC calls themselves are replaced.
/// </summary>
public sealed class FakePenumbraSource : IPlayerSyncExportApi, IDisposable
{
    private readonly FileVerificationCache _hashes = new();

    public FakePenumbraSource(TempDir dir)
    {
        CacheFolder = dir.Sub("playersync-cache");
        ModFolder = dir.Sub("penumbra-mods");
        Jobs = new ExportJobRegistry(Resolve, () => [CacheFolder, ModFolder], delay: (_, t) => Task.Delay(5, t), clock: () => Now);
    }

    public string CacheFolder { get; }
    public string ModFolder { get; }
    public ExportJobRegistry Jobs { get; }
    public DateTimeOffset Now { get; set; } = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    public LoadedResourceTracker Tracker => _tracker ??= new LoadedResourceTracker(() => Now);
    private LoadedResourceTracker? _tracker;
    public string Name { get; set; } = "Alpha Beta";
    public bool? Handled { get; set; } = true;
    public int Redraws { get; private set; }

    public string SourceLabel => "Penumbra";
    public string UnavailableMessage => "Penumbra is not loaded.";
    public (ProviderStatus Status, int Version) Probe() => (ProviderStatus.Available, 0);

    public CharacterResolution Resolve(uint entityId)
        => PenumbraResolution.Build(Name, Handled, Tracker.Snapshot(), CacheFolder, ModFolder, _hashes, Tracker.LastChangeUtc, Now, Vanilla);

    /// <summary>Stands in for the game's own files.</summary>
    public Dictionary<string, byte[]> GameFiles { get; } = new(StringComparer.Ordinal);
    public VanillaFileComparer Vanilla => _vanilla ??= new VanillaFileComparer(p => GameFiles.GetValueOrDefault(p));
    private VanillaFileComparer? _vanilla;

    public CharacterStateDto GetCharacterState(uint entityId) => Resolve(entityId).State;

    public bool RequestResync(uint entityId)
    {
        Redraws++;
        Tracker.MarkChanging();
        return Handled == true;
    }

    public ExportStartResultDto StartExport(ExportRequestDto request) => Jobs.Start(request);
    public ExportJobStatusDto GetExportStatus(string jobId) => Jobs.GetStatus(jobId);
    public bool CancelExport(string jobId) => Jobs.Cancel(jobId);

    public string AddCacheFile(string ext, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var path = Path.Combine(CacheFolder, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant() + "." + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Settle() => Now += PenumbraResolution.SettleTime + TimeSpan.FromSeconds(1);

    public void Dispose() => Jobs.Dispose();
}

public class LoadedResourceTrackerTests
{
    [Theory]
    [InlineData(@"C:\Cache\abc.tex", @"C:\Cache\abc.tex")]
    [InlineData(@"|0_1|C:\Cache\abc.mtrl", @"C:\Cache\abc.mtrl")]
    [InlineData("/cache/abc.tex", "/cache/abc.tex")]
    [InlineData("chara/equipment/e0001/texture/a.tex", null)]
    [InlineData("|bad", null)]
    [InlineData("", null)]
    public void ResolvedPathsKeepOnlyFilesOnDisk(string input, string? expected)
        => Assert.Equal(expected, LoadedResourceTracker.NormalizeResolvedPath(input));

    [Fact]
    public void RecordsRedirectsAndIgnoresVanillaAndDuplicates()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new LoadedResourceTracker(() => now);
        tracker.Record("Chara/Equipment/A.tex", "/cache/aa.tex");
        tracker.Record("chara/equipment/b.tex", "chara/equipment/b.tex");
        now = now.AddSeconds(10);
        tracker.Record("chara/equipment/a.tex", "/cache/aa.tex");
        Assert.Equal(DateTimeOffset.UnixEpoch, tracker.LastChangeUtc);
        tracker.Merge(new Dictionary<string, HashSet<string>> { ["/cache/aa.tex"] = ["chara/equipment/c.tex"] });

        var snapshot = tracker.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(["chara/equipment/a.tex", "chara/equipment/c.tex"], snapshot["/cache/aa.tex"]);
        Assert.Equal(now, tracker.LastChangeUtc);
    }

    [Fact]
    public void ReadsCacheFolderFromPlayerSyncConfigWithoutChangingIt()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("cache");
        var configDir = dir.Sub("pluginConfigs", PlayerSyncConfigReader.InternalName);
        var config = Path.Combine(configDir, "config.json");
        File.WriteAllText(config, "{\"Version\":1,\"CacheFolder\":" + System.Text.Json.JsonSerializer.Serialize(cache) + "}");
        var before = File.GetLastWriteTimeUtc(config);

        Assert.Equal(Path.GetFullPath(cache), PlayerSyncConfigReader.ReadCacheFolder(Path.Combine(dir.Path, "pluginConfigs")));
        Assert.Equal(before, File.GetLastWriteTimeUtc(config));
        Assert.Null(PlayerSyncConfigReader.ReadCacheFolder(dir.Sub("empty")));
        File.WriteAllText(config, "not json");
        Assert.Null(PlayerSyncConfigReader.ReadCacheFolder(Path.Combine(dir.Path, "pluginConfigs")));
    }
}

public class PenumbraResolutionTests
{
    [Fact]
    public void OffersOnlyPlayerSyncCacheAndPenumbraModFiles()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var tex = src.AddCacheFile("tex", "texture");
        var own = Path.Combine(src.ModFolder, "MyMod", "hair.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "own mod");
        var elsewhere = Path.Combine(dir.Sub("elsewhere"), "x.tex");
        File.WriteAllText(elsewhere, "x");
        src.Tracker.Record("chara/a.tex", tex);
        src.Tracker.Record("chara/hair.mdl", own);
        src.Tracker.Record("chara/x.tex", elsewhere);
        src.Settle();

        var resolution = src.Resolve(1);

        Assert.Equal(SyncState.Complete, resolution.State.SyncState);
        Assert.False(resolution.State.LinkDetailsKnown);
        Assert.Equal(2, resolution.State.Resources.Count);
        var cacheHash = Path.GetFileNameWithoutExtension(tex).ToUpperInvariant();
        Assert.Equal(tex, resolution.Files[cacheHash].Path);
        var ownHash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(own)));
        Assert.Equal(SourceKind.PenumbraModDirectory, resolution.State.Resources.Single(r => r.CacheHash == ownHash).SourceKind);
        Assert.Contains(resolution.State.Notes, n => n.Contains("1 loaded file(s) come from neither"));
    }

    [Fact]
    public void ReportsLoadingUntilTheSetSettles()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        Assert.Equal(SyncState.NotApplied, src.Resolve(1).State.SyncState);

        src.Tracker.Record("chara/a.tex", src.AddCacheFile("tex", "a"));
        Assert.Equal(SyncState.InProgress, src.Resolve(1).State.SyncState);
        src.Settle();
        Assert.Equal(SyncState.Complete, src.Resolve(1).State.SyncState);

        src.RequestResync(1);
        Assert.Equal(SyncState.InProgress, src.Resolve(1).State.SyncState);
        Assert.Equal(1, src.Redraws);
    }

    [Fact]
    public void NotHandledOrPlayerSyncMissingOffersNothing()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        src.Tracker.Record("chara/a.tex", src.AddCacheFile("tex", "a"));
        src.Settle();

        src.Handled = false;
        Assert.Equal(CharacterLookup.NotSynced, src.Resolve(1).State.Lookup);
        Assert.Empty(src.Resolve(1).Files);
        src.Handled = null;
        Assert.Equal(CharacterLookup.NotConnected, src.Resolve(1).State.Lookup);
    }

    [Fact]
    public void DeletedCacheFileIsReportedMissing()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var tex = src.AddCacheFile("tex", "gone soon");
        src.Tracker.Record("chara/a.tex", tex);
        src.Settle();
        File.Delete(tex);

        var state = src.Resolve(1).State;
        Assert.Equal(SyncState.CompleteWithMissing, state.SyncState);
        Assert.Equal(ResourceAvailability.Missing, state.Resources.Single().Availability);
    }
}

public class VanillaFilterTests
{
    [Fact]
    public void LeavesOutFilesIdenticalToTheGamesOwn()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var copy = src.AddCacheFile("tex", "vanilla texture");
        var changed = src.AddCacheFile("mdl", "modded model");
        var newFile = src.AddCacheFile("tex", "custom texture");
        var shared = src.AddCacheFile("mtrl", "vanilla material");
        src.GameFiles["chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex"] = Encoding.UTF8.GetBytes("vanilla texture");
        src.GameFiles["chara/equipment/e0001/model/c0101e0001_top.mdl"] = Encoding.UTF8.GetBytes("original model");
        src.GameFiles["chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"] = Encoding.UTF8.GetBytes("vanilla material");
        src.Tracker.Record("chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex", copy);
        src.Tracker.Record("chara/equipment/e0001/model/c0101e0001_top.mdl", changed);
        src.Tracker.Record("chara/common/texture/custom.tex", newFile);
        // Identical for one path but used for another path the game has no such file at: it is a real change.
        src.Tracker.Record("chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl", shared);
        src.Tracker.Record("chara/equipment/e0002/material/v0001/mt_c0101e0002_top_a.mtrl", shared);
        src.Settle();

        var state = src.Resolve(1).State;

        Assert.Equal(3, state.Resources.Count);
        Assert.DoesNotContain(state.Resources, r => r.GamePaths.Contains("chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex"));
        Assert.Contains(state.Notes, n => n.StartsWith("1 synced file(s) are identical to the game's own files", StringComparison.Ordinal));
        Assert.Equal(SyncState.Complete, state.SyncState);
    }

    [Fact]
    public void UnreadableGameFileKeepsTheFile()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "a.tex");
        File.WriteAllText(file, "x");
        var hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file)));
        var comparer = new VanillaFileComparer(_ => throw new IOException("locked"));
        Assert.False(comparer.IsUnchanged(hash, file, ["chara/a.tex"]));
        Assert.False(new VanillaFileComparer(_ => File.ReadAllBytes(file)).IsUnchanged(hash, file, []));
        Assert.True(new VanillaFileComparer(_ => File.ReadAllBytes(file)).IsUnchanged(hash, file, ["chara/a.tex"]));
    }
}

public class PenumbraSourceExportTests
{
    [Fact]
    public async Task ExportsVerifiedFilesAndPackageWithoutTouchingTheCache()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var tex = src.AddCacheFile("tex", "texture");
        var mtrl = src.AddCacheFile("mtrl", "material");
        src.Tracker.Record("chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex", tex);
        src.Tracker.Record("chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl", "|1_2|" + mtrl);
        src.Settle();
        var cacheBefore = Snapshot.Of(src.CacheFolder);

        var parent = dir.Sub("parent");
        var coordinator = new ExportCoordinator(src, new ExportFolderService(() => [src.CacheFolder, src.ModFolder]), "0.2.0.0", (_, _) => Task.Delay(5));
        var hashes = src.Resolve(1).Files.Keys.ToList();
        var outcome = await coordinator.ExportAsync(new ExportTarget(1, "Alpha Beta", "World"), Settings.For(parent), hashes, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
        Assert.All(outcome.Files, f => Assert.True(f.Sha1Verified));
        Assert.Equal(2, Directory.GetFiles(outcome.Layout!.AssetsFolder).Length);
        var package = Assert.Single(outcome.Manifest!.InstallablePackages, p => p.Item == null);
        using (var zip = ZipFile.OpenRead(Path.Combine(outcome.Layout.SessionFolder, package.File)))
            Assert.Contains(zip.Entries, e => e.FullName == "default_mod.json");
        Assert.Contains(outcome.Manifest.Notes, n => n.StartsWith("Read from Penumbra", StringComparison.Ordinal));
        Assert.Equal(cacheBefore, Snapshot.Of(src.CacheFolder));
    }

    [Fact]
    public async Task WaitsWhileFilesAreStillLoading()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        src.Tracker.Record("chara/a.tex", src.AddCacheFile("tex", "a"));
        var hashes = src.Resolve(1).Files.Keys.ToList();
        var parent = dir.Sub("parent");
        var coordinator = new ExportCoordinator(src, new ExportFolderService(() => [src.CacheFolder]), "0.2.0.0", (_, _) => Task.Delay(5));

        var export = coordinator.ExportAsync(new ExportTarget(1, "Alpha Beta", null), Settings.For(parent, s => s.SyncWaitSeconds = 60), hashes, null, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(export.IsCompleted);
        src.Settle();
        var outcome = await export;

        Assert.True(outcome.Success, outcome.Message);
    }

    [Fact]
    public async Task RenamedCharacterAbortsTheExport()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        src.Tracker.Record("chara/a.tex", src.AddCacheFile("tex", "a"));
        src.Settle();
        var hashes = src.Resolve(1).Files.Keys.ToList();
        src.Name = "Someone Else";
        var parent = dir.Sub("parent");
        var coordinator = new ExportCoordinator(src, new ExportFolderService(() => [src.CacheFolder]), "0.2.0.0", (_, _) => Task.Delay(5));

        var outcome = await coordinator.ExportAsync(new ExportTarget(1, "Alpha Beta", null), Settings.For(parent), hashes, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("different character", outcome.Message);
    }
}
