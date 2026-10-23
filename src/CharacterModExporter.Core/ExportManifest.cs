using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Core;

public sealed class ExportManifest
{
    public int ManifestVersion { get; set; } = 1;
    public string ExporterVersion { get; set; } = string.Empty;
    public int ExportIpcVersion { get; set; } = ExportIpc.ApiVersion;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string? World { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? ResultMessage { get; set; }
    public ManifestSync Sync { get; set; } = new();
    public string AssetsFolder { get; set; } = string.Empty;
    public List<ManifestAsset> Assets { get; set; } = [];
    public List<ManifestFileSwap> FileSwaps { get; set; } = [];
    public List<ManifestUnresolved> Unresolved { get; set; } = [];
    public List<ManifestPackage> InstallablePackages { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class ManifestSync
{
    public bool IsDirectlyPaired { get; set; }
    public bool IsZoneSyncOnly { get; set; }
    public bool SharesZoneSyncGroup { get; set; }
    public string SyncState { get; set; } = string.Empty;
    public string? AppliedDataHash { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public bool HasMetaManipulations { get; set; }
}

public sealed class ManifestAsset
{
    public string CacheHash { get; set; } = string.Empty;
    public string? SourceHash { get; set; }
    public string HashAlgorithm { get; set; } = "SHA-1 of file content (PlayerSync file identifier)";
    public bool IsCompressedAlternate { get; set; }
    public string Extension { get; set; } = string.Empty;
    public List<string> GamePaths { get; set; } = [];
    public List<string> ObjectKinds { get; set; } = [];
    public string? ExportedFile { get; set; }
    public long? Size { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public bool Selected { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool Sha1Verified { get; set; }
    public string? Sha256 { get; set; }
    public bool InInstallablePackage { get; set; }
}

public sealed record ManifestFileSwap(string ObjectKind, List<string> GamePaths, string? Target);
public sealed record ManifestUnresolved(string ObjectKind, List<string> GamePaths, string? Hash, string Reason);
public sealed record ManifestPackage(string File, string Format, int Files, int Redirects, int FileSwaps, string Validation, List<string> Omitted,
    string? Category = null, string? Item = null, string? Supports = null);

public static class ManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(ExportManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    public static ExportManifest? FromJson(string json) => JsonSerializer.Deserialize<ExportManifest>(json, Options);

    /// <summary>Writes the text atomically (temp file then move), keeping a previous file as <c>.previous</c>.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var temp = path + VerifiedFileCopier.TempSuffix;
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Replace(temp, path, path + ".previous", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    public static string BuildReadme(ExportManifest m, string sessionFolder)
    {
        var copied = m.Assets.Count(a => a.Outcome is nameof(FileExportOutcome.Copied) or nameof(FileExportOutcome.AlreadyPresentIdentical));
        var failed = m.Assets.Where(a => a.Selected && a.Outcome is not (nameof(FileExportOutcome.Copied) or nameof(FileExportOutcome.AlreadyPresentIdentical))).ToList();
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("Character Mod Exporter - export report");
        sb.AppendLine("======================================");
        sb.AppendLine();
        sb.AppendLine(inv, $"Character:   {m.CharacterName}{(m.World != null ? " @ " + m.World : string.Empty)}");
        sb.AppendLine(inv, $"Exported:    {m.ExportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(inv, $"Result:      {m.Result}{(m.ResultMessage != null ? " - " + m.ResultMessage : string.Empty)}");
        sb.AppendLine(inv, $"Sync source: {(m.Sync.IsDirectlyPaired ? "direct pair" : m.Sync.IsZoneSyncOnly ? "ZoneSync" : m.Sync.SharesZoneSyncGroup ? "syncshell + ZoneSync" : "syncshell")}, state {m.Sync.SyncState}");
        sb.AppendLine(inv, $"Folder:      {sessionFolder}");
        sb.AppendLine();
        sb.AppendLine("WHAT WAS EXPORTED");
        sb.AppendLine(inv, $"- {copied} individual game resource file(s) in '{m.AssetsFolder}'.");
        sb.AppendLine("  These are raw game files (.tex, .mtrl, .mdl, .avfx, .pap, .tmb, ...), named like PlayerSync's");
        sb.AppendLine("  cache: <SHA-1 of content>.<extension>. Manifest.json lists the game path(s) each file replaces.");
        sb.AppendLine("  They are NOT mod packages and cannot be installed by double-clicking them.");
        if (m.FileSwaps.Count > 0)
            sb.AppendLine(inv, $"- {m.FileSwaps.Count} file swap(s) (redirects to other game files) are listed in Manifest.json; they have no file of their own.");
        sb.AppendLine();
        sb.AppendLine("INSTALLABLE PACKAGES");
        if (m.InstallablePackages.Count == 0)
        {
            sb.AppendLine("- None were created for this export.");
        }
        else
        {
            foreach (var p in m.InstallablePackages.Where(p => p.Item == null))
            {
                sb.AppendLine(inv, $"- {p.File} ({p.Format}): {p.Redirects} file redirection(s), {p.FileSwaps} swap(s).");
                sb.AppendLine(inv, $"  Validation: {p.Validation}");
            }

            var items = m.InstallablePackages.Where(p => p.Item != null).ToList();
            if (items.Count > 0)
            {
                sb.AppendLine(inv, $"- {items.Count} item mod(s), one per item, sorted by category in the '{ExportFolderService.ModsFolderName}' folder:");
                foreach (var p in items)
                {
                    sb.AppendLine(inv, $"    {p.File}: {p.Redirects} file redirection(s).");
                    if (p.Supports != null) sb.AppendLine(inv, $"      {p.Supports}");
                }
                sb.AppendLine("  PlayerSync does not transfer the original mod names, so items are split and named by the game");
                sb.AppendLine("  item their files replace. A mod made for another race or body may not fit your character.");
            }
            sb.AppendLine("  To install: open Penumbra (/penumbra), Mods tab, 'Import Mod', pick the .pmp file,");
            sb.AppendLine("  then enable the mod in the collection you want. Nothing is installed or enabled automatically.");
            sb.AppendLine("  The package reproduces the file replacements PlayerSync applied. It does NOT contain");
            sb.AppendLine("  Penumbra meta manipulations, Glamourer designs, Customize+ scales or other plugin data.");
        }
        sb.AppendLine();
        sb.AppendLine("MISSING OR NOT EXPORTED");
        if (m.Unresolved.Count == 0 && failed.Count == 0)
        {
            sb.AppendLine("- Nothing: every resource PlayerSync resolved for this character was exported.");
        }
        foreach (var u in m.Unresolved)
            sb.AppendLine(inv, $"- {string.Join(", ", u.GamePaths)}: {u.Reason}");
        foreach (var f in failed)
            sb.AppendLine(inv, $"- {f.ExportedFile ?? f.CacheHash}: {f.Outcome}{(f.Error != null ? " - " + f.Error : string.Empty)}");
        var notSelected = m.Assets.Count(a => !a.Selected);
        if (notSelected > 0) sb.AppendLine(inv, $"- {notSelected} available file(s) were not selected for this export.");
        sb.AppendLine();
        sb.AppendLine("LIMITATIONS");
        sb.AppendLine("- Only what PlayerSync received and applied for this character is included. Resources removed by");
        sb.AppendLine("  your PlayerSync filters or by the other player's permissions were never received.");
        if (m.Sync.HasMetaManipulations)
            sb.AppendLine("- The character uses Penumbra meta manipulations; they are not exported.");
        sb.AppendLine("- Textures may be PlayerSync 'compressed alternates' (lower size) instead of the original; see IsCompressedAlternate.");
        foreach (var note in m.Notes) sb.AppendLine("- " + note);
        sb.AppendLine();
        sb.AppendLine("Respect the creators of these mods: keep exports for personal use unless their terms allow sharing.");
        return sb.ToString();
    }
}
