using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CharacterModExporter.Core;

namespace CharacterModExporter.Tests;

public class ItemModGrouperTests
{
    [Theory]
    [InlineData("chara/equipment/e0123/model/c0201e0123_top.mdl", "equipment:e0123:top", "Body")]
    [InlineData("chara/equipment/e0123/texture/v01_c0201e0123_glv_n.tex", "equipment:e0123:glv", "Hands")]
    [InlineData("chara/equipment/e0123/material/v0002/mt_c0201e0123_sho_a.mtrl", "equipment:e0123:sho", "Feet")]
    [InlineData("chara/accessory/a0042/model/c0201a0042_rir.mdl", "accessory:a0042:rir", "Ring (right)")]
    [InlineData("chara/weapon/w0201/obj/body/b0010/model/w0201b0010.mdl", "weapon:w0201b0010", "Weapon")]
    [InlineData("chara/human/c0801/obj/hair/h0105/model/c0801h0105_hir.mdl", "human:hair:h0105:c0801", "Hair")]
    [InlineData("chara/human/c0801/obj/body/b0001/texture/--c0801b0001_b_d.tex", "human:body:b0001:c0801", "Body mod")]
    [InlineData("chara/human/c0801/animation/a0001/bt_common/emote/dance.pap", "shared:emote:dance", "Emote")]
    [InlineData("chara/human/c0801/animation/a0001/bt_common/emote/s_pose01_loop.pap", "shared:emote:pose01", "Emote")]
    [InlineData("chara/human/c0801/animation/a0001/bt_common/resident/idle.pap", "shared:anim:idle", "Idle and movement")]
    [InlineData("chara/action/ability/x.tmb", "shared:anim:battle", "Battle animations")]
    [InlineData("chara/human/c0801/skeleton/hair/h0105/phy_c0801h0105.phyb", "human:hair:h0105:c0801", "Hair")]
    [InlineData("chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb", "human:body:b0001:c0801", "Body mod")]
    [InlineData("chara/human/c0801/skeleton/top/t6188/phy_c0801t6188.phyb", "equipment:e6188:top", "Body")]
    [InlineData("chara/common/texture/decal_face/_decal_12.tex", "common:facepaint", "Face paint")]
    [InlineData("chara/common/texture/eye/eye01_base.tex", "common:eyes", "Eyes")]
    [InlineData("vfx/common/eff/x.avfx", "shared:vfx", "VFX")]
    [InlineData("sound/foot/x.scd", "shared:sounds", "Sounds")]
    [InlineData("chara/monster/m0123/obj/body/b0001/model/m0123b0001.mdl", "monster:m0123", "Minion or mount")]
    [InlineData("chara/common/texture/skin_m.tex", "common:skin", "Skin")]
    [InlineData("shader/sm5/shpk/skin.shpk", "shared:other", "Other files")]
    public void ClassifiesGamePaths(string path, string key, string category)
    {
        var item = ItemModGrouper.Classify(path);
        Assert.Equal(key, item.Key);
        Assert.Equal(category, item.Category);
    }

    [Fact]
    public void ReadsVariantAndRingFolder()
    {
        Assert.Equal(2, ItemModGrouper.Classify("chara/equipment/e0123/material/v0002/mt_c0201e0123_top_a.mtrl").Variant);
        Assert.Equal(1, ItemModGrouper.Classify("chara/equipment/e0123/texture/v01_c0201e0123_top_d.tex").Variant);
        Assert.Equal("Accessories/Rings", ItemModGrouper.Classify("chara/accessory/a0001/model/c0101a0001_ril.mdl").Folder);
        Assert.Equal("Gear/Body", ItemModGrouper.Classify("chara/equipment/e0001/model/c0101e0001_top.mdl").Folder);
        Assert.Equal("Character/Body", ItemModGrouper.Classify("chara/human/c0801/obj/body/b0001/model/c0801b0001_top.mdl").Folder);
        Assert.Equal("Animations/Emotes", ItemModGrouper.Classify("chara/human/c0801/animation/a0001/bt_common/emote/dance.pap").Folder);
    }

