using System.Collections.Concurrent;
using CharacterModExporter.Core;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using PlayerSyncExport.Contracts;

namespace CharacterModExporter.Services;

/// <summary>
/// Works with stock PlayerSync. PlayerSync applies a synced character's files through a Penumbra temporary
/// collection, so Penumbra knows which local file stands behind each of the character's game paths. This source
/// reads that (on-screen resources plus every redirection Penumbra reports while the window is open), keeps
/// only files from PlayerSync's cache or the user's Penumbra mod folder that differ from the game's own files, and copies them with the same verified
/// job runner the PlayerSync IPC uses. It never downloads anything and never writes to PlayerSync or Penumbra.
/// </summary>
public sealed class PenumbraExportSource : IPlayerSyncExportApi, IDisposable
{
    private const int PenumbraBreakingVersion = 5;

    private readonly IDalamudPluginInterface _pi;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly PlayerSyncIpcClient _playerSync;
    private readonly Func<IEnumerable<string?>> _forbiddenRoots;
    private readonly ApiVersion _apiVersion;
    private readonly GetGameObjectResourcePaths _resourcePaths;
    private readonly RedrawObject _redraw;
    private readonly GetModDirectory _modDirectory;
    private readonly EventSubscriber<nint, string, string> _resolved;
    private readonly ConcurrentDictionary<uint, Watched> _watched = new();
    private readonly ConcurrentDictionary<nint, Watched> _byAddress = new();
    private readonly FileVerificationCache _hashes = new();
    private readonly ExportJobRegistry _jobs;
    private readonly VanillaFileComparer _vanilla;

    public PenumbraExportSource(IDalamudPluginInterface pi, IObjectTable objects, IFramework framework, IPluginLog log,
        PlayerSyncIpcClient playerSync, Func<IEnumerable<string?>> forbiddenRoots, VanillaFileComparer vanilla)
    {
        _vanilla = vanilla;
        _pi = pi;
        _objects = objects;
        _framework = framework;
        _log = log;
        _playerSync = playerSync;
        _forbiddenRoots = forbiddenRoots;
        _apiVersion = new ApiVersion(pi);
        _resourcePaths = new GetGameObjectResourcePaths(pi);
        _redraw = new RedrawObject(pi);
        _modDirectory = new GetModDirectory(pi);
        _resolved = GameObjectResourcePathResolved.Subscriber(pi, OnResolved);
        _jobs = new ExportJobRegistry(Resolve, () => _forbiddenRoots().Append(CacheFolder()).Append(ModDirectory()),
            log: (ex, msg) => _log.Warning(ex, msg));
        _framework.Update += OnFrameworkUpdate;
    }

    private DateTime _nextCheckUtc;

