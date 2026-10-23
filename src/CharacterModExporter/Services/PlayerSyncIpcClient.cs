using CharacterModExporter.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Services;

/// <summary>
/// Talks to PlayerSync over Dalamud IPC: the export gates added by the patched build, plus the
/// existing <c>PlayerSync.GetHandledAddresses</c> gate that stock PlayerSync already provides.
/// </summary>
public sealed class PlayerSyncIpcClient : IPlayerSyncExportApi
{
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<int> _getApiVersion;
    private readonly ICallGateSubscriber<uint, string> _getCharacterState;
    private readonly ICallGateSubscriber<uint, bool> _requestResync;
    private readonly ICallGateSubscriber<string, string> _startExport;
    private readonly ICallGateSubscriber<string, string> _getExportStatus;
    private readonly ICallGateSubscriber<string, bool> _cancelExport;
    private readonly ICallGateSubscriber<List<nint>> _handledAddresses;

    public PlayerSyncIpcClient(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _getApiVersion = pi.GetIpcSubscriber<int>(ExportIpc.GetApiVersion);
        _getCharacterState = pi.GetIpcSubscriber<uint, string>(ExportIpc.GetCharacterState);
        _requestResync = pi.GetIpcSubscriber<uint, bool>(ExportIpc.RequestResync);
        _startExport = pi.GetIpcSubscriber<string, string>(ExportIpc.StartExport);
        _getExportStatus = pi.GetIpcSubscriber<string, string>(ExportIpc.GetExportStatus);
        _cancelExport = pi.GetIpcSubscriber<string, bool>(ExportIpc.CancelExport);
        _handledAddresses = pi.GetIpcSubscriber<List<nint>>("PlayerSync.GetHandledAddresses");
    }

    public (ProviderStatus Status, int Version) Probe()
    {
        try
        {
            var version = _getApiVersion.InvokeFunc();
            return (version == ExportIpc.ApiVersion ? ProviderStatus.Available : ProviderStatus.IncompatibleVersion, version);
        }
        catch (IpcNotReadyError)
        {
            return (ProviderStatus.NotInstalled, 0);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Export IPC probe failed");
            return (ProviderStatus.NotInstalled, 0);
        }
    }

    /// <summary>Addresses of characters stock PlayerSync is handling, or null when PlayerSync is not loaded.</summary>
    public IReadOnlySet<nint>? GetHandledAddresses()
    {
        try
        {
            return _handledAddresses.InvokeFunc().ToHashSet();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public CharacterStateDto GetCharacterState(uint entityId)
        => ExportJson.Deserialize<CharacterStateDto>(_getCharacterState.InvokeFunc(entityId)) ?? new CharacterStateDto { Lookup = CharacterLookup.NotSynced };

    public bool RequestResync(uint entityId) => _requestResync.InvokeFunc(entityId);

    public ExportStartResultDto StartExport(ExportRequestDto request)
        => ExportJson.Deserialize<ExportStartResultDto>(_startExport.InvokeFunc(ExportJson.Serialize(request)))
           ?? new ExportStartResultDto { Error = "PlayerSync returned no answer." };

    public ExportJobStatusDto GetExportStatus(string jobId)
        => ExportJson.Deserialize<ExportJobStatusDto>(_getExportStatus.InvokeFunc(jobId)) ?? new ExportJobStatusDto { JobId = jobId };

    public bool CancelExport(string jobId) => _cancelExport.InvokeFunc(jobId);
}
