using System.Diagnostics;
using System.Numerics;
using CharacterModExporter.Core;
using CharacterModExporter.Core.Library;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
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
    private bool _showOriginals;
    private bool _viewSnapshot;
    private string _packageFilter = string.Empty;
    private int _statusFilter;
    private string _categoryEdit = string.Empty;
    private readonly HashSet<string> _selectedPackages = new(StringComparer.Ordinal);
    private readonly FileDialogManager _dialog = new();
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
        if (_showOriginals) DrawOriginals();
        else if (_showReport) DrawReport();
        else DrawInventory();
        _dialog.Draw();
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
        if (ImGui.Button(_showReport ? "Show Resources" : "View Export Report"))
        {
            _showReport = !_showReport;
            _showOriginals = false;
        }

        ImGui.SameLine();
        if (ImGui.Button(_showOriginals ? "Show Resources##orig" : "Original Packages"))
        {
            _showOriginals = !_showOriginals;
            _showReport = false;
        }

        Tooltip("Find the original .pmp files in your own library that this character's files come from, and copy them unchanged.");
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

    private static readonly MatchStatus[] PackageStatuses =
        [MatchStatus.UserConfirmed, MatchStatus.Exact, MatchStatus.Strong, MatchStatus.Partial, MatchStatus.Ambiguous];

    private static readonly string[] StatusFilterNames = ["All statuses", .. PackageStatuses.Select(Describe)];

    private void DrawOriginals()
    {
        var s = _session;
        var library = s.Library;
        if (library?.Index == null)
        {
            ImGui.TextWrapped("No package library is indexed yet. Choose the folder with your original .pmp files under Settings, Original Package Library, and index it.");
            if (ImGui.Button("Open Settings##orig")) _openSettings();
            return;
        }

        var index = library.Index;
        ImGui.TextUnformatted($"Library: {index.LibraryFolder}    {index.Packages.Count} package(s), indexed {index.BuiltAtUtc.ToLocalTime():g}");
        using (ImRaii.Disabled(s.IsBusy))
        {
            if (ImGui.Button("Match Live Character"))
            {
                _viewSnapshot = false;
                _ = s.MatchLibraryAsync();
            }

            Tooltip("Compares the files PlayerSync applied to this character now with the files inside your packages (SHA-1 content hashes).");
            ImGui.SameLine();
            if (ImGui.Button("Open MCDF Snapshot..."))
            {
                _dialog.OpenFileDialog("Open a saved PlayerSync character file (MCDF)", ".mcdf", (ok, path) =>
                {
                    if (!ok || string.IsNullOrWhiteSpace(path)) return;
                    _viewSnapshot = true;
                    _ = s.OpenSnapshotAsync(path);
                });
            }

            Tooltip("Reads a saved .mcdf file without applying it, and compares it with your library separately from the live character.");
        }

        if (s.Snapshot != null)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton("Live", !_viewSnapshot)) _viewSnapshot = false;
            ImGui.SameLine();
            if (ImGui.RadioButton("Snapshot", _viewSnapshot)) _viewSnapshot = true;
            ImGui.SameLine();
            if (ImGui.Button("Close Snapshot"))
            {
                s.CloseSnapshot();
                _viewSnapshot = false;
            }
        }

        if (_viewSnapshot && s.Snapshot is { } snap)
        {
            ImGui.TextColored(ImGuiColors.DalamudViolet,
                $"Saved snapshot (not live): {Path.GetFileName(snap.FilePath)}, saved {snap.FileLastWriteUtc.ToLocalTime():g}, {snap.Files.Count} file(s), " +
                (snap.AllVerified ? "every file matches its hash." : "some files could not be verified and are not used as evidence."));
            if (!string.IsNullOrWhiteSpace(snap.Description)) ImGui.TextColored(ImGuiColors.DalamudGrey, "Description: " + snap.Description);
        }

        var report = _viewSnapshot ? s.SnapshotMatch : s.LiveMatch;
        if (report == null)
        {
            ImGui.TextUnformatted(_viewSnapshot ? "Open an MCDF snapshot to compare it." : "Press Match Live Character to compare this character with your library.");
            return;
        }

        var counts = report.Resources.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        ImGui.TextUnformatted($"Character files: {counts.GetValueOrDefault(MatchStatus.Exact)} found in one package, {counts.GetValueOrDefault(MatchStatus.Ambiguous)} in several, " +
            $"{counts.GetValueOrDefault(MatchStatus.Unmatched)} in none, {counts.GetValueOrDefault(MatchStatus.Unresolvable)} could not be compared.");

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##pkgfilter", "Search name, number or category", ref _packageFilter, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.Combo("##status", ref _statusFilter, StatusFilterNames, StatusFilterNames.Length);
        ImGui.SameLine();
        if (ImGui.Button("Select Exact and Confirmed"))
        {
            _selectedPackages.Clear();
            foreach (var m in report.Packages.Where(m => m.Status is MatchStatus.Exact or MatchStatus.UserConfirmed)) _selectedPackages.Add(m.Package.RelativePath);
        }

        ImGui.SameLine();
        var chosen = report.Packages.Where(m => _selectedPackages.Contains(m.Package.RelativePath)).ToList();
        using (ImRaii.Disabled(s.IsBusy || chosen.Count == 0))
        {
            if (ImGui.Button($"Export Selected Originals ({chosen.Count})")) _ = s.ExportOriginalsAsync(chosen, _viewSnapshot);
        }

        Tooltip("Copies the selected .pmp files byte for byte (checked with SHA-256) into this character's export folder, under \"Original packages\". Existing files are never replaced.");

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        var tableHeight = s.LastOriginalExport != null ? -ImGui.GetTextLineHeightWithSpacing() * 6 : -ImGui.GetTextLineHeightWithSpacing() * 2;
        using (var table = ImRaii.Table("##packages", 9, flags, new Vector2(-1, tableHeight)))
        {
            if (table)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
                ImGui.TableSetupColumn("No.");
                ImGui.TableSetupColumn("Package", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Match");
                ImGui.TableSetupColumn("Files matched");
                ImGui.TableSetupColumn("Not seen");
                ImGui.TableSetupColumn("Options used");
                ImGui.TableSetupColumn("Category");
                ImGui.TableSetupColumn("Confirmed");
                ImGui.TableHeadersRow();

                foreach (var m in report.Packages)
                {
                    var p = m.Package;
                    var (category, fromUser, evidence) = library.Overrides.CategoryOf(p);
                    if (_statusFilter > 0 && m.Status != PackageStatuses[_statusFilter - 1]) continue;
                    if (_packageFilter.Length > 0 && !(p.FileName.Contains(_packageFilter, StringComparison.OrdinalIgnoreCase)
                                                       || (p.Name?.Contains(_packageFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                                                       || category.Contains(_packageFilter, StringComparison.OrdinalIgnoreCase))) continue;
                    var id = p.RelativePath;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var selected = _selectedPackages.Contains(id);
                    if (ImGui.Checkbox($"##pkg{id}", ref selected))
                    {
                        if (selected) _selectedPackages.Add(id);
                        else _selectedPackages.Remove(id);
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.NumberText ?? "-");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.FileName);
                    Tooltip($"{p.RelativePath}\nName: {p.DisplayName}\nAuthor: {p.Author ?? "-"}    Version: {p.Version ?? "-"}\nSHA-256: {p.Sha256}" +
                            (m.SharedWith.Count > 0 ? "\nShares matching files with: " + string.Join(", ", m.SharedWith.Take(5)) : string.Empty) +
                            (m.IdenticalCopies.Count > 0 ? "\nIdentical copies: " + string.Join(", ", m.IdenticalCopies) : string.Empty));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(StatusColor(m.Status), Describe(m.Status));
                    Tooltip(Explain(m));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted((m.MatchedPaths.Count + m.MatchedSwaps.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                         (m.ConflictPaths.Count > 0 ? $" ({m.ConflictPaths.Count} differ)" : string.Empty));
                    if (m.ConflictPaths.Count > 0) Tooltip("Different content on the character:\n" + string.Join("\n", m.ConflictPaths.Take(20)));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(m.UnseenCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.Join(", ", m.ActiveOptions));
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"{category}{(fromUser ? " (yours)" : string.Empty)}##cat{id}"))
                    {
                        _categoryEdit = fromUser ? category : string.Empty;
                        ImGui.OpenPopup($"category{id}");
                    }

                    Tooltip(evidence + " Click to set your own category; it is saved separately and never written into the package.");
                    using (var popup = ImRaii.Popup($"category{id}"))
                    {
                        if (popup)
                        {
                            ImGui.TextUnformatted("Category for " + p.FileName);
                            ImGui.SetNextItemWidth(260);
                            ImGui.InputTextWithHint("##catedit", "e.g. Gear/Body", ref _categoryEdit, 128);
                            if (ImGui.Button("Save"))
                            {
                                library.Overrides.SetCategory(p, _categoryEdit);
                                TrySaveOverrides(library);
                                ImGui.CloseCurrentPopup();
                            }

                            ImGui.SameLine();
                            if (ImGui.Button("Use detected"))
                            {
                                library.Overrides.SetCategory(p, null);
                                TrySaveOverrides(library);
                                ImGui.CloseCurrentPopup();
                            }
                        }
                    }

                    ImGui.TableNextColumn();
                    if (_viewSnapshot)
                    {
                        ImGui.TextDisabled("-");
                        Tooltip("Confirmations apply to the live character.");
                    }
                    else
                    {
                        var confirmed = m.Status == MatchStatus.UserConfirmed;
                        if (ImGui.Checkbox($"##conf{id}", ref confirmed))
                        {
                            library.Overrides.SetConfirmed(s.CharacterKey, p, confirmed);
                            TrySaveOverrides(library);
                            s.RecomputeMatches();
                        }

                        Tooltip("Mark this package as the one this character uses. The computed status is kept in the export manifest.");
                    }
                }
            }
        }

        if (s.LastOriginalExport is { } copies)
        {
            var ok = copies.Count(c => c.ExportedFile != null);
            ImGui.TextColored(ok == copies.Count ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow,
                $"Last copy: {ok} of {copies.Count} package(s) in {s.LastOriginalExportFolder}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Open##origfolder") && s.LastOriginalExportFolder != null) _openFolder(s.LastOriginalExportFolder);
            using var child = ImRaii.Child("##origresults", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 4), false);
            if (child)
            {
                foreach (var c in copies)
                {
                    var color = c.ExportedFile != null ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
                    ImGui.TextColored(color, $"{c.LibraryRelativePath}: {c.Outcome}" + (c.Error != null ? " - " + c.Error : string.Empty));
                }
            }
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "Originals are copied unchanged. The per-item mods in the Mods folder are rebuilt from synced files and are not originals.");
    }

    private void TrySaveOverrides(PackageLibrary library)
    {
        try
        {
            library.SaveOverrides();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session.ReportError("Could not save your package choices: " + ex.Message);
        }
    }

    private static string Describe(MatchStatus status) => status switch
    {
        MatchStatus.UserConfirmed => "Confirmed by you",
        MatchStatus.Exact => "Exact",
        MatchStatus.Strong => "Strong",
        MatchStatus.Partial => "Partial",
        MatchStatus.Ambiguous => "Ambiguous",
        MatchStatus.Unmatched => "Not in library",
        _ => "Cannot compare",
    };

    private static Vector4 StatusColor(MatchStatus status) => status switch
    {
        MatchStatus.Exact or MatchStatus.UserConfirmed => ImGuiColors.HealerGreen,
        MatchStatus.Strong => ImGuiColors.ParsedBlue,
        MatchStatus.Partial => ImGuiColors.DalamudYellow,
        MatchStatus.Ambiguous => ImGuiColors.DalamudOrange,
        _ => ImGuiColors.DalamudRed,
    };

    private static string Explain(PackageMatch m)
    {
        var text = (m.Status == MatchStatus.UserConfirmed ? m.ComputedStatus : m.Status) switch
        {
            MatchStatus.Exact => "Every file of the options used was found on the character with the same content, and no other package explains them.",
            MatchStatus.Strong => "Files match and none contradicts, but not every file of the options used was seen on the character.",
            MatchStatus.Partial => "Some files match, but the character has different content at other paths of the same options.",
            MatchStatus.Ambiguous => "The matching files are also in other packages, so they cannot tell which one is used.",
            _ => string.Empty,
        };
        if (m.Status == MatchStatus.UserConfirmed) text = $"You confirmed this package. Computed: {Describe(m.ComputedStatus)}. " + text;
        return string.Join("\n", new[] { text }.Concat(m.Reasons));
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