    [Fact]
    public void DetectsRacesGendersAndBodyType()
    {
        var support = ItemModGrouper.DetectSupport([
            "chara/equipment/e0123/model/c0201e0123_top.mdl",
            "chara/equipment/e0123/model/c0801e0123_top.mdl",
            "chara/bibo/texture/midlander_d.tex",
        ]);
        Assert.Equal(["Female Hyur Midlander", "Female Miqo'te"], support.Races);
        Assert.Equal("Female", support.Genders);
        Assert.Equal("Bibo+", support.BodyType);
        Assert.Equal("Female Hyur Midlander + Female Miqo'te, Bibo+", support.Short);
        Assert.Contains("Made for: Female Hyur Midlander, Female Miqo'te.", support.Describe());

        var mixed = ItemModGrouper.DetectSupport(["chara/equipment/e0001/model/c0101e0001_top.mdl", "chara/equipment/e0001/model/c0201e0001_top.mdl",
            "chara/equipment/e0001/model/c1401e0001_top.mdl"]);
        Assert.Equal("Male and female", mixed.Genders);
        Assert.Null(mixed.BodyType);
        Assert.Equal("Male and female", mixed.Short);
    }

    [Fact]
    public void SplitsFilesPerItemAndKeepsMaterialTexturesWithTheirItem()
    {
        using var dir = new TempDir();
        var files = dir.Sub("files");
        PackageFile Make(string name, params string[] gamePaths)
        {
            var path = Path.Combine(files, name);
            if (!File.Exists(path)) File.WriteAllText(path, name);
            return new PackageFile(path, name, gamePaths);
        }

        var customTexture = "chara/common/texture/custom_top_n.tex";
        var mtrlPath = Path.Combine(files, "m.mtrl");
        File.WriteAllBytes(mtrlPath, Material(customTexture, "chara/equipment/e0123/texture/v01_c0201e0123_top_d.tex"));
        var groups = ItemModGrouper.Group([
            Make("top.mdl", "chara/equipment/e0123/model/c0201e0123_top.mdl"),
            new PackageFile(mtrlPath, "m.mtrl", ["chara/equipment/e0123/material/v0001/mt_c0201e0123_top_a.mtrl"]),
            Make("d.tex", "chara/equipment/e0123/texture/v01_c0201e0123_top_d.tex"),
            Make("n.tex", customTexture),
            Make("hair.mdl", "chara/human/c0201/obj/hair/h0101/model/c0201h0101_hir.mdl"),
            Make("emote.pap", "chara/human/c0201/animation/a0001/bt_common/emote/x.pap"),
            Make("loose.tex", "chara/common/texture/unused.tex"),
            Make("skin.shpk", "shader/sm5/shpk/skin.shpk"),
        ], key => key.SetId == 123 && key.Slot == "top" ? "Test Coat" : null);

        var body = Assert.Single(groups, g => g.Item.Key == "equipment:e0123:top");
        Assert.Equal("Body - Test Coat (e0123)", body.Label);
        Assert.Equal(["d.tex", "m.mtrl", "n.tex", "top.mdl"], body.Files.Select(f => f.FileName));
        Assert.Contains(groups, g => g.Label == "Hair h0101 c0201");
        Assert.Contains(groups, g => g.Label == "Emote - x");
        var other = Assert.Single(groups, g => g.Item.Key == "shared:other");
        Assert.Equal(["loose.tex", "skin.shpk"], other.Files.Select(f => f.FileName));
        Assert.False(ItemModGrouper.IsMod(other));
        Assert.True(ItemModGrouper.IsMod(body));
    }

