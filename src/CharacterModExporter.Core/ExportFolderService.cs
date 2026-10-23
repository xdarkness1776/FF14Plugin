using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

/// <summary>The folders one export writes into. All of them lie inside <see cref="Root"/>.</summary>
public sealed record ExportLayout(string Root, string CharacterFolder, string SessionFolder, string AssetsFolder, string InstallableFolder)
{
    public string ManifestPath => Path.Combine(SessionFolder, "Manifest.json");
    public string ReadmePath => Path.Combine(SessionFolder, "README.txt");

    /// <summary>One Penumbra mod per item, in a folder per category.</summary>
    public string ModsFolder => Path.Combine(SessionFolder, ExportFolderService.ModsFolderName);
}

/// <summary>
/// Resolves and creates the export folder hierarchy:
/// <c>{parent}/{root name}/{Character_World}/[session]/Assets|Installable</c>.
/// </summary>
public sealed class ExportFolderService
{
    public const string AssetsFolderName = "Assets";
    public const string InstallableFolderName = "Installable";
    public const string ModsFolderName = "Mods";
    public const string SharedAssetsFolderName = "_SharedAssets";

    private readonly Func<string> _documentsProvider;
    private readonly Func<IEnumerable<string?>> _forbiddenRoots;

    /// <param name="documentsProvider">Returns the user's Documents folder; defaults to <see cref="Environment.SpecialFolder.MyDocuments"/>.</param>
    /// <param name="forbiddenRoots">Folders an export may never be inside (PlayerSync cache, Penumbra mods, game, plugin folders).</param>
    public ExportFolderService(Func<IEnumerable<string?>> forbiddenRoots, Func<string>? documentsProvider = null)
    {
        _forbiddenRoots = forbiddenRoots;
        _documentsProvider = documentsProvider ?? DocumentsOrHome;
    }

    public string DefaultParentFolder => _documentsProvider();

    private static string DocumentsOrHome()
    {
        // Resolved through the OS (handles redirected/OneDrive Documents). Some systems report no Documents folder.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : documents;
    }

    public string ResolveParent(ExportSettings settings)
        => string.IsNullOrWhiteSpace(settings.ParentFolder) ? DefaultParentFolder : settings.ParentFolder!;

    public static string ResolveRootName(ExportSettings settings)
        => ExportPathPolicy.SanitizeSegment(settings.RootFolderName, ExportSettings.DefaultRootFolderName);

    /// <summary>The export root, e.g. <c>D:\My FFXIV Mods\FFXIV Character Mod Exports</c>. Does not touch the disk.</summary>
    public string ResolveRoot(ExportSettings settings)
        => ExportPathPolicy.CombineWithin(ResolveParent(settings), ResolveRootName(settings));

    /// <summary>
    /// Checks that a parent folder can be used. Returns null when it can, otherwise a message for the user.
    /// The parent itself must exist (we never create drives or arbitrary parents); the root below it is created on demand.
    /// </summary>
    public string? ValidateParent(string? parent)
    {
        var error = ExportPathPolicy.ValidateDestination(parent, _forbiddenRoots());
        if (error != null) return error;
        if (!Directory.Exists(parent))
        {
            var root = Path.GetPathRoot(parent!);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                return $"The drive '{root}' is not available. Connect it or choose another folder.";
            return $"'{parent}' does not exist. Choose an existing folder; the export folder is created inside it.";
        }

        var probe = Path.Combine(parent!, ".cmx-write-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Cannot write to '{parent}': {ex.Message}";
        }

        return null;
    }

    /// <summary>Folder name for a character: <c>Name_World</c>, sanitized.</summary>
    public static string CharacterFolderName(string characterName, string? world)
    {
        var name = ExportPathPolicy.SanitizeSegment(characterName, "UnknownCharacter", 48);
        var worldPart = ExportPathPolicy.SanitizeSegment(string.IsNullOrWhiteSpace(world) ? "UnknownWorld" : world, "UnknownWorld", 24);
        return name.Replace(' ', '_') + "_" + worldPart.Replace(' ', '_');
    }

    /// <summary>
    /// Validates the configured location and creates the root, character, session, Assets and Installable folders.
    /// Never deletes or moves anything. Throws <see cref="ExportFolderException"/> with a user-facing message on failure.
    /// </summary>
    public ExportLayout Prepare(ExportSettings settings, string characterName, string? world, DateTimeOffset now, bool createInstallable)
    {
        var parent = ResolveParent(settings);
        var error = ValidateParent(parent);
        if (error != null) throw new ExportFolderException(error);

        var root = ResolveRoot(settings);
        var character = ExportPathPolicy.CombineWithin(root, CharacterFolderName(characterName, world));
        var session = settings.ReuseCharacterFolder
            ? character
            : UniqueChild(character, now.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture));
        var assets = settings.StorageMode == AssetStorageMode.SharedPool
            ? ExportPathPolicy.CombineWithin(root, SharedAssetsFolderName)
            : ExportPathPolicy.CombineWithin(session, AssetsFolderName);
        var installable = ExportPathPolicy.CombineWithin(session, InstallableFolderName);

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(character);
            Directory.CreateDirectory(session);
            Directory.CreateDirectory(assets);
            if (createInstallable) Directory.CreateDirectory(installable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExportFolderException($"Could not create the export folders in '{root}': {ex.Message}", ex);
        }

        return new ExportLayout(root, character, session, assets, installable);
    }

    /// <summary>Folder of the character, without creating anything.</summary>
    public string CharacterFolder(ExportSettings settings, string characterName, string? world)
        => ExportPathPolicy.CombineWithin(ResolveRoot(settings), CharacterFolderName(characterName, world));

    /// <summary>
    /// The Assets folder the next export will write to, if it is known in advance (reused character folder or shared pool).
    /// Null for timestamped sessions. Does not create anything.
    /// </summary>
    public string? PredictAssetsFolder(ExportSettings settings, string characterName, string? world)
    {
        if (settings.StorageMode == AssetStorageMode.SharedPool)
            return ExportPathPolicy.CombineWithin(ResolveRoot(settings), SharedAssetsFolderName);
        return settings.ReuseCharacterFolder
            ? ExportPathPolicy.CombineWithin(CharacterFolder(settings, characterName, world), AssetsFolderName)
            : null;
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>, or null if unknown.</summary>
    public static long? AvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string UniqueChild(string parent, string name)
    {
        var candidate = ExportPathPolicy.CombineWithin(parent, name);
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = ExportPathPolicy.CombineWithin(parent, $"{name}_{i}");
        return candidate;
    }
}

public sealed class ExportFolderException : Exception
{
    public ExportFolderException(string message) : base(message) { }
    public ExportFolderException(string message, Exception inner) : base(message, inner) { }
}
