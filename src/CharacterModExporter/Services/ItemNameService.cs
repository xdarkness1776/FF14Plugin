using CharacterModExporter.Core;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CharacterModExporter.Services;

/// <summary>
/// Names item mods after the game item whose model they replace, from the game's Item sheet. Model ids are shared by
/// dye and recolor variants, so the variant from the material path is used when it is known.
/// </summary>
public sealed class ItemNameService(IDataManager data, IPluginLog log)
{
    private readonly Lazy<Dictionary<string, string>> _names = new(() => Build(data, log));

    public string? Resolve(ItemKey item)
    {
        if (item.SetId is not { } set) return null;
        var names = _names.Value;
        if (item.WeaponBody is { } body)
        {
            return names.GetValueOrDefault(Key("w", set, body, item.Variant)) ?? names.GetValueOrDefault(Key("w", set, body, null));
        }

        if (item.Slot == null) return null;
        return names.GetValueOrDefault(Key(item.Slot, set, 0, item.Variant)) ?? names.GetValueOrDefault(Key(item.Slot, set, 0, null));
    }

    private static string Key(string slot, int set, int body, int? variant) => $"{slot}:{set}:{body}:{variant?.ToString() ?? "*"}";

    private static Dictionary<string, string> Build(IDataManager data, IPluginLog log)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in data.GetExcelSheet<Item>())
            {
                var name = item.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name) || !item.EquipSlotCategory.IsValid) continue;
                var slots = item.EquipSlotCategory.Value;
                if (slots.MainHand > 0 || slots.OffHand > 0)
                {
                    AddWeapon(names, item.ModelMain, name);
                    AddWeapon(names, item.ModelSub, name);
                    continue;
                }

                var model = item.ModelMain;
                var set = (int)(model & 0xFFFF);
                var variant = (int)((model >> 16) & 0xFF);
                if (set == 0) continue;
                foreach (var slot in Slots(slots))
                {
                    names.TryAdd(Key(slot, set, 0, variant), name);
                    names.TryAdd(Key(slot, set, 0, null), name);
                }
            }
        }
        catch (Exception ex)
        {
            // Names are cosmetic; item mods fall back to the game id.
            log.Warning(ex, "Could not read the item sheet");
        }

        return names;
    }

    private static void AddWeapon(Dictionary<string, string> names, ulong model, string name)
    {
        var set = (int)(model & 0xFFFF);
        var body = (int)((model >> 16) & 0xFFFF);
        var variant = (int)((model >> 32) & 0xFFFF);
        if (set == 0) return;
        names.TryAdd(Key("w", set, body, variant), name);
        names.TryAdd(Key("w", set, body, null), name);
    }

    private static IEnumerable<string> Slots(EquipSlotCategory c)
    {
        if (c.Head > 0) yield return "met";
        if (c.Body > 0) yield return "top";
        if (c.Gloves > 0) yield return "glv";
        if (c.Legs > 0) yield return "dwn";
        if (c.Feet > 0) yield return "sho";
        if (c.Ears > 0) yield return "ear";
        if (c.Neck > 0) yield return "nek";
        if (c.Wrists > 0) yield return "wrs";
        if (c.FingerR > 0) yield return "rir";
        if (c.FingerL > 0) yield return "ril";
    }
}
