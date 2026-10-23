using System.Numerics;
using CharacterModExporter.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Windows;

/// <summary>Settings, including the Export Location section with the folder picker.</summary>
public sealed class SettingsWindow : Window
{
    private readonly Configuration _config;
    private readonly ExportFolderService _folders;
    private readonly Action<string> _openFolder;
    private readonly FileDialogManager _dialog = new();
    private string _pendingParent;
    private string _pendingRootName;
    private string? _message;
    private bool _messageIsError;

    public SettingsWindow(Configuration config, ExportFolderService folders, Action<string> openFolder)
        : base("Character Mod Exporter Settings##cmx-settings")
    {
        _config = config;
        _folders = folders;
        _openFolder = openFolder;
        _pendingParent = folders.ResolveParent(config.Export);
        _pendingRootName = config.Export.RootFolderName;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 380), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public override void OnOpen()
    {
        _pendingParent = _folders.ResolveParent(_config.Export);
        _pendingRootName = _config.Export.RootFolderName;
        _message = null;
        // Warn about a saved folder that went away (unplugged drive, deleted folder); never redirect silently.
        var error = _folders.ValidateParent(_pendingParent);
        if (error != null) SetMessage("The saved export location is not usable: " + error + " Choose another folder.", true);
    }

    public override void Draw()
    {
        var export = _config.Export;
        ImGui.TextUnformatted("Export Location");
        ImGui.Separator();
        ImGui.TextUnformatted("Current Parent Folder: " + _folders.ResolveParent(export) + (string.IsNullOrWhiteSpace(export.ParentFolder) ? " (default: Documents)" : string.Empty));
        ImGui.TextUnformatted("Exports are written to: " + SafeRoot());

        ImGui.SetNextItemWidth(-160);
        ImGui.InputText("##parent", ref _pendingParent, 1024);
        ImGui.SameLine();
        if (ImGui.Button("Choose Parent Folder"))
        {
            var start = Directory.Exists(_pendingParent) ? _pendingParent : _folders.DefaultParentFolder;
            _dialog.OpenFolderDialog("Choose the parent folder for exports", (ok, path) =>
            {
                // Cancelling the dialog keeps the previous selection.
                if (ok && !string.IsNullOrWhiteSpace(path)) _pendingParent = path;
            }, start, isModal: true);
        }

        ImGui.SetNextItemWidth(300);
        ImGui.InputText("Export folder name", ref _pendingRootName, 128);

        if (ImGui.Button("Apply Location")) Apply();
        ImGui.SameLine();
        if (ImGui.Button("Open Export Folder")) _openFolder(SafeRoot());
        ImGui.SameLine();
        if (ImGui.Button("Reset to Default"))
        {
            export.ParentFolder = null;
            export.RootFolderName = ExportSettings.DefaultRootFolderName;
            _pendingParent = _folders.DefaultParentFolder;
            _pendingRootName = export.RootFolderName;
            _config.Save();
            SetMessage("Reset to Documents/" + ExportSettings.DefaultRootFolderName + ".", false);
        }

        if (_message != null) ImGui.TextColored(_messageIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen, _message);

        ImGui.Spacing();
        ImGui.TextUnformatted("Export Options");
        ImGui.Separator();
        var changed = false;
        var reuse = export.ReuseCharacterFolder;
        if (ImGui.Checkbox("Reuse the character's folder (off: new timestamped folder per export)", ref reuse)) { export.ReuseCharacterFolder = reuse; changed = true; }
        var shared = export.StorageMode == AssetStorageMode.SharedPool;
        if (ImGui.Checkbox("Store files once in a shared folder (_SharedAssets) for all characters", ref shared))
        {
            export.StorageMode = shared ? AssetStorageMode.SharedPool : AssetStorageMode.PerCharacter;
            changed = true;
        }
        var pmp = export.BuildPenumbraPackage;
        if (ImGui.Checkbox("Build a Penumbra package (.pmp) when the mapping is complete", ref pmp)) { export.BuildPenumbraPackage = pmp; changed = true; }
        var itemMods = export.BuildItemMods;
        if (ImGui.Checkbox("Build one Penumbra mod per item (Mods\\<category>), e.g. each gear piece, hairstyle, animations", ref itemMods)) { export.BuildItemMods = itemMods; changed = true; }
        var onlySynced = export.OnlyShowForSyncedCharacters;
        if (ImGui.Checkbox("Only show the context menu entry on characters PlayerSync is syncing", ref onlySynced)) { export.OnlyShowForSyncedCharacters = onlySynced; changed = true; }
        var wait = export.SyncWaitSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Seconds to wait for synchronization", ref wait)) { export.SyncWaitSeconds = Math.Clamp(wait, 5, 900); changed = true; }
        if (changed) _config.Save();

        _dialog.Draw();
    }

    private string SafeRoot()
    {
        try
        {
            return _folders.ResolveRoot(_config.Export);
        }
        catch (ArgumentException ex)
        {
            return "(invalid: " + ex.Message + ")";
        }
    }

    private void Apply()
    {
        var parent = _pendingParent.Trim();
        var error = _folders.ValidateParent(parent);
        if (error != null)
        {
            SetMessage(error, true);
            return;
        }

        var name = ExportPathPolicy.SanitizeSegment(_pendingRootName, ExportSettings.DefaultRootFolderName);
        _config.Export.ParentFolder = string.Equals(parent, _folders.DefaultParentFolder, StringComparison.OrdinalIgnoreCase) ? null : parent;
        _config.Export.RootFolderName = name;
        _pendingRootName = name;
        _config.Save();
        SetMessage("Saved. Exports go to " + SafeRoot() + " (created automatically on the first export).", false);
    }

    private void SetMessage(string message, bool isError)
    {
        _message = message;
        _messageIsError = isError;
    }
}
