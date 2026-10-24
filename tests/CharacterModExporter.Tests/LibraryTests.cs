using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CharacterModExporter.Core;
using CharacterModExporter.Core.Library;
using K4os.Compression.LZ4.Legacy;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

/// <summary>Writes test packages in the layout Penumbra uses (meta.json, default_mod.json, group_XXX_name.json, files).</summary>
internal static class TestPmp
{
    public static string Sha1(string content) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    public static string Write(string path, string name, object? defaultMod, IEnumerable<(string Entry, object Group)>? groups,
        IReadOnlyDictionary<string, string> files, object? extraMeta = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "meta.json", JsonSerializer.Serialize(new { FileVersion = 3, Name = name, Author = "Tester", Version = "1.0", ModTags = new[] { "tag" } }));
        if (defaultMod != null) Add(zip, "default_mod.json", JsonSerializer.Serialize(defaultMod));
        foreach (var (entry, group) in groups ?? []) Add(zip, entry, JsonSerializer.Serialize(group));
        foreach (var (entry, content) in files) Add(zip, entry, content);
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(Encoding.UTF8.GetBytes(content));
    }

    public static CharacterResource Res(string gamePath, string content, bool sourceKnown = true)
        => new([gamePath], new ContentHash(HashAlgorithmKind.Sha1, Sha1(content)), sourceKnown, null, ResourceAvailability.Available);
}

public class PmpReaderTests
{
    [Fact]
    public void ReadsDefaultSingleMultiCombiningAndImcGroups()
    {
        using var dir = new TempDir();
        var path = TestPmp.Write(Path.Combine(dir.Path, "0042 - Test Outfit.pmp"), "Test Outfit",
            new { Version = 0, Files = new Dictionary<string, string> { ["chara/equipment/e0001/model/c0201e0001_top.mdl"] = "files\\top.mdl" },
                FileSwaps = new Dictionary<string, string> { ["chara/a.tex"] = "chara/b.tex" }, Manipulations = Array.Empty<object>() },
            [
                ("group_001_colour.json", new { Name = "Colour", Type = "Single", Priority = 2, DefaultSettings = 0,
                    Options = new object[]
                    {
                        new { Name = "Red", Files = new Dictionary<string, string> { ["chara/equipment/e0001/texture/v01_c0201e0001_top_d.tex"] = "files/red.tex" } },
                        new { Name = "Blue", Files = new Dictionary<string, string> { ["chara/equipment/e0001/texture/v01_c0201e0001_top_d.tex"] = "files\\blue.tex" } },
                    } }),
                ("group_002_extras.json", new { Name = "Extras", Type = "Multi", Options = new object[]
                    { new { Name = "Glow", Priority = 5, Files = new Dictionary<string, string> { ["vfx/common/eff/glow.avfx"] = "files\\glow.avfx" } } } }),
                ("group_003_combo.json", new { Name = "Combo", Type = "Combining", Options = new object[] { new { Name = "A" } },
                    Containers = new object[] { new { Files = new Dictionary<string, string>() }, new { Name = "With A", Files = new Dictionary<string, string> { ["chara/c.tex"] = "files\\c.tex" } } } }),
                ("group_004_imc.json", new { Name = "Attributes", Type = "Imc", Options = new object[] { new { Name = "Off", IsDisableSubMod = true } } }),
            ],
            new Dictionary<string, string>
            {
                ["files/top.mdl"] = "model", ["files/red.tex"] = "red", ["files/blue.tex"] = "blue", ["files/glow.avfx"] = "glow",
                ["files/c.tex"] = "c", ["preview.png"] = "img",
            });

        var p = PmpReader.Read(path, "0042 - Test Outfit.pmp");

        Assert.Null(p.Error);
        Assert.Equal("0042", p.NumberText);
        Assert.Equal(42, p.Number);
        Assert.Equal("Test Outfit", p.Name);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), p.Sha256);
        Assert.Equal(["Colour", "Extras", "Combo", "Attributes"], p.Groups.Select(g => g.Name));
        Assert.Equal(["Default", "Colour / Red", "Colour / Blue", "Extras / Glow", "Combo / #0", "Combo / With A"], p.Containers.Select(c => c.Label));
        Assert.Equal(5, p.Containers[3].Priority);
        Assert.Equal(5, p.Payloads.Count);
        Assert.All(p.Payloads, x => Assert.NotNull(x.Sha1));
        Assert.Equal(TestPmp.Sha1("blue"), p.Payloads.Single(x => x.RelativePath == "files\\blue.tex").Sha1);
        Assert.Single(p.Swaps);
        Assert.Equal(1, p.UnreferencedEntries);
        Assert.Empty(p.Warnings);
    }

    [Theory]
    [InlineData("0042 - Outfit.pmp", "0042")]
    [InlineData("12_Hair.pmp", "12")]
    [InlineData("[7] Thing.pmp", "7")]
    [InlineData("2B Outfit.pmp", null)]
    [InlineData("Outfit 12.pmp", null)]
    [InlineData("123.pmp", "123")]
    public void ParsesOnlyALeadingNumber(string name, string? expected) => Assert.Equal(expected, PmpReader.ParseNumber(name));

    [Fact]
    public void BrokenArchivesAndMissingFilesAreReportedNotThrown()
    {
        using var dir = new TempDir();
        var bad = Path.Combine(dir.Path, "bad.pmp");
        File.WriteAllText(bad, "not a zip");
        Assert.NotNull(PmpReader.Read(bad, "bad.pmp").Error);

        var path = TestPmp.Write(Path.Combine(dir.Path, "m.pmp"), "M",
            new { Files = new Dictionary<string, string> { ["chara/a.tex"] = "files\\missing.tex", ["../evil"] = "x", ["chara/b.tex"] = "..\\..\\x.tex" } }, null,
            new Dictionary<string, string>());
        var p = PmpReader.Read(path, "m.pmp");
        Assert.Null(p.Error);
        Assert.Single(p.Payloads);
        Assert.Null(p.Payloads[0].Sha1);
        Assert.Contains(p.Warnings, w => w.Contains("not in the archive"));
        Assert.Equal(2, p.Warnings.Count(w => w.Contains("invalid redirection")));
    }

    [Fact]
    public void ReadingNeverChangesThePackage()
    {
        using var dir = new TempDir();
        var path = TestPmp.Write(Path.Combine(dir.Path, "m.pmp"), "M", new { Files = new Dictionary<string, string> { ["chara/a.tex"] = "files\\a.tex" } }, null,
            new Dictionary<string, string> { ["files/a.tex"] = "a" });
        var before = File.ReadAllBytes(path);
        var time = File.GetLastWriteTimeUtc(path);
        PmpReader.Read(path, "m.pmp");
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
    }
}

