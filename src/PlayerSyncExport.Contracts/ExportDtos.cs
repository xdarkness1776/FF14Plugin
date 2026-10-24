using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlayerSyncExport.Contracts;

/// <summary>Result of looking up a game object in PlayerSync.</summary>
public enum CharacterLookup
{
    /// <summary>The character is handled by PlayerSync and visible.</summary>
    Found,
    /// <summary>PlayerSync is not connected to its server.</summary>
    NotConnected,
    /// <summary>No PlayerSync pair (direct, syncshell or ZoneSync) is attached to this game object.</summary>
    NotSynced,
    /// <summary>The pair exists but is paused.</summary>
    Paused,
}

/// <summary>Synchronization state of a character as PlayerSync sees it.</summary>
public enum SyncState
{
    /// <summary>PlayerSync has not received character data for this pair yet.</summary>
    NoData,
    /// <summary>Data was received but has not been applied (deferred, paused by thresholds, combat, etc.).</summary>
    NotApplied,
    /// <summary>A download or application task is running.</summary>
    InProgress,
    /// <summary>The last applied data is older than the last received data.</summary>
    Stale,
    /// <summary>The last received data is applied and every file resolved to a local file.</summary>
    Complete,
    /// <summary>The last received data is applied but some files are missing or were excluded.</summary>
    CompleteWithMissing,
}

/// <summary>How a single resource of the character resolved.</summary>
public enum ResourceAvailability
{
    /// <summary>A local file exists for the resource.</summary>
    Available,
    /// <summary>PlayerSync could not obtain the file (download failed, forbidden, not yet downloaded).</summary>
    Missing,
    /// <summary>PlayerSync's file validation excluded the file because it could crash the game.</summary>
    ExcludedByValidation,
    /// <summary>The resource is a redirect to another game path; there is no file to copy.</summary>
    FileSwap,
}

/// <summary>
/// Whether a resource is still in use by the character, as far as the source can tell. Only the stock-PlayerSync
/// source (which watches Penumbra) sets it; the export IPC reports PlayerSync's applied mapping, which is current by
/// definition, and leaves it null.
/// </summary>
public enum ResourceActivity
{
    /// <summary>In Penumbra's latest resource tree for the character: on the character right now.</summary>
    Active,
    /// <summary>
    /// Seen loading for the character (an animation, VFX or sound as it played) but never in its resource tree, which
    /// does not list such files. Probably still part of the character, but this cannot be confirmed.
    /// </summary>
    ObservedLoaded,
    /// <summary>Was on the character earlier in this capture, but is gone from the latest resource tree or was replaced.</summary>
    NoLongerActive,
}

/// <summary>Where the resolved local file lives.</summary>
public enum SourceKind
{
    None,
    PlayerSyncCache,
    PenumbraModDirectory,
    Other,
}

public sealed class CharacterStateDto
{
    public int ApiVersion { get; set; } = ExportIpc.ApiVersion;
    public CharacterLookup Lookup { get; set; }
    public string? PlayerName { get; set; }

    /// <summary>True when the user has an individual (direct) pair with the character.</summary>
    public bool IsDirectlyPaired { get; set; }

    /// <summary>True when the only connection to the character is a ZoneSync group.</summary>
    public bool IsZoneSyncOnly { get; set; }

    /// <summary>True when at least one of the shared groups is a ZoneSync group.</summary>
    public bool SharesZoneSyncGroup { get; set; }

    /// <summary>
    /// PlayerSync's global "join ZoneSync" setting (<c>EnableGroupZoneSyncJoining</c>). Null when unknown. This is the
    /// default only: a per-character override can change it (see <see cref="ZoneSyncJoiningEffective"/>).
    /// </summary>
    public bool? ZoneSyncJoiningGlobal { get; set; }

    /// <summary>
    /// PlayerSync's override of that setting for the character you are logged in as (<c>ZoneSyncEnabledPerCharacter</c>,
    /// keyed by your own character's name). Null when there is none or it is unknown.
    /// </summary>
    public bool? ZoneSyncJoiningOverride { get; set; }

    /// <summary>
    /// Whether PlayerSync joins ZoneSync groups for you now: the override when there is one, otherwise the global
    /// setting (<see cref="ZoneSyncSettings.Effective"/>). It says nothing about this character: whether you share a
    /// ZoneSync group with it is <see cref="SharesZoneSyncGroup"/>, and whether its data is applied is
    /// <see cref="SyncState"/>.
    /// </summary>
    public bool? ZoneSyncJoiningEffective { get; set; }

    /// <summary>The character's home world as the source read it from the game object; null when it could not.</summary>
    public string? HomeWorld { get; set; }

    /// <summary>
    /// False when the source cannot tell how the character is linked (direct pair, syncshell or ZoneSync), so the
    /// link and ZoneSync fields above carry no information. Stock PlayerSync only reports that it handles a character.
    /// </summary>
    public bool LinkDetailsKnown { get; set; } = true;

    public SyncState SyncState { get; set; }
    public string? ReceivedDataHash { get; set; }
    public string? AppliedDataHash { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }

    /// <summary>True when the received data contains Penumbra meta manipulations. They are not exported.</summary>
    public bool HasManipulations { get; set; }

    public List<ResourceDto> Resources { get; set; } = [];

    /// <summary>Human readable notes about filtering or limits that apply to this view.</summary>
    public List<string> Notes { get; set; } = [];
}

public sealed class ResourceDto
{
    /// <summary>Player, MinionOrMount, Pet or Companion.</summary>
    public string ObjectKind { get; set; } = string.Empty;