    [Fact]
    public void OnlyGroupsWithModContentAreMods()
    {
        ItemGroup G(string key, params string[] paths)
            => new(ItemModGrouper.Classify(paths[0]) with { Key = key }, key, [new PackageFile("x", "x", paths)], new ItemSupport([], null, null));
        Assert.True(ItemModGrouper.IsMod(G("human:hair:h0105:c0801", "chara/human/c0801/skeleton/hair/h0105/phy_c0801h0105.phyb")));
        Assert.False(ItemModGrouper.IsMod(G("shared:other", "shader/sm5/shpk/skin.shpk")));
        Assert.True(ItemModGrouper.IsMod(G("human:hair:h0105:c0801", "chara/human/c0801/obj/hair/h0105/model/c0801h0105_hir.mdl")));
        Assert.False(ItemModGrouper.IsMod(G("shared:vfx", "vfx/common/texture/x.tex")));
        Assert.True(ItemModGrouper.IsMod(G("shared:vfx", "vfx/common/eff/x.avfx")));
        Assert.True(ItemModGrouper.IsMod(G("shared:emote:dance", "chara/human/c0801/animation/a0001/bt_common/emote/dance.pap")));
        Assert.False(ItemModGrouper.IsMod(G("shared:sounds", "sound/x.sklb")));
    }

    [Fact]
    public void ReadsGamePathsNamedInsideFiles()
    {
        using var dir = new TempDir();
        var tmb = Path.Combine(dir.Path, "a.tmb");
        File.WriteAllBytes(tmb, [.. "TMLB\0\0"u8, .. "vfx/rdb/bld_ws_s25/eff/bld_ws25c2k1.avfx\0"u8, 0xff, .. "sound/vfx/weponskill/SE_Vfx_Wep_Rdb_ws25_c.scd\0"u8,
            .. "C063 not a path"u8, .. "\u0001vfx/rdb/bld_ws_s25/eff/bld_ws25c2k1.avfx\0"u8]);
        Assert.Equal(["vfx/rdb/bld_ws_s25/eff/bld_ws25c2k1.avfx", "sound/vfx/weponskill/se_vfx_wep_rdb_ws25_c.scd"], ItemModGrouper.ReadReferences(tmb));
        Assert.Empty(ItemModGrouper.ReadReferences(Path.Combine(dir.Path, "missing.tmb")));
    }

    [Fact]
    public void ModsKeepTheEffectsSoundsAndFilesTheyUse()
    {
        using var dir = new TempDir();
        PackageFile Make(string name, byte[] content, params string[] gamePaths)
        {
            var path = Path.Combine(dir.Path, name);
            File.WriteAllBytes(path, content);
            return new PackageFile(path, name, gamePaths);
        }

        static byte[] Text(params string[] refs) => System.Text.Encoding.ASCII.GetBytes("\0" + string.Join("\0", refs) + "\0");
        var groups = ItemModGrouper.Group([
            Make("emote.pap", Text("vfx/emote_sp/phone/eff/phone.avfx", "sound/vfx/voice/mhmm.scd"), "chara/human/c0701/animation/a0001/bt_common/emote/j_pose03_loop.pap"),
            Make("phone.avfx", Text("vfx/emote_sp/phone/texture/phone_d.atex"), "vfx/emote_sp/phone/eff/phone.avfx"),
            Make("phone.atex", [1], "vfx/emote_sp/phone/texture/phone_d.atex"),
            Make("mhmm.scd", [2], "sound/vfx/voice/mhmm.scd"),
            Make("other.avfx", Text(), "vfx/common/eff/unrelated.avfx"),
            Make("helm.mdl", [3], "chara/equipment/e0774/model/c0101e0774_met.mdl"),
            Make("helm.avfx", Text(), "chara/equipment/e0774/vfx/eff/ve0001.avfx"),
            Make("body.mtrl", ItemModGrouperTests.Material("chara/human/c0101/obj/body/b0001/texture/c0101b0001_d.tex"), "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl"),
            Make("human.pbd", [4], "chara/xls/bonedeformer/human.pbd"),
        ]);

        var emote = Assert.Single(groups, g => g.Item.Key == "shared:emote:pose03");
        Assert.Equal(["emote.pap", "mhmm.scd", "phone.atex", "phone.avfx"], emote.Files.Select(f => f.FileName));
        var vfx = Assert.Single(groups, g => g.Item.Key == "shared:vfx");
        Assert.Equal(["other.avfx"], vfx.Files.Select(f => f.FileName));
        Assert.DoesNotContain(groups, g => g.Item.Key == "shared:sounds");
        var helm = Assert.Single(groups, g => g.Item.Key == "equipment:e0774:met");
        Assert.Equal(["helm.avfx", "helm.mdl"], helm.Files.Select(f => f.FileName));
        Assert.DoesNotContain(groups, g => g.Item.Key.EndsWith(":any", StringComparison.Ordinal));
        var body = Assert.Single(groups, g => g.Item.Key == "human:body:b0001:c0101");
        Assert.Contains(body.Files, f => f.FileName == "human.pbd");
        Assert.DoesNotContain(groups, g => g.Item.Key == ItemModGrouper.DeformKey);
    }

