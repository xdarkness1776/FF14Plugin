using CharacterModExporter.Core.Library;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

/// <summary>
/// State behind one export window: the selected character, the inventory PlayerSync reported, and the
/// background operations (refresh, resync, export). All slow work runs off the caller's thread; the UI
/// only reads the latest published values.
/// </summary>
public sealed class ExportSession : IDisposable
{
    private readonly IPlayerSyncExportApi _api;
    private readonly ExportFolderService _folders;
    private readonly ExportCoordinator _coordinator;
    private readonly Func<ExportSettings> _settings;
    private readonly Func<ExportTarget, Task<bool>> _isPresent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly FileVerificationCache _verification = new();
    private readonly object _gate = new();
    private CancellationTokenSource _cts = new();
    private Task _current = Task.CompletedTask;
    private bool _disposed;
    private readonly string _exporterVersion;

    public ExportSession(ExportTarget target, IPlayerSyncExportApi api, ExportFolderService folders, ExportCoordinator coordinator,
        Func<ExportSettings> settings, Func<ExportTarget, Task<bool>> isPresent, Func<TimeSpan, CancellationToken, Task>? delay = null,
        PackageLibrary? library = null, string exporterVersion = "")
    {
        Library = library;
        _exporterVersion = exporterVersion;
        Target = target;
        _api = api;
        _folders = folders;
        _coordinator = coordinator;
        _settings = settings;
        _isPresent = isPresent;
        _delay = delay ?? Task.Delay;
    }

