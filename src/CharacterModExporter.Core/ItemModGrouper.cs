using System.Text;
using System.Text.RegularExpressions;

namespace CharacterModExporter.Core;

/// <summary>
/// What a game path belongs to: one gear piece, accessory, weapon, hair style, face, tail, ears, body, a minion or
/// mount model, or a shared category (animations, VFX, sounds, other).
/// </summary>
/// <param name="Key">Stable grouping key, e.g. "equipment:e0123:top".</param>
/// <param name="Category">Display category, e.g. "Body", "Hair", "Animations".</param>
/// <param name="Code">Game id shown when no item name is known, e.g. "e0123".</param>
/// <param name="SetId">Model set id (equipment, accessory, weapon), when known.</param>
/// <param name="Slot">Equipment slot suffix (met, top, glv, dwn, sho, ear, nek, wrs, rir, ril), when known.</param>
/// <param name="Variant">Model variant from the material or texture path, when known.</param>
/// <param name="WeaponBody">Weapon body id (b####), for weapons.</param>
public sealed record ItemKey(string Key, string Category, string Code, int? SetId = null, string? Slot = null, int? Variant = null, int? WeaponBody = null)
{
    public bool IsShared => Key.StartsWith("shared:", StringComparison.Ordinal);

    /// <summary>Category folder inside Mods (both ring slots share one folder).</summary>
    public string Folder => Slot is "rir" or "ril" ? "Rings" : Category;
}

/// <summary>The files of one item, ready to be packaged as its own Penumbra mod.</summary>
/// <param name="Support">Which races/genders and body type the item's files are for, when that can be told.</param>
public sealed record ItemGroup(ItemKey Item, string Label, List<PackageFile> Files, ItemSupport Support);

/// <summary>
/// Which bodies an item's files are made for. Races come from the race codes in the file paths (c0201 is a female
/// Midlander model, and so on); the body type is detected from texture paths used by the common body mods.
/// </summary>
public sealed record ItemSupport(IReadOnlyList<string> Races, string? Genders, string? BodyType)
{
    public bool IsKnown => Races.Count > 0 || BodyType != null;

