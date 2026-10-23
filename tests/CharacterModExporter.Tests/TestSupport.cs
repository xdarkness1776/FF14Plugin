using System.Security.Cryptography;
using System.Text;
using CharacterModExporter.Core;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cmx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Temp cleanup only.
        }
    }
}

/// <summary>A file in the fake PlayerSync cache, named like PlayerSync names them: lowercase SHA-1 + extension.</summary>
public sealed record CacheFile(string Hash, string Path, byte[] Content);

/// <summary>
/// Stands in for one PlayerSync pair as the export IPC provider sees it. The state building mirrors
/// ExportIpcProvider.Resolve in the fork: same <see cref="ResourceResolver"/> and <see cref="ExportJobRegistry"/>.
/// </summary>
public sealed class FakePair
{
    public uint EntityId { get; init; }
    public string Name { get; set; } = "Test Character";
    public bool Visible { get; set; } = true;
    public bool Paused { get; set; }
    public bool ZoneSyncOnly { get; set; } = true;
    public bool InProgress { get; set; }
    public string? ReceivedHash { get; set; } = "DATA1";
    public string? AppliedHash { get; set; } = "DATA1";
    public bool HasManipulations { get; set; }
    public List<ReplacementInput> Applied { get; } = [];
    public Dictionary<(string GamePath, string? Hash), string> Modded { get; } = [];
    public Dictionary<string, string> Redirects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<(string GamePath, string? Hash)> Excluded { get; } = [];

    /// <summary>Adds a resource that PlayerSync resolved to a cache file.</summary>
    public void AddResolved(CacheFile file, string kind, params string[] gamePaths)
    {
        Applied.Add(new ReplacementInput(kind, gamePaths, file.Hash, null));
        foreach (var gp in gamePaths) Modded[(gp, file.Hash)] = file.Path;
    }

    public void AddMissing(string hash, params string[] gamePaths) => Applied.Add(new ReplacementInput("Player", gamePaths, hash, null));

    public void AddSwap(string gamePath, string target)
    {
        Applied.Add(new ReplacementInput("Player", [gamePath], string.Empty, target));
        Modded[(gamePath, null)] = target;
    }
}

public sealed class FakePlayerSync : IPlayerSyncExportApi, IDisposable
{
    private readonly TempDir _dir;

    public FakePlayerSync(TempDir dir)
    {
        _dir = dir;
        CacheFolder = dir.Sub("playersync-cache");
        PenumbraFolder = dir.Sub("penumbra-mods");
        Registry = new ExportJobRegistry(Resolve, () => [CacheFolder, PenumbraFolder],
            delay: (_, token) => Task.Delay(5, token));
    }

    public string CacheFolder { get; }
    public string PenumbraFolder { get; }
    public ExportJobRegistry Registry { get; }
    public Dictionary<uint, FakePair> Pairs { get; } = [];
    public bool Connected { get; set; } = true;
    public int ProvidedVersion { get; set; } = ExportIpc.ApiVersion;
    public bool Installed { get; set; } = true;
    public int ResyncRequests { get; private set; }

    /// <summary>Counts anything that would have caused a server transfer. The exporter must never cause one by itself.</summary>
    public int DownloadRequests { get; private set; }

    /// <summary>Called when a resync is requested, to simulate PlayerSync's pipeline.</summary>
    public Action<FakePair>? OnResync { get; set; }

    public CacheFile AddCacheFile(string extension, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA1.HashData(bytes));
        var path = System.IO.Path.Combine(CacheFolder, hash.ToLowerInvariant() + "." + extension);
        File.WriteAllBytes(path, bytes);
        return new CacheFile(hash, path, bytes);
    }

    public FakePair AddPair(uint entityId, string name)
    {
        var pair = new FakePair { EntityId = entityId, Name = name };
        Pairs[entityId] = pair;
        return pair;
    }

    public CharacterResolution Resolve(uint entityId)
    {
        var files = new Dictionary<string, ResolvedFile>(StringComparer.OrdinalIgnoreCase);
        var state = new CharacterStateDto { ZoneSyncEnabled = true };
        if (!Connected)
        {
            state.Lookup = CharacterLookup.NotConnected;
            return new(state, files);
        }

        if (!Pairs.TryGetValue(entityId, out var pair) || !pair.Visible)
        {
            state.Lookup = CharacterLookup.NotSynced;
            return new(state, files);
        }

        state.PlayerName = pair.Name;
        state.IsZoneSyncOnly = pair.ZoneSyncOnly;
        state.SharesZoneSyncGroup = pair.ZoneSyncOnly;
        if (pair.Paused)
        {
            state.Lookup = CharacterLookup.Paused;
            return new(state, files);
        }

        state.Lookup = CharacterLookup.Found;
        state.ReceivedDataHash = pair.ReceivedHash;
        state.AppliedDataHash = pair.AppliedHash;
        state.HasManipulations = pair.HasManipulations;
        state.Resources = pair.AppliedHash != null
            ? ResourceResolver.Resolve(pair.Applied, pair.Modded, pair.Redirects, pair.Excluded, CacheFolder, PenumbraFolder, files)
            : ResourceResolver.Unresolved(pair.Applied);
        state.SyncState = ResourceResolver.ComputeState(pair.ReceivedHash, pair.InProgress, pair.AppliedHash, state.Resources);
        return new(state, files);
    }

    public (ProviderStatus Status, int Version) Probe()
        => !Installed ? (ProviderStatus.NotInstalled, 0)
            : ProvidedVersion == ExportIpc.ApiVersion ? (ProviderStatus.Available, ProvidedVersion) : (ProviderStatus.IncompatibleVersion, ProvidedVersion);

    public string SourceLabel => "Fake PlayerSync export IPC v" + ProvidedVersion;
    public string UnavailableMessage => "Fake PlayerSync is not installed.";

    // Round-trip through JSON like the real IPC does.
    public CharacterStateDto GetCharacterState(uint entityId)
        => ExportJson.Deserialize<CharacterStateDto>(ExportJson.Serialize(Resolve(entityId).State))!;

    public bool RequestResync(uint entityId)
    {
        ResyncRequests++;
        if (!Pairs.TryGetValue(entityId, out var pair)) return false;
        OnResync?.Invoke(pair);
        return true;
    }

    public ExportStartResultDto StartExport(ExportRequestDto request)
        => ExportJson.Deserialize<ExportStartResultDto>(ExportJson.Serialize(
            Registry.Start(ExportJson.Deserialize<ExportRequestDto>(ExportJson.Serialize(request)))))!;

    public ExportJobStatusDto GetExportStatus(string jobId)
        => ExportJson.Deserialize<ExportJobStatusDto>(ExportJson.Serialize(Registry.GetStatus(jobId)))!;

    public bool CancelExport(string jobId) => Registry.Cancel(jobId);

    /// <summary>Simulates PlayerSync downloading a file through its own pipeline (used by resync scenarios).</summary>
    public CacheFile SimulatePlayerSyncDownload(string extension, string content)
    {
        DownloadRequests++;
        return AddCacheFile(extension, content);
    }

    public void Dispose() => Registry.Dispose();
}

public static class Snapshot
{
    /// <summary>Path, length, last write time and content hash of every file below a folder.</summary>
    public static Dictionary<string, (long, DateTime, string)> Of(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            f => f,
            f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
}

public static class Settings
{
    public static ExportSettings For(string parent, Action<ExportSettings>? configure = null)
    {
        var s = new ExportSettings { ParentFolder = parent, SyncWaitSeconds = 2 };
        configure?.Invoke(s);
        return s;
    }
}
