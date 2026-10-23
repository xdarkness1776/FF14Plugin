using System.Diagnostics;
using System.Numerics;
using CharacterModExporter.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Windows;

/// <summary>Export window for one character. Draws only; all work happens in <see cref="ExportSession"/>.</summary>
public sealed class ExportWindow : Window, IDisposable
{
    private readonly ExportSession _session;
    private readonly Action _openSettings;
    private readonly Action<string> _openFolder;
    private string _filter = string.Empty;
    private bool _showReport;
    private (string Path, DateTime Written, string Text)? _report;

    public ExportWindow(ExportSession session, Action openSettings, Action<string> openFolder)
        : base($"Export Character Mods: {session.Target.Name}##cmx-{session.Target.EntityId}")
    {
        _session = session;
        _openSettings = openSettings;
        _openFolder = openFolder;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(720, 460), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        Size = new Vector2(980, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public ExportSession Session => _session;

    public override void OnOpen() => _ = _session.RefreshAsync();

    public void Dispose() => _session.Dispose();

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        if (_showReport) DrawReport();
        else DrawInventory();
    }

    private void DrawHeader()
    {
        var s = _session;
        var state = s.State;
        ImGui.TextUnformatted($"Character: {s.Target.Name}    World: {s.Target.World ?? "unknown"}");

        var provider = s.Provider switch
        {
            ProviderStatus.Available => (s.SourceLabel, ImGuiColors.HealerGreen),
            ProviderStatus.IncompatibleVersion => ($"PlayerSync export IPC v{s.ProviderVersion} (incompatible, need v{ExportIpc.ApiVersion})", ImGuiColors.DalamudRed),
            _ => (s.UnavailableMessage, ImGuiColors.DalamudRed),
        };
        ImGui.TextColored(provider.Item2, "Source: " + provider.Item1);

        if (state != null)
        {
            var link = state.Lookup switch
            {
                CharacterLookup.Found when !state.LinkDetailsKnown => "synced by PlayerSync (pair, syncshell or ZoneSync)",
                CharacterLookup.Found when state.IsZoneSyncOnly => "ZoneSync",
                CharacterLookup.Found when state.IsDirectlyPaired => "direct pair",
                CharacterLookup.Found when state.SharesZoneSyncGroup => "syncshell + ZoneSync",
                CharacterLookup.Found => "syncshell",
                CharacterLookup.NotConnected => "PlayerSync is not connected",
                CharacterLookup.Paused => "paused in PlayerSync",
                _ => "not synced by PlayerSync in this area",
            };
            ImGui.TextUnformatted(state.LinkDetailsKnown
                ? $"Sync link: {link}    ZoneSync enabled: {(state.ZoneSyncEnabled ? "yes" : "no")}"
                : $"Sync link: {link}");
            var syncColor = state.SyncState switch
            {
                SyncState.Complete => ImGuiColors.HealerGreen,
                SyncState.CompleteWithMissing => ImGuiColors.DalamudYellow,
                SyncState.InProgress => ImGuiColors.DalamudOrange,
                _ => ImGuiColors.DalamudRed,
            };
            ImGui.TextColored(syncColor, "Synchronization: " + (state.LinkDetailsKnown ? DescribeSync(state.SyncState) : DescribeLoaded(state.SyncState)));
            foreach (var note in state.Notes) ImGui.TextColored(ImGuiColors.DalamudGrey, note);
        }

        if (!s.TargetPresent) ImGui.TextColored(ImGuiColors.DalamudRed, $"{s.Target.Name} is no longer nearby.");
        ImGui.TextUnformatted("Export folder: " + (s.LastOutcome?.Layout?.SessionFolder ?? s.CharacterFolder));

        if (s.Operation != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, s.Operation);
            if (s.Progress is { FilesTotal: > 0 } p)
            {
                var fraction = p.BytesTotal > 0 ? (float)p.BytesDone / p.BytesTotal : (float)p.FilesDone / p.FilesTotal;
                ImGui.ProgressBar(Math.Clamp(fraction, 0f, 1f), new Vector2(-1, 0),
                    $"{p.FilesDone}/{p.FilesTotal} files, {ExportCoordinator.FormatBytes(p.BytesDone)} / {ExportCoordinator.FormatBytes(p.BytesTotal)}");
            }
        }

        if (s.LastError != null) ImGui.TextColored(ImGuiColors.DalamudRed, s.LastError);
        if (s.LastOutcome is { } o)
        {
            var color = o.Success ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow;
            ImGui.TextColored(color, $"Last export: {o.Phase} - {o.Message}");
        }
    }

