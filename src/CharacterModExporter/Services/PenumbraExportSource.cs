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
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly PlayerSyncIpcClient _playerSync;
    private readonly Func<IEnumerable<string?>> _forbiddenRoots;
    private readonly ApiVersion _apiVersion;
    private readonly GetGameObjectResourcePaths _resourcePaths;
    private readonly RedrawObject _redraw;
    private readonly GetModDirectory _modDirectory;
    private readonly EventSubscriber<nint, string, string> _resolved;
    private readonly CharacterWatchList _watch = new();
    private readonly FileVerificationCache _hashes = new();
    private readonly ExportJobRegistry _jobs;
    private readonly VanillaFileComparer _vanilla;

    public PenumbraExportSource(IDalamudPluginInterface pi, IObjectTable objects, IFramework framework, IClientState clientState, IPluginLog log,
        PlayerSyncIpcClient playerSync, Func<IEnumerable<string?>> forbiddenRoots, VanillaFileComparer vanilla)
    {
        _clientState = clientState;
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
        _clientState.TerritoryChanged += OnZoneChanged;
        _clientState.InstanceChanged += OnZoneChanged;
    }

    /// <summary>
    /// Every object despawns when the zone or instance changes, and the game reuses their addresses for whoever loads
    /// next. Nothing recorded before stays valid, so every watched character starts over and is found again by its
    /// entity id when the window next reads it.
    /// </summary>
    private void OnZoneChanged<T>(T _) => _watch.ZoneChanged();

    /// <summary>
    /// Every frame, checks that every watched character is still the same object at the address its Penumbra events
    /// are matched by (same name and home world). When it despawned, moved or was replaced, its capture starts over,
    /// so an object that takes over the address is never recorded for it for longer than one frame. One lookup per
    /// watched character; only characters with an open window are watched.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_watch.Count == 0) return;
        foreach (var entityId in _watch.EntityIds)
        {
            var current = _objects.SearchByEntityId(entityId) as IPlayerCharacter;
            _watch.Check(entityId, current?.Address, current?.Name.TextValue, WorldOf(current));
        }
    }

    private static string? WorldOf(IPlayerCharacter? pc)
    {
        var world = pc?.HomeWorld.ValueNullable?.Name.ToString();
        return string.IsNullOrWhiteSpace(world) ? null : world;
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

    public bool SupportsRescan => true;

    /// <summary>
    /// Redraws the character through Penumbra so the game loads all its resources again. Penumbra's redraw is local to
    /// the game; nothing of PlayerSync is called, so nothing is downloaded or reapplied.
    /// </summary>
    public bool RequestRescan(uint entityId)
    {
        var info = Capture(entityId, readResources: false);
        if (info == null || info.Handled != true) return false;
        info.Tracker.MarkChanging();
        _framework.RunOnFrameworkThread(() => _redraw.Invoke(info.ObjectIndex, RedrawType.Redraw)).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>Stock PlayerSync offers no IPC to reapply data; its own context menu does that.</summary>
    public bool SupportsReapply => false;

    public bool RequestReapply(uint entityId) => false;

    public ExportStartResultDto StartExport(ExportRequestDto request) => _jobs.Start(request);
    public ExportJobStatusDto GetExportStatus(string jobId) => _jobs.GetStatus(jobId);
    public bool CancelExport(string jobId) => _jobs.Cancel(jobId);

    private CharacterResolution Resolve(uint entityId)
    {
        var info = Capture(entityId, readResources: true);
        if (info == null)
            return new(new CharacterStateDto { Lookup = CharacterLookup.NotSynced, LinkDetailsKnown = false }, new Dictionary<string, ResolvedFile>());

        // The capture's own tracker: the window may be closed (and the character forgotten) meanwhile.
        var tracker = info.Tracker;
        var resolution = PenumbraResolution.Build(info.Name, info.Handled, tracker.Snapshot(), CacheFolder(), ModDirectory(), _hashes,
            tracker.LastChangeUtc, DateTimeOffset.UtcNow, _vanilla, tracker.Truncated);
        resolution.State.HomeWorld = info.HomeWorld;
        return resolution;
    }

    private sealed record Captured(string Name, string? HomeWorld, int ObjectIndex, bool? Handled, LoadedResourceTracker Tracker);

    /// <summary>Reads the game object (and optionally Penumbra's on-screen resources) on the framework thread.</summary>
    private Captured? Capture(uint entityId, bool readResources)
    {
        return _framework.RunOnFrameworkThread(() =>
        {
            if (_objects.SearchByEntityId(entityId) is not IPlayerCharacter pc) return null;
            var world = WorldOf(pc);
            var tracker = _watch.Watch(entityId, pc.Address, pc.Name.TextValue, world);
            var handled = _playerSync.GetHandledAddresses()?.Contains(pc.Address);
            if (readResources && handled == true)
            {
                try
                {
                    tracker.Merge(_resourcePaths.Invoke(pc.ObjectIndex).FirstOrDefault());
                }
                catch (Exception ex) when (ex is IpcNotReadyError or IpcError)
                {
                    _log.Debug(ex, "Penumbra resource paths unavailable");
                }
            }

            return new Captured(pc.Name.TextValue, world, pc.ObjectIndex, handled, tracker);
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Stops watching a character (its window was closed): its events are no longer recorded and what was recorded is
    /// dropped, so watching stays bounded by the open windows. Reading the character again starts a new capture.
    /// </summary>
    public void Forget(uint entityId) => _watch.Forget(entityId);

    /// <summary>Penumbra event, raised on game threads for every redirected resource of an identified object.</summary>
    private void OnResolved(nint gameObject, string gamePath, string fullPath)
    {
        // The tracker is taken once: if the capture starts over meanwhile, this event goes to the discarded one.
        _watch.TrackerAt(gameObject)?.Record(gamePath, fullPath);
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
        _clientState.TerritoryChanged -= OnZoneChanged;
        _clientState.InstanceChanged -= OnZoneChanged;
        _resolved.Dispose();
        _jobs.Dispose();
    }

}
