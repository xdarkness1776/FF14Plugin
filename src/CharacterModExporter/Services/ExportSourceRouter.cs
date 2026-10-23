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
            _current = exportIpc;
            return ipc;
        }

        _current = penumbra;
        return penumbra.Probe();
    }

    public string SourceLabel => Current.SourceLabel;
    public string UnavailableMessage => Current.UnavailableMessage;
    public CharacterStateDto GetCharacterState(uint entityId) => Current.GetCharacterState(entityId);
    public bool RequestResync(uint entityId) => Current.RequestResync(entityId);

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