public class LibraryIndexTests
{
    [Fact]
    public void ReusesUnchangedPackagesAndRereadsChangedOnes()
    {
        using var dir = new TempDir();
        var lib = dir.Sub("library");
        TestPmp.Write(Path.Combine(lib, "001 A.pmp"), "A", new { Files = new Dictionary<string, string> { ["chara/a.tex"] = "files\\a.tex" } }, null,
            new Dictionary<string, string> { ["files/a.tex"] = "a" });
        var bPath = TestPmp.Write(Path.Combine(lib, "sub", "002 B.pmp"), "B", new { Files = new Dictionary<string, string>() }, null, new Dictionary<string, string>());

        var first = PmpLibraryIndexer.Refresh(lib, null);
        Assert.Equal(2, first.Read);
        Assert.Equal(["001 A.pmp", "sub/002 B.pmp"], first.Index.Packages.Select(p => p.RelativePath));

        var indexPath = Path.Combine(dir.Sub("config"), "library-index.json");
        PmpLibraryIndexer.Save(indexPath, first.Index);
        var loaded = PmpLibraryIndexer.Load(indexPath);
        Assert.NotNull(loaded);

        File.Delete(bPath);
        TestPmp.Write(Path.Combine(lib, "003 C.pmp"), "C", new { Files = new Dictionary<string, string>() }, null, new Dictionary<string, string>());
        var second = PmpLibraryIndexer.Refresh(lib, loaded);
        Assert.Equal(1, second.Reused);
        Assert.Equal(1, second.Read);
        Assert.Equal(1, second.Removed);
    }

    [Fact]
    public void TheIndexIsNeverWrittenIntoTheLibrary()
    {
        using var dir = new TempDir();
        var lib = dir.Sub("library");
        var result = PmpLibraryIndexer.Refresh(lib, null);
        Assert.Throws<InvalidOperationException>(() => PmpLibraryIndexer.Save(Path.Combine(lib, "index.json"), result.Index));
    }
}

