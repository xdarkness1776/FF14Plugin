using System.Collections.Concurrent;
using CharacterModExporter.Core;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Services;

/// <summary>
/// Picks the source on every probe: the PlayerSync export IPC when a PlayerSync build provides it (complete mapping,
/// real sync state), otherwise Penumbra next to stock PlayerSync. Jobs stay with the source that started them.
/// </summary>
public sealed class ExportSourceRouter(IPlayerSyncExportApi exportIpc, IPlayerSyncExportApi penumbra) : IPlayerSyncExportApi
{
    private readonly ConcurrentDictionary<string, IPlayerSyncExportApi> _jobs = new(StringComparer.Ordinal);
    private IPlayerSyncExportApi? _current;

    private IPlayerSyncExportApi Current
    {
        get
        {
            if (_current == null) Probe();
            return _current!;
        }
    }

    public (ProviderStatus Status, int Version) Probe()
    {
        var ipc = exportIpc.Probe();
        if (ipc.Status == ProviderStatus.Available)
        {
            _ignoredIpcVersion = null;
            _current = exportIpc;
            return ipc;
        }

        // An export IPC of another version is never called: its gates may mean something else (v1's resync downloads).
        _ignoredIpcVersion = ipc.Status == ProviderStatus.IncompatibleVersion ? ipc.Version : null;
        _current = penumbra;
        return penumbra.Probe();
    }

    private int? _ignoredIpcVersion;

    public string SourceLabel => Current.SourceLabel + (_ignoredIpcVersion is { } v && Current == penumbra
        ? $" (a PlayerSync export IPC v{v} is loaded but not compatible with v{ExportIpc.ApiVersion}; it is not used)"
        : string.Empty);
    public string UnavailableMessage => Current.UnavailableMessage;
    public CharacterStateDto GetCharacterState(uint entityId) => Current.GetCharacterState(entityId);
    public bool SupportsRescan => Current.SupportsRescan;
    public bool RequestRescan(uint entityId) => Current.RequestRescan(entityId);
    public bool SupportsReapply => Current.SupportsReapply;
    public bool RequestReapply(uint entityId) => Current.RequestReapply(entityId);

    public ExportStartResultDto StartExport(ExportRequestDto request)
    {
        var source = Current;
        var result = source.StartExport(request);
        if (result.Accepted && result.JobId != null) _jobs[result.JobId] = source;
        return result;
    }

    public ExportJobStatusDto GetExportStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var source) ? source.GetExportStatus(jobId) : new ExportJobStatusDto { JobId = jobId };

    public bool CancelExport(string jobId) => _jobs.TryGetValue(jobId, out var source) && source.CancelExport(jobId);
}
