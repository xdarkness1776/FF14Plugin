using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

/// <summary>How current the character data the window shows is. Separate from how the character is linked.</summary>
public enum MappingFreshness
{
    /// <summary>No mapping: the character is not synced, paused, PlayerSync is not connected, or no data was applied.</summary>
    Unavailable,
    /// <summary>PlayerSync is downloading or applying, or (Penumbra source) files are still appearing.</summary>
    Loading,
    /// <summary>PlayerSync received newer data than it applied: the files shown are the older set.</summary>
    Stale,
    /// <summary>The applied data is the latest received, but some of its files are missing or were excluded.</summary>
    Incomplete,
    /// <summary>The applied data is the latest received and every file is available locally.</summary>
    Current,
    /// <summary>Stock PlayerSync through Penumbra: only the files the game has loaded, settled; completeness is unknown.</summary>
    LoadedOnly,
}

public static class Freshness
{
    public static MappingFreshness Of(CharacterStateDto? state)
    {
        if (state is not { Lookup: CharacterLookup.Found }) return MappingFreshness.Unavailable;
        return state.SyncState switch
        {
            SyncState.NoData or SyncState.NotApplied => MappingFreshness.Unavailable,
            SyncState.InProgress => MappingFreshness.Loading,
            SyncState.Stale => MappingFreshness.Stale,
            _ when !state.LinkDetailsKnown => MappingFreshness.LoadedOnly,
            SyncState.CompleteWithMissing => MappingFreshness.Incomplete,
            _ => MappingFreshness.Current,
        };
    }

    public static string Describe(MappingFreshness freshness) => freshness switch
    {
        MappingFreshness.Current => "current: the latest data PlayerSync received is applied and every file is here",
        MappingFreshness.Incomplete => "current but incomplete: some files are missing or were excluded by PlayerSync",
        MappingFreshness.Stale => "stale: PlayerSync received newer data than it applied; the list shows the older set",
        MappingFreshness.Loading => "loading: PlayerSync (or the game) is still applying files",
        MappingFreshness.LoadedOnly => "files the game has loaded for this character; whether that is all of them is unknown",
        _ => "unavailable: no applied data for this character",
    };
}

/// <summary>Compares a captured target with what a source reports for the same game object.</summary>
public static class TargetIdentity
{
    public enum Result
    {
        Same,
        /// <summary>Same name, and the world is unknown on one side: identified by name and game object only.</summary>
        WorldUnconfirmed,
        Different,
    }

    public static Result Compare(ExportTarget target, string? name, string? world)
    {
        if (!string.Equals(target.Name, name, StringComparison.Ordinal)) return Result.Different;
        if (target.World == null || world == null) return Result.WorldUnconfirmed;
        return string.Equals(target.World, world, StringComparison.OrdinalIgnoreCase) ? Result.Same : Result.Different;
    }
}
