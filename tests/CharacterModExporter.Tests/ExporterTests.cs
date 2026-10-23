using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterModExporter.Core;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

public class ExportFolderServiceTests
{
    [Fact]
    public void DefaultsToDocumentsFolder()
    {
        using var dir = new TempDir();
        var folders = new ExportFolderService(() => [], () => dir.Path);
        Assert.Equal(Path.Combine(dir.Path, "FFXIV Character Mod Exports"), folders.ResolveRoot(new ExportSettings()));
    }

    [Fact]
    public void CreatesRootCharacterAndAssetsAutomatically()
    {
        using var dir = new TempDir();
        var parent = dir.Sub("My FFXIV Mods");
        var folders = new ExportFolderService(() => []);

        var layout = folders.Prepare(Settings.For(parent), "Alpha Beta", "Twintania", DateTimeOffset.UtcNow, createInstallable: false);

        Assert.Equal(Path.Combine(parent, "FFXIV Character Mod Exports"), layout.Root);
        Assert.Equal(Path.Combine(layout.Root, "Alpha_Beta_Twintania"), layout.CharacterFolder);
        Assert.True(Directory.Exists(layout.AssetsFolder));
        Assert.False(Directory.Exists(layout.InstallableFolder));
    }

    [Fact]
    public void TimestampedSessionsDoNotCollide()
    {
        using var dir = new TempDir();
        var folders = new ExportFolderService(() => []);
        var settings = Settings.For(dir.Path, s => s.ReuseCharacterFolder = false);
        var now = DateTimeOffset.UtcNow;

        var a = folders.Prepare(settings, "A B", "W", now, false);
        var b = folders.Prepare(settings, "A B", "W", now, false);

        Assert.NotEqual(a.SessionFolder, b.SessionFolder);
        Assert.Equal(a.CharacterFolder, b.CharacterFolder);
    }

    [Fact]
    public void SameNameOnDifferentWorldsGetsDifferentFolders()
        => Assert.NotEqual(ExportFolderService.CharacterFolderName("A B", "Twintania"), ExportFolderService.CharacterFolderName("A B", "Lich"));

    [Fact]
    public void HostileNamesStayInsideTheRoot()
    {
        using var dir = new TempDir();
        var folders = new ExportFolderService(() => []);
        var layout = folders.Prepare(Settings.For(dir.Path, s => s.RootFolderName = "../../escape"), "../..", "..\\x", DateTimeOffset.UtcNow, false);
        Assert.True(ExportPathPolicy.IsWithin(dir.Path, layout.AssetsFolder));
    }

    [Fact]
    public void InvalidOrUnavailableLocationsGiveActionableErrors()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("cache");
        var folders = new ExportFolderService(() => [cache]);

        Assert.Contains("does not exist", folders.ValidateParent(Path.Combine(dir.Path, "missing")));
        Assert.NotNull(folders.ValidateParent(cache));
        Assert.NotNull(folders.ValidateParent("not/absolute"));
        Assert.Throws<ExportFolderException>(() => folders.Prepare(Settings.For(Path.Combine(dir.Path, "missing")), "A", "B", DateTimeOffset.UtcNow, false));
        if (OperatingSystem.IsWindows())
            Assert.Contains("not available", folders.ValidateParent(@"Q:\Exports") ?? "not available");
    }

    [Fact]
    public void SettingsSurviveSerialization()
    {
        // Dalamud persists the configuration as JSON; the export location must round-trip.
        var settings = new ExportSettings { ParentFolder = @"D:\My FFXIV Mods", RootFolderName = "Exports", ReuseCharacterFolder = false, StorageMode = AssetStorageMode.SharedPool };
        var copy = JsonSerializer.Deserialize<ExportSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(settings.ParentFolder, copy.ParentFolder);
        Assert.Equal(settings.RootFolderName, copy.RootFolderName);
        Assert.Equal(settings.ReuseCharacterFolder, copy.ReuseCharacterFolder);
        Assert.Equal(settings.StorageMode, copy.StorageMode);
    }
}

public class PenumbraPackageBuilderTests
{
    [Theory]
    [InlineData("chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex", "chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex")]
    [InlineData("Chara\\Human\\c0101\\x.MDL", "chara/human/c0101/x.mdl")]
    [InlineData("/vfx/a.avfx", "vfx/a.avfx")]
    [InlineData("../evil.tex", null)]
    [InlineData("c:/evil.tex", null)]
    [InlineData("chara/ünicode.tex", null)]
    [InlineData("", null)]
    public void NormalizesGamePaths(string input, string? expected) => Assert.Equal(expected, PenumbraPackageBuilder.NormalizeGamePath(input));

