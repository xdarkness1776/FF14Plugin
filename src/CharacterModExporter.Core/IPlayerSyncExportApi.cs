using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

public enum ProviderStatus
{
    /// <summary>The export IPC answered with a compatible version.</summary>
    Available,
    /// <summary>No provider answered: PlayerSync is not loaded, or it is a build without the export IPC.</summary>
    NotInstalled,
    /// <summary>A provider answered with a different <see cref="ExportIpc.ApiVersion"/>.</summary>
    IncompatibleVersion,
}

/// <summary>The exporter's view of the PlayerSync export IPC. Implemented over Dalamud IPC in the plugin and faked in tests.</summary>
public interface IPlayerSyncExportApi
{
    /// <summary>Returns the provider status and the version it reported (0 when none answered).</summary>
    (ProviderStatus Status, int Version) Probe();

    CharacterStateDto GetCharacterState(uint entityId);
    bool RequestResync(uint entityId);
    ExportStartResultDto StartExport(ExportRequestDto request);
    ExportJobStatusDto GetExportStatus(string jobId);
    bool CancelExport(string jobId);
}