    private static string DescribeSync(SyncState state) => state switch
    {
        SyncState.Complete => "complete, every file is available locally",
        SyncState.CompleteWithMissing => "applied, but some files are missing or were excluded",
        SyncState.InProgress => "in progress (downloading or applying)",
        SyncState.Stale => "newer data received but not applied yet",
        SyncState.NotApplied => "data received but not applied (deferred, paused or filtered)",
        _ => "no character data received yet",
    };

    // Stock PlayerSync does not report sync progress; the Penumbra source can only see whether loading has settled.
    private static string DescribeLoaded(SyncState state) => state switch
    {
        SyncState.Complete => "loaded, no new files for a few seconds",
        SyncState.CompleteWithMissing => "loaded, but some files could not be read",
        SyncState.InProgress => "still loading (new files appeared in the last few seconds)",
        _ => "no PlayerSync files loaded for this character yet",
    };

    private void DrawControls()
    {
        var s = _session;
        var busy = s.IsBusy;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Refresh Character Resources")) _ = s.RefreshAsync();
            Tooltip("Reads the character's current files again. Nothing is downloaded.");
            ImGui.SameLine();
            if (ImGui.Button("Scan Character Resources")) _ = s.ResyncAsync();
            Tooltip("Redraws the character so the game loads all of its files again, then lists them. Nothing is downloaded by the exporter. " +
                "(With a PlayerSync build that has the export IPC, PlayerSync re-runs its normal sync instead.)");
            ImGui.SameLine();
            if (ImGui.Button("Select All Available")) s.SelectAllAvailable();
            ImGui.SameLine();
            using (ImRaii.Disabled(!s.CanExport))
            {
                if (ImGui.Button("Export Selected")) _ = s.ExportSelectedAsync();
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(s.FailedFiles.Count == 0))
            {
                if (ImGui.Button($"Retry Failed Exports ({s.FailedFiles.Count})")) _ = s.RetryFailedAsync();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("Cancel Export")) s.Cancel();
        }

