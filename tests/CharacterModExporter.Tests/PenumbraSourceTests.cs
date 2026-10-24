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
        => PenumbraResolution.Build(Name, Handled, Tracker.Snapshot(), CacheFolder, ModFolder, _hashes, Tracker.LastChangeUtc, Now, Vanilla, Tracker.Truncated);

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
        Assert.Equal(["chara/equipment/a.tex", "chara/equipment/c.tex"], snapshot["/cache/aa.tex"].GamePaths);
        Assert.Equal(now, tracker.LastChangeUtc);
    }

    private static Dictionary<string, HashSet<string>> Tree(params (string File, string GamePath)[] entries)
        => entries.GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.Select(e => e.GamePath).ToHashSet());

    [Fact]
    public void TreeEventsAndLaterTreesGiveEachFileItsActivity()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new LoadedResourceTracker(() => now);
        tracker.Merge(Tree(("/cache/top.tex", "chara/top.tex"), ("/cache/hair.mdl", "chara/hair.mdl")));
        now = now.AddSeconds(1);
        tracker.Record("chara/emote.pap", "/cache/emote.pap");
        var first = tracker.Snapshot();
        Assert.Equal(ResourceActivity.Active, first["/cache/top.tex"].Activity);
        Assert.Equal(ObservationSource.InitialSnapshot, first["/cache/top.tex"].FirstSource);
        Assert.Equal(ResourceActivity.ObservedLoaded, first["/cache/emote.pap"].Activity);
        Assert.Equal(ObservationSource.Event, first["/cache/emote.pap"].FirstSource);

        // Outfit change: the next tree has a new top and no hair model.
        now = now.AddSeconds(1);
        tracker.Merge(Tree(("/cache/top2.tex", "chara/top.tex")));
        var second = tracker.Snapshot();
        Assert.Equal(ResourceActivity.Active, second["/cache/top2.tex"].Activity);
        Assert.Equal(ObservationSource.LaterSnapshot, second["/cache/top2.tex"].FirstSource);
        Assert.Equal(ResourceActivity.NoLongerActive, second["/cache/top.tex"].Activity);
        Assert.Equal(["chara/top.tex"], second["/cache/top.tex"].GamePaths);
        Assert.Equal(ResourceActivity.NoLongerActive, second["/cache/hair.mdl"].Activity);
        // The tree never lists animations, so their absence from it says nothing.
        Assert.Equal(ResourceActivity.ObservedLoaded, second["/cache/emote.pap"].Activity);
        Assert.Equal(now, tracker.LastChangeUtc);
        Assert.Equal(2, tracker.SnapshotCount);
    }

    [Fact]
    public void FileSeenLoadingAgainAfterLeavingTheTreeIsObservedAndATreeBringsItBack()
    {
        var tracker = new LoadedResourceTracker();
        tracker.Merge(Tree(("/cache/a.tex", "chara/a.tex")));
        tracker.Merge(Tree());
        Assert.Equal(ResourceActivity.NoLongerActive, tracker.Snapshot()["/cache/a.tex"].Activity);

        tracker.Record("chara/a.tex", "/cache/a.tex");
        Assert.Equal(ResourceActivity.ObservedLoaded, tracker.Snapshot()["/cache/a.tex"].Activity);
        tracker.Merge(Tree());
        Assert.Equal(ResourceActivity.NoLongerActive, tracker.Snapshot()["/cache/a.tex"].Activity);
        tracker.Merge(Tree(("/cache/a.tex", "chara/a.tex")));
        Assert.Equal(ResourceActivity.Active, tracker.Snapshot()["/cache/a.tex"].Activity);
    }

    [Fact]
    public void NoTreeChangesNothingAndAFileKeepsItsMostCurrentGamePath()
    {
        var tracker = new LoadedResourceTracker();
        tracker.Merge(Tree(("/cache/a.tex", "chara/a.tex"), ("/cache/a.tex", "chara/b.tex")));
        tracker.Merge(null);
        Assert.Equal(1, tracker.SnapshotCount);
        Assert.Equal(ResourceActivity.Active, tracker.Snapshot()["/cache/a.tex"].Activity);

        // Still used for one of its game paths: the file is active.
        tracker.Merge(Tree(("/cache/a.tex", "chara/a.tex"), ("/cache/c.tex", "chara/b.tex")));
        var snapshot = tracker.Snapshot();
        Assert.Equal(ResourceActivity.Active, snapshot["/cache/a.tex"].Activity);
        Assert.Equal(["chara/a.tex"], snapshot["/cache/a.tex"].GamePaths);
    }

    [Fact]
    public void OutOfOrderAndDuplicateEventsKeepTheFirstSightingAndTheLatestFile()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new LoadedResourceTracker(() => now);
        tracker.Record("chara/a.tex", "/cache/a.tex");
        now = now.AddSeconds(5);
        tracker.Record("chara/a.tex", "/cache/a.tex");
        var entry = tracker.Snapshot()["/cache/a.tex"];
        Assert.Equal(DateTimeOffset.UnixEpoch, entry.FirstSeenUtc);
        Assert.Equal(now, entry.LastSeenUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch, tracker.LastChangeUtc);

        tracker.Record("chara/a.tex", "/cache/b.tex");
        var snapshot = tracker.Snapshot();
        Assert.Equal(ResourceActivity.ObservedLoaded, snapshot["/cache/b.tex"].Activity);
        Assert.Equal(ResourceActivity.NoLongerActive, snapshot["/cache/a.tex"].Activity);
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void ResetForgetsEverythingIncludingHistory()
    {
        var tracker = new LoadedResourceTracker();
        tracker.Merge(Tree(("/cache/a.tex", "chara/a.tex")));
        tracker.Record("chara/a.tex", "/cache/b.tex");
        tracker.Reset();
        Assert.Empty(tracker.Snapshot());
        Assert.Equal(0, tracker.SnapshotCount);
        tracker.Merge(Tree(("/cache/c.tex", "chara/c.tex")));
        Assert.Equal(ObservationSource.InitialSnapshot, tracker.Snapshot()["/cache/c.tex"].FirstSource);
    }

    [Fact]
    public void SizeIsBounded()
    {
        var tracker = new LoadedResourceTracker();
        for (var i = 0; i < LoadedResourceTracker.MaxEntries + 10; i++) tracker.Record($"chara/{i}.tex", $"/cache/{i}.tex");
        Assert.Equal(LoadedResourceTracker.MaxEntries, tracker.Count);
        Assert.True(tracker.Truncated);

        // Replacements of known game paths still count, and the history of replaced files is bounded too.
        for (var i = 0; i < LoadedResourceTracker.MaxReplaced + 10; i++) tracker.Record("chara/0.tex", $"/cache/r{i}.tex");
        Assert.Equal(LoadedResourceTracker.MaxEntries + LoadedResourceTracker.MaxReplaced, tracker.Snapshot().Count);
        tracker.Reset();
        Assert.False(tracker.Truncated);
    }

    [Fact]
    public void ConcurrentEventsAndTreesAreSafe()
    {
        var tracker = new LoadedResourceTracker();
        Parallel.For(0, 2000, i =>
        {
            if (i % 100 == 0) tracker.Merge(Tree(("/cache/t.tex", "chara/t.tex")));
            else tracker.Record($"chara/{i % 300}.tex", $"/cache/{i % 300}.tex");
            if (i % 50 == 0) _ = tracker.Snapshot();
        });
        // Paths 0, 100 and 200 only come up on the tree iterations; the tree adds chara/t.tex.
        Assert.Equal(297 + 1, tracker.Count);
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
    public void FilesNoLongerInUseAreListedButDoNotMakeTheSetIncomplete()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var old = src.AddCacheFile("tex", "old top");
        var current = src.AddCacheFile("tex", "new top");
        src.Tracker.Merge(new Dictionary<string, HashSet<string>> { [old] = ["chara/top.tex"] });
        src.Tracker.Merge(new Dictionary<string, HashSet<string>> { [current] = ["chara/top.tex"] });
        File.Delete(old);
        src.Settle();

        var state = src.Resolve(1).State;
        Assert.Equal(SyncState.Complete, state.SyncState);
        Assert.Equal(ResourceActivity.NoLongerActive, state.Resources.Single(r => r.Availability == ResourceAvailability.Missing).Activity);
        Assert.Equal(ResourceActivity.Active, state.Resources.Single(r => r.Availability == ResourceAvailability.Available).Activity);
        Assert.Contains(state.Notes, n => n.Contains("no longer in use"));
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
    public async Task FilesNoLongerInUseStartUnselectedAndNeverEnterPackages()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        const string gamePath = "chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex";
        var old = src.AddCacheFile("tex", "old top");
        var current = src.AddCacheFile("tex", "new top");
        src.Tracker.Merge(new Dictionary<string, HashSet<string>> { [old] = [gamePath] });
        src.Settle();
        var parent = dir.Sub("parent");
        var folders = new ExportFolderService(() => [src.CacheFolder, src.ModFolder]);
        var coordinator = new ExportCoordinator(src, folders, "0.5.0.0", (_, _) => Task.Delay(5));
        using var session = new ExportSession(new ExportTarget(1, "Alpha Beta", "World"), src, folders, coordinator,
            () => Settings.For(parent), _ => Task.FromResult(true), (_, t) => Task.Delay(1, t));
        await session.RefreshAsync();
        Assert.True(Assert.Single(session.Inventory.Assets).Selected);

        src.Tracker.Merge(new Dictionary<string, HashSet<string>> { [current] = [gamePath] });
        src.Settle();
        await session.RefreshAsync();
        var oldHash = Path.GetFileNameWithoutExtension(old).ToUpperInvariant();
        var oldRow = session.Inventory.Assets.Single(a => a.CacheHash == oldHash);
        Assert.Equal(ResourceActivity.NoLongerActive, oldRow.Activity);
        Assert.False(oldRow.Selected);
        Assert.True(session.Inventory.Assets.Single(a => a.CacheHash != oldHash).Selected);

        // Even exported on purpose, the old file stays out of the package, which would otherwise map the game path twice.
        oldRow.Selected = true;
        await session.ExportSelectedAsync();
        Assert.Null(session.LastError);
        var manifest = session.LastOutcome!.Manifest!;
        Assert.Equal(2, manifest.ManifestVersion);
        var oldAsset = manifest.Assets.Single(a => a.CacheHash == oldHash);
        Assert.Equal("NoLongerActive", oldAsset.Activity);
        Assert.NotNull(oldAsset.ExportedFile);
        Assert.False(oldAsset.InInstallablePackage);
        Assert.True(manifest.Assets.Single(a => a.CacheHash != oldHash).InInstallablePackage);
        var package = Assert.Single(manifest.InstallablePackages, p => p.Item == null);
        Assert.Equal(1, package.Files);
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
