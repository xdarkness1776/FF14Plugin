using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

/// <summary>The character the user right-clicked, captured at click time.</summary>
public sealed record ExportTarget(uint EntityId, string Name, string? World);

public sealed record ExportProgress(string Message, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

public sealed record ExportOutcome(
    bool Success,
    ExportJobPhase Phase,
    string Message,
    ExportLayout? Layout,
    ExportManifest? Manifest,
    IReadOnlyList<ExportFileResultDto> Files);

/// <summary>
/// Runs one export: folder preparation, the PlayerSync export job (which waits for synchronization and
/// copies the cache files), manifest and report, and optionally the Penumbra package.
/// UI-independent so it can be tested without the game.
/// </summary>
public sealed class ExportCoordinator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    private readonly IPlayerSyncExportApi _api;
    private readonly ExportFolderService _folders;
    private readonly string _exporterVersion;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;

    public ExportCoordinator(IPlayerSyncExportApi api, ExportFolderService folders, string exporterVersion,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null)
    {
        _api = api;
        _folders = folders;
        _exporterVersion = exporterVersion;
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ExportOutcome> ExportAsync(ExportTarget target, ExportSettings settings, IReadOnlyCollection<string> selectedCacheHashes,
        IProgress<ExportProgress>? progress, CancellationToken token, ExportLayout? reuseLayout = null)
    {
        var (status, version) = _api.Probe();
        if (status != ProviderStatus.Available)
        {
            return Fail(status == ProviderStatus.IncompatibleVersion
                ? $"PlayerSync provides export IPC v{version}, this exporter needs v{ExportIpc.ApiVersion}. Update both to matching versions."
                : _api.UnavailableMessage);
        }

        var hashes = selectedCacheHashes.Where(ExportPathPolicy.IsSha1Hex).Select(h => h.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count == 0) return Fail("No exportable files are selected.");

        progress?.Report(new("Preparing export folders.", 0, hashes.Count, 0, 0));
        ExportLayout layout;
        try
        {
            layout = reuseLayout ?? _folders.Prepare(settings, target.Name, target.World, _clock(), createInstallable: false);
        }
        catch (ExportFolderException ex)
        {
            return Fail(ex.Message);
        }

        // Space check with what we know before the job runs; the provider re-reads sizes later.
        var preState = _api.GetCharacterState(target.EntityId);
        var knownBytes = preState.Resources.Where(r => r.CacheHash != null && hashes.Contains(r.CacheHash.ToUpperInvariant(), StringComparer.Ordinal))
            .GroupBy(r => r.CacheHash!.ToUpperInvariant(), StringComparer.Ordinal).Sum(g => g.First().Size ?? 0);
        var free = ExportFolderService.AvailableFreeSpace(layout.AssetsFolder);
        var needed = settings.BuildPenumbraPackage ? knownBytes * 2 : knownBytes;
        if (free.HasValue && needed > 0 && free.Value < needed + (needed / 10))
            return Fail($"Not enough free space: about {FormatBytes(needed)} needed, {FormatBytes(free.Value)} available.", layout);

        VerifiedFileCopier.CleanupTempFiles(layout.AssetsFolder);

        var start = _api.StartExport(new ExportRequestDto
        {
            EntityId = target.EntityId,
            ExpectedPlayerName = target.Name,
            DestinationDirectory = layout.AssetsFolder,
            CacheHashes = hashes,
            SyncWaitSeconds = settings.SyncWaitSeconds,
        });
        if (!start.Accepted || start.JobId == null) return Fail(start.Error ?? "PlayerSync refused the export.", layout);

        var final = await PollAsync(start.JobId, progress, token).ConfigureAwait(false);
        if (!final.Found) return Fail("PlayerSync lost track of the export job (was PlayerSync reloaded?).", layout);

        var manifest = BuildManifest(target, settings, layout, final, hashes);
        if (final.State != null && settings.BuildPenumbraPackage)
            TryBuildPackage(target, layout, final, manifest);

        try
        {
            ManifestWriter.WriteAtomic(layout.ManifestPath, ManifestWriter.ToJson(manifest));
            ManifestWriter.WriteAtomic(layout.ReadmePath, ManifestWriter.BuildReadme(manifest, layout.SessionFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, final.Phase, "Files were exported but the manifest could not be written: " + ex.Message, layout, manifest, final.Files);
        }

        var success = final.Phase == ExportJobPhase.Completed;
        return new(success, final.Phase, final.Message ?? final.Phase.ToString(), layout, manifest, final.Files);
    }

    private async Task<ExportJobStatusDto> PollAsync(string jobId, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        var cancelSent = false;
        DateTimeOffset? cancelDeadline = null;
        while (true)
        {
            var status = _api.GetExportStatus(jobId);
            if (!status.Found || status.IsFinished) return status;

            progress?.Report(new(status.Message ?? status.Phase.ToString(), status.FilesDone, status.FilesTotal, status.BytesDone, status.BytesTotal));

            if (token.IsCancellationRequested && !cancelSent)
            {
                _api.CancelExport(jobId);
                cancelSent = true;
                cancelDeadline = _clock() + CancelGrace;
            }

            if (cancelDeadline.HasValue && _clock() > cancelDeadline.Value)
                return new ExportJobStatusDto { JobId = jobId, Found = true, Phase = ExportJobPhase.Cancelled, Message = "Cancelled; PlayerSync did not confirm in time." };

            // Not passing the token: after cancelling we keep polling until PlayerSync confirms.
            await _delay(PollInterval, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ExportManifest BuildManifest(ExportTarget target, ExportSettings settings, ExportLayout layout, ExportJobStatusDto final, List<string> requested)
    {
        var state = final.State ?? new CharacterStateDto();
        var inventory = AssetInventory.Build(state);
        var results = final.Files.ToDictionary(f => f.CacheHash, StringComparer.OrdinalIgnoreCase);
        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var manifest = new ExportManifest
        {
            ExporterVersion = _exporterVersion,
            ExportedAtUtc = _clock(),
            CharacterName = target.Name,
            World = target.World,
            Result = final.Phase.ToString(),
            ResultMessage = final.Message,
            AssetsFolder = Path.GetRelativePath(layout.SessionFolder, layout.AssetsFolder),
            Sync = new ManifestSync
            {
                IsDirectlyPaired = state.IsDirectlyPaired,
                IsZoneSyncOnly = state.IsZoneSyncOnly,
                SharesZoneSyncGroup = state.SharesZoneSyncGroup,
                SyncState = final.State == null ? "Unknown (export did not reach the copy phase)" : state.SyncState.ToString(),
                AppliedDataHash = state.AppliedDataHash,
                AppliedAtUtc = state.AppliedAtUtc,
                HasMetaManipulations = state.HasManipulations,
            },
            Notes = [.. state.Notes],
        };

        foreach (var row in inventory.Assets)
        {
            results.TryGetValue(row.CacheHash, out var r);
            manifest.Assets.Add(new ManifestAsset
            {
                CacheHash = row.CacheHash,
                SourceHash = row.SourceHash,
                IsCompressedAlternate = row.IsCompressedAlternate,
                Extension = row.Extension,
                GamePaths = [.. row.GamePaths],
                ObjectKinds = [.. row.ObjectKinds],
                ExportedFile = r != null && r.Outcome is FileExportOutcome.Copied or FileExportOutcome.AlreadyPresentIdentical
                    ? Path.Combine(manifest.AssetsFolder, r.FileName)
                    : null,
                Size = r?.Size > 0 ? r.Size : row.Size,
                SourceKind = row.SourceKind.ToString(),
                Selected = requestedSet.Contains(row.CacheHash),
                Outcome = r?.Outcome.ToString() ?? (requestedSet.Contains(row.CacheHash) ? FileExportOutcome.Pending.ToString() : "NotSelected"),
                Error = r?.Error,
                Sha1Verified = r?.Sha1Verified ?? false,
                Sha256 = r?.Sha256,
            });
        }

        // Files that were requested but are not in the final mapping any more.
        foreach (var r in final.Files.Where(f => !inventory.Assets.Exists(a => string.Equals(a.CacheHash, f.CacheHash, StringComparison.OrdinalIgnoreCase))))
        {
            manifest.Assets.Add(new ManifestAsset { CacheHash = r.CacheHash, Selected = true, Outcome = r.Outcome.ToString(), Error = r.Error });
        }

        manifest.FileSwaps.AddRange(inventory.FileSwaps.Select(s => new ManifestFileSwap(s.ObjectKind, [.. s.GamePaths], s.FileSwapTarget)));
        manifest.Unresolved.AddRange(inventory.Unresolved.Select(u => new ManifestUnresolved(u.ObjectKind, [.. u.GamePaths], u.Hash,
            u.Availability == ResourceAvailability.ExcludedByValidation
                ? "Excluded by PlayerSync's file validation (could crash the game)."
                : "PlayerSync has no local file for this resource (not downloaded, failed or forbidden).")));
        if (settings.StorageMode == AssetStorageMode.SharedPool)
            manifest.Notes.Add("Files are stored in the shared pool folder and may be referenced by other characters' manifests.");
        return manifest;
    }

    private static void TryBuildPackage(ExportTarget target, ExportLayout layout, ExportJobStatusDto final, ExportManifest manifest)
    {
        var state = final.State!;
        string? reason = null;
        if (final.Phase != ExportJobPhase.Completed) reason = "not every selected file was exported";
        else if (state.SyncState != SyncState.Complete) reason = $"the character's synchronization is {state.SyncState}, so some resources are missing";
        else if (manifest.Assets.Exists(a => !a.Selected)) reason = "not every available file was selected";

        if (reason != null)
        {
            manifest.Notes.Add($"No Penumbra package was built because {reason}. A package must contain the full mapping.");
            return;
        }

        var files = manifest.Assets.Where(a => a.ExportedFile != null)
            .Select(a => new PackageFile(Path.GetFullPath(Path.Combine(layout.SessionFolder, a.ExportedFile!)), Path.GetFileName(a.ExportedFile!), a.GamePaths))
            .ToList();
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in manifest.FileSwaps.Where(s => s.Target != null))
        {
            foreach (var gp in s.GamePaths) swaps.TryAdd(gp, s.Target!);
        }

        var description = $"Resources PlayerSync applied to {target.Name}{(target.World != null ? " @ " + target.World : string.Empty)}, exported {manifest.ExportedAtUtc:yyyy-MM-dd}. " +
            "Contains file replacements and swaps only; no meta manipulations, Glamourer or Customize+ data.";
        var result = PenumbraPackageBuilder.Build(layout.InstallableFolder, $"{target.Name} (exported)", description,
            manifest.ExportedAtUtc.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture), files, swaps);

        if (!result.Created)
        {
            manifest.Notes.Add("The Penumbra package could not be built: " + string.Join(" ", result.Problems));
            return;
        }

        manifest.InstallablePackages.Add(new ManifestPackage(Path.GetRelativePath(layout.SessionFolder, result.PackagePath!), "Penumbra mod package (.pmp), mod file version 3",
            result.FileCount, result.RedirectCount, result.SwapCount,
            "Structure checked after writing (meta.json, default_mod.json, game paths, every file present). Import in Penumbra has not been verified by this tool.",
            result.Omitted));
        foreach (var asset in manifest.Assets.Where(a => a.ExportedFile != null)) asset.InInstallablePackage = true;
        if (manifest.Sync.HasMetaManipulations)
            manifest.Notes.Add("The package omits the character's meta manipulations, so some items may look different than in game.");
    }

    private static ExportOutcome Fail(string message, ExportLayout? layout = null)
        => new(false, ExportJobPhase.Failed, message, layout, null, []);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