        if (ImGui.Button("Open Export Folder")) _openFolder(s.ExportRoot);
        ImGui.SameLine();
        if (ImGui.Button("Open Character Folder")) _openFolder(s.LastOutcome?.Layout?.SessionFolder ?? s.CharacterFolder);
        ImGui.SameLine();
        if (ImGui.Button(_showReport ? "Show Resources" : "View Export Report")) _showReport = !_showReport;
        ImGui.SameLine();
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Verify Exported Files")) _ = s.RefreshAsync(verifyExported: true);
        }
        Tooltip("Hashes files already in the export folder and compares them with their SHA-1 identifiers. Results are cached until a file changes.");
        ImGui.SameLine();
        if (ImGui.Button("Settings")) _openSettings();

        DrawSummary();
    }

    private void DrawSummary()
    {
        var inv = _session.Inventory;
        var selected = inv.Assets.Count(a => a.Selected && a.CanExport);
        var available = inv.Assets.Count(a => a.CanExport);
        var already = inv.Assets.Count(a => a.Selected && a.ExportedState is ExportedFileState.PresentVerified or ExportedFileState.PresentUnverified);
        var pending = _session.State?.SyncState is SyncState.InProgress or SyncState.Stale;
        ImGui.TextUnformatted($"Selected {selected} of {available} available files ({ExportCoordinator.FormatBytes(inv.KnownSelectedBytes)}), " +
            $"{already} already in the export folder, {inv.Unresolved.Count} missing/unresolved, {inv.FileSwaps.Count} file swaps" +
            (pending ? ", synchronization pending" : string.Empty) +
            (_session.State?.HasManipulations == true ? ", meta manipulations not exportable" : string.Empty) + ".");
    }

    private void DrawInventory()
    {
        var inv = _session.Inventory;
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##filter", "Filter by game path or hash", ref _filter, 256);

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##assets", 9, flags, new Vector2(-1, -1));
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableSetupColumn("Game path", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cache identifier");
        ImGui.TableSetupColumn("Ext");
        ImGui.TableSetupColumn("Size");
        ImGui.TableSetupColumn("Cache");
        ImGui.TableSetupColumn("In export folder");
        ImGui.TableSetupColumn("Last export");
        ImGui.TableSetupColumn("Notes");
        ImGui.TableHeadersRow();

        var results = _session.LastOutcome?.Files.ToDictionary(f => f.CacheHash, StringComparer.OrdinalIgnoreCase);
        foreach (var row in inv.Assets)
        {
            if (!Matches(row.GamePaths, row.CacheHash)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(!row.CanExport || _session.IsBusy))
            {
                var selected = row.Selected;
                if (ImGui.Checkbox($"##sel{row.CacheHash}", ref selected)) row.Selected = selected;
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.GamePaths.FirstOrDefault() ?? "?");
            if (row.GamePaths.Count > 1) Tooltip(string.Join("\n", row.GamePaths));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.CacheHash[..Math.Min(12, row.CacheHash.Length)] + "...");
            Tooltip(row.CacheHash + (row.SourceHash != null && row.SourceHash != row.CacheHash ? "\nsource: " + row.SourceHash : string.Empty));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Extension);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Size.HasValue ? ExportCoordinator.FormatBytes(row.Size.Value) : "?");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.SourceKind == SourceKind.PenumbraModDirectory ? "local mod" : "cached");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.ExportedState switch
            {
                ExportedFileState.PresentVerified => "yes, verified",
                ExportedFileState.PresentUnverified => "yes",
                ExportedFileState.PresentMismatch => "yes, DIFFERENT content",
                _ => "-",
            });
            ImGui.TableNextColumn();
            if (results != null && results.TryGetValue(row.CacheHash, out var r))
            {
                ImGui.TextUnformatted(r.Outcome + (r.Sha1Verified ? " (SHA-1 ok)" : string.Empty));
                if (r.Error != null) Tooltip(r.Error);
            }
            ImGui.TableNextColumn();
            var notes = new List<string>();
            if (row.IsCompressedAlternate) notes.Add("compressed alternate");
            if (row.ObjectKinds.Count > 0 && !(row.ObjectKinds.Count == 1 && row.ObjectKinds.Contains("Player"))) notes.Add(string.Join("/", row.ObjectKinds));
            notes.Add("individual resource");
            ImGui.TextUnformatted(string.Join(", ", notes));
        }

        foreach (var row in inv.Unresolved)
        {
            if (!Matches(row.GamePaths, row.Hash)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudRed, row.GamePaths.FirstOrDefault() ?? "?");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Hash is { Length: > 12 } h ? h[..12] + "..." : row.Hash ?? "-");
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudRed, row.Availability == ResourceAvailability.ExcludedByValidation ? "excluded" : "missing");
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Availability == ResourceAvailability.ExcludedByValidation ? "PlayerSync validation excluded it" : "not available locally");
        }

        foreach (var row in inv.FileSwaps)
        {
            if (!Matches(row.GamePaths, row.FileSwapTarget)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, row.GamePaths.FirstOrDefault() ?? "?");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("-");
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("swap");
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("file swap to " + row.FileSwapTarget);
        }
    }

    private bool Matches(IReadOnlyList<string> gamePaths, string? hash)
    {
        if (_filter.Length == 0) return true;
        return (hash?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || gamePaths.Any(p => p.Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawReport()
    {
        var outcome = _session.LastOutcome;
        if (outcome?.Layout == null)
        {
            ImGui.TextUnformatted("No export has run in this window yet.");
            return;
        }

        var readme = outcome.Layout.ReadmePath;
        using var child = ImRaii.Child("##report", new Vector2(-1, -1), true);
        if (!child) return;
        ImGui.TextUnformatted(ReadReport(readme) ?? "README.txt was not written: " + outcome.Message);
    }

    private string? ReadReport(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var written = File.GetLastWriteTimeUtc(path);
            if (_report is not { } cached || cached.Path != path || cached.Written != written)
                _report = (path, written, File.ReadAllText(path));
            return _report.Value.Text;
        }
        catch (IOException ex)
        {
            return "Could not read the report: " + ex.Message;
        }
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }

    /// <summary>Opens a folder in the system file browser (Explorer on Windows). Creates nothing.</summary>
    public static void OpenInExplorer(string path, Action<string> onError)
    {
        if (!Directory.Exists(path))
        {
            onError($"'{path}' does not exist yet. It is created by the first export.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            onError("Could not open the folder: " + ex.Message);
        }
    }
}
