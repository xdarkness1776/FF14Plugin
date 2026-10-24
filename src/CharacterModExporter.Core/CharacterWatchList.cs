using System.Collections.Concurrent;

namespace CharacterModExporter.Core;

/// <summary>
/// The characters whose Penumbra events are being recorded, keyed by entity id, and the address each one's events are
/// matched by. An address is only a transient attribution key, never an identity: whenever a character is found at
/// another address, with another name or home world, after a zone change, or after it despawned, its capture starts
/// over with a <b>new</b> tracker. An event already on its way to the old tracker lands in that discarded tracker and
/// can never appear in the new capture.
/// <para>
/// <see cref="Watch"/>, <see cref="Check"/>, <see cref="ZoneChanged"/> and <see cref="Forget"/> are called on the
/// framework thread; <see cref="TrackerAt"/> is called from Penumbra's event on any thread.
/// </para>
/// </summary>
public sealed class CharacterWatchList(Func<LoadedResourceTracker>? newTracker = null)
{
    private readonly Func<LoadedResourceTracker> _newTracker = newTracker ?? (() => new LoadedResourceTracker());
    private readonly ConcurrentDictionary<uint, Entry> _byEntity = new();
    private readonly ConcurrentDictionary<nint, LoadedResourceTracker> _byAddress = new();

    public int Count => _byEntity.Count;
    public IEnumerable<uint> EntityIds => _byEntity.Keys;

    /// <summary>The tracker an event from <paramref name="address"/> belongs to, or null when no watched character is there.</summary>
    public LoadedResourceTracker? TrackerAt(nint address)
        => address != nint.Zero && _byAddress.TryGetValue(address, out var tracker) ? tracker : null;

    /// <summary>The current tracker of a watched character.</summary>
    public LoadedResourceTracker? TrackerOf(uint entityId) => _byEntity.TryGetValue(entityId, out var e) ? e.Tracker : null;

    /// <summary>Starts or continues watching the character found at <paramref name="address"/>; returns its current tracker.</summary>
    public LoadedResourceTracker Watch(uint entityId, nint address, string name, string? homeWorld)
    {
        var entry = _byEntity.GetOrAdd(entityId, _ => new Entry(nint.Zero, name, homeWorld, _newTracker()));
        if (entry.Address != address || !string.Equals(entry.Name, name, StringComparison.Ordinal)
                                     || !string.Equals(entry.HomeWorld, homeWorld, StringComparison.OrdinalIgnoreCase))
            entry = StartOver(entityId, entry, address, name, homeWorld);
        return entry.Tracker;
    }

    /// <summary>
    /// Per-frame check of one watched character against the object currently holding its entity id (null when none).
    /// Returns true when its capture started over.
    /// </summary>
    public bool Check(uint entityId, nint? currentAddress, string? currentName, string? currentHomeWorld)
    {
        if (!_byEntity.TryGetValue(entityId, out var entry)) return false;
        if (currentAddress == null)
        {
            if (entry.Address == nint.Zero) return false;
            StartOver(entityId, entry, nint.Zero, entry.Name, entry.HomeWorld);
            return true;
        }

        if (entry.Address == currentAddress && string.Equals(entry.Name, currentName, StringComparison.Ordinal)
                                             && string.Equals(entry.HomeWorld, currentHomeWorld, StringComparison.OrdinalIgnoreCase))
            return false;
        // Respawned elsewhere, or the entity id now belongs to someone else: follow it with a new capture.
        StartOver(entityId, entry, currentAddress.Value, currentName ?? entry.Name, currentHomeWorld);
        return true;
    }

    /// <summary>Every object despawned and addresses will be reused: every capture starts over, matched by nothing until found again.</summary>
    public void ZoneChanged()
    {
        foreach (var (id, entry) in _byEntity) StartOver(id, entry, nint.Zero, entry.Name, entry.HomeWorld);
    }

    /// <summary>Stops watching a character; its tracker no longer receives events.</summary>
    public void Forget(uint entityId)
    {
        if (_byEntity.TryRemove(entityId, out var entry))
            _byAddress.TryRemove(new KeyValuePair<nint, LoadedResourceTracker>(entry.Address, entry.Tracker));
    }

    private Entry StartOver(uint entityId, Entry old, nint address, string name, string? homeWorld)
    {
        _byAddress.TryRemove(new KeyValuePair<nint, LoadedResourceTracker>(old.Address, old.Tracker));
        var fresh = new Entry(address, name, homeWorld, _newTracker());
        _byEntity[entityId] = fresh;
        if (address != nint.Zero) _byAddress[address] = fresh.Tracker;
        return fresh;
    }

    private sealed record Entry(nint Address, string Name, string? HomeWorld, LoadedResourceTracker Tracker);
}
