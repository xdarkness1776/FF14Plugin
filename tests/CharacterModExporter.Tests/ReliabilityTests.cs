using System.Text;
using CharacterModExporter.Core;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

/// <summary>Destination checks that resolve links, as the UI and the export IPC both run them.</summary>
public class DestinationValidationTests
{
    [Fact]
    public void SymbolicLinkIntoAForbiddenFolderIsRejected()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("playersync-cache");
        var link = Path.Combine(dir.Path, "looks-harmless");
        Directory.CreateSymbolicLink(link, cache);

        var error = ExportPathPolicy.ValidateDestination(link, [cache]);
        Assert.NotNull(error);
        Assert.Contains("link or junction", error);

        // A folder that does not exist yet below the link is resolved through the link too.
        Assert.NotNull(ExportPathPolicy.ValidateDestination(Path.Combine(link, "not-yet", "created"), [cache]));
    }

    [Fact]
    public void LinkedForbiddenFolderIsCheckedByItsTarget()
    {
        using var dir = new TempDir();
        var realCache = dir.Sub("real-cache");
        var cacheLink = Path.Combine(dir.Path, "cache-link");
        Directory.CreateSymbolicLink(cacheLink, realCache);

        Assert.NotNull(ExportPathPolicy.ValidateDestination(Path.Combine(realCache, "out"), [cacheLink]));
    }

    [Fact]
    public void LinkToAnAllowedFolderIsAccepted()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("playersync-cache");
        var exports = dir.Sub("exports");
        var link = Path.Combine(dir.Path, "exports-link");
        Directory.CreateSymbolicLink(link, exports);

        Assert.Null(ExportPathPolicy.ValidateDestination(Path.Combine(link, "new"), [cache]));
        Assert.Equal(Path.Combine(exports, "new"), ExportPathPolicy.ResolveExistingLinks(Path.Combine(link, "new")));
    }

    [Fact]
    public void TraversalIntoAForbiddenFolderIsRejected()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("playersync-cache");
        dir.Sub("exports");
        var sneaky = Path.Combine(dir.Path, "exports", "..", "playersync-cache", "x");

        Assert.NotNull(ExportPathPolicy.ValidateDestination(sneaky, [cache]));
    }

    [Fact]
    public void NonexistentChildOfAnAllowedFolderIsAccepted()
    {
        using var dir = new TempDir();
        var cache = dir.Sub("playersync-cache");
        var dest = Path.Combine(dir.Path, "exports", "a", "b");

        Assert.Null(ExportPathPolicy.ValidateDestination(dest, [cache]));
        Assert.False(Directory.Exists(dest)); // validation never creates anything
    }

    [Fact]
    public void FileInThePathIsRejected()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "a-file");
        File.WriteAllText(file, "x");

        Assert.Contains("is a file", ExportPathPolicy.ValidateDestination(file, []));
        Assert.NotNull(ExportPathPolicy.ValidateDestination(Path.Combine(file, "below"), []));
    }

    [Fact]
    public void UnavailableDriveIsRejected()
    {
        using var dir = new TempDir();
        var error = ExportPathPolicy.ValidateDestination(Path.Combine(dir.Path, "out"), [], driveExists: _ => false);
        Assert.NotNull(error);
        Assert.Contains("not available", error);
    }

    [Fact]
    public void CommonRootsCoverPluginConfigsDalamudAndInstalledPlugins()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmx-roots");
        var installed = Path.Combine(root, "installedPlugins");
        var assemblyDir = Path.Combine(installed, "CharacterModExporter", "0.6.0.0");
        var roots = ExportPathPolicy.CommonForbiddenRoots(Path.Combine(root, "pluginConfigs"), assemblyDir, Path.Combine(root, "dalamud"))
            .Where(r => r != null).ToList();

        Assert.Contains(Path.Combine(root, "pluginConfigs"), roots);
        Assert.Contains(Path.Combine(root, "dalamud"), roots);
        Assert.Contains(assemblyDir, roots);
        Assert.Contains(installed, roots);
    }

    [Fact]
    public void IpcRequestIntoAForbiddenFolderIsRefused()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "a");
        ps.AddPair(1, "A B").AddResolved(file, "Player", "chara/a.tex");
        var link = Path.Combine(dir.Path, "into-cache");
        Directory.CreateSymbolicLink(link, ps.CacheFolder);
        var before = Snapshot.Of(ps.CacheFolder);

        // Called directly, as another plugin could, without the exporter's UI checks.
        var direct = ps.Registry.Start(new ExportRequestDto
        {
            EntityId = 1, ExpectedPlayerName = "A B", DestinationDirectory = ps.CacheFolder, CacheHashes = [file.Hash],
        });
        var viaLink = ps.Registry.Start(new ExportRequestDto
        {
            EntityId = 1, ExpectedPlayerName = "A B", DestinationDirectory = link, CacheHashes = [file.Hash],
        });

        Assert.False(direct.Accepted);
        Assert.False(viaLink.Accepted);
        Assert.Contains("link or junction", viaLink.Error);
        Assert.Equal(before, Snapshot.Of(ps.CacheFolder));
    }
}

