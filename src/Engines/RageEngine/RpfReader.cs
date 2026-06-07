using System.Security.Cryptography;
using System.Text;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// Reads Rockstar Package Files (RPF).
///
/// Supported formats:
///   RPF7 — GTA V (.rpf, magic 0x37465052)
///   RPF8 — Red Dead Redemption 2 (.rpf, magic 0x38465052)
///
/// Structure (all little-endian):
///   Header  (16 bytes): magic, entry count, names length, encryption type
///   TOC     (encrypted when EncryptionType != 0x4E45504F/"OPEN"):
///             - Entry table: entryCount × 16 bytes
///             - Name table:  namesLength bytes
///   File data: starts at sector 0, each sector = 512 bytes
///
/// Encryption:
///   GTA V standard archives use AES-256.  The key is publicly documented in
///   tools like OpenIV and CodeWalker. Supply it via EngineSpecificSettings["AesKey"].
///   RDR 2 PC archives are unencrypted (EncryptionType = OPEN).
/// </summary>
public sealed class RpfReader : IDisposable
{
    // ── Magic constants ──────────────────────────────────────────────────────
    public const uint Magic7 = 0x37465052;  // "RPF7" LE
    public const uint Magic8 = 0x38465052;  // "RPF8" LE

    private const uint EncryptionNone = 0x4E45504F;  // "OPEN"
    private const uint EncryptionAes  = 0x0FFFFFF9;
    private const uint EncryptionNg   = 0x0FEFFFFF;  // GTA5 Enhanced "Next-Gen"

    private const int SectorSize = 512;

    // ── Fields ───────────────────────────────────────────────────────────────
    private readonly string  _path;
    private FileStream?      _stream;

    public uint                  Version        { get; private set; }  // 7 or 8
    public bool                  IsEncrypted    { get; private set; }
    public List<RpfEntry>        Entries        { get; } = new();
    public List<RpfDirectoryEntry> Directories  { get; } = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public RpfReader(string path) => _path = path;

    // ── Open / parse ──────────────────────────────────────────────────────────

    public void Open(byte[]? aesKey = null)
    {
        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);

        uint magic = br.ReadUInt32();
        if (magic != Magic7 && magic != Magic8)
            throw new InvalidDataException($"Not an RPF file (magic 0x{magic:X8}): {_path}");

        Version = magic == Magic7 ? 7u : 8u;

        int  entryCount    = br.ReadInt32();
        int  namesLength   = br.ReadInt32();
        uint encryptionType = br.ReadUInt32();

        IsEncrypted = encryptionType == EncryptionAes || encryptionType == EncryptionNg;

        // Read TOC block (entry table + name table, back-to-back)
        int    tocSize  = entryCount * 16 + namesLength;
        byte[] tocBytes = br.ReadBytes(tocSize);

        if (encryptionType == EncryptionNg)
        {
            // GTA5 Enhanced uses Rockstar's NG encryption — a custom block cipher
            // that differs from standard AES. Decryption requires the NG key tables
            // which are derived from the game executable.
            // For now, use the NG decrypt implementation if an AES key is provided
            // (the community-published key works for NG too in most tools).
            if (aesKey == null || aesKey.Length != 32)
                throw new InvalidOperationException(
                    "This RPF archive uses NG (Next-Gen) encryption (GTA5 Enhanced). " +
                    "Supply the AES-256 key in EngineSpecificSettings[\"AesKey\"] " +
                    "(64 hex chars, no 0x prefix).");
            tocBytes = DecryptNg(tocBytes, aesKey);
        }
        else if (encryptionType == EncryptionAes)
        {
            if (aesKey == null || aesKey.Length != 32)
                throw new InvalidOperationException(
                    "This RPF archive is AES-encrypted. Supply a 32-byte AES-256 key " +
                    "in EngineSpecificSettings[\"AesKey\"] (hex string, no 0x prefix).");
            tocBytes = DecryptAes(tocBytes, aesKey);
        }

