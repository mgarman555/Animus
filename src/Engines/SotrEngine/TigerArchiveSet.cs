namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Every .tiger archive in a Shadow of the Tomb Raider install, keyed by (archiveId, archiveSubId).
///
/// A single game install is several archive sets — bigfile.000.tiger plus patch and DLC
/// bigfiles. TOC entries and DRM resource descriptors both address their data by
/// (archiveId, archiveSubId, archivePart, offset), and that pair frequently points at a
/// *different* set than the one whose TOC you read it from: that indirection is how patches
/// and DLC replace base-game data. Resolving through this set rather than through the
/// owning <see cref="TigerReader"/> is what makes patched assets load correctly.
///
/// Those coordinates are only meaningful inside the archive that owns them, so a read whose
/// (archiveId, subId) is not mounted is refused rather than redirected — pointing it at some
/// other file's part index and offset would return plausible-looking garbage.
/// </summary>
public sealed class TigerArchiveSet : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<(int Id, int SubId), TigerReader> _byId = new();
    private readonly List<TigerReader> _archives = new();
    private readonly List<TigerReader> _shadowed = new();

    /// <summary>
    /// The archives whose TOCs should be harvested — one per (id, subId). Archives that lost
    /// the race for an id are excluded: their entries would be resolved back to the winner and
    /// read at the wrong file's offsets. They stay tracked only so <see cref="Dispose"/>
    /// still closes them.
    /// </summary>
    public IReadOnlyList<TigerReader> Archives
    {
        get { lock (_lock) return _archives.ToArray(); }
    }

    /// <summary>Number of routable archives, without allocating a snapshot.</summary>
    public int Count
    {
        get { lock (_lock) return _archives.Count; }
    }

    public void Add(TigerReader archive)
    {
        lock (_lock)
        {
            if (_byId.TryAdd((archive.Id, archive.SubId), archive))
                _archives.Add(archive);
            else
                _shadowed.Add(archive);
        }
    }

    /// <summary>Archives that declared an id already claimed by another archive.</summary>
    public IReadOnlyList<TigerReader> Shadowed
    {
        get { lock (_lock) return _shadowed.ToArray(); }
    }

    public TigerReader? Find(int archiveId, int archiveSubId)
    {
        lock (_lock) return _byId.GetValueOrDefault((archiveId, archiveSubId));
    }

    /// <summary>
    /// Reads a TOC entry's bytes from whichever archive its archiveId/subId names.
    /// Returns an empty array when that archive is not mounted (an uninstalled DLC, typically).
    /// </summary>
    public byte[] ReadEntry(TigerEntry entry, TigerReader listedIn)
        => ReadEntryCore(entry, listedIn, int.MaxValue);

    /// <summary>First CDRM chunk only — enough to identify a file without inflating it.</summary>
    public byte[] PeekEntry(TigerEntry entry, TigerReader listedIn)
        => ReadEntryCore(entry, listedIn, chunkLimit: 1);

    private byte[] ReadEntryCore(TigerEntry entry, TigerReader listedIn, int chunkLimit)
    {
        var owner = Find(entry.ArchiveId, entry.ArchiveSubId);
        if (owner == null)
        {
            Console.WriteLine(
                $"[SOTR] {Path.GetFileName(listedIn.IndexPath)}: entry 0x{entry.NameHash:X16} " +
                $"references archive {entry.ArchiveId}.{entry.ArchiveSubId}, which is not mounted.");
            return Array.Empty<byte>();
        }

        return owner.ReadBlob(entry.ArchivePart, entry.Offset, entry.UncompressedSize,
                              chunkLimit: chunkLimit);
    }

    /// <summary>
    /// Reads a resource's bytes — the refDefinitions block followed by the body.
    /// A resource whose refDefinitions + body exactly fill its archive slot is stored raw;
    /// anything else is CDRM-compressed.
    /// </summary>
    public byte[]? ReadResource(SotrResource resource)
    {
        var owner = Find(resource.ArchiveId, resource.ArchiveSubId);
        if (owner == null) return null;

        bool raw = resource.RefDefinitionsSize + resource.BodySize == resource.Length;
        return owner.ReadBlob(resource.ArchivePart, resource.Offset, resource.Length,
                              assumeUncompressed: raw);
    }

    /// <summary>Resource bytes with the refDefinitions prefix stripped, leaving just the body.</summary>
    public byte[]? ReadResourceBody(SotrResource resource)
    {
        var data = ReadResource(resource);
        if (data == null) return null;

        int skip = (int)Math.Min(resource.RefDefinitionsSize, (uint)data.Length);
        return skip == 0 ? data : data.AsSpan(skip).ToArray();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var a in _archives) a.Dispose();
            foreach (var a in _shadowed) a.Dispose();
            _archives.Clear();
            _shadowed.Clear();
            _byId.Clear();
        }
    }
}
