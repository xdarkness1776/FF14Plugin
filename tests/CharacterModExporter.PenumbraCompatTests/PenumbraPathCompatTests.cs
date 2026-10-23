using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CharacterModExporter.Core;
using Penumbra.String;
using Penumbra.String.Classes;

namespace CharacterModExporter.PenumbraCompatTests;

public class PenumbraPathCompatTests
{
    // Paths in the style PlayerSync sends (taken from the shapes of real game paths; no real character data).
    private static readonly string[] SampleGamePaths =
    [
        "chara/equipment/e6200/texture/v01_c0201e6200_top_norm.tex",
        "chara/equipment/e6200/material/v0001/mt_c0201e6200_top_a.mtrl",
        "chara/human/c1401/obj/body/b0001/model/c1401b0001_top.mdl",
        "chara/human/c1401/animation/a0001/bt_common/emote/pose01_loop.pap",
        "chara/action/emote/pose01_loop.tmb",
        "vfx/common/eff/dk05ht_ht_c0g.avfx",
        "vfx/common/texture/eff_ht_glow01t.atex",
    ];

    private static unsafe bool PenumbraAcceptsGamePath(string path)
    {
        // Same call Penumbra's GroupDeserialization.LoadDataContainer makes for every key in "Files" and "FileSwaps".
        var bytes = Encoding.UTF8.GetBytes(path);
        return Utf8GamePath.FromSpan(bytes, MetaDataComputation.All, out _);
    }

    [Fact]
    public void NormalizedPathsAreAcceptedByPenumbra()
    {
        foreach (var path in SampleGamePaths)
        {
            var normalized = PenumbraPackageBuilder.NormalizeGamePath(path);
            Assert.NotNull(normalized);
            Assert.True(PenumbraAcceptsGamePath(normalized!), normalized);
        }
    }

    [Fact]
    public void GeneratedPackageParsesWithPenumbraPathRules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmx-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = SampleGamePaths.Select((gp, i) =>
            {
                var name = new string((char)('a' + i), 40) + Path.GetExtension(gp);
                var src = Path.Combine(dir, name);
                File.WriteAllText(src, gp);
                return new PackageFile(src, name, [gp]);
            }).ToList();

            var result = PenumbraPackageBuilder.Build(Path.Combine(dir, "Installable"), "Compat", "test", "1", files,
                new Dictionary<string, string> { ["chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex"] = "chara/equipment/e0002/texture/v01_c0101e0002_top_d.tex" });
            Assert.True(result.Created);

            using var zip = ZipFile.OpenRead(result.PackagePath!);
            var def = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!.AsObject();
            foreach (var (gamePath, rel) in def["Files"]!.AsObject())
            {
                Assert.True(PenumbraAcceptsGamePath(gamePath), gamePath);
                Assert.True(Utf8RelPath.FromString(rel!.GetValue<string>(), out var relPath));
                Assert.NotNull(zip.GetEntry(relPath.ToString().Replace('\\', '/')));
            }

            foreach (var (from, to) in def["FileSwaps"]!.AsObject())
            {
                Assert.True(PenumbraAcceptsGamePath(from), from);
                Assert.True(Utf8GamePath.FromString(to!.GetValue<string>(), out _));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
