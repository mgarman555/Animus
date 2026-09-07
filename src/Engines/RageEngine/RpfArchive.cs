using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GameAssetExplorer.Engines.RageEngine;

public enum RpfEncryption : uint
{
    None = 0,
    Open = 0x4E45504F,   // "OPEN" — OpenIV-style unencrypted TOC
    Aes  = 0x0FFFFFF9,
    Ng   = 0x0FEFFFFF,   // shipped with the 2015 PC port; nothing to do with Enhanced
}

/// <summary>
/// A Rockstar Package File, version 7 (GTA V, PC).
///
/// Layout, all little-endian, everything relative to <see cref="StartPos"/> because an archive
/// can be a region inside another archive:
///
///   +0   uint32 Version        must read as 0x52504637 ("RPF7" reversed on disk: 37 46 50 52)
///   +4   uint32 EntryCount
///   +8   uint32 NamesLength
///   +12  uint32 Encryption     see <see cref="RpfEncryption"/>
///   +16  EntryCount * 16 bytes of entries, then NamesLength bytes of names. Both halves are
///        encrypted together as one run when the archive is AES or NG.
///
/// Entry kind is decided by the SECOND uint32 (at +4 within the entry):
///   == 0x7FFFFF00           directory
///   (x &amp; 0x80000000) == 0  binary file
///   otherwise               resource file (.ydr/.ytd/.ymap/... — everything that matters here)
///
/// Effectively all of GTA V's map data lives in RPFs nested inside other RPFs, so an archive
/// carries a StartPos and children are opened against the same physical file.
/// </summary>
public sealed class RpfArchive
{
    public const uint Rpf7Magic = 0x52504637;
    public const int  SectorSize = 512;

    /// <summary>Original casing. NG key derivation hashes this, and lowercasing it breaks decryption.</summary>
    public string Name      { get; }
    public string NameLower { get; }

    /// <summary>Virtual path of this archive, e.g. "x64m.rpf\levels\gta5\_cityw\venice_01\vb_01.rpf".</summary>
    public string Path      { get; }

    /// <summary>Absolute path of the physical .rpf on disk that ultimately contains this archive.</summary>
    public string PhysicalPath { get; }

    /// <summary>Byte offset of this archive's header inside the physical file. 0 for a root archive.</summary>
    public long StartPos { get; private set; }

    /// <summary>Archive length. NG key derivation uses this, so it must be the archive's own size.</summary>
    public long FileSize { get; }

    public RpfEncryption Encryption { get; private set; }
    public uint          EntryCount { get; private set; }
    public uint          NamesLength { get; private set; }

    public RpfArchive?    Parent   { get; }
    public List<RpfArchive> Children { get; } = new();

    /// <summary>Every entry in TOC order. Directory entries index into this list by position.</summary>
    public List<RpfEntry> AllEntries { get; } = new();

    /// <summary>File entries of this archive only, with virtual paths resolved. Excludes children.</summary>
    public List<RpfFileEntry> Files { get; } = new();

    public RpfDirectoryEntry? Root { get; private set; }

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>Root archive: a .rpf sitting on the filesystem.</summary>
    public RpfArchive(string physicalPath)
    {
        var fi       = new FileInfo(physicalPath);
        Name         = fi.Name;
        NameLower    = Name.ToLowerInvariant();
        Path         = Name.ToLowerInvariant();
        PhysicalPath = physicalPath;
        FileSize     = fi.Length;
        StartPos     = 0;
    }

    /// <summary>Child archive: a .rpf stored as a binary entry inside another archive.</summary>
    private RpfArchive(RpfArchive parent, RpfBinaryFileEntry entry)
    {
        Name         = entry.Name;
        NameLower    = Name.ToLowerInvariant();
        Path         = entry.Path;
        PhysicalPath = parent.PhysicalPath;
        FileSize     = entry.GetFileSize();
        Parent       = parent;
        StartPos     = parent.StartPos + (long)entry.FileOffset * SectorSize;
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads this archive's table of contents and recurses into every nested .rpf. One shared
    /// reader walks the whole physical file, so a 4 GB archive is never copied.
    /// </summary>
    public void ScanStructure(BinaryReader br, GtaKeys? keys, Action<string>? onError = null)
    {
        ReadHeader(br, keys);

        foreach (var entry in AllEntries)
        {
            if (entry is not RpfBinaryFileEntry bin) continue;
            if (!bin.NameLower.EndsWith(".rpf", StringComparison.Ordinal)) continue;
            if (!IsSanePath(bin.Path)) continue;

            try
            {
                var child = new RpfArchive(this, bin);
                br.BaseStream.Position = child.StartPos;
                child.ScanStructure(br, keys, onError);
                Children.Add(child);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"{bin.Path}: {ex.Message}");
            }
        }
    }