/// <summary>Job cleanup, the one-job slot and the frozen-mapping policy.</summary>
public class JobLifecycleTests
{
    private static async Task<ExportJobStatusDto> RunToEnd(ExportJobRegistry registry, ExportRequestDto request)
    {
        var start = registry.Start(request);
        Assert.True(start.Accepted, start.Error);
        await registry.LastJobTask.WaitAsync(TimeSpan.FromSeconds(30));
        return registry.GetStatus(start.JobId);
    }

    private static ExportRequestDto Request(string dest, params string[] hashes)
        => new() { EntityId = 1, ExpectedPlayerName = "A B", DestinationDirectory = dest, CacheHashes = [.. hashes], SyncWaitSeconds = 2 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailingCleanupIsReportedAndTheNextJobStillRuns(bool nonIoException)
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "original");
        File.WriteAllText(file.Path, "changed after indexing"); // the copy fails verification and its temp file must go
        ps.AddPair(1, "A B").AddResolved(file, "Player", "chara/a.tex");
        var deletes = 0;
        using var registry = new ExportJobRegistry(ps.Resolve, () => [ps.CacheFolder], delay: (_, t) => Task.Delay(5, t),
            deleteTempFile: _ =>
            {
                deletes++;
                throw nonIoException ? new InvalidOperationException("boom") : new IOException("locked");
            });
        var dest = dir.Sub("out");

        var first = await RunToEnd(registry, Request(dest, file.Hash));

        Assert.True(deletes >= 1);
        Assert.Equal(ExportJobPhase.CompletedWithIssues, first.Phase); // the job's own outcome is kept
        Assert.Equal(FileExportOutcome.IntegrityMismatch, first.Files.Single().Outcome);
        Assert.Contains("could not be removed", first.Message);