    /// <summary>
    /// The game paths of the resource. For an available resource these are only the paths PlayerSync handed to
    /// Penumbra; paths it dropped are listed in <see cref="ExcludedGamePaths"/> or <see cref="MissingGamePaths"/>.
    /// </summary>
    public List<string> GamePaths { get; set; } = [];

    /// <summary>Received game paths PlayerSync's file validation removed. Null when there are none.</summary>
    public List<string>? ExcludedGamePaths { get; set; }

    /// <summary>
    /// Received game paths of an otherwise applied resource that are absent from PlayerSync's mapping. Null when
    /// there are none or when nothing of the resource was applied (then <see cref="Availability"/> says so).
    /// </summary>
    public List<string>? MissingGamePaths { get; set; }

    /// <summary>The hash PlayerSync received for this resource (SHA-1 of the file content, uppercase hex).</summary>
    public string? Hash { get; set; }

    /// <summary>
    /// The hash of the file actually used locally. Differs from <see cref="Hash"/> when PlayerSync
    /// substituted a compressed alternate texture.
    /// </summary>
    public string? CacheHash { get; set; }

    public bool IsCompressedAlternate { get; set; }

    /// <summary>For file swaps: the game path the resource is redirected to.</summary>
    public string? FileSwapTarget { get; set; }

    /// <summary>Extension of the cached file without the dot, lowercase.</summary>
    public string Extension { get; set; } = string.Empty;

    public long? Size { get; set; }
    public ResourceAvailability Availability { get; set; }
    public SourceKind SourceKind { get; set; }

    /// <summary>Null when the source does not track it (see <see cref="ResourceActivity"/>).</summary>
    public ResourceActivity? Activity { get; set; }

    /// <summary>When the source first and last saw the resource in this capture; null when it does not track it.</summary>
    public DateTimeOffset? FirstSeenUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }
}

public sealed class ExportRequestDto
{
    public int ApiVersion { get; set; } = ExportIpc.ApiVersion;
    public uint EntityId { get; set; }

    /// <summary>Name the exporter resolved for the entity; the provider rejects the job if it no longer matches.</summary>
    public string ExpectedPlayerName { get; set; } = string.Empty;

    /// <summary>Absolute directory the files are written into. Must already exist.</summary>
    public string DestinationDirectory { get; set; } = string.Empty;

    /// <summary>Cache hashes (see <see cref="ResourceDto.CacheHash"/>) to export.</summary>
    public List<string> CacheHashes { get; set; } = [];

    /// <summary>
    /// The home world the exporter captured with the name. When both sides know it, a different world fails the
    /// job; when the provider cannot read it, the job notes that the world was not confirmed.
    /// </summary>
    public string? ExpectedHomeWorld { get; set; }

    /// <summary>How long to wait for a running synchronization before giving up.</summary>
    public int SyncWaitSeconds { get; set; } = 120;
}

public sealed class ExportStartResultDto
{
    public bool Accepted { get; set; }
    public string? JobId { get; set; }
    public string? Error { get; set; }
}

public enum ExportJobPhase
{
    Queued,
    WaitingForSync,
    Copying,
    Completed,
    CompletedWithIssues,
    Failed,
    Cancelled,
}

public enum FileExportOutcome
{
    Pending,
    Copied,
    AlreadyPresentIdentical,
    NotInCharacterMapping,
    SourceMissing,
    IntegrityMismatch,
    DestinationConflict,
    Failed,
    Cancelled,
}

public sealed class ExportFileResultDto
{
    public string CacheHash { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public FileExportOutcome Outcome { get; set; }
    public string? Error { get; set; }
    public long Size { get; set; }

    /// <summary>True when the SHA-1 of the written file equals <see cref="CacheHash"/>.</summary>
    public bool Sha1Verified { get; set; }

    /// <summary>SHA-256 of the written file, computed by the exporter, recorded separately from PlayerSync's identifier.</summary>
    public string? Sha256 { get; set; }
}

public sealed class ExportJobStatusDto
{
    public string JobId { get; set; } = string.Empty;
    public bool Found { get; set; }
    public ExportJobPhase Phase { get; set; }
    public string? Message { get; set; }
    public int FilesTotal { get; set; }
    public int FilesDone { get; set; }
    public long BytesTotal { get; set; }
    public long BytesDone { get; set; }
    public List<ExportFileResultDto> Files { get; set; } = [];

    /// <summary>The character state the job exported from (taken once synchronization finished).</summary>
    public CharacterStateDto? State { get; set; }

    [JsonIgnore]
    public bool IsFinished => Phase is ExportJobPhase.Completed or ExportJobPhase.CompletedWithIssues
        or ExportJobPhase.Failed or ExportJobPhase.Cancelled;
}

/// <summary>How PlayerSync decides whether it joins ZoneSync groups for the logged-in character.</summary>
public static class ZoneSyncSettings
{
    /// <summary>
    /// Mirrors <c>GroupZoneSyncManager.ScheduleGroupZoneSync</c> (PlayerSync 1.15.5.9): the per-character entry for the
    /// logged-in character's name wins when there is one, otherwise the global setting applies.
    /// </summary>
    /// <returns>The effective setting and the override that decided it (null when the global setting applied).</returns>
    public static (bool Effective, bool? Override) Effective(bool global, IReadOnlyDictionary<string, bool>? perCharacter, string? localPlayerName)
    {
        if (perCharacter != null && !string.IsNullOrEmpty(localPlayerName) && perCharacter.TryGetValue(localPlayerName, out var value))
            return (value, value);
        return (global, null);
    }
}

public static class ExportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