    [Fact]
    public void UnreadableMaterialYieldsNoTextures()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "bad.mtrl");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Empty(ItemModGrouper.ReadMaterialTextures(path));
        Assert.Empty(ItemModGrouper.ReadMaterialTextures(Path.Combine(dir.Path, "missing.mtrl")));
    }

    /// <summary>A minimal material: header, texture offsets and the string table, as the game lays them out.</summary>
    public static byte[] Material(params string[] textures)
    {
        var strings = new MemoryStream();
        var offsets = new List<ushort>();
        foreach (var t in textures)
        {
            offsets.Add((ushort)strings.Length);
            var b = Encoding.UTF8.GetBytes(t);
            strings.Write(b);
            strings.WriteByte(0);
        }

        var shader = (ushort)strings.Length;
        strings.Write("characterlegacy.shpk\0"u8);
        var table = strings.ToArray();
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x01030000u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)table.Length);
        w.Write(shader);
        w.Write((byte)textures.Length);
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((byte)0);
        foreach (var o in offsets)
        {
            w.Write(o);
            w.Write((ushort)0);
        }

        w.Write(table);
        return ms.ToArray();
    }
}

public class ItemModExportTests
{
    [Fact]
    public async Task ExportWritesOneModPerItemInCategoryFoldersAndReusesIdenticalOnes()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var top = src.AddCacheFile("mdl", "coat model");
        var mtrl = Path.Combine(src.CacheFolder, "placeholder");
        var material = ItemModGrouperTests.Material("chara/equipment/e0123/texture/v01_c0201e0123_top_d.tex");
        var mtrlHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(material)).ToLowerInvariant();
        mtrl = Path.Combine(src.CacheFolder, mtrlHash + ".mtrl");
        File.WriteAllBytes(mtrl, material);
        var tex = src.AddCacheFile("tex", "coat diffuse");
        var hair = src.AddCacheFile("mdl", "hair model");
        var emote = src.AddCacheFile("pap", "dance");
        var shader = src.AddCacheFile("shpk", "shader");
        src.Tracker.Record("shader/sm5/shpk/skin.shpk", shader);
        src.Tracker.Record("chara/equipment/e0123/model/c0201e0123_top.mdl", top);
        src.Tracker.Record("chara/equipment/e0123/model/c0801e0123_top.mdl", top);
        src.Tracker.Record("chara/equipment/e0123/material/v0001/mt_c0201e0123_top_a.mtrl", mtrl);
        src.Tracker.Record("chara/equipment/e0123/texture/v01_c0201e0123_top_d.tex", tex);
        src.Tracker.Record("chara/human/c0801/obj/hair/h0105/model/c0801h0105_hir.mdl", hair);
        src.Tracker.Record("chara/human/c0801/animation/a0001/bt_common/emote/dance.pap", emote);
        src.Settle();

        var parent = dir.Sub("parent");
        var coordinator = new ExportCoordinator(src, new ExportFolderService(() => [src.CacheFolder]), "0.3.0.0", (_, _) => Task.Delay(5),
            itemNames: k => k.SetId == 123 ? "Test Coat" : null);
        var hashes = src.Resolve(1).Files.Keys.ToList();
        var outcome = await coordinator.ExportAsync(new ExportTarget(1, "Alpha Beta", "World"), Settings.For(parent), hashes, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
        var mods = outcome.Layout!.ModsFolder;
        var coat = Assert.Single(Directory.GetFiles(Path.Combine(mods, "Gear", "Body"), "*.pmp"));
        Assert.Equal("Body - Test Coat (e0123) (Female Hyur Midlander + Female Miqo'te) [Alpha Beta].pmp", Path.GetFileName(coat));
        Assert.Empty(PenumbraPackageBuilder.Validate(coat));
        using (var zip = ZipFile.OpenRead(coat))
        {
            var meta = JsonNode.Parse(zip.GetEntry("meta.json")!.Open())!;
            Assert.Contains("Made for: Female Hyur Midlander, Female Miqo'te.", meta["Description"]!.GetValue<string>());
            Assert.Contains("Female", meta["ModTags"]!.AsArray().Select(t => t!.GetValue<string>()));
            var redirects = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!["Files"]!.AsObject();
            Assert.Equal(4, redirects.Count);
        }

        Assert.Single(Directory.GetFiles(Path.Combine(mods, "Character", "Hair"), "*.pmp"));
        Assert.Single(Directory.GetFiles(Path.Combine(mods, "Animations", "Emotes"), "*.pmp"));
        Assert.Equal(3, outcome.Manifest!.InstallablePackages.Count(p => p.Item != null));
        Assert.False(Directory.Exists(Path.Combine(mods, "Other files")));
        Assert.Contains(outcome.Manifest.Notes, n => n.StartsWith("Not sorted into a mod category", StringComparison.Ordinal) && n.Contains("Other files (1 file(s))"));
        Assert.Contains("item mod(s), one per item", File.ReadAllText(outcome.Layout.ReadmePath));

        // Exporting the same character again keeps the identical item mods instead of adding copies.
        var again = await coordinator.ExportAsync(new ExportTarget(1, "Alpha Beta", "World"), Settings.For(parent), hashes, null, CancellationToken.None);
        Assert.True(again.Success, again.Message);
        Assert.Single(Directory.GetFiles(Path.Combine(mods, "Gear", "Body"), "*.pmp"));
    }

    [Fact]
    public async Task ItemWithUnexportedFilesGetsNoMod()
    {
        using var dir = new TempDir();
        using var src = new FakePenumbraSource(dir);
        var top = src.AddCacheFile("mdl", "coat model");
        var tex = src.AddCacheFile("tex", "coat diffuse");
        var hair = src.AddCacheFile("mdl", "hair model");
        src.Tracker.Record("chara/equipment/e0123/model/c0201e0123_top.mdl", top);
        src.Tracker.Record("chara/equipment/e0123/texture/v01_c0201e0123_top_d.tex", tex);
        src.Tracker.Record("chara/human/c0801/obj/hair/h0105/model/c0801h0105_hir.mdl", hair);
        src.Settle();
        var texHash = Path.GetFileNameWithoutExtension(tex).ToUpperInvariant();

        var parent = dir.Sub("parent");
        var coordinator = new ExportCoordinator(src, new ExportFolderService(() => [src.CacheFolder]), "0.3.0.0", (_, _) => Task.Delay(5));
        var hashes = src.Resolve(1).Files.Keys.Where(h => h != texHash).ToList();
        var outcome = await coordinator.ExportAsync(new ExportTarget(1, "Alpha Beta", null), Settings.For(parent), hashes, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(outcome.Layout!.ModsFolder, "Gear")));
        Assert.Single(Directory.GetFiles(Path.Combine(outcome.Layout.ModsFolder, "Character", "Hair"), "*.pmp"));
        Assert.Contains(outcome.Manifest!.Notes, n => n.StartsWith("No item mod was built for Body e0123", StringComparison.Ordinal));
    }
}