        // The slot was released: a later job is accepted and runs.
        File.WriteAllText(file.Path, "original");
        var second = await RunToEnd(registry, Request(dir.Sub("out2"), file.Hash));
        Assert.Equal(ExportJobPhase.Completed, second.Phase);
    }

    [Fact]
    public async Task AnotherJobsPartialFileIsNeverDeleted()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "a");
        ps.AddPair(1, "A B").AddResolved(file, "Player", "chara/a.tex");
        var dest = dir.Sub("out");
        var foreign = Path.Combine(dest, file.Hash.ToLowerInvariant() + ".tex.abcdef12" + VerifiedFileCopier.TempSuffix);
        File.WriteAllText(foreign, "in use by another export");

        var status = await RunToEnd(ps.Registry, Request(dest, file.Hash));

        Assert.Equal(ExportJobPhase.Completed, status.Phase);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task CancelledJobRemovesItsTempFilesAndReleasesTheSlot()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var files = Enumerable.Range(0, 20).Select(i => ps.AddCacheFile("tex", new string('x', 4096) + i)).ToList();
        var pair = ps.AddPair(1, "A B");
        foreach (var (f, i) in files.Select((f, i) => (f, i))) pair.AddResolved(f, "Player", $"chara/{i}.tex");
        var dest = dir.Sub("out");
        var gate = new TaskCompletionSource();
        using var registry = new ExportJobRegistry(ps.Resolve, () => [ps.CacheFolder], delay: async (_, t) => await gate.Task.WaitAsync(t));
        pair.InProgress = true; // holds the job in its wait loop until cancelled

        var start = registry.Start(Request(dest, [.. files.Select(f => f.Hash)]));
        Assert.True(start.Accepted);
        Assert.False(registry.Start(Request(dest, files[0].Hash)).Accepted);
        Assert.True(registry.Cancel(start.JobId));
        await registry.LastJobTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExportJobPhase.Cancelled, registry.GetStatus(start.JobId).Phase);
        Assert.Empty(Directory.GetFiles(dest, "*" + VerifiedFileCopier.TempSuffix));
        pair.InProgress = false;
        gate.SetResult();
        Assert.Equal(ExportJobPhase.Completed, (await RunToEnd(registry, Request(dir.Sub("out2"), files[0].Hash))).Phase);
    }

    [Fact]
    public async Task MappingIsFrozenOnceCopyingStarts()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        var b = ps.AddCacheFile("tex", "b");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(a, "Player", "chara/a.tex");
        pair.AddResolved(b, "Player", "chara/b.tex");
        var reads = 0;
        // After the first read the character is gone: the copies continue from the mapping read at the start.
        using var registry = new ExportJobRegistry(id =>
        {
            if (++reads > 1) pair.Visible = false;
            return ps.Resolve(id);
        }, () => [ps.CacheFolder], delay: (_, t) => Task.Delay(5, t));

        var status = await RunToEnd(registry, Request(dir.Sub("out"), a.Hash, b.Hash));

        Assert.Equal(1, reads);
        Assert.Equal(ExportJobPhase.Completed, status.Phase);
        Assert.Equal(2, status.Files.Count(f => f.Outcome == FileExportOutcome.Copied));
    }

    [Fact]
    public async Task DestinationIsCheckedAgainBeforeEveryFile()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        ps.AddPair(1, "A B").AddResolved(a, "Player", "chara/a.tex");
        var dest = dir.Sub("out");
        var checks = 0;
        // Allowed when the job starts, forbidden by the time the first file is written (as if a junction was swapped in).
        using var registry = new ExportJobRegistry(ps.Resolve, () => ++checks > 1 ? [dest] : [ps.CacheFolder], delay: (_, t) => Task.Delay(5, t));

        var status = await RunToEnd(registry, Request(dest, a.Hash));

        Assert.Equal(ExportJobPhase.Failed, status.Phase);
        Assert.Contains("no longer allowed", status.Message);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task SameNameFromAnotherWorldIsNotExported()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        var pair = ps.AddPair(1, "A B");
        pair.HomeWorld = "Odin";
        pair.AddResolved(a, "Player", "chara/a.tex");
        var dest = dir.Sub("out");
        var request = Request(dest, a.Hash);
        request.ExpectedHomeWorld = "Ragnarok";

        var status = await RunToEnd(ps.Registry, request);

        Assert.Equal(ExportJobPhase.Failed, status.Phase);
        Assert.Contains("Odin", status.Message);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task UnconfirmedWorldIsRecorded()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var a = ps.AddCacheFile("tex", "a");
        ps.AddPair(1, "A B").AddResolved(a, "Player", "chara/a.tex"); // the source reports no world
        var request = Request(dir.Sub("out"), a.Hash);
        request.ExpectedHomeWorld = "Ragnarok";

        var status = await RunToEnd(ps.Registry, request);

        Assert.Equal(ExportJobPhase.Completed, status.Phase);
        Assert.Contains(status.State!.Notes, n => n.Contains("home world could not be confirmed"));
    }
}

/// <summary>Scan and Refresh are read-only; only the explicit reapply action may make PlayerSync download.</summary>
public class ScanAndReapplyTests
{
    private static ExportSession Session(TempDir dir, FakePlayerSync ps, ExportTarget? target = null)
    {
        var folders = new ExportFolderService(() => [ps.CacheFolder]);
        var settings = Settings.For(dir.Sub("parent"));
        return new ExportSession(target ?? new ExportTarget(7, "A B", null), ps, folders,
            new ExportCoordinator(ps, folders, "t", (_, _) => Task.Delay(5)), () => settings, _ => Task.FromResult(true), (_, _) => Task.Delay(5));
    }

    [Fact]
    public async Task RefreshScanAndExportNeverReapply()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var cached = ps.AddCacheFile("tex", "cached");
        var pair = ps.AddPair(7, "A B");
        pair.AddResolved(cached, "Player", "chara/a.tex");
        pair.AddMissing("CDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCD", "chara/b.tex");
        var cacheBefore = Snapshot.Of(ps.CacheFolder);
        using var session = Session(dir, ps);

        await session.RefreshAsync();
        await session.ScanAsync();
        await session.ExportSelectedAsync();

