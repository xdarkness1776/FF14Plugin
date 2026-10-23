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
    /// Validates a destination directory for an export.
    /// Returns null when valid, otherwise an actionable error message.
    /// </summary>
    public static string? ValidateDestination(string? destination, IEnumerable<string?> forbiddenRoots)
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

            if (IsWithin(forbiddenFull, full))
                return $"The export folder may not be inside '{forbiddenFull}' (PlayerSync cache, Penumbra mods, game or plugin folders are off limits).";
        }

        return null;
    }
}