    private void ReadHeader(BinaryReader br, GtaKeys? keys)
    {
        StartPos = br.BaseStream.Position;

        uint version = br.ReadUInt32();
        if (version != Rpf7Magic)
            throw new InvalidDataException(
                $"Not an RPF7 archive (version reads 0x{version:X8}, expected 0x{Rpf7Magic:X8}).");

        EntryCount  = br.ReadUInt32();
        NamesLength = br.ReadUInt32();
        Encryption  = (RpfEncryption)br.ReadUInt32();

        if (EntryCount == 0)
            throw new InvalidDataException("RPF7 archive has no entries.");

        byte[] entriesData = br.ReadBytes((int)EntryCount * 16);
        byte[] namesData   = br.ReadBytes((int)NamesLength);

        switch (Encryption)
        {
            case RpfEncryption.None:
            case RpfEncryption.Open:
                break;

            case RpfEncryption.Aes:
                RequireKeys(keys, "AES");
                entriesData = GtaCrypto.DecryptAes(entriesData, keys!.AesKey);
                namesData   = GtaCrypto.DecryptAes(namesData,   keys.AesKey);
                break;

            default:
                // Unknown encryption types are treated as NG, matching CodeWalker: in practice
                // only OpenIV-modified archives land here and they are NG.
                RequireKeys(keys, Encryption == RpfEncryption.Ng ? "NG" : $"unknown (0x{(uint)Encryption:X8}, assuming NG)");
                entriesData = GtaCrypto.DecryptNg(entriesData, Name, (uint)FileSize, keys!);
                namesData   = GtaCrypto.DecryptNg(namesData,   Name, (uint)FileSize, keys);
                break;
        }

        ParseEntries(br, entriesData, namesData);
        BuildPaths();
    }

    private void RequireKeys(GtaKeys? keys, string kind)
    {
        if (keys == null)
            throw new InvalidOperationException(
                $"{Name} uses {kind} encryption and no key set is loaded. See GtaKeys for how to get one.");
    }

