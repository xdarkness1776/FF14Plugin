using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

public enum ProviderStatus
{
    /// <summary>The source can be used.</summary>
    Available,
    /// <summary>The source is not usable: a plugin it needs is not loaded (see <see cref="IPlayerSyncExportApi.UnavailableMessage"/>).</summary>
    NotInstalled,
    /// <summary>A provider answered with a different <see cref="ExportIpc.ApiVersion"/>.</summary>
    IncompatibleVersion,
}

/// <summary>
/// Where the exporter gets a character's files from. Implemented over Penumbra (works with stock PlayerSync),
/// over the optional PlayerSync export IPC, and faked in tests.
/// </summary>
public interface IPlayerSyncExportApi
{
    /// <summary>Returns the source status and the version it reported (0 when none answered).</summary>
    (ProviderStatus Status, int Version) Probe();

    /// <summary>Short description of the source for the window, valid after <see cref="Probe"/>.</summary>
    string SourceLabel { get; }

    /// <summary>Why the source cannot be used, valid after <see cref="Probe"/> returned something other than Available.</summary>
    string UnavailableMessage { get; }

    CharacterStateDto GetCharacterState(uint entityId);
    bool RequestResync(uint entityId);
    ExportStartResultDto StartExport(ExportRequestDto request);
    ExportJobStatusDto GetExportStatus(string jobId);
    bool CancelExport(string jobId);
}
