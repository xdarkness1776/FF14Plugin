using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

public class ResourceResolverTests
{
    [Fact]
    public void ClassifiesAvailableMissingExcludedSwapAndCompressedAlternates()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var tex = ps.AddCacheFile("tex", "texture");
        var alt = ps.AddCacheFile("tex", "compressed texture");
        var pair = ps.AddPair(10, "Alpha Beta");
        pair.AddResolved(tex, "Player", "chara/a.tex");
        // Compressed alternate: the received hash differs from the cache file actually used.
        pair.Applied.Add(new ReplacementInput("Player", ["chara/b.tex"], "1111111111111111111111111111111111111111", null));
        pair.Redirects["1111111111111111111111111111111111111111"] = alt.Hash;
        pair.Modded[("chara/b.tex", alt.Hash)] = alt.Path;
        pair.AddMissing("2222222222222222222222222222222222222222", "chara/c.mtrl");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/d.mdl"], "3333333333333333333333333333333333333333", null));
        pair.Excluded.Add(("chara/d.mdl", "3333333333333333333333333333333333333333"));
        pair.AddSwap("chara/e.tex", "chara/original.tex");

        var resolution = ps.Resolve(10);
        var byPath = resolution.State.Resources.ToDictionary(r => r.GamePaths[0]);

        Assert.Equal(ResourceAvailability.Available, byPath["chara/a.tex"].Availability);
        Assert.Equal(SourceKind.PlayerSyncCache, byPath["chara/a.tex"].SourceKind);
        Assert.True(byPath["chara/b.tex"].IsCompressedAlternate);
        Assert.Equal(alt.Hash, byPath["chara/b.tex"].CacheHash);
        Assert.Equal(ResourceAvailability.Missing, byPath["chara/c.mtrl"].Availability);
        Assert.Equal(ResourceAvailability.ExcludedByValidation, byPath["chara/d.mdl"].Availability);
        Assert.Equal(ResourceAvailability.FileSwap, byPath["chara/e.tex"].Availability);
        Assert.Equal(SyncState.CompleteWithMissing, resolution.State.SyncState);
        Assert.Equal(2, resolution.Files.Count);
    }

    [Fact]
    public void FilesOutsideCacheAndPenumbraAreNeverOffered()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var outside = Path.Combine(dir.Sub("elsewhere"), "abc.tex");
        File.WriteAllText(outside, "secret");
        var pair = ps.AddPair(1, "A B");
        var hash = new string('C', 40);
        pair.Applied.Add(new ReplacementInput("Player", ["chara/a.tex"], hash, null));
        pair.Modded[("chara/a.tex", hash)] = outside;

        var resolution = ps.Resolve(1);

        Assert.Empty(resolution.Files);
        Assert.Equal(ResourceAvailability.Missing, resolution.State.Resources[0].Availability);
    }

    [Theory]
    [InlineData(null, false, null, SyncState.NoData)]
    [InlineData("A", true, "A", SyncState.InProgress)]
    [InlineData("A", false, null, SyncState.NotApplied)]
    [InlineData("B", false, "A", SyncState.Stale)]
    [InlineData("A", false, "A", SyncState.Complete)]
    public void SyncStateNeedsAppliedLatestData(string? received, bool inProgress, string? applied, SyncState expected)
        => Assert.Equal(expected, ResourceResolver.ComputeState(received, inProgress, applied, []));
}

public class ExportJobRegistryTests
{
    private static async Task<ExportJobStatusDto> RunToEnd(FakePlayerSync ps, ExportRequestDto request)
    {
        var start = ps.Registry.Start(request);
        Assert.True(start.Accepted, start.Error);
        await ps.Registry.LastJobTask.WaitAsync(TimeSpan.FromSeconds(30));
        return ps.Registry.GetStatus(start.JobId);
    }

    private static ExportRequestDto Request(uint id, string name, string dest, params string[] hashes)
        => new() { EntityId = id, ExpectedPlayerName = name, DestinationDirectory = dest, CacheHashes = [.. hashes], SyncWaitSeconds = 2 };

    [Fact]
    public void RejectsInvalidRequests()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var dest = dir.Sub("out");
        var hash = new string('A', 40);