        Assert.Equal(0, ps.ReapplyRequests);
        Assert.Equal(0, ps.DownloadRequests);
        Assert.Equal(cacheBefore, Snapshot.Of(ps.CacheFolder));
        Assert.Single(session.Inventory.Unresolved); // the missing file is reported, not fetched
    }

    [Fact]
    public async Task StaleMappingIsReportedNotRepaired()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var pair = ps.AddPair(7, "A B");
        pair.AddResolved(ps.AddCacheFile("tex", "old"), "Player", "chara/a.tex");
        pair.ReceivedHash = "NEW";
        pair.AppliedHash = "OLD";
        using var session = Session(dir, ps);

        await session.ScanAsync();

        Assert.Equal(MappingFreshness.Stale, session.Freshness);
        Assert.Equal(0, ps.ReapplyRequests);
    }

    [Fact]
    public async Task ReplacedTargetClearsTheInventory()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var pair = ps.AddPair(7, "A B");
        pair.HomeWorld = "Odin";
        pair.AddResolved(ps.AddCacheFile("tex", "a"), "Player", "chara/a.tex");
        using var session = Session(dir, ps, new ExportTarget(7, "A B", "Odin"));

        await session.RefreshAsync();
        Assert.Single(session.Inventory.Assets);

        pair.HomeWorld = "Ragnarok"; // same entity id and name, another character
        await session.RefreshAsync();
        Assert.Null(session.State);
        Assert.Empty(session.Inventory.Assets);
        Assert.NotNull(session.LastError);
    }
}

public class FreshnessAndIdentityTests
{
    [Theory]
    [InlineData(CharacterLookup.NotSynced, SyncState.Complete, true, MappingFreshness.Unavailable)]
    [InlineData(CharacterLookup.Found, SyncState.NoData, true, MappingFreshness.Unavailable)]
    [InlineData(CharacterLookup.Found, SyncState.NotApplied, true, MappingFreshness.Unavailable)]
    [InlineData(CharacterLookup.Found, SyncState.InProgress, true, MappingFreshness.Loading)]
    [InlineData(CharacterLookup.Found, SyncState.Stale, true, MappingFreshness.Stale)]
    [InlineData(CharacterLookup.Found, SyncState.CompleteWithMissing, true, MappingFreshness.Incomplete)]
    [InlineData(CharacterLookup.Found, SyncState.Complete, true, MappingFreshness.Current)]
    [InlineData(CharacterLookup.Found, SyncState.Complete, false, MappingFreshness.LoadedOnly)]
    public void FreshnessFollowsTheReportedState(CharacterLookup lookup, SyncState sync, bool linkKnown, MappingFreshness expected)
        => Assert.Equal(expected, Freshness.Of(new CharacterStateDto { Lookup = lookup, SyncState = sync, LinkDetailsKnown = linkKnown }));

    [Theory]
    [InlineData("A B", "Odin", "A B", "Odin", TargetIdentity.Result.Same)]
    [InlineData("A B", "Odin", "A B", "odin", TargetIdentity.Result.Same)]
    [InlineData("A B", "Odin", "A B", null, TargetIdentity.Result.WorldUnconfirmed)]
    [InlineData("A B", null, "A B", "Odin", TargetIdentity.Result.WorldUnconfirmed)]
    [InlineData("A B", "Odin", "A B", "Ragnarok", TargetIdentity.Result.Different)]
    [InlineData("A B", "Odin", "a b", "Odin", TargetIdentity.Result.Different)]
    [InlineData("A B", "Odin", null, "Odin", TargetIdentity.Result.Different)]
    public void TargetIdentityUsesNameAndWorld(string name, string? world, string? seenName, string? seenWorld, TargetIdentity.Result expected)
        => Assert.Equal(expected, TargetIdentity.Compare(new ExportTarget(1, name, world), seenName, seenWorld));
}

public class ZoneSyncSettingsTests
{
    [Theory]
    [InlineData(true, null, true, null)]   // no override: the global setting
    [InlineData(false, null, false, null)]
    [InlineData(false, true, true, true)]  // the override for the local character wins
    [InlineData(true, false, false, false)]
    public void PerCharacterOverrideWins(bool global, bool? overrideValue, bool effective, bool? reportedOverride)
    {
        var perCharacter = new Dictionary<string, bool>();
        if (overrideValue is { } v) perCharacter["Local Player"] = v;
        perCharacter["Someone Else"] = !global; // another of the user's characters never applies

        Assert.Equal((effective, reportedOverride), ZoneSyncSettings.Effective(global, perCharacter, "Local Player"));
    }