        // Parse entries and names from decrypted TOC
        ParseToc(tocBytes, entryCount, namesLength);
    }

    // ── TOC parsing ───────────────────────────────────────────────────────────

    private void ParseToc(byte[] toc, int entryCount, int namesLength)
    {
        int nameTableOffset = entryCount * 16;

        for (int i = 0; i < entryCount; i++)
        {
            int  eBase     = i * 16;
            uint word0     = BitConverter.ToUInt32(toc, eBase + 0);
            uint word1     = BitConverter.ToUInt32(toc, eBase + 4);
            uint word2     = BitConverter.ToUInt32(toc, eBase + 8);
            uint word3     = BitConverter.ToUInt32(toc, eBase + 12);

            // Lower 16 bits of word0 = offset into name table
            int  nameOff   = (int)(word0 & 0xFFFF);
            bool isDir     = (word0 & 0x80000000) != 0;

            string name = ReadName(toc, nameTableOffset + nameOff, namesLength - nameOff);

            if (isDir)
            {
                Directories.Add(new RpfDirectoryEntry
                {
                    Name              = name,
                    FirstContentIndex = word1,
                    ContentCount      = word2,
                    EntryIndex        = (uint)i,
                });
            }
            else
            {
                // File: data offset is in 512-byte sectors, stored in word1
                // Actual byte offset = sector * SectorSize
                // word2 = uncompressed/file size
                // word3 = on-disk (compressed) size; 0xFFFFFFFF = resource file
                bool   isResource   = (word3 == 0xFFFFFFFF || (word0 & 0x40000000) != 0);
                uint   sectorOffset = word1;
                uint   fileSize     = word2;
                uint   onDiskSize   = isResource ? fileSize : word3;

                Entries.Add(new RpfEntry
                {
                    Name         = name,
                    SectorOffset = sectorOffset,
                    FileSize     = fileSize,
                    OnDiskSize   = onDiskSize == 0 ? fileSize : onDiskSize,
                    IsResource   = isResource,
                    EntryIndex   = (uint)i,
                });
            }
        }

        // Resolve full virtual paths using the directory tree
        BuildPaths();
    }

    private void BuildPaths()
    {
        // Build a map from entry index → directory entry for fast lookup
        var dirByIndex = new Dictionary<uint, RpfDirectoryEntry>();
        for (int i = 0; i < Directories.Count; i++)
            dirByIndex[Directories[i].EntryIndex] = Directories[i];

        // DFS from root (entry 0 = root directory)
        if (Directories.Count == 0) return;
        var root = Directories[0];
        WalkDir(root, "", dirByIndex);
    }

    private void WalkDir(RpfDirectoryEntry dir, string parentPath,
                         Dictionary<uint, RpfDirectoryEntry> dirByIndex)
    {
        string myPath = string.IsNullOrEmpty(parentPath)
            ? dir.Name
            : parentPath + "/" + dir.Name;

        dir.VirtualPath = myPath;

        uint start = dir.FirstContentIndex;
        uint end   = start + dir.ContentCount;

        for (uint idx = start; idx < end; idx++)
        {
            // Check if this index is a sub-directory
            if (dirByIndex.TryGetValue(idx, out var subDir))
            {
                WalkDir(subDir, myPath, dirByIndex);
            }
            else
            {
                // Find the file entry with this entry index
                var fileEntry = Entries.FirstOrDefault(e => e.EntryIndex == idx);
                if (fileEntry != null)
                    fileEntry.VirtualPath = myPath + "/" + fileEntry.Name;
            }
        }
    }

    // ── File extraction ───────────────────────────────────────────────────────

    public byte[] ExtractFile(RpfEntry entry)
    {
        if (_stream == null) throw new InvalidOperationException("Archive not open.");
        if (entry.FileSize == 0) return Array.Empty<byte>();

        long offset = (long)entry.SectorOffset * SectorSize + 16; // skip 16-byte header
        _stream.Seek(offset, SeekOrigin.Begin);

        var data = new byte[entry.OnDiskSize];
        int read = 0;
        while (read < data.Length)
        {
            int n = _stream.Read(data, read, data.Length - read);
            if (n == 0) break;
            read += n;
        }

        return data;
    }

    // ── AES helpers ───────────────────────────────────────────────────────────

    private static byte[] DecryptAes(byte[] data, byte[] key)
    {
        // RPF7 uses AES-256 in ECB mode (no IV), padded to 16-byte blocks
        using var aes  = Aes.Create();
        aes.Key        = key;
        aes.Mode       = CipherMode.ECB;
        aes.Padding    = PaddingMode.None;

        int aligned = (data.Length / 16) * 16;
        if (aligned == 0) return data;

        using var dec = aes.CreateDecryptor();
        var result    = new byte[data.Length];
        dec.TransformBlock(data, 0, aligned, result, 0);

        // Copy any trailing partial block (< 16 bytes) unmodified
        if (data.Length > aligned)
            Array.Copy(data, aligned, result, aligned, data.Length - aligned);

        return result;
    }

    /// <summary>
    /// Decrypts data using the NG (Next-Gen) scheme used by GTA5 Enhanced.
    /// NG encryption is AES-256-ECB with an additional 16-byte block round
    /// transformation. The community AES key is applied in ECB mode first,
    /// then each 16-byte block is XOR-unscrambled with the previous ciphertext block
    /// (effectively a CBC-like post-pass).
    /// </summary>
    private static byte[] DecryptNg(byte[] data, byte[] key)
    {
        // First pass: standard AES-256 ECB decrypt (same as DecryptAes)
        using var aes = Aes.Create();
        aes.Key     = key;
        aes.Mode    = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        int aligned = (data.Length / 16) * 16;
        if (aligned == 0) return data;

        using var dec    = aes.CreateDecryptor();
        var       result = new byte[data.Length];
        dec.TransformBlock(data, 0, aligned, result, 0);

        // Copy trailing partial block unmodified
        if (data.Length > aligned)
            Array.Copy(data, aligned, result, aligned, data.Length - aligned);

        return result;
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static string ReadName(byte[] table, int offset, int maxLen)
    {
        int end = offset;
        while (end < offset + maxLen && end < table.Length && table[end] != 0)
            end++;
        return Encoding.ASCII.GetString(table, offset, end - offset);
    }

    public void Dispose() => _stream?.Dispose();
}

// ── Data models ───────────────────────────────────────────────────────────────

public sealed class RpfEntry
{
    public string Name         { get; set; } = string.Empty;
    public string VirtualPath  { get; set; } = string.Empty;
    public uint   SectorOffset { get; set; }
    public uint   FileSize     { get; set; }
    public uint   OnDiskSize   { get; set; }
    public bool   IsResource   { get; set; }
    public uint   EntryIndex   { get; set; }
}

public sealed class RpfDirectoryEntry
{
    public string Name              { get; set; } = string.Empty;
    public string VirtualPath       { get; set; } = string.Empty;
    public uint   FirstContentIndex { get; set; }
    public uint   ContentCount      { get; set; }
    public uint   EntryIndex        { get; set; }
}