    /// <summary>
    /// Once a second, checks that every watched character still lives at the address its Penumbra events are matched by.
    /// When it despawned or moved, another object can take the address, and its files must never be recorded for it.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < _nextCheckUtc || _watched.IsEmpty) return;
        _nextCheckUtc = now.AddSeconds(1);
        foreach (var (entityId, watched) in _watched)
        {
            var current = _objects.SearchByEntityId(entityId) as IPlayerCharacter;
            if (current != null ? current.Address == watched.Address : watched.Address == nint.Zero) continue;
            _byAddress.TryRemove(new KeyValuePair<nint, Watched>(watched.Address, watched));
            watched.Tracker.Reset();
            if (current == null)
            {
                watched.Address = nint.Zero;
                continue;
            }

            // Respawned at another address: follow it; the game loads its files again and they are recorded afresh.
            watched.Address = current.Address;
            watched.Name = current.Name.TextValue;
            _byAddress[current.Address] = watched;
        }
    }

    public string SourceLabel => "Official PlayerSync, files read through Penumbra";
    public string UnavailableMessage { get; private set; } = "Penumbra is not loaded.";

    public (ProviderStatus Status, int Version) Probe()
    {
        try
        {
            var (breaking, _) = _apiVersion.Invoke();
            if (breaking != PenumbraBreakingVersion)
            {
                UnavailableMessage = $"Penumbra API {breaking} is not supported (this exporter uses API {PenumbraBreakingVersion}). Update the exporter or Penumbra.";
                return (ProviderStatus.NotInstalled, 0);
            }
        }
        catch (Exception ex) when (ex is IpcNotReadyError or IpcError)
        {
            UnavailableMessage = "Penumbra is not loaded. The exporter reads PlayerSync's applied files from Penumbra.";
            return (ProviderStatus.NotInstalled, 0);
        }

        if (_playerSync.GetHandledAddresses() == null)
        {
            UnavailableMessage = "PlayerSync is not loaded.";
            return (ProviderStatus.NotInstalled, 0);
        }

        return (ProviderStatus.Available, 0);
    }

    public CharacterStateDto GetCharacterState(uint entityId) => Resolve(entityId).State;

    /// <summary>Redraws the character through Penumbra so the game loads all its resources again. Downloads nothing.</summary>
    public bool RequestResync(uint entityId)
    {
        var info = Capture(entityId, readResources: false);
        if (info == null || info.Handled != true) return false;
        _watched[entityId].Tracker.MarkChanging();
        _framework.RunOnFrameworkThread(() => _redraw.Invoke(info.ObjectIndex, RedrawType.Redraw)).GetAwaiter().GetResult();
        return true;
    }

    public ExportStartResultDto StartExport(ExportRequestDto request) => _jobs.Start(request);
    public ExportJobStatusDto GetExportStatus(string jobId) => _jobs.GetStatus(jobId);
    public bool CancelExport(string jobId) => _jobs.Cancel(jobId);

    private CharacterResolution Resolve(uint entityId)
    {
        var info = Capture(entityId, readResources: true);
        if (info == null)
            return new(new CharacterStateDto { Lookup = CharacterLookup.NotSynced, LinkDetailsKnown = false }, new Dictionary<string, ResolvedFile>());

        var tracker = _watched[entityId].Tracker;
        return PenumbraResolution.Build(info.Name, info.Handled, tracker.Snapshot(), CacheFolder(), ModDirectory(), _hashes,
            tracker.LastChangeUtc, DateTimeOffset.UtcNow, _vanilla);
    }

    private sealed record Captured(string Name, int ObjectIndex, bool? Handled);

    /// <summary>Reads the game object (and optionally Penumbra's on-screen resources) on the framework thread.</summary>
    private Captured? Capture(uint entityId, bool readResources)
    {
        return _framework.RunOnFrameworkThread(() =>
        {
            if (_objects.SearchByEntityId(entityId) is not IPlayerCharacter pc) return null;
            var watched = Watch(entityId, pc.Address, pc.Name.TextValue);
            var handled = _playerSync.GetHandledAddresses()?.Contains(pc.Address);
            if (readResources && handled == true)
            {
                try
                {
                    watched.Tracker.Merge(_resourcePaths.Invoke(pc.ObjectIndex).FirstOrDefault());
                }
                catch (Exception ex) when (ex is IpcNotReadyError or IpcError)
                {
                    _log.Debug(ex, "Penumbra resource paths unavailable");
                }
            }

            return new Captured(pc.Name.TextValue, pc.ObjectIndex, handled);
        }).GetAwaiter().GetResult();
    }

    private Watched Watch(uint entityId, nint address, string name)
    {
        var watched = _watched.GetOrAdd(entityId, _ => new Watched(address, name));
        if (watched.Address != address || !string.Equals(watched.Name, name, StringComparison.Ordinal))
        {
            // The entity id now points at a respawned or different object: start over.
            _byAddress.TryRemove(watched.Address, out _);
            watched.Address = address;
            watched.Name = name;
            watched.Tracker.Reset();
        }

        _byAddress[address] = watched;
        return watched;
    }

    /// <summary>Penumbra event, raised on game threads for every redirected resource of an identified object.</summary>
    private void OnResolved(nint gameObject, string gamePath, string fullPath)
    {
        if (gameObject != nint.Zero && _byAddress.TryGetValue(gameObject, out var watched))
            watched.Tracker.Record(gamePath, fullPath);
    }

    private string? CacheFolder() => PlayerSyncConfigReader.ReadCacheFolder(_pi.ConfigDirectory.Parent?.FullName);

    private string? ModDirectory()
    {
        try
        {
            var dir = _modDirectory.Invoke();
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception ex) when (ex is IpcNotReadyError or IpcError)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _resolved.Dispose();
        _jobs.Dispose();
    }

    private sealed class Watched(nint address, string name)
    {
        public nint Address { get; set; } = address;
        public string Name { get; set; } = name;
        public LoadedResourceTracker Tracker { get; } = new();
    }
}