    [Fact]
    public void UnknownLocalPlayerUsesTheGlobalSetting()
    {
        Assert.Equal((true, (bool?)null), ZoneSyncSettings.Effective(true, new Dictionary<string, bool> { ["X"] = false }, null));
        Assert.Equal((false, (bool?)null), ZoneSyncSettings.Effective(false, null, "X"));
    }

    [Fact]
    public async Task StateReportsEffectiveSettingSeparatelyFromSyncAndCompleteness()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        ps.ZoneSyncGlobal = false;
        ps.ZoneSyncPerCharacter["Local Player"] = true;
        var pair = ps.AddPair(1, "A B");
        pair.AddMissing("CDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCDCD", "chara/b.tex");

        var state = ps.GetCharacterState(1);

        Assert.False(state.ZoneSyncJoiningGlobal);
        Assert.True(state.ZoneSyncJoiningOverride);
        Assert.True(state.ZoneSyncJoiningEffective);
        Assert.Equal(SyncState.CompleteWithMissing, state.SyncState); // joining enabled says nothing about completeness
        await Task.CompletedTask;
    }

    [Fact]
    public void EligibleButNotSyncedIsNotFound()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        ps.ZoneSyncGlobal = true; // joining is on, but no pair exists for this object

        var state = ps.GetCharacterState(99);

        Assert.Equal(CharacterLookup.NotSynced, state.Lookup);
        Assert.True(state.ZoneSyncJoiningEffective);
    }
}

/// <summary>Attribution of Penumbra events by address, with captures that start over on any change.</summary>
public class CharacterWatchListTests
{
    [Fact]
    public void StaleEventAfterReplacementNeverReachesTheNewCapture()
    {
        var watch = new CharacterWatchList();
        var first = watch.Watch(1, 100, "A B", "Odin");
        var staleTarget = watch.TrackerAt(100); // an event that looked up its tracker before the change

        // Another character takes over the entity id and address.
        Assert.True(watch.Check(1, 100, "C D", "Odin"));
        var second = watch.TrackerOf(1)!;
        staleTarget!.Record("chara/old.tex", "/cache/old.tex");

        Assert.NotSame(first, second);
        Assert.Empty(second.Snapshot());
        Assert.Same(second, watch.TrackerAt(100));
    }

    [Fact]
    public void DespawnStopsAttributionAndRespawnStartsOver()
    {
        var watch = new CharacterWatchList();
        var first = watch.Watch(1, 100, "A B", "Odin");

        Assert.True(watch.Check(1, null, null, null));
        Assert.Null(watch.TrackerAt(100)); // whoever loads at the old address is not recorded for A B
        Assert.False(watch.Check(1, null, null, null));

        Assert.True(watch.Check(1, 200, "A B", "Odin"));
        Assert.NotSame(first, watch.TrackerOf(1));
        Assert.Same(watch.TrackerOf(1), watch.TrackerAt(200));
    }

    [Fact]
    public void ZoneChangeAndForgetDetachEveryAddress()
    {
        var watch = new CharacterWatchList();
        watch.Watch(1, 100, "A B", "Odin");
        watch.Watch(2, 200, "C D", null);

        watch.ZoneChanged();
        Assert.Null(watch.TrackerAt(100));
        Assert.Null(watch.TrackerAt(200));

        var again = watch.Watch(1, 300, "A B", "Odin");
        Assert.Same(again, watch.TrackerAt(300));
        watch.Forget(1);
        Assert.Null(watch.TrackerAt(300));
        Assert.Null(watch.TrackerOf(1));
        Assert.Equal(1, watch.Count);
    }

    [Fact]
    public void UnchangedCharacterKeepsItsCapture()
    {
        var watch = new CharacterWatchList();
        var tracker = watch.Watch(1, 100, "A B", "Odin");
        tracker.Record("chara/a.tex", "/cache/a.tex");

        Assert.False(watch.Check(1, 100, "A B", "odin"));
        Assert.Same(tracker, watch.Watch(1, 100, "A B", "Odin"));
        Assert.Single(tracker.Snapshot());
    }

    [Fact]
    public void ForgettingOneCharacterKeepsAnotherAtAReusedAddress()
    {
        var watch = new CharacterWatchList();
        watch.Watch(1, 100, "A B", null);
        watch.ZoneChanged();
        var other = watch.Watch(2, 100, "C D", null); // the address now belongs to someone else

        watch.Forget(1);
        Assert.Same(other, watch.TrackerAt(100));
    }
}