    private void ParseEntries(BinaryReader br, byte[] entriesData, byte[] namesData)
    {
        for (int i = 0; i < EntryCount; i++)
        {
            var span = entriesData.AsSpan(i * 16, 16);
            uint h1  = BinaryPrimitives.ReadUInt32LittleEndian(span);
            uint h2  = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);

            RpfEntry e;
            if (h2 == 0x7FFFFF00u)
            {
                var dir = new RpfDirectoryEntry();
                dir.NameOffset   = h1;
                dir.EntriesIndex = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                dir.EntriesCount = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                e = dir;
            }
            else if ((h2 & 0x80000000u) == 0)
            {
                // One packed uint64: 16 bits name offset, 24 bits on-disk size, 24 bits sector offset.
                ulong buf = BinaryPrimitives.ReadUInt64LittleEndian(span);
                var bin = new RpfBinaryFileEntry
                {
                    NameOffset           = (uint)(buf & 0xFFFF),
                    FileSize             = (uint)(buf >> 16) & 0xFFFFFF,
                    FileOffset           = (uint)(buf >> 40) & 0xFFFFFF,
                    FileUncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                    EncryptionType       = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
                };
                bin.IsEncrypted = bin.EncryptionType == 1;
                e = bin;
            }
            else
            {
                var res = new RpfResourceFileEntry
                {
                    NameOffset    = BinaryPrimitives.ReadUInt16LittleEndian(span),
                    FileSize      = (uint)span[2] | ((uint)span[3] << 8) | ((uint)span[4] << 16),
                    FileOffset    = ((uint)span[5] | ((uint)span[6] << 8) | ((uint)span[7] << 16)) & 0x7FFFFF,
                    SystemFlags   = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                    GraphicsFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
                };

                // 0xFFFFFF is a sentinel, not a size: the real one is smeared across the first
                // 16 bytes of the payload in that order. Applies to a handful of huge .ytd/.ydd.
                if (res.FileSize == 0xFFFFFF)
                {
                    long save = br.BaseStream.Position;
                    br.BaseStream.Position = StartPos + (long)res.FileOffset * SectorSize;
                    byte[] head = br.ReadBytes(16);
                    if (head.Length == 16)
                    {
                        res.FileSize = ((uint)head[7]) | ((uint)head[14] << 8)
                                     | ((uint)head[5] << 16) | ((uint)head[2] << 24);
                    }
                    br.BaseStream.Position = save;
                }

                e = res;
            }

            e.Archive = this;
            e.H1 = h1;
            e.H2 = h2;
            e.Name = ReadName(namesData, (int)e.NameOffset);
            e.NameLower = e.Name.ToLowerInvariant();

            // The only per-file encryption GTA V actually uses is on scripts.
            if (e is RpfResourceFileEntry rfe)
                rfe.IsEncrypted = rfe.NameLower.EndsWith(".ysc", StringComparison.Ordinal);

            AllEntries.Add(e);
        }
    }

    private void BuildPaths()
    {
        if (AllEntries[0] is not RpfDirectoryEntry root)
            throw new InvalidDataException("RPF7 entry 0 is not the root directory.");

        Root = root;
        root.Path = Path;

        var stack = new Stack<RpfDirectoryEntry>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir   = stack.Pop();
            long from = dir.EntriesIndex;
            long to   = dir.EntriesIndex + (long)dir.EntriesCount;

            if (from < 0 || to > AllEntries.Count) continue;   // corrupt or modded TOC

            for (long i = from; i < to; i++)
            {
                var e = AllEntries[(int)i];
                e.Parent = dir;
                e.Path   = dir.Path + "\\" + e.NameLower;

                if (e is RpfDirectoryEntry sub)
                {
                    dir.Directories.Add(sub);
                    stack.Push(sub);
                }
                else if (e is RpfFileEntry f)
                {
                    dir.FileEntries.Add(f);
                    Files.Add(f);
                }
            }
        }
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one entry out and returns it decrypted and inflated. Opens its own handle so
    /// callers can extract in parallel, which matters when a map export pulls thousands of files.
    /// </summary>
    public byte[]? ExtractFile(RpfFileEntry entry, GtaKeys? keys)
    {
        using var fs = new FileStream(PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        return ExtractFile(entry, keys, br);
    }

    public byte[]? ExtractFile(RpfFileEntry entry, GtaKeys? keys, BinaryReader br) => entry switch
    {
        RpfBinaryFileEntry b   => ExtractBinary(b, keys, br),
        RpfResourceFileEntry r => ExtractResource(r, keys, br),
        _                      => null,
    };

    private byte[]? ExtractBinary(RpfBinaryFileEntry entry, GtaKeys? keys, BinaryReader br)
    {
        long length = entry.GetFileSize();
        if (length <= 0) return null;

        // Binary entries start at the sector boundary. Only resources skip a header.
        br.BaseStream.Position = StartPos + (long)entry.FileOffset * SectorSize;
        byte[] raw = br.ReadBytes((int)length);

        byte[] data = Decrypt(raw, entry, entry.FileUncompressedSize, keys);

        // A non-zero FileSize is what marks a binary entry as deflated; FileSize == 0 means the
        // payload is stored and FileUncompressedSize is the real length.
        if (entry.FileSize > 0)
            data = Inflate(data) ?? data;

        return data;
    }

    private byte[]? ExtractResource(RpfResourceFileEntry entry, GtaKeys? keys, BinaryReader br)
    {
        if (entry.FileSize == 0) return null;

        // Skip the 16-byte RSC7 header: SystemFlags and GraphicsFlags are already in the TOC.
        const uint headerSize = 0x10;
        if (entry.FileSize <= headerSize) return null;

        br.BaseStream.Position = StartPos + (long)entry.FileOffset * SectorSize + headerSize;
        byte[] raw = br.ReadBytes((int)(entry.FileSize - headerSize));

        byte[] data = Decrypt(raw, entry, entry.FileSize, keys);
        return Inflate(data) ?? data;
    }

    private byte[] Decrypt(byte[] raw, RpfFileEntry entry, uint ngLength, GtaKeys? keys)
    {
        if (!entry.IsEncrypted) return raw;
        if (keys == null)
            throw new InvalidOperationException($"{entry.Path} is encrypted and no key set is loaded.");

        return Encryption == RpfEncryption.Aes
            ? GtaCrypto.DecryptAes(raw, keys.AesKey)
            : GtaCrypto.DecryptNg(raw, entry.Name, ngLength, keys);
    }

    /// <summary>Raw DEFLATE, no zlib or gzip wrapper. Returns null when the payload is not deflated.</summary>
    private static byte[]? Inflate(byte[] data)
    {
        try
        {
            using var src = new MemoryStream(data);
            using var ds  = new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            ds.CopyTo(dst);
            return dst.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Every archive in this subtree, this one first.</summary>
    public IEnumerable<RpfArchive> EnumerateArchives()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var a in child.EnumerateArchives())
                yield return a;
    }

    private static string ReadName(byte[] names, int offset)
    {
        if (offset < 0 || offset >= names.Length) return string.Empty;
        int end = offset;
        while (end < names.Length && names[end] != 0) end++;
        int len = Math.Min(end - offset, 256);   // a pathological length would only be a corrupt TOC
        return Encoding.ASCII.GetString(names, offset, len);
    }

    private static bool IsSanePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 500) return false;
        int depth = 0;
        foreach (char c in path)
        {
            if (c is ':' or ';') return false;
            if (c is '/' or '\\') depth++;
        }
        return depth <= 20;
    }
}

