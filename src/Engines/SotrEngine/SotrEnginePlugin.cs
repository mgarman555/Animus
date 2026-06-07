using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Engine plugin for Shadow of the Tomb Raider (2018) PC — Foundation Engine (Eidos-Montréal).
///
/// Asset naming:
///   TAFS archives store FNV-64 hashes. A 335K-entry hash→path dictionary
///   (SOTR_PC_Release.list, MIT — arcusmaximus/TrRebootModTools) is bundled as an
///   embedded resource and loaded automatically at mount — no external files required.
///
/// Virtual path structure (type-first):
///   /models/{resolved-path-or-hash}
///   /textures/{resolved-path-or-hash}
///   /audio/{resolved-path-or-hash}
///   /animations/{resolved-path-or-hash}
///   /levels/{resolved-path-or-hash}
///   /other/{resolved-path-or-hash}
///
///   Resolved paths strip the "pcx64-w\" platform prefix and use forward slashes.
///   Example: pcx64-w\characters\lara\outfit_01\lara_body.drm
///         → /models/characters/lara/outfit_01/lara_body.drm
///
/// Type detection:
///   • Raw DDS / RIFF / OGG magic → classified immediately
///   • DRM v23 wrapper → background scan reads the primary section type
///   • Results cached to %AppData%\GameAssetExplorer\sotr-{key}-types.bin
///     so the tree is fully classified from the second mount onward
/// </summary>
public class SotrEnginePlugin : IGameEngine
{
    // ── Events / state ────────────────────────────────────────────────────────

    /// <summary>Fires once the background type scan finishes. Used to trigger a tree rebuild.</summary>
    public event EventHandler? TypeScanCompleted;

    private readonly List<TigerReader>                    _readers   = new();
    private readonly List<string>                         _slugs     = new();
    private readonly List<(TigerEntry E, int R)>          _index     = new();
    private readonly Dictionary<string, int>              _byPath    = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string>            _nameCache = new();   // hash → resolved path
    private readonly ConcurrentDictionary<ulong, AssetType> _typeMap = new();  // hash → asset type

    private IProgress<string>? _progress;
    private GameConfig?         _config;

    public string EngineName => "Foundation Engine";
    public string EngineId   => "SOTR";
    public bool   IsMounted  => _readers.Count > 0;

    public IReadOnlyList<string> SupportedVersions => new[] { "SOTR-PC" };
    public IReadOnlyList<string> ArchiveExtensions => new[] { ".tiger" };

    public void SetBackgroundProgress(IProgress<string>? p) => _progress = p;

    // ── Detection ─────────────────────────────────────────────────────────────

    public float DetectEngine(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return 0f;
        if (SafeEnumerateFiles(gameDirectory, "bigfile.000.tiger").Any()) return 0.95f;
        if (SafeEnumerateFiles(gameDirectory, "*.tiger").Any()) return 0.70f;
        return 0f;
    }

    // ── Mount ─────────────────────────────────────────────────────────────────