    public ExportTarget Target { get; }
    public ProviderStatus Provider { get; private set; } = ProviderStatus.NotInstalled;
    public int ProviderVersion { get; private set; }
    public string SourceLabel => _api.SourceLabel;
    public string UnavailableMessage => _api.UnavailableMessage;
    public bool TargetPresent { get; private set; } = true;
    public CharacterStateDto? State { get; private set; }
    public AssetInventory Inventory { get; private set; } = new();
    public ExportOutcome? LastOutcome { get; private set; }
    public ExportProgress? Progress { get; private set; }
    public string? Operation { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy => !_current.IsCompleted;
    public string? PredictedAssetsFolder => TryPath(() => _folders.PredictAssetsFolder(_settings(), Target.Name, Target.World));
    public string CharacterFolder => LastOutcome?.Layout?.CharacterFolder ?? TryPath(() => _folders.CharacterFolder(_settings(), Target.Name, Target.World)) ?? "(export location is not valid)";
    public string ExportRoot => TryPath(() => _folders.ResolveRoot(_settings())) ?? "(export location is not valid)";

    private static string? TryPath(Func<string?> path)
    {
        try
        {
            return path();
        }
        catch (ArgumentException)
        {
            // An invalid saved location; the settings window explains it.
            return null;
        }
    }

    public bool CanExport => !IsBusy && Provider == ProviderStatus.Available && TargetPresent
        && State is { Lookup: CharacterLookup.Found } && Inventory.Assets.Exists(a => a.Selected && a.CanExport);

    public IReadOnlyList<ExportFileResultDto> FailedFiles => LastOutcome?.Files
        .Where(f => f.Outcome is not (FileExportOutcome.Copied or FileExportOutcome.AlreadyPresentIdentical)).ToList() ?? [];

    /// <summary>
    /// Re-reads what the source knows about the character. Read-only: no synchronization, download or reapplication
    /// is started, and a stale mapping is reported as stale (<see cref="Freshness"/>), not refreshed.
    /// </summary>
    public Task RefreshAsync(bool verifyExported = false) => Run("Reading the character's resources.", t => RefreshCoreAsync(verifyExported, t));

    public bool SupportsRescan => _api.SupportsRescan;
    public bool SupportsReapply => _api.SupportsReapply;

    /// <summary>How current <see cref="State"/> is.</summary>
    public MappingFreshness Freshness => Core.Freshness.Of(State);

    /// <summary>
    /// Makes the game load the character's files again (Penumbra redraw) and lists them once loading settles. Local
    /// only: PlayerSync is not asked to synchronize, download or reapply anything. With a source that already reports
    /// the full mapping (the export IPC) this is the same as a refresh.
    /// </summary>
    public Task ScanAsync() => Run("Scanning the character's resources.", async t =>
    {
        if (!await CheckReadyAsync().ConfigureAwait(false)) return;
        if (!_api.SupportsRescan)
        {
            await RefreshCoreAsync(false, t).ConfigureAwait(false);
            return;
        }

        if (!_api.RequestRescan(Target.EntityId))
        {
            LastError = "The character could not be redrawn (not synced by PlayerSync, or no longer nearby).";
            await RefreshCoreAsync(false, t).ConfigureAwait(false);
            return;
        }

        await WaitWhileLoadingAsync("Waiting for the game to load the character's files", t).ConfigureAwait(false);
        await RefreshCoreAsync(false, t).ConfigureAwait(false);
    });

    /// <summary>
    /// Explicit user action: asks PlayerSync to reapply the data it last received for this character, as its own
    /// "reapply" menu entry does. PlayerSync reuses cached files but <b>downloads missing ones</b>. Never called by a
    /// refresh, scan or export.
    /// </summary>
    public Task ReapplyAsync() => Run("Asking PlayerSync to reapply the character's data.", async t =>
    {
        if (!await CheckReadyAsync().ConfigureAwait(false)) return;
        if (!_api.SupportsReapply)
        {
            LastError = "This source cannot ask PlayerSync to reapply data. Use PlayerSync's own menu for that.";
            return;
        }

        if (!_api.RequestReapply(Target.EntityId))
        {
            LastError = "PlayerSync did not accept the request (character not synced, paused, or no data received yet).";
            await RefreshCoreAsync(false, t).ConfigureAwait(false);
            return;
        }

        // Give PlayerSync a moment to start its pipeline before we look at the state.
        await _delay(TimeSpan.FromMilliseconds(500), t).ConfigureAwait(false);
        await WaitWhileLoadingAsync("PlayerSync is applying", t).ConfigureAwait(false);
        await RefreshCoreAsync(false, t).ConfigureAwait(false);
    });

    private async Task WaitWhileLoadingAsync(string what, CancellationToken t)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, _settings().SyncWaitSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            t.ThrowIfCancellationRequested();
            var state = _api.GetCharacterState(Target.EntityId);
            if (state.SyncState is not SyncState.InProgress) return;
            Operation = what + ".";
            await _delay(TimeSpan.FromSeconds(1), t).ConfigureAwait(false);
        }
    }

    public Task ExportSelectedAsync() => Run("Exporting.", async t =>
    {
        var hashes = Inventory.Assets.Where(a => a.Selected && a.CanExport).Select(a => a.CacheHash).ToList();
        await ExportCoreAsync(hashes, null, t).ConfigureAwait(false);
    });

    /// <summary>Exports again only the files that did not succeed last time, into the same folder.</summary>
    public Task RetryFailedAsync() => Run("Retrying failed files.", async t =>
    {
        var failed = FailedFiles.Select(f => f.CacheHash).ToList();
        if (failed.Count == 0) return;
        await ExportCoreAsync(failed, LastOutcome?.Layout, t).ConfigureAwait(false);
    });

    public PackageLibrary? Library { get; }

    /// <summary>The last comparison of the live character with the library.</summary>
    public MatchReport? LiveMatch { get; private set; }

    /// <summary>A saved MCDF snapshot the user opened, and its own comparison. Never merged with <see cref="LiveMatch"/>.</summary>
    public McdfSnapshot? Snapshot { get; private set; }

    public MatchReport? SnapshotMatch { get; private set; }

    /// <summary>The result of the last export of original packages.</summary>
    public List<OriginalPackageCopy>? LastOriginalExport { get; private set; }

    public string? LastOriginalExportFolder { get; private set; }

    public string CharacterKey => PackageOverrideStore.CharacterKey(Target.Name, Target.World);

    /// <summary>Compares the character's current resources with the original package library.</summary>
    public Task MatchLibraryAsync() => Run("Comparing the character with your package library.", async t =>
    {
        if (Library?.Index == null) throw new InvalidOperationException("Index your package library first (Settings, Original Package Library).");
        // Always read the character again: matching a stale list would report packages the character no longer wears.
        await RefreshCoreAsync(false, t).ConfigureAwait(false);
        if (State == null) throw new InvalidOperationException("The character's resources could not be read.");
        LiveMatch = Library.MatchLive(State, CharacterKey);
    });

    /// <summary>Reads a saved MCDF without applying it and compares it with the library as a separate snapshot.</summary>
    public Task OpenSnapshotAsync(string path) => Run("Reading the MCDF snapshot.", t =>
    {
        var snapshot = McdfReader.Read(path, verifyContents: true, t);
        Snapshot = snapshot;
        SnapshotMatch = Library?.MatchSnapshot(snapshot);
        return Task.CompletedTask;
    });

    /// <summary>Shows a message from the window (for example a failed save) the same way operation errors are shown.</summary>
    public void ReportError(string message) => LastError = message;

    public void CloseSnapshot()
    {
        Snapshot = null;
        SnapshotMatch = null;
    }

    /// <summary>Re-applies the user's confirmations to the current reports without reading the character again.</summary>
    public void RecomputeMatches()
    {
        if (Library == null) return;
        if (State != null && LiveMatch != null) LiveMatch = Library.MatchLive(State, CharacterKey);
        if (Snapshot != null && SnapshotMatch != null) SnapshotMatch = Library.MatchSnapshot(Snapshot);
    }

    /// <summary>Copies the chosen original packages, unchanged and verified, into the character's export folder.</summary>
    public Task ExportOriginalsAsync(IReadOnlyList<PackageMatch> packages, bool fromSnapshot) => Run("Copying original packages.", async t =>
    {
        if (Library?.Index == null) throw new InvalidOperationException("Index your package library first.");
        if (packages.Count == 0) throw new InvalidOperationException("Select at least one package.");
        var settings = _settings();
        var layout = LastOutcome?.Layout ?? _folders.Prepare(settings, Target.Name, Target.World, DateTimeOffset.Now, createInstallable: false);
        var bytes = packages.Sum(p => p.Package.Size);
        long done = 0;
        var progress = new SyncProgress<long>(n =>
        {
            done += n;
            // Packages are copied in order, so the finished ones are those whose sizes fit in the bytes copied so far.
            long sum = 0;
            var files = packages.TakeWhile(p => (sum += p.Package.Size) <= done).Count();
            Progress = new ExportProgress("Copying original packages.", files, packages.Count, done, bytes);
        });
        var index = Library.Index;
        var copies = await OriginalPackageExporter.CopyAsync(index.LibraryFolder, layout.SessionFolder, packages,
            p => { var c = Library.Overrides.CategoryOf(p); return (c.Category, c.FromUser); }, progress, t).ConfigureAwait(false);

        foreach (var c in copies)
        {
            c.ExportedAtUtc = DateTimeOffset.UtcNow;
            c.Evidence = fromSnapshot ? "MCDF snapshot" : "Live";
        }

        var report = fromSnapshot ? SnapshotMatch : LiveMatch;
        var manifest = new OriginalPackagesManifest
        {
            ExporterVersion = _exporterVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            CharacterName = Target.Name,
            World = Target.World,
            Evidence = fromSnapshot ? "MCDF snapshot" : "Live",
            SnapshotFile = fromSnapshot ? Snapshot?.FilePath : null,
            EvidenceCoverage = (report?.Coverage ?? MatchCoverage.Unknown).ToString(),
            EvidenceDescription = report?.SourceDescription,
            LibraryFolder = index.LibraryFolder,
            LibraryIndexedAtUtc = index.BuiltAtUtc,
            Packages = OriginalPackagesManifest.KeepEarlier(Path.Combine(layout.SessionFolder, OriginalPackageExporter.FolderName), copies),
            NotFoundInLibrary = report?.Resources.Where(r => r.Status is MatchStatus.Unmatched or MatchStatus.Unresolvable)
                .Select(r => new ManifestUnmatched([.. r.Resource.GamePaths], r.Resource.SourceHash?.Hex, r.Status.ToString(), r.Reason)).ToList() ?? [],
        };
        var folder = Path.Combine(layout.SessionFolder, OriginalPackageExporter.FolderName);
        manifest.Write(folder);
        LastOriginalExport = copies;
        LastOriginalExportFolder = folder;
        var failed = copies.Count(c => c.ExportedFile == null);
        if (failed > 0) LastError = $"{failed} of {copies.Count} package(s) were not copied; see the list for the reason.";
    });

    public void SelectAllAvailable()
    {
        foreach (var row in Inventory.Assets) row.Selected = row.CanExport;
    }

    public void Cancel()
    {
        lock (_gate) _cts.Cancel();
    }

    private async Task ExportCoreAsync(List<string> hashes, ExportLayout? layout, CancellationToken t)
    {
        if (!await CheckReadyAsync().ConfigureAwait(false)) return;
        var progress = new SyncProgress<ExportProgress>(p => { Progress = p; Operation = p.Message; });
        var outcome = await _coordinator.ExportAsync(Target, _settings(), hashes, progress, t, layout).ConfigureAwait(false);
        LastOutcome = outcome;
        if (!outcome.Success) LastError = outcome.Message;
        await RefreshCoreAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> CheckReadyAsync()
    {
        (Provider, ProviderVersion) = _api.Probe();
        if (Provider != ProviderStatus.Available)
        {
            LastError = Provider == ProviderStatus.IncompatibleVersion
                ? $"PlayerSync provides export IPC v{ProviderVersion}; this exporter needs v{ExportIpc.ApiVersion}."
                : _api.UnavailableMessage;
            return false;
        }

        TargetPresent = await _isPresent(Target).ConfigureAwait(false);
        if (!TargetPresent)
        {
            LastError = $"{Target.Name} is no longer nearby (left the area or despawned).";
            return false;
        }

        return true;
    }

    private async Task RefreshCoreAsync(bool verify, CancellationToken t)
    {
        if (!await CheckReadyAsync().ConfigureAwait(false)) return;
        var state = _api.GetCharacterState(Target.EntityId);
        if (state.Lookup == CharacterLookup.Found && TargetIdentity.Compare(Target, state.PlayerName, state.HomeWorld) == TargetIdentity.Result.Different)
        {
            // Never show (or later export) another character's files under this target.
            LastError = "The source reports a different character for this game object. Reopen the window from the context menu.";
            State = null;
            Inventory = new AssetInventory();
            return;
        }

        var previous = Inventory.Assets.ToDictionary(a => a.CacheHash, a => (a.Selected, a.Activity), StringComparer.OrdinalIgnoreCase);
        var inventory = AssetInventory.Build(state);
        foreach (var row in inventory.Assets)
        {
            // Files the character no longer uses start unselected, and are unselected when they stop being used; the
            // user can still tick them. Otherwise the user's choice is kept.
            var inactive = row.Activity == ResourceActivity.NoLongerActive;
            var keep = previous.TryGetValue(row.CacheHash, out var was) && (was.Activity == ResourceActivity.NoLongerActive) == inactive;
            row.Selected = row.CanExport && (keep ? was.Selected : !inactive);
        }

        t.ThrowIfCancellationRequested();
        inventory.CompareWithExport(PredictedAssetsFolder ?? LastOutcome?.Layout?.AssetsFolder, _verification, verify);
        State = state;
        Inventory = inventory;
    }

    private Task Run(string operation, Func<CancellationToken, Task> work)
    {
        lock (_gate)
        {
            if (_disposed || !_current.IsCompleted) return _current;
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Operation = operation;
            LastError = null;
            Progress = null;
            _current = Task.Run(async () =>
            {
                try
                {
                    await work(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LastError = "Cancelled.";
                }
                catch (Exception ex)
                {
                    // IPC errors, PlayerSync unloading mid-operation, file system errors: keep the window usable.
                    LastError = ex.Message;
                }
                finally
                {
                    Operation = null;
                }
            });
            return _current;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
        }

        try
        {
            // Let a running export notify PlayerSync about the cancellation.
            _current.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Shutdown path; errors are already reported through LastError.
        }

        _cts.Dispose();
    }

    /// <summary>IProgress that invokes synchronously; <see cref="Progress{T}"/> would post to the thread pool.</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
