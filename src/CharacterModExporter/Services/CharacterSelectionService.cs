using CharacterModExporter.Core;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace CharacterModExporter.Services;

/// <summary>Turns a context menu target into an <see cref="ExportTarget"/> and re-validates it later.</summary>
public sealed class CharacterSelectionService(IObjectTable objectTable, IFramework framework)
{
    /// <summary>
    /// Captures the exact character the menu was opened on. Returns null for anything that is not
    /// another player character (NPCs, the local player, chat names without a game object).
    /// </summary>
    public ExportTarget? FromMenuTarget(MenuTargetDefault target)
    {
        if (target.TargetObject is not IPlayerCharacter player) return null;
        if (objectTable.LocalPlayer is { } self && self.EntityId == player.EntityId) return null;
        if (player.EntityId is 0 or 0xE0000000) return null;

        var world = player.HomeWorld.ValueNullable?.Name.ToString();
        return new ExportTarget(player.EntityId, player.Name.TextValue, string.IsNullOrWhiteSpace(world) ? null : world);
    }

    /// <summary>
    /// True when the game object with the target's EntityId still exists and still has the same name.
    /// Runs on the framework thread because it reads the object table.
    /// </summary>
    public Task<bool> IsStillPresentAsync(ExportTarget target)
        => framework.RunOnFrameworkThread(() =>
            objectTable.SearchByEntityId(target.EntityId) is IPlayerCharacter pc
            && string.Equals(pc.Name.TextValue, target.Name, StringComparison.Ordinal));
}