    public async Task<bool> MountGameAsync(GameConfig config, IProgress<string>? progress = null)
    {
        _config = config;
        _progress = progress;

        // Step 1: load the bundled 335K-entry hash→path dictionary
        progress?.Report("Loading asset name dictionary…");
        await Task.Run(LoadBundledHashList);
        progress?.Report($"  {_nameCache.Count:N0} paths loaded from built-in dictionary.");

        // Step 2: load the type cache from a previous scan (if it exists)
        LoadTypeCache(config.GameDirectory);

        // Step 3: open all .000.tiger index files and read their TOCs
        var indexFiles = SafeEnumerateFiles(config.GameDirectory, "*.tiger")
            .Where(f => Path.GetFileName(f).EndsWith(".000.tiger", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToArray();

        if (indexFiles.Length == 0)
        {
            progress?.Report("No .tiger index files found. Point the game directory at the SOTR install folder.");
            return false;
        }

        progress?.Report($"Found {indexFiles.Length} archive(s). Reading TOCs…");
        await Task.Run(() =>
        {
            for (int i = 0; i < indexFiles.Length; i++)
            {
                string path = indexFiles[i];
                string slug = ArchiveSlug(path);
                progress?.Report($"[{i + 1}/{indexFiles.Length}] {Path.GetFileName(path)}");
                try
                {
                    var reader = new TigerReader(path);
                    reader.Open();
                    int rid = _readers.Count;
                    _readers.Add(reader);
                    _slugs.Add(slug);

                    foreach (var entry in reader.Entries)
                    {
                        int flatIdx = _index.Count;
                        _index.Add((entry, rid));
                        _byPath[MakeVirtualPath(entry)] = flatIdx;
                    }
                    progress?.Report($"  → {reader.NumFiles:N0} entries");
                }
                catch (NotSupportedException ex)
                {
                    progress?.Report($"  Skipped {Path.GetFileName(path)}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SOTR] {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        });

        int resolved = _index.Count(t => _nameCache.ContainsKey(t.E.NameHash));
        progress?.Report(
            $"Mounted {_index.Count:N0} assets. " +
            $"{resolved:N0} named ({100.0 * resolved / Math.Max(1, _index.Count):F0}%). " +
            $"Running type scan in background…");

        // Step 4: background type scan — classifies every asset and saves the type cache
        _ = Task.Run(() => RunBackgroundTypeScan());

        return _index.Count > 0;
    }

    public async Task UnmountGameAsync()
    {
        SaveTypeCache(_config?.GameDirectory ?? "");
        foreach (var r in _readers) r.Dispose();
        _readers.Clear();
        _slugs.Clear();
        _index.Clear();
        _byPath.Clear();
        _nameCache.Clear();
        _typeMap.Clear();
        _config = null;
        await Task.CompletedTask;
    }

    // ── Asset listing ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync()
    {
        var result = new List<AssetInfo>(_index.Count);
        for (int i = 0; i < _index.Count; i++)
        {
            var (entry, rid) = _index[i];
            result.Add(EntryToInfo(entry, rid));
        }
        return Task.FromResult<IReadOnlyList<AssetInfo>>(result);
    }

    public async Task<IReadOnlyList<AssetInfo>> GetAssetsAtPathAsync(string virtualPath)
    {
        var all  = await GetAllAssetsAsync();
        var norm = virtualPath.TrimEnd('/').ToLowerInvariant();
        return all.Where(a =>
        {
            var dir = Path.GetDirectoryName(a.VirtualPath)?.Replace('\\', '/').ToLowerInvariant() ?? "";
            return dir.StartsWith(norm);
        }).ToList();
    }

    // ── Asset loading ─────────────────────────────────────────────────────────

    public async Task<AssetData> LoadAssetAsync(AssetInfo asset)
    {
        if (!_byPath.TryGetValue(asset.VirtualPath, out int flatIdx))
            throw new Exception($"Asset not found: {asset.VirtualPath}");

        var (entry, rid) = _index[flatIdx];
        var reader = _readers[rid];

        byte[] raw = await Task.Run(() => reader.ExtractEntry(entry)).ConfigureAwait(false);

        var props = BuildBaseProps(entry, reader);
        if (raw.Length >= 4)
            props["DataMagic"] = $"0x{BitConverter.ToUInt32(raw, 0):X8}";

        AssetType type = ClassifyAsset(raw, out int ddsOffset);

        // Update type map on first load (also updates the cached type)
        if (type != AssetType.Unknown)
            _typeMap[entry.NameHash] = type;

        if (type == AssetType.Texture && ddsOffset >= 0)
            return BuildTextureAsset(asset, raw, ddsOffset, props);

        if (type == AssetType.Audio)
            return BuildAudioAsset(asset, raw, props);

        // Attempt to extract geometry from DRM mesh sections (type 2 = CDCRenderModel)
        bool isDrmMesh = raw.Length >= 4 && BitConverter.ToUInt32(raw, 0) == 23 &&
                         (type == AssetType.StaticMesh || type == AssetType.SkeletalMesh ||
                          type == AssetType.Unknown);

        if (isDrmMesh)
        {
            AnnotateDrmProps(raw, props);
            props["_RawSize"] = raw.Length;

            var meshData = DrmMeshParser.TryParse(raw, asset);
            if (meshData != null)
            {
                // Merge DRM annotation props into the parsed mesh's RawProperties
                foreach (var (k, v) in props)
                    meshData.RawProperties.TryAdd(k, v);

                // Update the asset type so the UI shows the correct icon/tab
                asset.Type = meshData.Lods[0].VertexCount > 0
                    ? AssetType.StaticMesh
                    : AssetType.Unknown;
                _typeMap[entry.NameHash] = asset.Type;

                return meshData;
            }
        }
        else if (raw.Length >= 4 && BitConverter.ToUInt32(raw, 0) == 23)
        {
            AnnotateDrmProps(raw, props);
        }

        props["_RawSize"] = raw.Length;
        return new SotrRawAssetData { Info = info(asset, type), RawData = raw, RawProperties = props };
    }

    private static AssetInfo info(AssetInfo src, AssetType t)
    {
        src.Type = t;
        return src;
    }

    // ── Background type scan ──────────────────────────────────────────────────

    private void RunBackgroundTypeScan()
    {
        try
        {
            // Classify only entries not already in the type map
            var todo = _index
                .Where(t => !_typeMap.ContainsKey(t.E.NameHash))
                .OrderBy(t => t.E.TigerPart)
                .ThenBy(t => t.E.Offset)
                .ToList();

            if (todo.Count == 0)
            {
                _progress?.Report("Type scan: all assets already classified.");
                TypeScanCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }

            int done = 0;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = 3 };

            Parallel.ForEach(todo, opts, item =>
            {
                var (entry, rid) = item;
                var reader = _readers[rid];
                try
                {
                    // PeekBytes decompresses the full first MRDC chunk — gives enough data
                    // to reach the DRM section headers even for heavily-relocated assets
                    byte[] peek = reader.PeekBytes(entry);
                    AssetType t = ClassifyFromPeek(peek, entry);
                    if (t != AssetType.Unknown)
                        _typeMap[entry.NameHash] = t;
                }
                catch { /* best-effort: leave unclassified */ }

                int n = Interlocked.Increment(ref done);
                if (n % 2000 == 0 || n == todo.Count)
                    _progress?.Report($"Classifying… {n:N0} / {todo.Count:N0}");
            });

            SaveTypeCache(_config?.GameDirectory ?? "");
            _progress?.Report($"Type scan complete. Rebuilding tree…");
            TypeScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOTR] Background type scan failed: {ex.Message}");
        }
    }

    // ── Type classification ────────────────────────────────────────────────────

    private AssetType ClassifyFromPeek(byte[] peek, TigerEntry entry)
    {
        // 1. Extension-based fast path — instant when name resolved from hash list
        if (_nameCache.TryGetValue(entry.NameHash, out string? name))
        {
            string ext  = Path.GetExtension(name).ToLowerInvariant();
            string file = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            string dir  = (Path.GetDirectoryName(name) ?? "").ToLowerInvariant().Replace('\\', '/');

            AssetType extType = ext switch
            {
                ".dds"           => AssetType.Texture,
                ".fsb" or ".mul" => AssetType.Audio,
                ".anm"           => AssetType.Animation,
                _                => AssetType.Unknown
            };
            if (extType != AssetType.Unknown) return extType;

            // SOTR naming conventions for .drm files:
            //   an_*  = animation sequences
            //   ahst_* / ahztl_* = animation hosts / scripted sequences
            //   path contains "sounds/" or "audio/" or "music/" → audio
            //   path contains "textures/" → texture
            //   path contains "levels/" or "worlds/" → level
            if (ext == ".drm")
            {
                if (file.StartsWith("an_", StringComparison.Ordinal)
                 || file.StartsWith("ahst_", StringComparison.Ordinal)
                 || file.StartsWith("ahztl_", StringComparison.Ordinal)
                 || file.StartsWith("anim_", StringComparison.Ordinal))
                    return AssetType.Animation;

                if (dir.Contains("sounds") || dir.Contains("audio") || dir.Contains("music"))
                    return AssetType.Audio;

                if (dir.Contains("textures") || dir.Contains("texture"))
                    return AssetType.Texture;

                if (dir.Contains("levels") || dir.Contains("worlds") || dir.Contains("maps"))
                    return AssetType.Level;

                // Default: most .drm files are models/objects/characters/props
                // The DRM section-type check below will override this when it can
            }
        }

        // 2. Magic / DRM section type from decompressed bytes
        AssetType fromMagic = ClassifyAsset(peek, out _);
        if (fromMagic != AssetType.Unknown) return fromMagic;

        // 3. Final fallback: unresolved or unclassified .drm → treat as model (statistically correct)
        //    (statistically correct — the majority of SOTR DRM files are meshes/objects)
        if (peek.Length >= 4 && BitConverter.ToUInt32(peek, 0) == 23)
            return AssetType.StaticMesh;

        return AssetType.Unknown;
    }

    private static AssetType ClassifyAsset(byte[] data, out int ddsOffset)
    {
        ddsOffset = -1;
        if (data.Length < 4) return AssetType.Unknown;

        uint magic = BitConverter.ToUInt32(data, 0);

        // Raw DDS
        if (magic == 0x20534444) { ddsOffset = 0; return AssetType.Texture; }

        // Raw audio
        if (magic == 0x46464952 || magic == 0x5367674F) return AssetType.Audio;

        // DRM v23 wrapper
        if (magic == 23)
        {
            AssetType t = ClassifyDrm(data);
            if (t == AssetType.Texture)
            {
                // Find the DDS by computing the exact section-data start offset
                ddsOffset = FindDrmSectionDataOffset(data, sectionIndex: 0);
                return AssetType.Texture;
            }
            if (t != AssetType.Unknown) return t;
        }

        return AssetType.Unknown;
    }

    /// <summary>
    /// Computes the byte offset at which section[sectionIndex]'s data begins
    /// within a fully-decompressed DRM v23 buffer.
    ///
    /// DRM layout:
    ///   [28-byte fixed header]
    ///   [numObjects  × 4 bytes]
    ///   [numRelocs   × 8 bytes]
    ///   [numImports  × 8 bytes]
    ///   [numSections × 20-byte section headers]
    ///   [section data blocks — section[0].data | section[1].data | …]
    ///
    /// Each section's data is padded to its allocationSize (header field +8).
    /// </summary>
    private static int FindDrmSectionDataOffset(byte[] data, int sectionIndex)
    {
        if (data.Length < 28) return -1;

        uint numObjects  = BitConverter.ToUInt32(data,  4);
        uint numRelocs   = BitConverter.ToUInt32(data,  8);
        uint numImports  = BitConverter.ToUInt32(data, 12);
        uint numSections = BitConverter.ToUInt32(data, 16);

        if (numObjects > 65536 || numRelocs > 1_048_576 || numImports > 65536
         || numSections == 0   || numSections > 128) return -1;
        if (sectionIndex >= (int)numSections) return -1;

        long firstSectionHeader = 28L + numObjects * 4 + numRelocs * 8 + numImports * 8;
        long firstSectionData   = firstSectionHeader + numSections * 20;

        // Sum allocationSize of every section that precedes sectionIndex
        long dataOffset = firstSectionData;
        for (int s = 0; s < sectionIndex; s++)
        {
            long hdrOff    = firstSectionHeader + s * 20;
            if (hdrOff + 12 > data.Length) return -1;
            uint allocSize = BitConverter.ToUInt32(data, (int)(hdrOff + 8));
            dataOffset += allocSize;
        }

        if (dataOffset >= data.Length) return -1;

        // Verify DDS magic at the computed offset (±16 bytes for alignment slop)
        for (int delta = 0; delta <= 16; delta += 4)
        {
            long candidate = dataOffset + delta;
            if (candidate + 4 > data.Length) break;
            if (BitConverter.ToUInt32(data, (int)candidate) == 0x20534444)
                return (int)candidate;
        }

        // DDS magic not at expected position — do a broader scan within this section's range
        long secHdrOff = firstSectionHeader + sectionIndex * 20;
        if (secHdrOff + 8 > data.Length) return -1;
        uint dataSize = BitConverter.ToUInt32(data, (int)secHdrOff + 4);
        long scanEnd  = Math.Min(dataOffset + dataSize, data.Length - 4);
        for (long i = dataOffset; i <= scanEnd; i += 4)
        {
            if (BitConverter.ToUInt32(data, (int)i) == 0x20534444)
                return (int)i;
        }

        return -1;
    }

    private static AssetType ClassifyDrm(byte[] data)
    {
        if (data.Length < 28) return AssetType.Unknown;

        uint numObjects  = BitConverter.ToUInt32(data,  4);
        uint numRelocs   = BitConverter.ToUInt32(data,  8);
        uint numImports  = BitConverter.ToUInt32(data, 12);
        uint numSections = BitConverter.ToUInt32(data, 16);

        if (numObjects > 65536 || numRelocs > 1_048_576 || numImports > 65536
            || numSections == 0 || numSections > 128) return AssetType.Unknown;

        long off = 28L + numObjects * 4 + numRelocs * 8 + numImports * 8;
        if (off + 4 > data.Length) return AssetType.Unknown;

        uint sectionType = BitConverter.ToUInt32(data, (int)off);
        return sectionType switch
        {
            1  => AssetType.Animation,
            2  => AssetType.StaticMesh,
            5  => AssetType.Texture,
            13 => AssetType.Audio,
            14 => AssetType.Audio,
            _  => AssetType.Unknown
        };
    }

    private static void AnnotateDrmProps(byte[] data, Dictionary<string, object?> props)
    {
        if (data.Length < 28) return;

        uint numObjects  = BitConverter.ToUInt32(data,  4);
        uint numRelocs   = BitConverter.ToUInt32(data,  8);
        uint numImports  = BitConverter.ToUInt32(data, 12);
        uint numSections = BitConverter.ToUInt32(data, 16);

        if (numObjects > 65536 || numRelocs > 1_048_576 || numImports > 65536 || numSections > 128) return;

        props["DRM Version"]  = 23;
        props["DRM Sections"] = numSections;
        props["DRM Objects"]  = numObjects;
        props["DRM Imports"]  = numImports;

        long sectionOff = 28L + numObjects * 4 + numRelocs * 8 + numImports * 8;
        for (int s = 0; s < (int)numSections && sectionOff + 20 <= data.Length; s++, sectionOff += 20)
        {
            uint sType    = BitConverter.ToUInt32(data, (int)sectionOff);
            uint dataSize = BitConverter.ToUInt32(data, (int)sectionOff + 4);
            props[$"Section[{s}]"] = $"{DrmSectionTypeName(sType)}  ({dataSize:N0} B)";
        }
    }

    private static string DrmSectionTypeName(uint t) => t switch
    {
        0 => "Generic",   1 => "Animation",   2 => "RenderMesh",
        3 => "Havok",     4 => "Script",      5 => "Texture",
        6 => "Material",  7 => "Object",      8 => "CollisionMesh",
        9 => "Dependent", 10 => "ScriptLib",  11 => "ShaderLib",
        12 => "TRLang",   13 => "SoundFSB",   14 => "MusicFSB",
        15 => "Blueprint", _ => $"Type{t}"
    };

    // ── Type cache ────────────────────────────────────────────────────────────

    private string TypeCachePath(string gameDir)
    {
        ulong key = CdcHash64(gameDir.ToLowerInvariant());
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "GameAssetExplorer", $"sotr-{key:X16}-types.bin");
    }

    private void LoadTypeCache(string gameDir)
    {
        try
        {
            string path = TypeCachePath(gameDir);
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                ulong hash = br.ReadUInt64();
                var   type = (AssetType)br.ReadByte();
                _typeMap[hash] = type;
            }
        }
        catch { /* silently ignore corrupt cache */ }
    }

    private void SaveTypeCache(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || _typeMap.IsEmpty) return;
        try
        {
            string path = TypeCachePath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                var entries = _typeMap.ToArray();
                bw.Write(entries.Length);
                foreach (var (hash, type) in entries)
                {
                    bw.Write(hash);
                    bw.Write((byte)type);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOTR] Could not save type cache: {ex.Message}");
        }
    }

    // ── Hash list ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the 335K-path list bundled as an embedded resource.
    /// Hash function: FNV-64 applied directly to the path string chars (UTF-16),
    /// no lowercasing, no slash conversion — matches ShadowHash.Calculate64 exactly.
    /// </summary>
    private void LoadBundledHashList()
    {
        var asm    = Assembly.GetExecutingAssembly();
        const string res = "GameAssetExplorer.SotrEngine.SOTR_PC_Release.list";
        using var stream = asm.GetManifestResourceStream(res);
        if (stream == null)
        {
            Console.WriteLine("[SOTR] Embedded hash list not found — assets will show as hex hashes.");
            return;
        }

        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ulong hash = CdcHash64(line);
            _nameCache[hash] = line;
        }
    }

    /// <summary>
    /// FNV-1 64-bit — matches arcusmaximus ShadowHash.Calculate64 exactly.
    /// Applied to UTF-16 chars, no normalization.
    /// </summary>
    private static ulong CdcHash64(string str)
    {
        ulong hash = 0xCBF29CE484222325;
        foreach (char c in str)
            hash = (hash ^ c) * 0x100000001B3;
        return hash;
    }

    // ── Virtual path building ─────────────────────────────────────────────────

    private string MakeVirtualPath(TigerEntry entry)
    {
        string type  = TypeFolder(entry.NameHash);
        string name  = ResolveName(entry.NameHash);
        return $"/{type}/{name}";
    }

    private string TypeFolder(ulong hash)
    {
        if (!_typeMap.TryGetValue(hash, out var t)) t = AssetType.Unknown;
        return t switch
        {
            AssetType.Texture      => "textures",
            AssetType.StaticMesh
            or AssetType.SkeletalMesh => "models",
            AssetType.Audio        => "audio",
            AssetType.Animation    => "animations",
            AssetType.Level        => "levels",
            _                      => "other"
        };
    }

    private string ResolveName(ulong hash)
    {
        if (!_nameCache.TryGetValue(hash, out string? raw)) return $"{hash:X16}";

        // Strip "pcx64-w\" platform prefix, normalize to forward slashes
        if (raw.StartsWith("pcx64-w\\", StringComparison.OrdinalIgnoreCase))
            raw = raw["pcx64-w\\".Length..];
        return raw.Replace('\\', '/');
    }

    // ── AssetInfo builder ─────────────────────────────────────────────────────

    private AssetInfo EntryToInfo(TigerEntry entry, int readerId)
    {
        _typeMap.TryGetValue(entry.NameHash, out var type);
        bool resolved = _nameCache.ContainsKey(entry.NameHash);

        return new AssetInfo
        {
            VirtualPath      = MakeVirtualPath(entry),
            Name             = resolved
                                   ? Path.GetFileName(ResolveName(entry.NameHash))
                                   : $"{entry.NameHash:X16}",
            Type             = type,
            CompressedSize   = entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize,
            UncompressedSize = entry.DecompressedSize,
            ArchivePath      = _readers[readerId].IndexPath,
            EngineClassName  = "TigerEntry",
            IsEncrypted      = false,
        };
    }

    private static Dictionary<string, object?> BuildBaseProps(TigerEntry entry, TigerReader reader)
        => new()
        {
            ["Hash"]      = $"0x{entry.NameHash:X16}",
            ["Archive"]   = Path.GetFileName(reader.IndexPath),
            ["TigerPart"] = entry.TigerPart,
            ["Offset"]    = $"0x{entry.Offset:X8}",
            ["Locale"]    = DescribeLocale(entry.Locale),
            ["Priority"]  = entry.Priority,
        };

    // ── Locale ────────────────────────────────────────────────────────────────

    private static string DescribeLocale(ulong locale)
    {
        if (locale == 0)           return "common (language-independent)";
        if (locale == ulong.MaxValue) return "all locales";
        if ((locale & (locale - 1)) == 0) return SingleLocaleName(locale);
        return $"multi-locale (0x{locale:X16})";
    }

    private static string SingleLocaleName(ulong locale) => locale switch
    {
        0x0001 => "en",   0x0002 => "fr",    0x0004 => "de",
        0x0008 => "it",   0x0010 => "es",    0x0020 => "nl",
        0x0040 => "pl",   0x0080 => "pt-br", 0x0100 => "ru",
        0x0200 => "ja",   0x0400 => "ko",    0x0800 => "zh-tw",
        0x1000 => "zh-cn",0x2000 => "ar",    _ => $"locale-{locale:X}"
    };

    // ── Asset data builders ───────────────────────────────────────────────────

    private static TextureAssetData BuildTextureAsset(
        AssetInfo info, byte[] data, int ddsStart, Dictionary<string, object?> props)
    {
        var tex = new TextureAssetData { Info = info };
        var dds = data.AsSpan(ddsStart).ToArray();

        if (dds.Length >= 128 && dds[0] == 'D' && dds[1] == 'D' && dds[2] == 'S' && dds[3] == ' ')
        {
            tex.Height = (int)BitConverter.ToUInt32(dds, 12);
            tex.Width  = (int)BitConverter.ToUInt32(dds, 16);
            int mipCount = (int)BitConverter.ToUInt32(dds, 28);
            uint fourCC  = BitConverter.ToUInt32(dds, 84);
            tex.SourceFormat = fourCC switch
            {
                0x31545844 => "DXT1", 0x33545844 => "DXT3", 0x35545844 => "DXT5",
                0x30315844 => ReadDx10Format(dds), 0 => "RGBA",
                _ => $"FourCC:0x{fourCC:X8}"
            };
            tex.IsSrgb = tex.SourceFormat is "DXT1" or "DXT5" or "BC1" or "BC3" or "BC7";
            tex.Mips.Add(new MipData
            {
                Width = tex.Width, Height = tex.Height,
                Data = dds.Length > 128 ? dds[128..] : Array.Empty<byte>(),
            });
            props["Width"]    = tex.Width;
            props["Height"]   = tex.Height;
            props["Format"]   = tex.SourceFormat;
            props["MipCount"] = mipCount;
            if (ddsStart > 0) props["DrmHeaderBytes"] = ddsStart;
        }

        tex.RawProperties = props;
        return tex;
    }

    private static AudioAssetData BuildAudioAsset(
        AssetInfo info, byte[] data, Dictionary<string, object?> props)
    {
        var audio = new AudioAssetData { Info = info, RawAudioData = data };
        uint magic = data.Length >= 4 ? BitConverter.ToUInt32(data, 0) : 0;
        if (magic == 0x46464952 && data.Length >= 44)
        {
            audio.SourceFormat = "WAV";
            if (data[12] == 'f' && data[13] == 'm' && data[14] == 't')
            {
                audio.Channels   = BitConverter.ToUInt16(data, 22);
                audio.SampleRate = (int)BitConverter.ToUInt32(data, 24);
            }
        }
        else
        {
            audio.SourceFormat = "OGG";
        }
        audio.RawProperties = props;
        props["AudioFormat"] = audio.SourceFormat;
        props["SampleRate"]  = audio.SampleRate > 0 ? audio.SampleRate : (object?)"unknown";
        props["Channels"]    = audio.Channels   > 0 ? audio.Channels   : (object?)"unknown";
        return audio;
    }

    private static string ReadDx10Format(byte[] dds)
    {
        if (dds.Length < 132) return "DX10";
        uint dxgi = BitConverter.ToUInt32(dds, 128);
        return dxgi switch
        {
            71 => "BC1", 74 => "BC2", 77 => "BC3", 80 => "BC4",
            83 => "BC5", 95 => "BC6H", 98 => "BC7", 87 => "BGRA8", 28 => "RGBA8",
            _ => $"DXGI:{dxgi}"
        };
    }

    // ── Archive slug ─────────────────────────────────────────────────────────

    private static string ArchiveSlug(string indexPath)
    {
        string stem = Path.GetFileNameWithoutExtension(indexPath);
        if (stem.EndsWith(".000", StringComparison.Ordinal)) stem = stem[..^4];
        if (stem.Length > 4 && stem[^4] == '.' && stem[^3..].All(char.IsAsciiDigit))
            stem = stem[..^4];
        if (stem.Equals("bigfile", StringComparison.OrdinalIgnoreCase)) return "base";
        if (stem.StartsWith("bigfile.update", StringComparison.OrdinalIgnoreCase))
            return "update-" + stem["bigfile.update".Length..];
        if (stem.StartsWith("bigfile.dlc.", StringComparison.OrdinalIgnoreCase))
            return "dlc-" + stem["bigfile.dlc.".Length..].Replace('.', '-');
        int dot = stem.IndexOf('.');
        return dot >= 0 ? stem[(dot + 1)..].Replace('.', '-') : stem;
    }

    // ── Directory walk ────────────────────────────────────────────────────────

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            IEnumerable<string> files = Enumerable.Empty<string>();
            try { files = Directory.EnumerateFiles(dir, pattern); } catch { }
            foreach (var f in files) yield return f;
            IEnumerable<string> subs = Enumerable.Empty<string>();
            try { subs = Directory.EnumerateDirectories(dir); } catch { }
            foreach (var s in subs) queue.Enqueue(s);
        }
    }
}

/// <summary>Raw tiger entry for formats not yet fully decoded (materials, scripts, etc.)</summary>
public class SotrRawAssetData : AssetData
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