public class PackageMatcherTests
{
    private static PmpPackage Package(string relative, params (int Container, string GamePath, string Content)[] payloads)
    {
        var p = new PmpPackage { RelativePath = relative, FileName = Path.GetFileName(relative), Sha256 = relative };
        p.Containers.Add(new PmpContainer());
        foreach (var c in payloads.Select(x => x.Container).Where(c => c > 0).Distinct().Order())
            p.Containers.Add(new PmpContainer { Group = "Group", Option = "Option " + c });
        foreach (var (container, gp, content) in payloads)
            p.Payloads.Add(new PmpPayload { Container = container, GamePath = gp, RelativePath = gp, Sha1 = TestPmp.Sha1(content) });
        return p;
    }

    [Fact]
    public void EveryFileSeenWithSameContentIsExact()
    {
        var pkg = Package("001 A.pmp", (0, "chara/a.mdl", "a"), (0, "chara/a.tex", "t"));
        var report = PackageMatcher.Match([TestPmp.Res("chara/a.mdl", "a"), TestPmp.Res("chara/a.tex", "t")], [pkg]);

        var m = Assert.Single(report.Packages);
        Assert.Equal(MatchStatus.Exact, m.Status);
        Assert.All(report.Resources, r => Assert.Equal(MatchStatus.Exact, r.Status));
    }

    [Fact]
    public void FilesNotSeenOnTheCharacterMakeItStrongNotExact()
    {
        var pkg = Package("001 A.pmp", (0, "chara/a.mdl", "a"), (0, "chara/c0801.mdl", "other race"));
        var m = Assert.Single(PackageMatcher.Match([TestPmp.Res("chara/a.mdl", "a")], [pkg]).Packages);
        Assert.Equal(MatchStatus.Strong, m.Status);
        Assert.Equal(1, m.UnseenCount);
    }

    [Fact]
    public void DifferentContentAtAPathOfTheUsedOptionIsPartial()
    {
        var pkg = Package("001 A.pmp", (0, "chara/a.mdl", "a"), (0, "chara/a.tex", "t"));
        var m = Assert.Single(PackageMatcher.Match([TestPmp.Res("chara/a.mdl", "a"), TestPmp.Res("chara/a.tex", "edited")], [pkg]).Packages);
        Assert.Equal(MatchStatus.Partial, m.Status);
        Assert.Equal(["chara/a.tex"], m.ConflictPaths);
    }

    [Fact]
    public void OnlyTheUsedOptionCounts()
    {
        var pkg = Package("001 A.pmp", (1, "chara/a.tex", "red"), (2, "chara/a.tex", "blue"));
        var m = Assert.Single(PackageMatcher.Match([TestPmp.Res("chara/a.tex", "blue")], [pkg]).Packages);
        Assert.Equal(MatchStatus.Exact, m.Status);
        Assert.Equal(["Group / Option 2"], m.ActiveOptions);
    }

    [Fact]
    public void SameFilesInTwoPackagesAreAmbiguous()
    {
        var a = Package("001 A.pmp", (0, "chara/a.tex", "t"));
        var b = Package("002 B.pmp", (0, "chara/a.tex", "t"), (0, "chara/b.tex", "u"));
        var report = PackageMatcher.Match([TestPmp.Res("chara/a.tex", "t")], [a, b]);
        Assert.All(report.Packages, m => Assert.Equal(MatchStatus.Ambiguous, m.Status));
        Assert.Equal(MatchStatus.Ambiguous, report.Resources.Single().Status);
        Assert.Equal(["001 A.pmp", "002 B.pmp"], report.Resources.Single().Packages);
    }

    [Fact]
    public void AUniqueFileMakesOnePackageWinOverOneThatSharesFiles()
    {
        var a = Package("001 A.pmp", (0, "chara/a.tex", "t"));
        var b = Package("002 B.pmp", (0, "chara/a.tex", "t"), (0, "chara/b.tex", "u"));
        var report = PackageMatcher.Match([TestPmp.Res("chara/a.tex", "t"), TestPmp.Res("chara/b.tex", "u")], [a, b]);
        Assert.Equal(MatchStatus.Exact, report.Packages.Single(m => m.Package.RelativePath == "002 B.pmp").Status);
        Assert.Equal(MatchStatus.Ambiguous, report.Packages.Single(m => m.Package.RelativePath == "001 A.pmp").Status);
    }

