using CharacterModExporter.Core;
using CharacterModExporter.Core.Library;
using CharacterModExporter.Services;
using CharacterModExporter.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/cmexport";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;

    private readonly WindowSystem _windows = new("CharacterModExporter");
    private readonly Dictionary<uint, ExportWindow> _exportWindows = [];
    private readonly Configuration _config;
    private readonly PlayerSyncIpcClient _ipc;
    private readonly PenumbraExportSource _penumbra;
    private readonly ExportSourceRouter _source;
    private readonly ExportFolderService _folders;
    private readonly ExportCoordinator _coordinator;
    private readonly CharacterSelectionService _selection;
    private readonly PlayerContextMenuService _contextMenu;
    private readonly SettingsWindow _settingsWindow;
    private readonly ICallGateSubscriber<string> _penumbraModDirectory;
    private readonly PackageLibrary _library;

    public Plugin()
    {
        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _config.Export ??= new ExportSettings();

        _ipc = new PlayerSyncIpcClient(PluginInterface, Log);
        _penumbraModDirectory = PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        _folders = new ExportFolderService(ForbiddenRoots);
        var vanilla = new VanillaFileComparer(gamePath => Data.FileExists(gamePath) ? Data.GetFile(gamePath)?.Data : null);
        _penumbra = new PenumbraExportSource(PluginInterface, ObjectTable, Framework, ClientState, Log, _ipc, ForbiddenRoots, vanilla);
        _source = new ExportSourceRouter(_ipc, _penumbra);
        var itemNames = new ItemNameService(Data, Log);
        _coordinator = new ExportCoordinator(_source, _folders, PluginInterface.Manifest.AssemblyVersion.ToString(), itemNames: itemNames.Resolve);
        _selection = new CharacterSelectionService(ObjectTable, Framework);

        // The index and the user's package choices live in this plugin's own configuration folder, never in the library.
        _library = new PackageLibrary(PluginInterface.ConfigDirectory.FullName);
        _settingsWindow = new SettingsWindow(_config, _folders, OpenFolder, _library);
        _windows.AddWindow(_settingsWindow);

        _contextMenu = new PlayerContextMenuService(ContextMenu, _selection, _ipc, () => _config.Export, OpenExportWindow);

        CommandManager.AddHandler(Command, new CommandInfo((_, _) => _settingsWindow.Toggle())
        {
            HelpMessage = "Open Character Mod Exporter settings. Exports start from a player's right-click menu: \"Export Character Mods\".",
        });

        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _settingsWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += _settingsWindow.Toggle;
    }

    /// <summary>
    /// Folders an export may never be written into or through: the game installation, Dalamud and every plugin's
    /// configuration and installation, Penumbra's mod directory and PlayerSync's cache. The PlayerSync export IPC
    /// checks the same common roots itself, so a direct IPC call cannot bypass them.
    /// </summary>
    private IEnumerable<string?> ForbiddenRoots()
    {
        foreach (var root in ExportPathPolicy.CommonForbiddenRoots(PluginInterface.ConfigDirectory.Parent?.FullName,
                     PluginInterface.AssemblyLocation.Directory?.FullName,
                     Path.GetDirectoryName(typeof(IDalamudPluginInterface).Assembly.Location)))
            yield return root;

        string? penumbra = null;
        try
        {
            penumbra = _penumbraModDirectory.InvokeFunc();
        }
        catch (Exception)
        {
            // Penumbra not loaded.
        }

        yield return penumbra;
        yield return PlayerSyncConfigReader.ReadCacheFolder(PluginInterface.ConfigDirectory.Parent?.FullName);
    }

    private void OpenExportWindow(ExportTarget target)
    {
        if (_exportWindows.TryGetValue(target.EntityId, out var existing))
        {
            if (existing.Session.Target == target)
            {
                existing.IsOpen = true;
                existing.BringToFront();
                _ = existing.Session.RefreshAsync();
                return;
            }

            // The entity id now belongs to another character.
            _windows.RemoveWindow(existing);
            existing.Dispose();
            _exportWindows.Remove(target.EntityId);
            _penumbra.Forget(target.EntityId);
        }

        var session = new ExportSession(target, _source, _folders, _coordinator, () => _config.Export, _selection.IsStillPresentAsync,
            library: _library, exporterVersion: PluginInterface.Manifest.AssemblyVersion.ToString());
        var window = new ExportWindow(session, () => _settingsWindow.IsOpen = true, OpenFolder, () => _penumbra.Forget(target.EntityId));
        _exportWindows[target.EntityId] = window;
        _windows.AddWindow(window);
    }

    private static void OpenFolder(string path)
        => ExportWindow.OpenInExplorer(path, message => Chat.PrintError("[Character Mod Exporter] " + message));

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _settingsWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= _settingsWindow.Toggle;
        CommandManager.RemoveHandler(Command);
        _contextMenu.Dispose();

        // Cancels running exports; partial files are cleaned up by the job runner.
        foreach (var window in _exportWindows.Values) window.Dispose();
        _exportWindows.Clear();
        _windows.RemoveAllWindows();
        _library.CancelRefresh();
        _penumbra.Dispose();
    }
}
