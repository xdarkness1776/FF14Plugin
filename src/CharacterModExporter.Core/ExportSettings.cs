namespace CharacterModExporter.Core;

public enum AssetStorageMode
{
    /// <summary>Every character folder gets its own <c>Assets</c> copy. Self-contained, may duplicate shared files.</summary>
    PerCharacter,

    /// <summary>Files go to <c>{root}/_SharedAssets</c> once; character manifests reference them.</summary>
    SharedPool,
}

/// <summary>User-facing export settings, persisted through the plugin configuration.</summary>
public sealed class ExportSettings
{
    public const string DefaultRootFolderName = "FFXIV Character Mod Exports";

    /// <summary>Parent folder chosen by the user. Null means the Documents folder.</summary>
    public string? ParentFolder { get; set; }

    /// <summary>Name of the folder created inside <see cref="ParentFolder"/>.</summary>
    public string RootFolderName { get; set; } = DefaultRootFolderName;

    /// <summary>When false, each export goes into a new timestamped folder inside the character folder.</summary>
    public bool ReuseCharacterFolder { get; set; } = true;

    public AssetStorageMode StorageMode { get; set; } = AssetStorageMode.PerCharacter;

    /// <summary>Also build a Penumbra mod package (.pmp) when the mapping allows it.</summary>
    public bool BuildPenumbraPackage { get; set; } = true;

    /// <summary>Also build one Penumbra mod per item (gear piece, hair, animations, ...) under Mods/&lt;category&gt;.</summary>
    public bool BuildItemMods { get; set; } = true;

    /// <summary>How long the export waits for PlayerSync to finish a running synchronization.</summary>
    public int SyncWaitSeconds { get; set; } = 120;

    /// <summary>Folder with the user's original (numbered) .pmp packages. Only read, never written. Null when not set.</summary>
    public string? LibraryFolder { get; set; }

    /// <summary>Only offer the context menu entry on characters PlayerSync currently handles.</summary>
    public bool OnlyShowForSyncedCharacters { get; set; } = true;
}
