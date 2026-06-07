using System.Text.Json;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Builds and caches a hash-keyed index of all VRAM_DESC entries found in
/// TLOU2 texturedict3 pak files, enabling full-resolution texture lookup by
/// the texPath string stored in actor-pak VRAM_DESC records.
///
/// The texturedict paks (~2 GB each) are only page-table-scanned once; the
/// resulting index is serialised to %AppData%\GameAssetExplorer\{key}-texdict.json
/// and reloaded on subsequent runs.
///
/// Usage:
///   await dict.EnsureBuiltAsync(pakPaths, cacheKey, progress);
///   var result = await dict.LookupAsync(texPath);
/// </summary>
public class NdTextureDictionary
{
    // ── Index types ───────────────────────────────────────────────────────────

    private sealed class TexEntry
    {
        public string File   { get; set; } = string.Empty;
        public long   Offset { get; set; }
        public int    Size   { get; set; }
        public int    Width  { get; set; }
        public int    Height { get; set; }
        public string Format { get; set; } = string.Empty;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private Dictionary<string, TexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public bool IsLoaded => _loaded;
    public int  EntryCount => _index.Count;

    // ── Build / load ──────────────────────────────────────────────────────────

    public async Task EnsureBuiltAsync(
        IReadOnlyList<string> dictPakPaths,
        string               cacheKey,
        IProgress<string>?   progress = null)
    {
        if (_loaded) return;

        string cachePath = GetCachePath(cacheKey);

        if (File.Exists(cachePath))
        {
            try
            {
                progress?.Report($"Loading texture dictionary cache from {cachePath}…");
                var json = await File.ReadAllTextAsync(cachePath).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TexEntry>>(json);
                if (loaded != null && loaded.Count > 0)
                {
                    _index = new Dictionary<string, TexEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                    _loaded = true;
                    progress?.Report($"Texture dictionary loaded: {_index.Count:N0} entries.");
                    return;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Cache read failed ({ex.Message}), rebuilding…");
            }
        }

        // Rebuild from pak files
        await BuildIndexAsync(dictPakPaths, progress).ConfigureAwait(false);

        // Persist cache
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var json = JsonSerializer.Serialize(_index,
                new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(cachePath, json).ConfigureAwait(false);
            progress?.Report($"Texture dictionary cached to {cachePath}.");
        }
        catch (Exception ex)
        {
            progress?.Report($"Cache write failed (non-fatal): {ex.Message}");
        }

        _loaded = true;
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Look up a texPath (e.g. "/art/characters/ellie/body/E3A7F201C94B6D8.bdn").
    /// Returns raw BC-compressed bytes + dimensions + format, or null if not found.
    /// </summary>
    public async Task<(byte[] Data, int Width, int Height, string Format)?> LookupAsync(string texPath)
    {
        if (string.IsNullOrEmpty(texPath) || !_loaded) return null;

        string hash = ExtractHash(texPath);
        if (string.IsNullOrEmpty(hash)) return null;

        if (!_index.TryGetValue(hash, out var entry)) return null;

        try
        {
            using var fs = new FileStream(
                entry.File, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, bufferSize: 65536, useAsync: true);
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            var buf = new byte[entry.Size];
            int read = 0;
            while (read < buf.Length)
            {
                int got = await fs.ReadAsync(buf.AsMemory(read)).ConfigureAwait(false);
                if (got == 0) break;
                read += got;
            }
            if (read < buf.Length) return null;
            return (buf, entry.Width, entry.Height, entry.Format);
        }
        catch { return null; }
    }

    // ── Index builder ─────────────────────────────────────────────────────────

    private async Task BuildIndexAsync(
        IReadOnlyList<string> pakPaths,
        IProgress<string>?   progress)
    {
        int total = pakPaths.Count;
        for (int pi = 0; pi < total; pi++)
        {
            string pak = pakPaths[pi];
            progress?.Report($"[{pi + 1}/{total}] Scanning {Path.GetFileName(pak)}…");

            try
            {
                await Task.Run(() => ScanDictPak(pak)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                progress?.Report($"  Skipped {Path.GetFileName(pak)}: {ex.Message}");
            }
        }

        progress?.Report($"Texture dictionary built: {_index.Count:N0} entries.");
    }

    private void ScanDictPak(string pakPath)
    {
        const int PAGE_ENTRY_SZ  = 12;
        const int FIX_ENTRY_SZ   = 8;
        const int VRAM_HEADER_SZ = 16;

        // ── Read structural pages (all pages except the trailing texture blob) ──
        // For a 2 GB dict pak, the page-table metadata is at the start; we read
        // just enough to parse all VRAM_DESC records without loading 2 GB into RAM.

        using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 65536, false);

        if (fs.Length < 0x20) return;

        var hdr = new byte[0x20];
        fs.Read(hdr, 0, hdr.Length);

        uint magic   = BitConverter.ToUInt32(hdr, 0);
        if (magic != 0xA79 && magic != 0x10A79 && magic != 0x80000A79 && magic != 0xA7D)
            return;

        int pageCt  = (int)BitConverter.ToUInt32(hdr, 0x10);
        int ptOff   = (int)BitConverter.ToUInt32(hdr, 0x14);
        int fixupOf = (int)BitConverter.ToUInt32(hdr, 0x1C);
        if (pageCt < 1 || pageCt > 10_000) return;

        // ── Page table ── (fileOffset, size) tuples ───────────────────────────
        var pageOffsets = new int[pageCt];
        var pageSizes   = new int[pageCt];

        int ptSz = pageCt * PAGE_ENTRY_SZ;
        var ptBuf = new byte[ptSz];
        fs.Seek(ptOff, SeekOrigin.Begin);
        fs.Read(ptBuf, 0, ptSz);
        for (int i = 0; i < pageCt; i++)
        {
            int o = i * PAGE_ENTRY_SZ;
            pageOffsets[i] = (int)BitConverter.ToUInt32(ptBuf, o);
            pageSizes[i]   = (int)BitConverter.ToUInt32(ptBuf, o + 4);
        }

        // The texture bulk data starts right after the last page ends.
        int lastPageEnd = pageOffsets[pageCt - 1] + pageSizes[pageCt - 1];

        // ── Load structural portion (all pages, up to lastPageEnd) ────────────
        if (lastPageEnd <= 0 || lastPageEnd > 300_000_000) return; // sanity: pages ≤ 300 MB
        var data = new byte[lastPageEnd];
        fs.Seek(0, SeekOrigin.Begin);
        int totalRead = 0;
        while (totalRead < lastPageEnd)
        {
            int got = fs.Read(data, totalRead, lastPageEnd - totalRead);
            if (got == 0) break;
            totalRead += got;
        }
        if (totalRead < lastPageEnd) return;

        // ── Fixup table ───────────────────────────────────────────────────────
        if (fixupOf + 12 > data.Length) return;
        int fixDataOff = (int)BitConverter.ToUInt32(data, fixupOf + 4);
        int fixCount   = (int)BitConverter.ToUInt32(data, fixupOf + 8);
        if (fixDataOff <= 0) fixDataOff = fixupOf;

        var fixups = new Dictionary<int, int>(Math.Max(fixCount, 64));
        for (int i = 0; i < fixCount; i++)
        {
            int fo = fixDataOff + i * FIX_ENTRY_SZ;
            if (fo + 8 > data.Length) break;
            int  src  = BitConverter.ToUInt16(data, fo);
            int  dst  = BitConverter.ToUInt16(data, fo + 2);
            uint poff = BitConverter.ToUInt32(data, fo + 4);
            if (src < pageCt && dst < pageCt)
                fixups[pageOffsets[src] + (int)poff] = dst;
        }
        _ = fixups; // used for future extension; path strings are relative

        // ── Scan page headers for VRAM_DESC ───────────────────────────────────
        for (int p = 0; p < pageCt; p++)
        {
            int start = pageOffsets[p];
            if (start + 20 > data.Length) continue;
            int nEnt = BitConverter.ToUInt16(data, start + 18);
            if (nEnt <= 0) continue; // uint16 max (65535) is safe upper bound

            int cur = start + 20;
            for (int ph = 0; ph < nEnt; ph++)
            {
                if (cur + 16 > data.Length) break;
                int riOff  = (int)BitConverter.ToUInt32(data, cur + 8);
                cur += 16;

                int riBase = start + riOff;
                if (riBase + 16 > data.Length) continue;

                long typePtr = BitConverter.ToInt64(data, riBase + 8);
                if (typePtr <= 0) continue;

                int tOff = start + (int)typePtr;
                if (tOff < 0 || tOff >= data.Length) continue;

                if (ReadString(data, tOff) != "VRAM_DESC") continue;

                int vramBase = start + riOff + VRAM_HEADER_SZ;
                if (vramBase + 120 > data.Length) continue;

                uint pakOffset = BitConverter.ToUInt32(data, vramBase + 40); // uint — can be >2GB into file
                uint vramSizeU = BitConverter.ToUInt32(data, vramBase + 48);
                int  imgFormat = (int)BitConverter.ToUInt32(data, vramBase + 72);
                int  width     = (int)BitConverter.ToUInt32(data, vramBase + 84);
                int  height    = (int)BitConverter.ToUInt32(data, vramBase + 88);

                if (width < 4 || width > 8192 || height < 4 || height > 8192) continue;
                if (vramSizeU < 16 || vramSizeU > 50_000_000) continue;
                int vramSize = (int)vramSizeU;

                string? fmt = imgFormat switch
                {
                    71 => "BC1", 80 => "BC4", 83 => "BC5", 98 => "BC7", _ => null
                };
                if (fmt == null) continue;

                string texPath = vramBase + 116 <= data.Length
                    ? ReadString(data, vramBase + 112)
                    : string.Empty;
                if (string.IsNullOrEmpty(texPath)) continue;

                string hash = ExtractHash(texPath);
                if (string.IsNullOrEmpty(hash)) continue;

                long absOffset = lastPageEnd + (long)pakOffset;
                if (absOffset + vramSize > fs.Length) continue;

                // Prefer BC7 > BC5 > BC4 > BC1 when same hash appears in multiple paks
                bool prefer = !_index.TryGetValue(hash, out var existing)
                    || FormatPriority(fmt) > FormatPriority(existing.Format);
                if (prefer)
                {
                    _index[hash] = new TexEntry
                    {
                        File   = pakPath,
                        Offset = absOffset,
                        Size   = vramSize,
                        Width  = width,
                        Height = height,
                        Format = fmt,
                    };
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract the hash token from a texPath.
    /// "/art/characters/ellie/body/E3A7F201C94B6D8.bdn" → "E3A7F201C94B6D8"
    /// </summary>
    private static string ExtractHash(string texPath)
    {
        var fname = Path.GetFileNameWithoutExtension(texPath);
        return fname ?? string.Empty;
    }

    // v2 bumped when BC4/BC5 indexing was added — forces rebuild of v1 caches
    private static string GetCachePath(string cacheKey)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "GameAssetExplorer", $"{cacheKey}-texdict-v2.json");
    }

    private static int FormatPriority(string? fmt) => fmt switch
    {
        "BC7" => 4, "BC5" => 3, "BC4" => 2, "BC1" => 1, _ => 0
    };

    private static string ReadString(byte[] b, int off)
    {
        var sb = new System.Text.StringBuilder(64);
        while (off >= 0 && off < b.Length && b[off] != 0)
            sb.Append((char)b[off++]);
        return sb.ToString();
    }
}