    [Fact]
    public void BuildsAValidPackage()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Sub("assets"), "aaaa.tex");
        File.WriteAllText(a, "tex");
        var b = Path.Combine(dir.Path, "assets", "bbbb.mtrl");
        File.WriteAllText(b, "mtrl");

        var result = PenumbraPackageBuilder.Build(dir.Sub("installable"), "Alpha (exported)", "desc", "1",
            [new PackageFile(a, "aaaa.tex", ["chara/a.tex", "chara/a2.tex"]), new PackageFile(b, "bbbb.mtrl", ["chara/b.mtrl", "../bad"])],
            new Dictionary<string, string> { ["chara/c.tex"] = "chara/orig.tex", ["chara/a.tex"] = "chara/x.tex" });

        Assert.True(result.Created);
        Assert.Empty(PenumbraPackageBuilder.Validate(result.PackagePath!));
        Assert.Equal(3, result.RedirectCount);
        Assert.Equal(1, result.SwapCount);
        Assert.Equal(2, result.Omitted.Count);

        using var zip = ZipFile.OpenRead(result.PackagePath!);
        var meta = JsonNode.Parse(zip.GetEntry("meta.json")!.Open())!;
        Assert.Equal(3, meta["FileVersion"]!.GetValue<int>());
        var def = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!;
        Assert.Equal("files\\aaaa.tex", def["Files"]!["chara/a2.tex"]!.GetValue<string>());
        Assert.NotNull(zip.GetEntry("files/bbbb.mtrl"));
    }

    [Fact]
    public void ValidationCatchesBrokenPackages()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "broken.pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("meta.json").Open())) w.Write("{\"FileVersion\":3,\"Name\":\"x\"}");
            using (var w = new StreamWriter(zip.CreateEntry("default_mod.json").Open())) w.Write("{\"Files\":{\"chara/a.tex\":\"files\\\\missing.tex\"}}");
        }

        Assert.Contains(PenumbraPackageBuilder.Validate(path), p => p.Contains("not in the archive"));
    }

    [Fact]
    public void NothingToPackageCreatesNoFolder()
    {
        using var dir = new TempDir();
        var output = Path.Combine(dir.Path, "Installable");
        var result = PenumbraPackageBuilder.Build(output, "x", "", "1", [], new Dictionary<string, string>());
        Assert.False(result.Created);
        Assert.False(Directory.Exists(output));
    }
}

public class ExportCoordinatorTests
{
    private static (TempDir Dir, FakePlayerSync Ps, FakePair Pair, CacheFile[] Files, ExportCoordinator Coordinator, string Parent) Setup()
    {
        var dir = new TempDir();
        var ps = new FakePlayerSync(dir);
        var files = new[]
        {
            ps.AddCacheFile("tex", "base texture"),
            ps.AddCacheFile("mtrl", "material"),
            ps.AddCacheFile("avfx", "effect"),
        };
        var pair = ps.AddPair(42, "Alpha Beta");
        pair.AddResolved(files[0], "Player", "chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex");
        pair.AddResolved(files[1], "Player", "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl");
        pair.AddResolved(files[2], "MinionOrMount", "vfx/common/eff/x.avfx");
        pair.AddSwap("chara/equipment/e0002/texture/a.tex", "chara/equipment/e0003/texture/a.tex");
        var folders = new ExportFolderService(() => [ps.CacheFolder, ps.PenumbraFolder]);
        var coordinator = new ExportCoordinator(ps, folders, "0.1.0.0", (_, _) => Task.Delay(5));
        return (dir, ps, pair, files, coordinator, dir.Sub("parent"));
    }

