using System.Text;

namespace PlayerSyncExport.Contracts;

/// <summary>
/// Path rules shared by the exporter and the PlayerSync export provider.
/// Everything that ends up on disk goes through here so that remote metadata
/// (names, extensions, hashes) can never steer a write outside the chosen folder.
/// </summary>
public static class ExportPathPolicy
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Windows forbids these everywhere; we apply the same set on every OS so exports are portable.
    private static readonly char[] InvalidNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>True for a 40 character hex string, the format of PlayerSync's SHA-1 file hashes.</summary>
    public static bool IsSha1Hex(string? value)
    {
        if (value is null || value.Length != 40) return false;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>Returns a lowercase alphanumeric extension (max 16 chars) or null if nothing usable remains.</summary>
    /// <summary>
    /// True for files that are never a finished game resource: PlayerSync's download leftovers (<c>&lt;hash&gt;.bin</c>,
    /// still LZ4-wrapped), its MCDF extraction files (<c>mare_N.tmp</c>), and partial copies. PlayerSync's cache scan
    /// indexes any file whose name starts with 40 characters by the hash of its content, so such a file can appear in
    /// the cache index; it is never offered for export.
    /// </summary>
    public static bool IsTemporaryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(VerifiedFileCopier.TempSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? SanitizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        var trimmed = extension.TrimStart('.');
        var sb = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append(char.ToLowerInvariant(c));
            else return null;
        }
        if (sb.Length == 0 || sb.Length > 16) return null;
        return sb.ToString();
    }

    /// <summary>Returns the extension of a game path such as <c>chara/x/y.tex</c>.</summary>
    public static string? ExtensionOfGamePath(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return null;
        var slash = gamePath.LastIndexOfAny(['/', '\\']);
        var dot = gamePath.LastIndexOf('.');
        if (dot <= slash || dot == gamePath.Length - 1) return null;
        return SanitizeExtension(gamePath[(dot + 1)..]);
    }

    /// <summary>The file name used for an exported cache file: <c>{sha1 lowercase}.{ext}</c>, the same as PlayerSync's cache.</summary>
    public static string CacheFileName(string sha1, string extension)
    {
        if (!IsSha1Hex(sha1)) throw new ArgumentException("Not a SHA-1 hex string.", nameof(sha1));
        var ext = SanitizeExtension(extension) ?? throw new ArgumentException("Invalid extension.", nameof(extension));
        return sha1.ToLowerInvariant() + "." + ext;
    }

    /// <summary>
    /// Turns arbitrary text (character names, mod names) into a single safe path segment.
    /// Never returns an empty string, ".", ".." or a reserved device name.
    /// </summary>
    public static string SanitizeSegment(string? text, string fallback = "Unnamed", int maxLength = 64)
    {
        var sb = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || Array.IndexOf(InvalidNameChars, c) >= 0) sb.Append('_');
            else sb.Append(c);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        while (result.Contains("__", StringComparison.Ordinal)) result = result.Replace("__", "_", StringComparison.Ordinal);
        if (result.Length > maxLength) result = result[..maxLength].TrimEnd('.', ' ');

        if (result.Length == 0 || result.All(ch => ch == '.' || ch == '_')) result = fallback;
        var stem = result.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem)) result = "_" + result;
        return result;
    }

    /// <summary>Normalizes to a full path without a trailing separator.</summary>
    public static string NormalizeFull(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Length > root.Length) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or lies below it.</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var r = NormalizeFull(root);
        var c = NormalizeFull(candidate);
        if (string.Equals(r, c, PathComparison)) return true;
        var rootWithSep = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(rootWithSep, PathComparison);
    }

    /// <summary>
    /// Combines <paramref name="root"/> with sanitized relative segments and guarantees the result stays inside root.
    /// </summary>
    public static string CombineWithin(string root, params string[] segments)
    {
        var current = NormalizeFull(root);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(segment))
                throw new ArgumentException($"Unsafe path segment '{segment}'.", nameof(segments));
            current = Path.Combine(current, segment);
        }

        if (!IsWithin(root, current)) throw new ArgumentException("Combined path escapes the root.", nameof(segments));
        return current;
    }

    /// <summary>
    /// Validates a destination directory for an export. Both the exporter's own folder service and the export job
    /// (and so the PlayerSync export IPC) call this, so a direct IPC caller gets the same checks as the UI.
    /// Returns null when valid, otherwise an actionable error message.
    /// <para>
    /// Checks, in order: a full path; the drive exists; the deepest existing part of the path is a folder; and that
    /// neither the path as written nor the path with every existing symbolic link or junction resolved lies inside a
    /// forbidden folder (whose own links are resolved too). Folders that do not exist yet are allowed below a valid
    /// existing parent; they are created later. Windows cloud-file placeholders are reparse points without a link
    /// target and are not treated as links.
    /// </para>
    /// </summary>
    public static string? ValidateDestination(string? destination, IEnumerable<string?> forbiddenRoots, Func<string, bool>? driveExists = null)
    {
        if (string.IsNullOrWhiteSpace(destination)) return "No export folder is set.";
        if (!Path.IsPathFullyQualified(destination)) return $"'{destination}' is not a full path. Choose a folder such as D:\\My FFXIV Mods.";

        string full;
        try
        {
            full = NormalizeFull(destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{destination}' is not a valid path: {ex.Message}";
        }

        var drive = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(drive) || !(driveExists ?? Directory.Exists)(drive))
            return $"The drive '{drive}' is not available. Connect it or choose another folder.";

        string real;
        try
        {
            real = ResolveExistingLinks(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"'{full}' cannot be checked: {ex.Message}";
        }

        if (File.Exists(real)) return $"'{full}' is a file, not a folder.";

        foreach (var forbidden in forbiddenRoots)
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            string forbiddenFull;
            try
            {
                forbiddenFull = NormalizeFull(forbidden);
            }
            catch (Exception)
            {
                continue;
            }

            var forbiddenReal = forbiddenFull;
            try
            {
                forbiddenReal = ResolveExistingLinks(forbiddenFull);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The forbidden folder is still compared as written.
            }

            if (IsWithin(forbiddenFull, full) || IsWithin(forbiddenReal, full))
                return $"The export folder may not be inside '{forbiddenFull}' (PlayerSync cache, Penumbra mods, game or plugin folders are off limits).";
            if (IsWithin(forbiddenFull, real) || IsWithin(forbiddenReal, real))
                return $"The export folder '{full}' goes through a link or junction to '{real}', which is inside '{forbiddenFull}' " +
                       "(PlayerSync cache, Penumbra mods, game or plugin folders are off limits).";
        }

        return null;
    }

    private const int MaxLinkDepth = 32;

    /// <summary>
    /// The path with every existing component that is a symbolic link or junction replaced by its final target.
    /// Components that do not exist yet are appended as written. Does not create or change anything.
    /// </summary>
    public static string ResolveExistingLinks(string path) => ResolveExistingLinks(NormalizeFull(path), 0);

    private static string ResolveExistingLinks(string full, int depth)
    {
        if (depth > MaxLinkDepth) throw new IOException("Too many nested links.");
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                // Nothing below can be a link yet: the rest is created later as ordinary folders.
                return NormalizeFull(Path.Combine([current, .. segments[i..]]));
            }

            if (info.LinkTarget != null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                             ?? throw new IOException($"'{next}' is a link whose target cannot be resolved.");
                // The target's own parents may be links too.
                next = ResolveExistingLinks(NormalizeFull(target.FullName), depth + 1);
            }

            if (i < segments.Length - 1 && File.Exists(next)) throw new IOException($"'{next}' is a file, not a folder.");
            current = next;
        }

        return NormalizeFull(current);
    }

    /// <summary>
    /// Folders every export provider must refuse besides the PlayerSync cache and the Penumbra mod folder: the game
    /// installation, Dalamud's plugin configurations (PlayerSync's settings and FileCache.csv among them), the calling
    /// plugin's installation and the installedPlugins folder above it, and Dalamud itself.
    /// </summary>
    /// <param name="pluginConfigsDirectory">Dalamud's pluginConfigs folder.</param>
    /// <param name="pluginAssemblyDirectory">The folder of the calling plugin's assembly.</param>
    /// <param name="dalamudDirectory">The folder of Dalamud's own assembly, when known.</param>
    public static IEnumerable<string?> CommonForbiddenRoots(string? pluginConfigsDirectory, string? pluginAssemblyDirectory, string? dalamudDirectory)
    {
        yield return GameInstallFolder();
        yield return pluginConfigsDirectory;
        yield return dalamudDirectory;
        if (string.IsNullOrEmpty(pluginAssemblyDirectory)) yield break;
        // installedPlugins/<plugin>/<version>/plugin.dll; a dev plugin lives elsewhere and only its own folder is refused.
        var version = new DirectoryInfo(pluginAssemblyDirectory);
        yield return version.FullName;
        var plugin = version.Parent;
        if (plugin == null) yield break;
        yield return plugin.FullName;
        if (plugin.Parent is { Name: "installedPlugins" } installed) yield return installed.FullName;
    }

    /// <summary>The game installation folder (ffxiv_dx11.exe lives in &lt;install&gt;/game), or null when unknown.</summary>
    public static string? GameInstallFolder()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !Path.GetFileName(exe).StartsWith("ffxiv", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetDirectoryName(Path.GetDirectoryName(exe));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
