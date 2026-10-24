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

    [Fact]
    public void MissingFirstPathDoesNotHideTheOtherAppliedPaths()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var tex = ps.AddCacheFile("tex", "shared texture");
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/first.tex", "chara/second.tex", "chara/third.tex"], tex.Hash, null));
        pair.Modded[("chara/second.tex", tex.Hash)] = tex.Path;
        pair.Modded[("chara/third.tex", tex.Hash)] = tex.Path;

        var r = ps.Resolve(1).State.Resources.Single();

        Assert.Equal(ResourceAvailability.Available, r.Availability);
        Assert.Equal(["chara/second.tex", "chara/third.tex"], r.GamePaths);
        Assert.Equal(["chara/first.tex"], r.MissingGamePaths);
        Assert.Null(r.ExcludedGamePaths);
    }

    [Fact]
    public void ValidationExcludingOnePathKeepsTheResourceForTheOthers()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var mdl = ps.AddCacheFile("mdl", "model");
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/a.mdl", "chara/b.mdl"], mdl.Hash, null));
        pair.Excluded.Add(("chara/a.mdl", mdl.Hash));
        pair.Modded[("chara/b.mdl", mdl.Hash)] = mdl.Path;

        var r = ps.Resolve(1).State.Resources.Single();

        Assert.Equal(ResourceAvailability.Available, r.Availability);
        Assert.Equal(["chara/b.mdl"], r.GamePaths);
        Assert.Equal(["chara/a.mdl"], r.ExcludedGamePaths);
    }

    [Fact]
    public void AllPathsExcludedIsExcludedButPartlyMissingIsMissing()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var pair = ps.AddPair(1, "A B");
        var h1 = new string('1', 40);
        var h2 = new string('2', 40);
        pair.Applied.Add(new ReplacementInput("Player", ["chara/a.mdl", "chara/b.mdl"], h1, null));
        pair.Excluded.Add(("chara/a.mdl", h1));
        pair.Excluded.Add(("chara/b.mdl", h1));
        pair.Applied.Add(new ReplacementInput("Player", ["chara/c.mdl", "chara/d.mdl"], h2, null));
        pair.Excluded.Add(("chara/c.mdl", h2));

        var byHash = ps.Resolve(1).State.Resources.ToDictionary(r => r.Hash!);

        Assert.Equal(ResourceAvailability.ExcludedByValidation, byHash[h1].Availability);
        Assert.Equal(ResourceAvailability.Missing, byHash[h2].Availability);
        Assert.Equal(["chara/c.mdl"], byHash[h2].ExcludedGamePaths);
    }

    [Fact]
    public void CompressedAlternateIsFoundOnEveryPathAndExcludedUnderEitherHash()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var alt = ps.AddCacheFile("tex", "bc7 texture");
        var source = new string('A', 40);
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/a.tex", "chara/b.tex", "chara/c.tex"], source, null));
        pair.Redirects[source] = alt.Hash;
        pair.Modded[("chara/b.tex", alt.Hash)] = alt.Path;
        pair.Excluded.Add(("chara/a.tex", alt.Hash));
        pair.Excluded.Add(("chara/c.tex", source));

        var resolution = ps.Resolve(1);
        var r = resolution.State.Resources.Single();

        Assert.Equal(ResourceAvailability.Available, r.Availability);
        Assert.True(r.IsCompressedAlternate);
        Assert.Equal(source, r.Hash);
        Assert.Equal(alt.Hash, r.CacheHash);
        Assert.Equal(["chara/b.tex"], r.GamePaths);
        Assert.Equal(["chara/a.tex", "chara/c.tex"], r.ExcludedGamePaths);
        Assert.True(resolution.Files.ContainsKey(alt.Hash));
        Assert.False(resolution.Files.ContainsKey(source));
    }

    [Fact]
    public void LookupIgnoresCaseOfHashAndPath()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var tex = ps.AddCacheFile("tex", "texture");
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/A.tex"], tex.Hash, null));
        pair.Modded[("chara/a.tex", tex.Hash.ToLowerInvariant())] = tex.Path;

        Assert.Equal(ResourceAvailability.Available, ps.Resolve(1).State.Resources.Single().Availability);
    }

    [Fact]
    public void SwapsRemovedByValidationAreNotReportedAsApplied()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", ["chara/a.tex"], string.Empty, "bg/ex5/missing.tex"));
        pair.Excluded.Add(("chara/a.tex", null));
        pair.Applied.Add(new ReplacementInput("Player", ["chara/b.tex", "chara/c.tex"], string.Empty, "chara/target.tex"));
        pair.Modded[("chara/c.tex", null)] = "chara/target.tex";

        var resources = ps.Resolve(1).State.Resources;

        Assert.Equal(ResourceAvailability.ExcludedByValidation, resources[0].Availability);
        Assert.Equal(ResourceAvailability.FileSwap, resources[1].Availability);
        Assert.Equal(["chara/c.tex"], resources[1].GamePaths);
    }

    [Fact]
    public void ResourceWithoutGamePathsIsMissing()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var pair = ps.AddPair(1, "A B");
        pair.Applied.Add(new ReplacementInput("Player", [], new string('B', 40), null));

        var r = ps.Resolve(1).State.Resources.Single();

        Assert.Equal(ResourceAvailability.Missing, r.Availability);
        Assert.Empty(r.GamePaths);
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