    [Fact]
    public void AMaybeCompressedTextureIsNeitherProofNorConflict()
    {
        var pkg = Package("001 A.pmp", (0, "chara/a.mdl", "a"), (0, "chara/a.tex", "original"));
        var report = PackageMatcher.Match([TestPmp.Res("chara/a.mdl", "a"), TestPmp.Res("chara/a.tex", "bc7 copy", sourceKnown: false)], [pkg]);
        var m = report.Packages.Single();
        Assert.Equal(MatchStatus.Strong, m.Status);
        Assert.Equal(["chara/a.tex"], m.CompressedUnverifiedPaths);
        Assert.Equal(MatchStatus.Unresolvable, report.Resources.Single(r => r.Resource.GamePaths[0] == "chara/a.tex").Status);
    }

    [Fact]
    public void HashesOfDifferentAlgorithmsAreNeverCompared()
    {
        var pkg = Package("001 A.pmp", (0, "chara/a.tex", "t"));
        var sha256 = new ContentHash(HashAlgorithmKind.Sha256, TestPmp.Sha1("t"));
        var report = PackageMatcher.Match([new CharacterResource(["chara/a.tex"], sha256, true, null, ResourceAvailability.Available)], [pkg]);
        Assert.Empty(report.Packages);
    }

    [Fact]
    public void UnknownFilesAreUnmatchedAndMissingOnesUnresolvable()
    {
        var report = PackageMatcher.Match(
        [
            TestPmp.Res("chara/x.tex", "x"),
            new CharacterResource(["chara/y.tex"], null, true, null, ResourceAvailability.Missing),
        ], []);
        Assert.Equal(MatchStatus.Unmatched, report.Resources[0].Status);
        Assert.Equal(MatchStatus.Unresolvable, report.Resources[1].Status);
    }

    [Fact]
    public void SwapsCountAsEvidence()
    {
        var pkg = Package("001 A.pmp");
        pkg.Swaps.Add(new PmpSwap { Container = 0, GamePath = "chara/a.tex", Target = "chara/b.tex" });
        var report = PackageMatcher.Match([new CharacterResource(["chara/a.tex"], null, true, "chara/b.tex", ResourceAvailability.FileSwap)], [pkg]);
        Assert.Equal(MatchStatus.Exact, report.Packages.Single().Status);
        Assert.Equal(MatchStatus.Exact, report.Resources.Single().Status);
    }

    [Fact]
    public void UserConfirmationKeepsTheComputedStatus()
    {
        var pkg = Package("001 A.pmp", (0, "chara/a.mdl", "a"), (0, "chara/b.mdl", "b"));
        var m = PackageMatcher.Match([TestPmp.Res("chara/a.mdl", "a")], [pkg], new HashSet<string> { "001 A.pmp" }).Packages.Single();
        Assert.Equal(MatchStatus.UserConfirmed, m.Status);
        Assert.Equal(MatchStatus.Strong, m.ComputedStatus);
    }

    [Fact]
    public void FromResourceDtoUsesTheOriginalHashOfACompressedAlternate()
    {
        var dto = new ResourceDto
        {
            GamePaths = ["chara/A.tex"], Hash = TestPmp.Sha1("original"), CacheHash = TestPmp.Sha1("compressed"), IsCompressedAlternate = true,
            Availability = ResourceAvailability.Available,
        };
        var r = CharacterResource.From(dto, sourceHashKnown: true);
        var pkg = Package("001 A.pmp", (0, "chara/a.tex", "original"));
        Assert.Equal(MatchStatus.Exact, PackageMatcher.Match([r], [pkg]).Packages.Single().Status);
    }
}

public class OriginalPackageExportTests
{
    [Fact]
    public async Task CopiesTheOriginalUnchangedAndVerified()
    {
        using var dir = new TempDir();
        var lib = dir.Sub("library");
        var src = TestPmp.Write(Path.Combine(lib, "outfits", "0042 Outfit.pmp"), "Outfit", new { Files = new Dictionary<string, string>() }, null,
            new Dictionary<string, string>());
        var pkg = PmpReader.Read(src, "outfits/0042 Outfit.pmp");
        var session = dir.Sub("export", "session");
        var match = new PackageMatch { Package = pkg, Status = MatchStatus.Exact, ComputedStatus = MatchStatus.Exact };

        var results = await OriginalPackageExporter.CopyAsync(lib, session, [match], _ => ("Gear/Body", false), null, CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal("Copied", r.Outcome);
        Assert.Equal("Original packages/outfits/0042 Outfit.pmp", r.ExportedFile);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(session, r.ExportedFile!)));
        Assert.Equal(pkg.Sha256, r.CopySha256);

