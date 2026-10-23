using CharacterModExporter.Core;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace CharacterModExporter.Services;

/// <summary>Adds "Export Character Mods" to the right-click menu of other player characters.</summary>
public sealed class PlayerContextMenuService : IDisposable
{
    public const string MenuLabel = "Export Character Mods";

    private readonly IContextMenu _contextMenu;
    private readonly CharacterSelectionService _selection;
    private readonly PlayerSyncIpcClient _ipc;
    private readonly Func<ExportSettings> _settings;
    private readonly Action<ExportTarget> _open;

    public PlayerContextMenuService(IContextMenu contextMenu, CharacterSelectionService selection, PlayerSyncIpcClient ipc,
        Func<ExportSettings> settings, Action<ExportTarget> open)
    {
        _contextMenu = contextMenu;
        _selection = selection;
        _ipc = ipc;
        _settings = settings;
        _open = open;
        _contextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target) return;
        var exportTarget = _selection.FromMenuTarget(target);
        if (exportTarget == null) return;

        if (_settings().OnlyShowForSyncedCharacters)
        {
            // Stock PlayerSync IPC; cheap and safe to call while the menu opens.
            var handled = _ipc.GetHandledAddresses();
            if (handled != null && target.TargetObject is IPlayerCharacter pc && !handled.Contains(pc.Address)) return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = new SeStringBuilder().AddText(MenuLabel).Build(),
            PrefixChar = 'E',
            UseDefaultPrefix = false,
            OnClicked = _ => _open(exportTarget),
        });
    }

    public void Dispose() => _contextMenu.OnMenuOpened -= OnMenuOpened;
}