        var wrongVersion = Request(1, "A B", dest, hash);
        wrongVersion.ApiVersion = 99;
        Assert.Contains("v99", ps.Registry.Start(wrongVersion).Error);
        Assert.False(ps.Registry.Start(Request(1, "A B", dest)).Accepted);
        Assert.False(ps.Registry.Start(Request(1, "A B", dest, "../../evil")).Accepted);
        Assert.False(ps.Registry.Start(Request(1, "A B", Path.Combine(ps.CacheFolder, "x"), hash)).Accepted);
        Assert.False(ps.Registry.Start(Request(1, "A B", Path.Combine(dir.Path, "does-not-exist"), hash)).Accepted);
        Assert.False(ps.Registry.Start(Request(1, "A B", "relative", hash)).Accepted);
        Assert.False(ps.Registry.Start(null).Accepted);
    }

    [Fact]
    public async Task ExportsOnlyFilesInTheCharactersMapping()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var mine = ps.AddCacheFile("tex", "mine");
        var other = ps.AddCacheFile("tex", "other player's file");
        ps.AddPair(1, "A B").AddResolved(mine, "Player", "chara/a.tex");
        ps.AddPair(2, "C D").AddResolved(other, "Player", "chara/b.tex");
        var dest = dir.Sub("out");
        var cacheBefore = Snapshot.Of(ps.CacheFolder);

        var status = await RunToEnd(ps, Request(1, "A B", dest, mine.Hash, other.Hash));

        Assert.Equal(ExportJobPhase.CompletedWithIssues, status.Phase);
        Assert.Equal(FileExportOutcome.Copied, status.Files.Single(f => f.CacheHash == mine.Hash).Outcome);
        Assert.Equal(FileExportOutcome.NotInCharacterMapping, status.Files.Single(f => f.CacheHash == other.Hash).Outcome);
        Assert.Equal([mine.Hash.ToLowerInvariant() + ".tex"], Directory.GetFiles(dest).Select(Path.GetFileName));
        Assert.Equal(cacheBefore, Snapshot.Of(ps.CacheFolder));
    }

    [Fact]
    public async Task WaitsForRunningSynchronization()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("pap", "animation");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(file, "Player", "chara/anim.pap");
        pair.InProgress = true;
        var dest = dir.Sub("out");

        var start = ps.Registry.Start(Request(1, "A B", dest, file.Hash));
        await Task.Delay(200);
        Assert.Equal(ExportJobPhase.WaitingForSync, ps.Registry.GetStatus(start.JobId).Phase);
        Assert.Empty(Directory.GetFiles(dest));
        pair.InProgress = false;
        await ps.Registry.LastJobTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExportJobPhase.Completed, ps.Registry.GetStatus(start.JobId).Phase);
    }

    [Fact]
    public async Task IncompleteSynchronizationNeverReportsSuccess()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "old");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(file, "Player", "chara/a.tex");
        pair.ReceivedHash = "NEW";
        pair.AppliedHash = "OLD";
        var dest = dir.Sub("out");

        var status = await RunToEnd(ps, Request(1, "A B", dest, file.Hash));

        Assert.Equal(ExportJobPhase.Failed, status.Phase);
        Assert.Contains("did not finish", status.Message);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CharacterLeavingOrChangingFailsSafely(bool leaves, bool renamed)
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "a");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(file, "Player", "chara/a.tex");
        if (leaves) pair.Visible = false;
        if (renamed) pair.Name = "Someone Else";

        var status = await RunToEnd(ps, Request(1, "A B", dir.Sub("out"), file.Hash));

        Assert.Equal(ExportJobPhase.Failed, status.Phase);
    }

    [Fact]
    public async Task OnlyOneJobAtATimeAndCancellationWorks()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "a");
        var pair = ps.AddPair(1, "A B");
        pair.AddResolved(file, "Player", "chara/a.tex");
        pair.InProgress = true;
        var dest = dir.Sub("out");

        var longWait = Request(1, "A B", dest, file.Hash);
        longWait.SyncWaitSeconds = 60;
        var first = ps.Registry.Start(longWait);
        Assert.True(first.Accepted);
        Assert.False(ps.Registry.Start(Request(1, "A B", dest, file.Hash)).Accepted);
        Assert.True(ps.Registry.Cancel(first.JobId));
        await ps.Registry.LastJobTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExportJobPhase.Cancelled, ps.Registry.GetStatus(first.JobId).Phase);
        Assert.Empty(Directory.GetFiles(dest));
        Assert.True(ps.Registry.Start(Request(1, "A B", dest, file.Hash)).Accepted);
    }

    [Fact]
    public async Task SharedResourceIsCopiedOnceForTwoCharacters()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var shared = ps.AddCacheFile("mtrl", "shared material");
        ps.AddPair(1, "A B").AddResolved(shared, "Player", "chara/a.mtrl");
        ps.AddPair(2, "C D").AddResolved(shared, "Player", "chara/a.mtrl");
        var pool = dir.Sub("pool");

        var s1 = await RunToEnd(ps, Request(1, "A B", pool, shared.Hash));
        var s2 = await RunToEnd(ps, Request(2, "C D", pool, shared.Hash));

        Assert.Equal(FileExportOutcome.Copied, s1.Files[0].Outcome);
        Assert.Equal(FileExportOutcome.AlreadyPresentIdentical, s2.Files[0].Outcome);
        Assert.Single(Directory.GetFiles(pool));
    }

    [Fact]
    public async Task NoDownloadIsTriggeredByAnExport()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var file = ps.AddCacheFile("tex", "cached");
        ps.AddPair(1, "A B").AddResolved(file, "Player", "chara/a.tex");

        await RunToEnd(ps, Request(1, "A B", dir.Sub("out"), file.Hash));

        Assert.Equal(0, ps.DownloadRequests);
        Assert.Equal(0, ps.ResyncRequests);
    }
}
