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
    /// </summary>
    public const int ApiVersion = 1;

    /// <summary><c>Func&lt;int&gt;</c>: returns <see cref="ApiVersion"/> of the provider.</summary>
    public const string GetApiVersion = "PlayerSync.Export.GetApiVersion";

    /// <summary>
    /// <c>Func&lt;uint, string&gt;</c>: takes the game object's EntityId and returns a
    /// <see cref="CharacterStateDto"/> as JSON.
    /// </summary>
    public const string GetCharacterState = "PlayerSync.Export.GetCharacterState";

    /// <summary>
    /// <c>Func&lt;uint, bool&gt;</c>: asks PlayerSync to run its normal "reapply last received data"
    /// pipeline for the character. Cached files are reused; only missing files are downloaded.
    /// </summary>
    public const string RequestResync = "PlayerSync.Export.RequestResync";

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