        // Exporting again finds the identical copy and writes nothing new.
        var again = await OriginalPackageExporter.CopyAsync(lib, session, [match], null, null, CancellationToken.None);
        Assert.Equal("AlreadyPresentIdentical", again[0].Outcome);
    }

    [Fact]
    public async Task ADifferentFileAtTheDestinationIsNeverOverwritten()
    {
        using var dir = new TempDir();
        var lib = dir.Sub("library");
        var src = TestPmp.Write(Path.Combine(lib, "1 A.pmp"), "A", new { Files = new Dictionary<string, string>() }, null, new Dictionary<string, string>());
        var pkg = PmpReader.Read(src, "1 A.pmp");
        var session = dir.Sub("export");
        var existing = Path.Combine(dir.Sub("export", "Original packages"), "1 A.pmp");
        File.WriteAllText(existing, "keep me");

        var r = (await OriginalPackageExporter.CopyAsync(lib, session, [new PackageMatch { Package = pkg }], null, null, CancellationToken.None)).Single();

        Assert.Equal("DestinationConflict", r.Outcome);
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Null(r.ExportedFile);
    }

    [Fact]
    public async Task APackageChangedSinceIndexingIsNotExported()
    {
        using var dir = new TempDir();
        var lib = dir.Sub("library");
        var src = TestPmp.Write(Path.Combine(lib, "1 A.pmp"), "A", new { Files = new Dictionary<string, string>() }, null, new Dictionary<string, string>());
        var pkg = PmpReader.Read(src, "1 A.pmp");
        File.AppendAllText(src, "changed");
        var session = dir.Sub("export");

        var r = (await OriginalPackageExporter.CopyAsync(lib, session, [new PackageMatch { Package = pkg }], null, null, CancellationToken.None)).Single();

        Assert.Equal("IntegrityMismatch", r.Outcome);
        Assert.False(File.Exists(Path.Combine(session, "Original packages", "1 A.pmp")));
    }

    [Fact]
    public async Task TheExportMayNotBeInsideTheLibrary()
    {
        using var dir = new TempDir();
        var lib = dir.Sub("library");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OriginalPackageExporter.CopyAsync(lib, Path.Combine(lib, "out"), [], null, null, CancellationToken.None));
    }
}

public class PackageCategoryTests
{
    private static PmpPackage WithPaths(params string[] paths)
    {
        var p = new PmpPackage { Sha256 = "ABC", RelativePath = "x.pmp" };
        p.Payloads.AddRange(paths.Select(gp => new PmpPayload { GamePath = gp }));
        return p;
    }

    [Fact]
    public void CategoryComesFromTheGamePaths()
    {
        var c = PackageCategorizer.Categorize(WithPaths(
            "chara/equipment/e6001/model/c0201e6001_top.mdl",
            "chara/equipment/e6001/material/v0001/mt_c0201e6001_top_a.mtrl"));
        Assert.Equal("Gear/Body", c.Category);
    }

    [Fact]
    public void SeveralCategoriesWithoutAClearMajorityAreMixed()
    {
        var c = PackageCategorizer.Categorize(WithPaths(
            "chara/equipment/e6001/model/c0201e6001_top.mdl",
            "chara/human/c0201/obj/hair/h0101/model/c0201h0101_hir.mdl"));
        Assert.Equal(PackageCategorizer.Mixed, c.Category);
    }

    [Fact]
    public void NothingKnownIsUncategorized()
        => Assert.Equal(PackageCategorizer.Uncategorized, PackageCategorizer.Categorize(WithPaths("shader/sm5/shpk/skin.shpk")).Category);