// ── Entries ───────────────────────────────────────────────────────────────────

public abstract class RpfEntry
{
    public RpfArchive?        Archive    { get; set; }
    public RpfDirectoryEntry? Parent     { get; set; }
    public uint               NameOffset { get; set; }
    public string             Name       { get; set; } = string.Empty;
    public string             NameLower  { get; set; } = string.Empty;
    public string             Path       { get; set; } = string.Empty;
    public uint               H1;
    public uint               H2;

    public string ShortNameLower
    {
        get
        {
            int i = NameLower.LastIndexOf('.');
            return i > 0 ? NameLower[..i] : NameLower;
        }
    }

    public override string ToString() => Path;
}

public sealed class RpfDirectoryEntry : RpfEntry
{
    public uint EntriesIndex { get; set; }
    public uint EntriesCount { get; set; }

    public List<RpfDirectoryEntry> Directories { get; } = new();
    public List<RpfFileEntry>      FileEntries { get; } = new();
}

public abstract class RpfFileEntry : RpfEntry
{
    public uint FileOffset  { get; set; }
    public uint FileSize    { get; set; }
    public bool IsEncrypted { get; set; }

    public abstract long GetFileSize();
}

public sealed class RpfBinaryFileEntry : RpfFileEntry
{
    public uint FileUncompressedSize { get; set; }
    public uint EncryptionType       { get; set; }

    public override long GetFileSize() => FileSize == 0 ? FileUncompressedSize : FileSize;
}

public sealed class RpfResourceFileEntry : RpfFileEntry
{
    public uint SystemFlags   { get; set; }
    public uint GraphicsFlags { get; set; }

    public int SystemSize   => GetSizeFromFlags(SystemFlags);
    public int GraphicsSize => GetSizeFromFlags(GraphicsFlags);

    /// <summary>Resource version, e.g. 165 for .ydr, 13 for .ymap. Top nibble of each flag word.</summary>
    public int Version => (int)((((SystemFlags >> 28) & 0xF) << 4) | ((GraphicsFlags >> 28) & 0xF));

    public override long GetFileSize() => FileSize == 0 ? (long)SystemSize + GraphicsSize : FileSize;

    /// <summary>
    /// Page flags pack a page count across scattered bit groups times a base page size. This is
    /// what tells you how big the system and graphics segments are once inflated.
    /// </summary>
    public static int GetSizeFromFlags(uint flags)
    {
        uint s0 = ((flags >> 27) & 0x1)  << 0;
        uint s1 = ((flags >> 26) & 0x1)  << 1;
        uint s2 = ((flags >> 25) & 0x1)  << 2;
        uint s3 = ((flags >> 24) & 0x1)  << 3;
        uint s4 = ((flags >> 17) & 0x7F) << 4;
        uint s5 = ((flags >> 11) & 0x3F) << 5;
        uint s6 = ((flags >> 7)  & 0xF)  << 6;
        uint s7 = ((flags >> 5)  & 0x3)  << 7;
        uint s8 = ((flags >> 4)  & 0x1)  << 8;
        uint ss = (flags >> 0)   & 0xF;
        uint baseSize = 0x200u << (int)ss;
        return (int)(baseSize * (s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8));
    }
}