    [Fact]
    public async Task FullExportWritesAssetsManifestReadmeAndValidPackage()
    {
        var (dir, ps, _, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;
        var cacheBefore = Snapshot.Of(ps.CacheFolder);

        var outcome = await coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", "Twintania"), Settings.For(parent),
            files.Select(f => f.Hash).ToList(), null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
        var layout = outcome.Layout!;
        Assert.Equal(3, Directory.GetFiles(layout.AssetsFolder).Length);
        Assert.True(File.Exists(layout.ManifestPath));
        Assert.True(File.Exists(layout.ReadmePath));

        var manifest = ManifestWriter.FromJson(File.ReadAllText(layout.ManifestPath))!;
        Assert.Equal("Completed", manifest.Result);
        Assert.All(manifest.Assets, a => Assert.True(a.Sha1Verified));
        Assert.All(manifest.Assets, a => Assert.NotNull(a.Sha256));
        Assert.Single(manifest.FileSwaps);
        Assert.True(manifest.Sync.IsZoneSyncOnly);
        var package = Assert.Single(manifest.InstallablePackages, p => p.Item == null);
        Assert.Empty(PenumbraPackageBuilder.Validate(Path.Combine(layout.SessionFolder, package.File)));
        Assert.Contains("individual game resource", File.ReadAllText(layout.ReadmePath));
        Assert.Equal(cacheBefore, Snapshot.Of(ps.CacheFolder));
        Assert.Equal(0, ps.DownloadRequests);
    }

    [Fact]
    public async Task MissingResourcesAreReportedAndNoPackageIsBuilt()
    {
        var (dir, ps, pair, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;
        pair.AddMissing("ABABABABABABABABABABABABABABABABABABABAB", "chara/missing.tex");

        var outcome = await coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", "Twintania"), Settings.For(parent),
            files.Select(f => f.Hash).ToList(), null, CancellationToken.None);

        var manifest = outcome.Manifest!;
        Assert.Single(manifest.Unresolved);
        // The full-character package needs every file; item mods for complete items are still built.
        Assert.DoesNotContain(manifest.InstallablePackages, p => p.Item == null);
        Assert.All(manifest.InstallablePackages, p => Assert.StartsWith("Mods/", p.File));
        Assert.False(Directory.Exists(outcome.Layout!.InstallableFolder));
        Assert.Contains(manifest.Notes, n => n.Contains("No Penumbra package"));
        Assert.Contains("chara/missing.tex", File.ReadAllText(outcome.Layout.ReadmePath));
    }

    [Fact]
    public async Task PartialSelectionDoesNotClaimAnInstallablePackage()
    {
        var (dir, ps, _, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;

        var outcome = await coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", "Twintania"), Settings.For(parent),
            [files[0].Hash], null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.Manifest!.InstallablePackages);
        Assert.Equal(2, outcome.Manifest.Assets.Count(a => !a.Selected));
    }

    [Fact]
    public async Task UnavailableOrIncompatiblePlayerSyncFailsCleanly()
    {
        var (dir, ps, _, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;

        ps.Installed = false;
        var a = await coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", null), Settings.For(parent), [files[0].Hash], null, CancellationToken.None);
        ps.Installed = true;
        ps.ProvidedVersion = 2;
        var b = await coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", null), Settings.For(parent), [files[0].Hash], null, CancellationToken.None);

        Assert.False(a.Success);
        Assert.Equal(ps.UnavailableMessage, a.Message);
        Assert.False(b.Success);
        Assert.Contains("v2", b.Message);
        Assert.False(Directory.Exists(Path.Combine(parent, ExportSettings.DefaultRootFolderName)));
    }

    [Fact]
    public async Task ReExportKeepsExistingFilesIntact()
    {
        var (dir, ps, _, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;
        var target = new ExportTarget(42, "Alpha Beta", "Twintania");
        var first = await coordinator.ExportAsync(target, Settings.For(parent), files.Select(f => f.Hash).ToList(), null, CancellationToken.None);
        var userFile = Path.Combine(first.Layout!.CharacterFolder, "my notes.txt");
        File.WriteAllText(userFile, "keep me");
        var assetsBefore = Snapshot.Of(first.Layout.AssetsFolder);

        var second = await coordinator.ExportAsync(target, Settings.For(parent), files.Select(f => f.Hash).ToList(), null, CancellationToken.None);

        Assert.True(second.Success);
        Assert.All(second.Files, f => Assert.Equal(FileExportOutcome.AlreadyPresentIdentical, f.Outcome));
        Assert.Equal(assetsBefore, Snapshot.Of(first.Layout.AssetsFolder));
        Assert.Equal("keep me", File.ReadAllText(userFile));
        Assert.True(File.Exists(first.Layout.ManifestPath + ".previous"));
        // A second package gets a new name rather than replacing the first.
        Assert.Equal(2, Directory.GetFiles(first.Layout.InstallableFolder, "*.pmp").Length);
    }

    [Fact]
    public async Task SharedPoolStoresSharedFilesOnce()
    {
        var (dir, ps, _, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;
        ps.AddPair(43, "Gamma Delta").AddResolved(files[1], "Player", "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl");
        var settings = Settings.For(parent, s => s.StorageMode = AssetStorageMode.SharedPool);

        var a = await coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", "W"), settings, files.Select(f => f.Hash).ToList(), null, CancellationToken.None);
        var b = await coordinator.ExportAsync(new ExportTarget(43, "Gamma Delta", "W"), settings, [files[1].Hash], null, CancellationToken.None);

        Assert.Equal(a.Layout!.AssetsFolder, b.Layout!.AssetsFolder);
        Assert.Equal(3, Directory.GetFiles(a.Layout.AssetsFolder).Length);
        Assert.Contains(b.Manifest!.Assets, x => x.CacheHash == files[1].Hash && x.ExportedFile != null && x.ExportedFile.Contains("_SharedAssets"));
    }

    [Fact]
    public async Task CancellationIsForwardedToPlayerSync()
    {
        var (dir, ps, pair, files, coordinator, parent) = Setup();
        using var _d = dir;
        using var _p = ps;
        pair.InProgress = true;
        using var cts = new CancellationTokenSource();
        var settings = Settings.For(parent, s => s.SyncWaitSeconds = 60);

        var task = coordinator.ExportAsync(new ExportTarget(42, "Alpha Beta", "W"), settings, files.Select(f => f.Hash).ToList(), null, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(outcome.Success);
        Assert.Equal(ExportJobPhase.Cancelled, outcome.Phase);
        Assert.Empty(Directory.GetFiles(outcome.Layout!.AssetsFolder));
    }
}

public class ExportSessionTests
{
    [Fact]
    public async Task RefreshBuildsInventoryAndKeepsSelections()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        var b = ps.AddCacheFile("tex", "b");
        var pair = ps.AddPair(7, "A B");
        pair.AddResolved(a, "Player", "chara/a.tex");
        pair.AddResolved(b, "Player", "chara/b.tex", "chara/b2.tex");
        var folders = new ExportFolderService(() => []);
        var settings = Settings.For(dir.Sub("parent"));
        using var session = new ExportSession(new ExportTarget(7, "A B", "W"), ps, folders,
            new ExportCoordinator(ps, folders, "t"), () => settings, _ => Task.FromResult(true));

        await session.RefreshAsync();
        Assert.Equal(2, session.Inventory.Assets.Count);
        Assert.All(session.Inventory.Assets, r => Assert.True(r.Selected));
        session.Inventory.Assets[0].Selected = false;

        await session.RefreshAsync();
        Assert.False(session.Inventory.Assets[0].Selected);
        Assert.Equal(2, session.Inventory.Assets.Single(r => r.CacheHash == b.Hash).GamePaths.Count);
        Assert.Equal(0, ps.ResyncRequests);
    }

    [Fact]
    public async Task DisappearedTargetLeavesWindowRecoverable()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        ps.AddPair(7, "A B");
        var present = true;
        var folders = new ExportFolderService(() => []);
        using var session = new ExportSession(new ExportTarget(7, "A B", "W"), ps, folders,
            new ExportCoordinator(ps, folders, "t"), () => new ExportSettings(), _ => Task.FromResult(present));

        present = false;
        await session.RefreshAsync();
        Assert.False(session.TargetPresent);
        Assert.Contains("no longer nearby", session.LastError);
        Assert.False(session.CanExport);
        Assert.False(session.IsBusy);

        present = true;
        await session.RefreshAsync();
        Assert.True(session.TargetPresent);
        Assert.Null(session.LastError);
    }

    [Fact]
    public async Task ResyncUsesPlayerSyncPipelineAndRetryExportsOnlyFailures()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var cached = ps.AddCacheFile("tex", "cached");
        var pair = ps.AddPair(7, "A B");
        pair.AddResolved(cached, "Player", "chara/a.tex");
        pair.AddMissing("CDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCD", "chara/b.tex");
        ps.OnResync = p =>
        {
            // PlayerSync's own pipeline fetches only the missing file; the cached one is reused.
            var downloaded = ps.SimulatePlayerSyncDownload("tex", "was missing");
            p.Applied.RemoveAll(r => r.Hash == "CDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCD");
            p.AddResolved(downloaded, "Player", "chara/b.tex");
        };
        var folders = new ExportFolderService(() => [ps.CacheFolder]);
        var settings = Settings.For(dir.Sub("parent"));
        using var session = new ExportSession(new ExportTarget(7, "A B", "W"), ps, folders,
            new ExportCoordinator(ps, folders, "t", (_, _) => Task.Delay(5)), () => settings, _ => Task.FromResult(true), (_, _) => Task.Delay(5));

        await session.RefreshAsync();
        Assert.Single(session.Inventory.Unresolved);
        await session.ResyncAsync();
        Assert.Empty(session.Inventory.Unresolved);
        Assert.Equal(1, ps.DownloadRequests);

        // Make one file fail: an unrelated file with the same name already sits in the export folder.
        var assets = folders.PredictAssetsFolder(settings, "A B", "W")!;
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, cached.Hash.ToLowerInvariant() + ".tex"), "unrelated");
        await session.ExportSelectedAsync();
        Assert.Single(session.FailedFiles);
        Assert.Equal(FileExportOutcome.DestinationConflict, session.FailedFiles[0].Outcome);

        File.Delete(Path.Combine(assets, cached.Hash.ToLowerInvariant() + ".tex"));
        await session.RetryFailedAsync();
        Assert.Empty(session.FailedFiles);
        Assert.Single(session.LastOutcome!.Files);
        Assert.Equal(1, ps.DownloadRequests);
    }
}