    [Fact]
    public void OverridesAreStoredApartFromThePackagesAndSurviveARename()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "overrides.json");
        var store = new PackageOverrideStore();
        var pkg = WithPaths("chara/equipment/e6001/model/c0201e6001_top.mdl");
        store.SetCategory(pkg, "My outfits");
        store.SetConfirmed("Alpha Beta@World", pkg, true);
        store.Save(path);

        var renamed = WithPaths("chara/equipment/e6001/model/c0201e6001_top.mdl");
        renamed.RelativePath = "renamed.pmp";
        var loaded = PackageOverrideStore.Load(path);
        Assert.Equal(("My outfits", true), (loaded.CategoryOf(renamed).Category, loaded.CategoryOf(renamed).FromUser));
        Assert.Contains("renamed.pmp", loaded.ConfirmedPaths("Alpha Beta@World", [renamed]));
    }

    [Fact]
    public void AnUnreadableOverrideFileIsBackedUpBeforeBeingReplaced()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "overrides.json");
        File.WriteAllText(path, "{ broken");
        var store = PackageOverrideStore.Load(path);
        Assert.NotNull(store.LoadError);
        store.Save(path);
        Assert.Single(Directory.GetFiles(dir.Path, "overrides.json.unreadable-*.bak"));
    }
}

public class McdfReaderTests
{
    private static string WriteMcdf(string path, IReadOnlyList<(string[] Paths, string Content)> files, bool corruptSecond = false)
    {
        var data = new
        {
            Description = "Saved look",
            GlamourerData = "glam",
            CustomizePlusData = "",
            ManipulationData = "manip",
            Files = files.Select(f => new { GamePaths = f.Paths, Length = Encoding.UTF8.GetByteCount(f.Content), Hash = TestPmp.Sha1(f.Content) }).ToList(),
            FileSwaps = new[] { new { GamePaths = new[] { "chara/s.tex" }, FileSwapPath = "chara/t.tex" } },
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        using var fs = File.Create(path);
        using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression);
        using var w = new BinaryWriter(lz4);
        w.Write('M'); w.Write('C'); w.Write('D'); w.Write('F');
        w.Write((byte)1);
        w.Write(json.Length);
        w.Write(json);
        for (var i = 0; i < files.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(files[i].Content);
            if (corruptSecond && i == 1) bytes[0] ^= 0xFF;
            w.Write(bytes);
        }

        return path;
    }

    [Fact]
    public void ReadsAndVerifiesASnapshotWithoutApplyingIt()
    {
        using var dir = new TempDir();
        var path = WriteMcdf(Path.Combine(dir.Path, "look.mcdf"), [(["chara/a.mdl"], "model"), (["chara/a.tex", "chara/b.tex"], "texture")]);
        var before = File.ReadAllBytes(path);

        var snap = McdfReader.Read(path);

        Assert.Equal("Saved look", snap.Description);
        Assert.True(snap.HasGlamourerData);
        Assert.False(snap.HasCustomizePlusData);
        Assert.True(snap.HasManipulationData);
        Assert.Equal(2, snap.Files.Count);
        Assert.True(snap.AllVerified);
        Assert.Single(snap.FileSwaps);
        Assert.Equal(before, File.ReadAllBytes(path));

        var resources = McdfReader.ToResources(snap);
        Assert.Equal(3, resources.Count);
        Assert.Equal("chara/t.tex", resources[2].FileSwapTarget);
    }

    [Fact]
    public void AFileWhoseBytesDoNotMatchItsHashIsNotEvidence()
    {
        using var dir = new TempDir();
        var path = WriteMcdf(Path.Combine(dir.Path, "look.mcdf"), [(["chara/a.mdl"], "model"), (["chara/a.tex"], "texture")], corruptSecond: true);
        var snap = McdfReader.Read(path);
        Assert.False(snap.AllVerified);
        Assert.Null(McdfReader.ToResources(snap)[1].SourceHash);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "x.mcdf");
        using (var fs = File.Create(path))
        using (var lz4 = new LZ4Stream(fs, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression))
            lz4.Write("NOPE0000000"u8);
        Assert.Throws<InvalidDataException>(() => McdfReader.Read(path));
    }
}

