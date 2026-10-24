namespace PlayerSyncExport.Contracts;

/// <summary>
/// Names and version of the PlayerSync export IPC.
///
/// The IPC is provided by a PlayerSync build that carries the export patch in
/// <c>playersync-fork/</c> of this repository. Stock PlayerSync does not register these
/// gates. All payloads are JSON strings so that no types cross the plugin load contexts.
/// </summary>
public static class ExportIpc
{
    /// <summary>
    /// Bumped whenever a gate signature or a DTO changes incompatibly.
    /// Additive DTO fields do not bump the version.
    /// <para>
    /// 2: <c>RequestResync</c> is gone. Reading a character (<see cref="GetCharacterState"/>) never starts a
    /// synchronization; reapplying PlayerSync's data is the separate <see cref="RequestReapply"/>, which may download.
    /// <c>CharacterStateDto.ZoneSyncEnabled</c> was replaced by fields that say which ZoneSync setting they are.
    /// </para>
    /// </summary>
    public const int ApiVersion = 2;

    /// <summary><c>Func&lt;int&gt;</c>: returns <see cref="ApiVersion"/> of the provider.</summary>
    public const string GetApiVersion = "PlayerSync.Export.GetApiVersion";

    /// <summary>
    /// <c>Func&lt;uint, string&gt;</c>: takes the game object's EntityId and returns a
    /// <see cref="CharacterStateDto"/> as JSON. Read-only: it reports the mapping PlayerSync last applied and how it
    /// relates to the data last received (see <see cref="SyncState"/>), and never starts a synchronization, a
    /// download or a reapplication.
    /// </summary>
    public const string GetCharacterState = "PlayerSync.Export.GetCharacterState";

    /// <summary>
    /// <c>Func&lt;uint, bool&gt;</c>: asks PlayerSync to run its "reapply last received data" pipeline for the
    /// character, the same as its own context-menu entry. Cached files are reused, but files missing from the cache
    /// <b>are downloaded</b>. Only for an explicit user action that says so; never part of reading or exporting.
    /// </summary>
    public const string RequestReapply = "PlayerSync.Export.RequestReapply";

    /// <summary>
    /// <c>Func&lt;string, string&gt;</c>: takes an <see cref="ExportRequestDto"/> as JSON and returns an
    /// <see cref="ExportStartResultDto"/> as JSON.
    /// </summary>
    public const string StartExport = "PlayerSync.Export.StartExport";

    /// <summary><c>Func&lt;string, string&gt;</c>: takes a job id and returns an <see cref="ExportJobStatusDto"/> as JSON.</summary>
    public const string GetExportStatus = "PlayerSync.Export.GetExportStatus";

    /// <summary><c>Func&lt;string, bool&gt;</c>: requests cancellation of a job.</summary>
    public const string CancelExport = "PlayerSync.Export.CancelExport";
}
