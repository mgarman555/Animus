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
/// </summary>
public sealed class TigerArchiveSet : IDisposable
{
    private readonly Dictionary<(int Id, int SubId), TigerReader> _byId = new();
    private readonly List<TigerReader> _archives = new();

    public IReadOnlyList<TigerReader> Archives => _archives;

    public void Add(TigerReader archive)
    {
        _archives.Add(archive);
        // First archive claiming an id wins; duplicates are unusual but must not throw.
        _byId.TryAdd((archive.Id, archive.SubId), archive);
    }

    public TigerReader? Find(int archiveId, int archiveSubId)
        => _byId.GetValueOrDefault((archiveId, archiveSubId));

    /// <summary>
    /// Reads a TOC entry's bytes, following its archiveId/subId to whichever archive
    /// actually owns the data. Falls back to <paramref name="listedIn"/> when the target
    /// set is missing (an uninstalled DLC, typically).
    /// </summary>
    public byte[] ReadEntry(TigerEntry entry, TigerReader listedIn)
    {
        var owner = Find(entry.ArchiveId, entry.ArchiveSubId) ?? listedIn;
        return owner.ReadBlob(entry.ArchivePart, entry.Offset, entry.UncompressedSize);
    }

    /// <summary>First CDRM chunk only — enough to identify a file without inflating it.</summary>
    public byte[] PeekEntry(TigerEntry entry, TigerReader listedIn)
    {
        var owner = Find(entry.ArchiveId, entry.ArchiveSubId) ?? listedIn;
        return owner.ReadBlob(entry.ArchivePart, entry.Offset, entry.UncompressedSize, chunkLimit: 1);
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
        foreach (var a in _archives) a.Dispose();
        _archives.Clear();
        _byId.Clear();
    }
}