    /// <summary>Short form for mod names, e.g. "Female Miqo'te, Bibo+" or "Female, Bibo+" when many races are included.</summary>
    public string Short => string.Join(", ", new[] { Races.Count is > 0 and <= 2 ? string.Join(" + ", Races) : Genders, BodyType }
        .Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Penumbra mod tags, so imported mods can be filtered by gender and body type.</summary>
    public List<string> Tags => new[] { Genders, BodyType }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

    /// <summary>Full form for descriptions.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Races.Count > 0) parts.Add("Made for: " + string.Join(", ", Races) + ".");
        parts.Add(BodyType != null ? $"Body type: {BodyType} (detected from texture paths)." : "Body type: not detected (vanilla body paths or not a body item).");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// PlayerSync only transfers a flat game path to file mapping; the names and boundaries of the original Penumbra
/// mods are not part of it. This splits the mapping back into one mod per game item, using the game's path layout
/// (and the texture references inside materials, so textures stored at shared or custom paths go with the item
/// whose material uses them).
/// </summary>
public static partial class ItemModGrouper
{
    private static readonly Dictionary<string, string> SlotNames = new(StringComparer.Ordinal)
    {
        ["met"] = "Head", ["top"] = "Body", ["glv"] = "Hands", ["dwn"] = "Legs", ["sho"] = "Feet",
        ["ear"] = "Earrings", ["nek"] = "Necklace", ["wrs"] = "Bracelets", ["rir"] = "Ring (right)", ["ril"] = "Ring (left)",
    };

    private static readonly Dictionary<string, string> HumanParts = new(StringComparer.Ordinal)
    {
        ["hair"] = "Hair", ["face"] = "Face", ["tail"] = "Tail", ["zear"] = "Ears", ["body"] = "Body (skin)",
    };

    [GeneratedRegex(@"^chara/equipment/e(\d{4})/")] private static partial Regex Equipment();
    [GeneratedRegex(@"^chara/accessory/a(\d{4})/")] private static partial Regex Accessory();
    [GeneratedRegex(@"_(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)(?:[_.])")] private static partial Regex SlotSuffix();
    [GeneratedRegex(@"/v(\d{4})/|/v(\d{2})_")] private static partial Regex VariantPart();
    [GeneratedRegex(@"^chara/weapon/w(\d{4})/obj/body/b(\d{4})/")] private static partial Regex Weapon();
    [GeneratedRegex(@"^chara/human/(c\d{4})/obj/(hair|face|tail|zear|body)/([a-z])(\d{4})/")] private static partial Regex HumanPart();
    [GeneratedRegex(@"^chara/(monster|demihuman)/([md])(\d{4})/")] private static partial Regex NonHuman();
    [GeneratedRegex(@"(?:^|[/_-])c(\d{2})(\d{2})(?=[/_a-z.])")] private static partial Regex RaceCode();

    private static readonly string[] Races =
    [
        "Hyur Midlander", "Hyur Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera",
    ];

    /// <summary>The body mods whose textures live at recognizable paths.</summary>
    private static readonly (string Needle, string Name)[] BodyTypes =
    [
        ("bibo", "Bibo+"), ("gen3", "Gen3"), ("tight&firm", "Gen3 (Tight&Firm)"), ("tbse", "TBSE"), ("hrbody", "HrBody"), ("yab", "YAB"),
    ];

    /// <summary>"Female Miqo'te" for c0801; null for codes that are not player races.</summary>
    public static string? RaceName(int raceGender, int variant)
    {
        if (raceGender is < 1 or > 18) return null;
        var race = Races[(raceGender - 1) / 2];
        var gender = raceGender % 2 == 1 ? "Male" : "Female";
        return variant == 1 ? $"{gender} {race}" : $"{gender} {race} (NPC model)";
    }

    public static ItemSupport DetectSupport(IEnumerable<string> gamePaths)
    {
        var races = new SortedDictionary<int, string>();
        var genders = new HashSet<string>(StringComparer.Ordinal);
        string? bodyType = null;
        foreach (var raw in gamePaths)
        {
            var p = raw.Replace('\\', '/').ToLowerInvariant();
            foreach (Match m in RaceCode().Matches(p))
            {
                var code = int.Parse(m.Groups[1].Value);
                var variant = int.Parse(m.Groups[2].Value);
                var name = RaceName(code, variant);
                if (name == null) continue;
                races.TryAdd(code * 100 + variant, name);
                genders.Add(code % 2 == 1 ? "Male" : "Female");
            }

            bodyType ??= BodyTypes.Where(b => p.Contains(b.Needle, StringComparison.Ordinal)).Select(b => b.Name).FirstOrDefault();
        }

        var genderText = genders.Count switch
        {
            0 => null,
            1 => genders.First(),
            _ => "Male and female",
        };
        return new ItemSupport([.. races.Values], genderText, bodyType);
    }

    public static ItemKey Classify(string gamePath)
    {
        var p = gamePath.Replace('\\', '/').ToLowerInvariant();

        if (p.EndsWith(".pap", StringComparison.Ordinal) || p.Contains("/animation/", StringComparison.Ordinal) || p.StartsWith("chara/action/", StringComparison.Ordinal))
            return new("shared:animations", "Animations", "animations");
        if (p.StartsWith("vfx/", StringComparison.Ordinal) || p.EndsWith(".avfx", StringComparison.Ordinal) || p.EndsWith(".atex", StringComparison.Ordinal))
            return new("shared:vfx", "VFX", "vfx");
        if (p.StartsWith("sound/", StringComparison.Ordinal) || p.EndsWith(".scd", StringComparison.Ordinal))
            return new("shared:sounds", "Sounds", "sounds");

        var m = Equipment().Match(p);
        if (m.Success) return Gear("equipment", "e", m.Groups[1].Value, p);
        m = Accessory().Match(p);
        if (m.Success) return Gear("accessory", "a", m.Groups[1].Value, p);

        m = Weapon().Match(p);
        if (m.Success)
        {
            var code = $"w{m.Groups[1].Value}b{m.Groups[2].Value}";
            return new($"weapon:{code}", "Weapon", code, int.Parse(m.Groups[1].Value), Variant: ParseVariant(p), WeaponBody: int.Parse(m.Groups[2].Value));
        }

        m = HumanPart().Match(p);
        if (m.Success)
        {
            var part = m.Groups[2].Value;
            var code = $"{m.Groups[3].Value}{m.Groups[4].Value}";
            // Hair, faces, tails and ears are usually modded for one race and gender at a time.
            return new($"human:{part}:{code}:{m.Groups[1].Value}", HumanParts[part], $"{code} {m.Groups[1].Value}");
        }

        if (p.StartsWith("chara/human/", StringComparison.Ordinal) && p.Contains("/skeleton/", StringComparison.Ordinal))
            return new("human:skeleton", "Skeleton", "skeleton");

        m = NonHuman().Match(p);
        if (m.Success)
        {
            var code = $"{m.Groups[2].Value}{m.Groups[3].Value}";
            return new($"{m.Groups[1].Value}:{code}", m.Groups[1].Value == "monster" ? "Minion or mount" : "Companion or mount", code);
        }

        return new("shared:other", "Other files", "other");
    }

    private static ItemKey Gear(string kind, string prefix, string id, string path)
    {
        var code = prefix + id;
        var slotMatch = SlotSuffix().Match(path[(path.IndexOf('/', $"chara/{kind}/".Length) + 1)..]);
        var slot = slotMatch.Success ? slotMatch.Groups[1].Value : null;
        var category = slot != null ? SlotNames[slot] : kind == "equipment" ? "Equipment" : "Accessory";
        return new($"{kind}:{code}:{slot ?? "any"}", category, code, int.Parse(id), slot, ParseVariant(path));
    }

    private static int? ParseVariant(string path)
    {
        var m = VariantPart().Match(path);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    /// <summary>
    /// Splits the exported files into one group per item. A file whose game paths belong to several items is
    /// included in each of them. Textures referenced by an item's materials join that item even when they live at
    /// a shared path. What is left in the shared categories becomes its own group per category.
    /// </summary>
    /// <param name="itemName">Optional real item name (for example from the game's item sheet).</param>
    public static List<ItemGroup> Group(IEnumerable<PackageFile> files, Func<ItemKey, string?>? itemName = null)
    {
        var byKey = new Dictionary<string, (ItemKey Item, Dictionary<string, (PackageFile File, SortedSet<string> Paths)> Files)>(StringComparer.Ordinal);
        var fileByGamePath = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase);

        void Add(ItemKey item, PackageFile file, string gamePath)
        {
            if (!byKey.TryGetValue(item.Key, out var group))
            {
                group = (item, new Dictionary<string, (PackageFile, SortedSet<string>)>(StringComparer.Ordinal));
                byKey[item.Key] = group;
            }
            else if (group.Item.Variant == null && item.Variant != null)
            {
                byKey[item.Key] = group = (item, group.Files);
            }

            if (!group.Files.TryGetValue(file.FileName, out var entry))
            {
                entry = (file, new SortedSet<string>(StringComparer.Ordinal));
                group.Files[file.FileName] = entry;
            }

            entry.Paths.Add(gamePath);
        }

        var list = files.ToList();
        foreach (var file in list)
        {
            foreach (var gp in file.GamePaths)
            {
                var normalized = PenumbraPackageBuilder.NormalizeGamePath(gp);
                if (normalized == null) continue;
                fileByGamePath.TryAdd(normalized, file);
                Add(Classify(normalized), file, normalized);
            }
        }

        // Textures used by an item's materials belong to that item, wherever they are stored.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, group) in byKey.Where(kv => !kv.Value.Item.IsShared).ToList())
        {
            foreach (var (file, paths) in group.Files.Values.ToList())
            {
                if (!paths.Any(p => p.EndsWith(".mtrl", StringComparison.Ordinal))) continue;
                foreach (var texture in ReadMaterialTextures(file.SourcePath))
                {
                    var tp = PenumbraPackageBuilder.NormalizeGamePath(texture);
                    if (tp == null || !fileByGamePath.TryGetValue(tp, out var texFile)) continue;
                    Add(group.Item, texFile, tp);
                    claimed.Add(tp);
                }
            }
        }

        // A texture claimed by a material no longer needs to sit in a shared group on its own.
        foreach (var (_, group) in byKey.Where(kv => kv.Value.Item.IsShared))
        {
            foreach (var entry in group.Files.Values) entry.Paths.ExceptWith(claimed);
            foreach (var name in group.Files.Where(kv => kv.Value.Paths.Count == 0).Select(kv => kv.Key).ToList()) group.Files.Remove(name);
        }

        return byKey.Values.Where(g => g.Files.Count > 0)
            .Select(g => new ItemGroup(g.Item, Label(g.Item, itemName),
                g.Files.Values.Select(e => e.File with { GamePaths = [.. e.Paths] }).OrderBy(f => f.FileName, StringComparer.Ordinal).ToList(),
                DetectSupport(g.Files.Values.SelectMany(e => e.Paths))))
            .OrderBy(g => g.Item.IsShared)
            .ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static string Label(ItemKey item, Func<ItemKey, string?>? itemName)
    {
        string? name = null;
        try
        {
            name = item.IsShared ? null : itemName?.Invoke(item);
        }
        catch (Exception)
        {
            // Names are cosmetic; fall back to the game id.
        }

        if (item.IsShared) return item.Category;
        return string.IsNullOrWhiteSpace(name) ? $"{item.Category} {item.Code}" : $"{item.Category} - {name} ({item.Code})";
    }

    /// <summary>
    /// Texture game paths referenced by a material (.mtrl) file. Reads only the header and string table;
    /// returns nothing for files it does not understand.
    /// </summary>
    public static List<string> ReadMaterialTextures(string mtrlPath)
    {
        var result = new List<string>();
        try
        {
            var data = File.ReadAllBytes(mtrlPath);
            if (data.Length < 16) return result;
            // Header: uint version, ushort fileSize, ushort dataSetSize, ushort stringTableSize, ushort shaderNameOffset,
            // byte textureCount, byte uvSetCount, byte colorSetCount, byte additionalDataSize.
            var stringTableSize = BitConverter.ToUInt16(data, 8);
            int textureCount = data[12], uvSetCount = data[13], colorSetCount = data[14];
            var offsetsStart = 16;
            var stringsStart = offsetsStart + 4 * (textureCount + uvSetCount + colorSetCount);
            if (stringsStart + stringTableSize > data.Length) return result;

            for (var i = 0; i < textureCount; i++)
            {
                var offset = BitConverter.ToUInt16(data, offsetsStart + 4 * i);
                if (offset >= stringTableSize) continue;
                var start = stringsStart + offset;
                var end = Array.IndexOf(data, (byte)0, start, stringsStart + stringTableSize - start);
                if (end < 0) continue;
                var text = Encoding.UTF8.GetString(data, start, end - start);
                if (text.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) result.Add(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable material: its textures simply stay where the path layout put them.
        }

        return result;
    }
}
