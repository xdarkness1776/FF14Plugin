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

    /// <summary>Read-only: what the source knows about the character now. Never starts a synchronization or a download.</summary>
    CharacterStateDto GetCharacterState(uint entityId);

    /// <summary>True when <see cref="RequestRescan"/> does something (the Penumbra source redraws the character).</summary>
    bool SupportsRescan { get; }

    /// <summary>
    /// Makes the game load the character's files again so they can be observed, without involving PlayerSync's network
    /// side: no synchronization, no download, no reapplication. False when not supported or the character is not synced.
    /// </summary>
    bool RequestRescan(uint entityId);

    /// <summary>True when <see cref="RequestReapply"/> is available (only the PlayerSync export IPC).</summary>
    bool SupportsReapply { get; }

    /// <summary>
    /// Asks PlayerSync to reapply the data it last received for the character. PlayerSync reuses cached files but
    /// <b>downloads missing ones</b>. Only for the explicit user action that says so.
    /// </summary>
    bool RequestReapply(uint entityId);
    ExportStartResultDto StartExport(ExportRequestDto request);
    ExportJobStatusDto GetExportStatus(string jobId);
    bool CancelExport(string jobId);
}
