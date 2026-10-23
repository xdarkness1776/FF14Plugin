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

    public ExportSession(ExportTarget target, IPlayerSyncExportApi api, ExportFolderService folders, ExportCoordinator coordinator,
        Func<ExportSettings> settings, Func<ExportTarget, Task<bool>> isPresent, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
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

    /// <summary>Re-reads the character's current mapping from PlayerSync. Does not ask PlayerSync to download anything.</summary>
    public Task RefreshAsync(bool verifyExported = false) => Run("Reading character resources from PlayerSync.", t => RefreshCoreAsync(verifyExported, t));

    /// <summary>
    /// Asks PlayerSync to re-run its normal pipeline for this character (reapply last received data):
    /// it recomputes the mapping, reuses cached files and downloads only what is missing. Then refreshes.
    /// </summary>
    public Task ResyncAsync() => Run("Asking PlayerSync to resynchronize the character.", async t =>
    {
        if (!await CheckReadyAsync().ConfigureAwait(false)) return;
        if (!_api.RequestResync(Target.EntityId))
        {
            LastError = "PlayerSync did not accept the resync request (character not synced, paused, or no data received yet).";
            await RefreshCoreAsync(false, t).ConfigureAwait(false);
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, _settings().SyncWaitSeconds));
        // Give PlayerSync a moment to start its pipeline before we look at the state.
        await _delay(TimeSpan.FromMilliseconds(500), t).ConfigureAwait(false);
        while (DateTimeOffset.UtcNow < deadline)
        {
            t.ThrowIfCancellationRequested();
            var state = _api.GetCharacterState(Target.EntityId);
            Operation = $"PlayerSync is synchronizing: {state.SyncState}.";
            if (state.SyncState is not SyncState.InProgress) break;
            await _delay(TimeSpan.FromSeconds(1), t).ConfigureAwait(false);
        }

        await RefreshCoreAsync(false, t).ConfigureAwait(false);
    });

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
        if (state.Lookup == CharacterLookup.Found && !string.Equals(state.PlayerName, Target.Name, StringComparison.Ordinal))
        {
            LastError = "PlayerSync reports a different character for this game object. Reopen the window from the context menu.";
            State = null;
            Inventory = new AssetInventory();
            return;
        }

        var previous = Inventory.Assets.ToDictionary(a => a.CacheHash, a => a.Selected, StringComparer.OrdinalIgnoreCase);
        var inventory = AssetInventory.Build(state);
        foreach (var row in inventory.Assets)
            row.Selected = row.CanExport && (previous.Count == 0 || previous.GetValueOrDefault(row.CacheHash, true));

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