public class OriginalPackageSessionTests
{
    [Fact]
    public async Task MatchesTheLiveCharacterAndCopiesTheOriginalWithoutTouchingTheLibrary()
    {
        using var dir = new TempDir();
        using var ps = new FakePlayerSync(dir);
        var top = ps.AddCacheFile("mdl", "top model");
        var tex = ps.AddCacheFile("tex", "top texture");
        var other = ps.AddCacheFile("tex", "something else");
        var pair = ps.AddPair(42, "Alpha Beta");
        pair.AddResolved(top, "Player", "chara/equipment/e6001/model/c0201e6001_top.mdl");
        pair.AddResolved(tex, "Player", "chara/equipment/e6001/texture/v01_c0201e6001_top_d.tex");
        pair.AddResolved(other, "Player", "chara/equipment/e0009/texture/v01_c0201e0009_glv_d.tex");

        var lib = dir.Sub("library");
        TestPmp.Write(Path.Combine(lib, "0042 Top.pmp"), "Top",
            new { Files = new Dictionary<string, string>
            {
                ["chara/equipment/e6001/model/c0201e6001_top.mdl"] = "files\\top.mdl",
                ["chara/equipment/e6001/texture/v01_c0201e6001_top_d.tex"] = "files\\top.tex",
            } }, null,
            new Dictionary<string, string> { ["files/top.mdl"] = "top model", ["files/top.tex"] = "top texture" });
        TestPmp.Write(Path.Combine(lib, "0043 Unrelated.pmp"), "Unrelated",
            new { Files = new Dictionary<string, string> { ["chara/equipment/e0100/model/c0201e0100_dwn.mdl"] = "files\\x.mdl" } }, null,
            new Dictionary<string, string> { ["files/x.mdl"] = "x" });
        var libraryBefore = Snapshot.Of(lib);

        var library = new PackageLibrary(dir.Sub("config"));
        await library.RefreshAsync(lib);
        Assert.Null(library.LastError);
        Assert.Equal(2, library.Index!.Packages.Count);

        var parent = dir.Sub("parent");
        var folders = new ExportFolderService(() => [ps.CacheFolder, ps.PenumbraFolder]);
        var coordinator = new ExportCoordinator(ps, folders, "0.1.0.0", (_, _) => Task.Delay(5));
        using var session = new ExportSession(new ExportTarget(42, "Alpha Beta", "Twintania"), ps, folders, coordinator,
            () => Settings.For(parent), _ => Task.FromResult(true), library: library, exporterVersion: "0.1.0.0");

        await session.RefreshAsync();
        await session.MatchLibraryAsync();
        Assert.Null(session.LastError);
        var match = Assert.Single(session.LiveMatch!.Packages);
        Assert.Equal("0042 Top.pmp", match.Package.RelativePath);
        Assert.Equal(MatchStatus.Exact, match.Status);
        Assert.Equal(MatchStatus.Unmatched, session.LiveMatch.Resources.Single(r => r.Resource.GamePaths[0].Contains("e0009")).Status);

        await session.ExportOriginalsAsync([match], fromSnapshot: false);
        Assert.Null(session.LastError);
        var copy = Assert.Single(session.LastOriginalExport!);
        Assert.Equal("Copied", copy.Outcome);
        Assert.Equal("Gear/Body", copy.Category);
        var copied = Path.Combine(session.LastOriginalExportFolder!, "0042 Top.pmp");
        Assert.Equal(File.ReadAllBytes(Path.Combine(lib, "0042 Top.pmp")), File.ReadAllBytes(copied));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.LastOriginalExportFolder!, OriginalPackagesManifest.FileName))).RootElement;
        Assert.Equal(1, manifest.GetProperty("ManifestVersion").GetInt32());
        Assert.Equal("Live", manifest.GetProperty("Evidence").GetString());
        Assert.Equal(1, manifest.GetProperty("NotFoundInLibrary").GetArrayLength());
        Assert.Equal(libraryBefore, Snapshot.Of(lib));
    }

    [Fact]
    public void StockSourceCacheTexturesMayBeCompressedCopies()
    {
        var state = new CharacterStateDto { LinkDetailsKnown = false };
        var cacheTex = new ResourceDto { GamePaths = ["chara/a.tex"], SourceKind = SourceKind.PlayerSyncCache };
        var modTex = new ResourceDto { GamePaths = ["chara/a.tex"], SourceKind = SourceKind.PenumbraModDirectory };
        var cacheMdl = new ResourceDto { GamePaths = ["chara/a.mdl"], SourceKind = SourceKind.PlayerSyncCache };
        Assert.False(PackageLibrary.SourceHashKnown(state, cacheTex));
        Assert.True(PackageLibrary.SourceHashKnown(state, modTex));
        Assert.True(PackageLibrary.SourceHashKnown(state, cacheMdl));
        Assert.True(PackageLibrary.SourceHashKnown(new CharacterStateDto { LinkDetailsKnown = true }, cacheTex));
    }
}
