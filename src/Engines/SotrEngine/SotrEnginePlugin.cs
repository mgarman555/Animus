using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Engine plugin for Shadow of the Tomb Raider (2018) PC — Foundation Engine (Eidos-Montréal).
///
/// How the game actually stores things, and why this plugin is shaped the way it is:
///
///   .tiger archives hold *files*, keyed by an FNV-1 64 hash of their path. Almost every one
///   of those files is a .drm — and a .drm is a manifest, not a container. It holds no pixels
///   and no vertices. What it holds is a table of the resources a piece of content needs,
///   each with the archive, part, offset and size where that resource's bytes live.
///
///   So the browsable asset is the *resource*, not the tiger entry. Mounting reads the
///   archive TOCs (fast); a background pass then parses every .drm and builds the resource
///   index that the tree is actually built from. That index is cached under %AppData% so the
///   second mount is instant.
///
/// Virtual path layout, type-first to match the other engine plugins:
///   /textures/{collection path}/Texture_{id}.dds
///   /models/{collection path}/Model_{id}.tr11modeldata
///   /animations/… /audio/… /materials/… /collision/… /data/… /scripts/… /strings/… /other/…
///   /collections/{path}.drm                 — the manifests themselves
///   /files/{path}                           — every other archive entry, so nothing is hidden
///
/// Asset naming comes from the bundled 335K-entry hash→path dictionary
/// (SOTR_PC_Release.list, MIT — arcusmaximus/TrRebootModTools), embedded in the assembly.
/// </summary>
public class SotrEnginePlugin : IGameEngine
{
    // ── Events / state ────────────────────────────────────────────────────────

    /// <summary>Fires once the background resource index finishes. Triggers a tree rebuild.</summary>
    public event EventHandler? TypeScanCompleted;

    private readonly TigerArchiveSet _archives = new();

    /// <summary>Every tiger TOC entry, after patch/DLC override resolution.</summary>
    private readonly List<(TigerEntry Entry, TigerReader Owner)> _files = new();

    /// <summary>Resource index built by the background pass, unique per (type, id, locale).</summary>
    private readonly ConcurrentDictionary<(SotrResourceType, int, ulong), IndexedResource> _resources = new();

    private readonly Dictionary<ulong, string> _nameCache = new();   // path hash → path
    private readonly Dictionary<string, object> _byPath   = new(StringComparer.OrdinalIgnoreCase);

    private IProgress<string>? _progress;
    private GameConfig?        _config;
    private volatile bool      _indexReady;

    private CancellationTokenSource? _indexCts;
    private Task?                    _indexTask;

    public string EngineName => "Foundation Engine";
    public string EngineId   => "SOTR";
    public bool   IsMounted  => _archives.Count > 0;

    public IReadOnlyList<string> SupportedVersions => new[] { "SOTR-PC" };
    public IReadOnlyList<string> ArchiveExtensions => new[] { ".tiger" };

    /// <summary>False while the background resource index is still running.</summary>
    public bool IsResourceIndexReady => _indexReady;

    /// <summary>Resources discovered so far by the background index.</summary>
    public int ResourceCount => _resources.Count;

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
        _config   = config;
        _progress = progress;

        progress?.Report("Loading asset name dictionary…");
        await Task.Run(LoadBundledHashList);
        progress?.Report($"  {_nameCache.Count:N0} paths loaded from built-in dictionary.");

        var indexFiles = SafeEnumerateFiles(config.GameDirectory, "*.tiger")
            .Where(f => Path.GetFileName(f).EndsWith(".000.tiger", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (indexFiles.Length == 0)
        {
            progress?.Report("No .tiger index files found. Point the game directory at the SOTR install folder.");
            return false;
        }

        progress?.Report($"Found {indexFiles.Length} archive(s). Reading TOCs…");

        await Task.Run(() =>
        {
            foreach (var path in indexFiles)
            {
                try
                {
                    var archive = new TigerReader(path);
                    archive.Open();
                    _archives.Add(archive);
                    progress?.Report($"  {Path.GetFileName(path)} → {archive.NumFiles:N0} entries " +
                                     $"(id {archive.Id}.{archive.SubId})");
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

            BuildFileIndex();
        });

        foreach (var shadowed in _archives.Shadowed)
            progress?.Report($"  Ignored {Path.GetFileName(shadowed.IndexPath)}: another archive " +
                             $"already claims id {shadowed.Id}.{shadowed.SubId}.");

        if (_files.Count == 0)
        {
            progress?.Report("Archives opened but no readable entries were found.");
            return false;
        }

        // Make the collections loadable straight away; the resource index adds to this later.
        RebuildPathLookup();

        int named = _files.Count(f => _nameCache.ContainsKey(f.Entry.NameHash));
        progress?.Report(
            $"Mounted {_files.Count:N0} files. {named:N0} named " +
            $"({100.0 * named / Math.Max(1, _files.Count):F0}%). Indexing resources…");

        if (LoadResourceCache(config.GameDirectory))
        {
            _indexReady = true;
            progress?.Report($"Resource index loaded from cache — {_resources.Count:N0} assets.");
            RebuildPathLookup();
            TypeScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _indexCts  = new CancellationTokenSource();
            var token  = _indexCts.Token;
            _indexTask = Task.Run(() => RunResourceIndex(token), token);
        }

        return true;
    }

    public async Task UnmountGameAsync()
    {
        // The index runs on a pool thread holding open FileStreams. Stop and join it before
        // disposing anything, or it races the teardown and reopens handles into a dead reader.
        _indexCts?.Cancel();
        if (_indexTask != null)
        {
            try { await _indexTask.ConfigureAwait(false); }
            catch { /* cancellation or a failed index must not block unmount */ }
        }
        _indexCts?.Dispose();
        _indexCts  = null;
        _indexTask = null;

        _archives.Dispose();
        _files.Clear();
        _resources.Clear();
        _nameCache.Clear();
        lock (_byPath) _byPath.Clear();
        _indexReady = false;
        _config = null;
    }

    /// <summary>
    /// Collapses TOC entries down to one per (nameHash, locale). Later archives — patches and
    /// DLC, which sort after the base bigfile — replace earlier ones, matching load order.
    /// </summary>
    private void BuildFileIndex()
    {
        var winner = new Dictionary<(ulong, ulong), (TigerEntry Entry, TigerReader Owner)>();

        foreach (var archive in _archives.Archives)
        {
            foreach (var entry in archive.Entries)
                winner[(entry.NameHash, entry.Locale)] = (entry, archive);
        }

        _files.AddRange(winner.Values);
    }

    // ── Background resource index ─────────────────────────────────────────────

    /// <summary>
    /// Parses every .drm collection and records the resources they point at. This is the pass
    /// that turns "a heap of hashed files" into a browsable, typed asset tree.
    /// </summary>
    private void RunResourceIndex(CancellationToken token)
    {
        try
        {
            var collections = _files
                .Where(IsCollection)
                .OrderBy(f => f.Entry.ArchivePart)
                .ThenBy(f => f.Entry.Offset)
                .ToList();

            _progress?.Report($"Indexing {collections.Count:N0} collections…");

            int done = 0;
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
                CancellationToken      = token,
            };

            Parallel.ForEach(collections, opts, item =>
            {
                if (token.IsCancellationRequested) return;

                var (entry, owner) = item;
                try
                {
                    byte[] raw = _archives.ReadEntry(entry, owner);
                    var collection = SotrDrmReader.TryParse(raw);
                    if (collection != null) RegisterResources(entry.NameHash, collection);
                }
                catch { /* a single unreadable collection must not abort the pass */ }

                int n = Interlocked.Increment(ref done);
                if (n % 2000 == 0 || n == collections.Count)
                    _progress?.Report($"Indexing resources… {n:N0} / {collections.Count:N0} " +
                                      $"({_resources.Count:N0} found)");
            });

            if (token.IsCancellationRequested) return;

            // Order matters: the lookup must be able to resolve a resource before
            // GetAllAssetsAsync is allowed to advertise it.
            RebuildPathLookup();
            _indexReady = true;
            SaveResourceCache(_config?.GameDirectory ?? "");

            _progress?.Report($"Resource index complete — {_resources.Count:N0} assets. Rebuilding tree…");
            TypeScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // unmounted mid-scan; nothing to report
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOTR] Resource index failed: {ex.Message}");
            _progress?.Report($"Resource index failed: {ex.Message}");
        }
    }

    private void RegisterResources(ulong collectionHash, SotrDrmReader collection)
    {
        foreach (var resource in collection.Resources)
        {
            if (!resource.Enabled || resource.Length == 0) continue;
            if (resource.Type == SotrResourceType.Unknown || resource.Type == SotrResourceType.Empty) continue;

            _resources.TryAdd(resource.Key, new IndexedResource
            {
                Resource         = resource,
                CollectionHash   = collectionHash,
            });
        }
    }

    private bool IsCollection((TigerEntry Entry, TigerReader Owner) file)
    {
        if (_nameCache.TryGetValue(file.Entry.NameHash, out string? name))
            return name.EndsWith(".drm", StringComparison.OrdinalIgnoreCase);

        // Unnamed entry: a v23 header in the first bytes identifies a collection.
        try   { return SotrDrmReader.LooksLikeDrm(_archives.PeekEntry(file.Entry, file.Owner)); }
        catch { return false; }
    }

    // ── Asset listing ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync()
    {
        var result = new List<AssetInfo>(_resources.Count + _files.Count);

        // Only publish resources once the index is complete and _byPath knows about them.
        // Listing them mid-scan would put assets in the tree that LoadAssetAsync cannot find.
        if (_indexReady)
        {
            foreach (var indexed in _resources.Values)
                result.Add(ResourceToInfo(indexed));
        }

        // Every archive entry is listed too, so nothing in the game is unreachable —
        // manifests under /collections, anything else (stream data, tocs) under /files.
        foreach (var (entry, owner) in _files)
            result.Add(FileToInfo(entry, owner));

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

    private void RebuildPathLookup()
    {
        lock (_byPath)
        {
            _byPath.Clear();
            foreach (var indexed in _resources.Values)
                _byPath[ResourceVirtualPath(indexed)] = indexed;

            foreach (var (entry, owner) in _files)
                _byPath[FileVirtualPath(entry)] = (entry, owner);
        }
    }

    // ── Asset loading ─────────────────────────────────────────────────────────

    public async Task<AssetData> LoadAssetAsync(AssetInfo asset)
    {
        object? target;
        lock (_byPath) _byPath.TryGetValue(asset.VirtualPath, out target);

        if (target == null)
            throw new Exception($"Asset not found: {asset.VirtualPath}");

        if (target is IndexedResource indexed)
            return await Task.Run(() => LoadResource(asset, indexed)).ConfigureAwait(false);

        var (entry, owner) = ((TigerEntry, TigerReader))target;
        return await Task.Run(() => LoadArchiveFile(asset, entry, owner)).ConfigureAwait(false);
    }

    private AssetData LoadResource(AssetInfo info, IndexedResource indexed)
    {
        var resource = indexed.Resource;
        byte[]? body = _archives.ReadResourceBody(resource);

        var props = new Dictionary<string, object?>
        {
            ["Resource Type"] = resource.Type.ToString(),
            ["Resource SubType"] = DescribeSubType(resource),
            ["Resource Id"]   = resource.Id,
            ["Locale"]        = DescribeLocale(resource.Locale),
            ["Archive"]       = $"{resource.ArchiveId}.{resource.ArchiveSubId} part {resource.ArchivePart}",
            ["Offset"]        = $"0x{resource.Offset:X8}",
            ["Size In Archive"] = resource.Length,
            ["Body Size"]     = resource.BodySize,
            ["RefDefinitions Size"] = resource.RefDefinitionsSize,
            ["Collection"]    = ResolveName(indexed.CollectionHash),
        };

        if (body == null || body.Length == 0)
        {
            props["_RawSize"] = 0;
            return new SotrRawAssetData { Info = info, RawData = Array.Empty<byte>(), RawProperties = props };
        }

        props["_RawSize"] = body.Length;

        if (resource.Type == SotrResourceType.Texture)
        {
            var texture = SotrTextureReader.TryRead(body);
            if (texture != null) return BuildTextureAsset(info, texture, props);
        }

        if (resource.Type == SotrResourceType.Model && resource.SubType == SotrResourceSubType.ModelData)
        {
            var mesh = SotrMeshParser.TryParse(body, info);
            if (mesh != null)
            {
                foreach (var (k, v) in props) mesh.RawProperties.TryAdd(k, v);
                info.Type = mesh.IsSkeletal ? AssetType.SkeletalMesh : AssetType.StaticMesh;
                mesh.Info = info;
                return mesh;
            }
        }

        if (resource.Type == SotrResourceType.SoundBank)
            return BuildAudioAsset(info, body, props);

        return new SotrRawAssetData { Info = info, RawData = body, RawProperties = props };
    }

    private AssetData LoadArchiveFile(AssetInfo info, TigerEntry entry, TigerReader owner)
    {
        byte[] raw = _archives.ReadEntry(entry, owner);

        var props = new Dictionary<string, object?>
        {
            ["Hash"]      = $"0x{entry.NameHash:X16}",
            ["Archive"]   = Path.GetFileName(owner.IndexPath),
            ["Tiger Part"] = entry.ArchivePart,
            ["Offset"]    = $"0x{entry.Offset:X8}",
            ["Locale"]    = DescribeLocale(entry.Locale),
            ["_RawSize"]  = raw.Length,
        };

        var collection = SotrDrmReader.TryParse(raw);
        if (collection != null)
        {
            props["DRM Version"]   = SotrDrmReader.DrmVersion;
            props["Resources"]     = collection.Resources.Count;
            props["Dependencies"]  = collection.Dependencies.Count;
            props["Main Resource"] = collection.MainResourceIndex >= 0 &&
                                     collection.MainResourceIndex < collection.Resources.Count
                ? DescribeResource(collection.Resources[collection.MainResourceIndex])
                : "(none)";

            var byType = collection.Resources
                .Where(r => r.Enabled)
                .GroupBy(r => r.Type)
                .OrderByDescending(g => g.Count());
            foreach (var group in byType)
                props[$"Contains[{group.Key}]"] = group.Count();

            for (int i = 0; i < collection.Dependencies.Count && i < 64; i++)
                props[$"Dependency[{i}]"] = collection.Dependencies[i];
        }

        return new SotrRawAssetData { Info = info, RawData = raw, RawProperties = props };
    }

    // ── Asset data builders ───────────────────────────────────────────────────

    private static TextureAssetData BuildTextureAsset(
        AssetInfo info, SotrTextureReader texture, Dictionary<string, object?> props)
    {
        var mips = texture.SplitMips();

        props["Width"]     = texture.Width;
        props["Height"]    = texture.Height;
        props["Format"]    = texture.FormatName;
        props["DXGI"]      = texture.Format;
        props["MipCount"]  = mips.Count;
        props["Cube Map"]  = texture.IsCubeMap;
        props["Depth"]     = texture.VolumeDepth;
        props["sRGB"]      = texture.IsSrgb;
        if (texture.HighResMipMapLevels > 0)
            props["HighResMipMapLevels"] = texture.HighResMipMapLevels;

        var tex = new TextureAssetData
        {
            Info             = info,
            Width            = texture.Width,
            Height           = texture.Height,
            SourceFormat     = texture.FormatName,
            // Carry the numeric format so the exporter never has to re-derive it from a name.
            SourceDxgiFormat = texture.Format,
            IsCubeMap        = texture.IsCubeMap,
            Depth            = texture.VolumeDepth,
            IsSrgb           = texture.IsSrgb,
            RawProperties    = props,
        };

        foreach (var (mipWidth, mipHeight, data) in mips)
            tex.Mips.Add(new MipData { Width = mipWidth, Height = mipHeight, Data = data });

        return tex;
    }

    private static AudioAssetData BuildAudioAsset(
        AssetInfo info, byte[] data, Dictionary<string, object?> props)
    {
        // SOTTR ships Wwise sound banks; individual streams live inside the .bnk.
        string format = data.Length >= 4 && data[0] == 'B' && data[1] == 'K' && data[2] == 'H' && data[3] == 'D'
            ? "Wwise BNK"
            : "Wwise";

        props["AudioFormat"] = format;

        return new AudioAssetData
        {
            Info          = info,
            RawAudioData  = data,
            SourceFormat  = format,
            RawProperties = props,
        };
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    private AssetInfo ResourceToInfo(IndexedResource indexed)
    {
        var resource = indexed.Resource;
        string path  = ResourceVirtualPath(indexed);

        // Name is extension-less by convention — exporters append their own.
        return new AssetInfo
        {
            VirtualPath      = path,
            Name             = Path.GetFileNameWithoutExtension(path),
            Type             = MapAssetType(resource),
            CompressedSize   = resource.Length,
            UncompressedSize = resource.RefDefinitionsSize + resource.BodySize,
            ArchivePath      = _archives.Find(resource.ArchiveId, resource.ArchiveSubId)?.IndexPath ?? string.Empty,
            EngineClassName  = DescribeSubType(resource),
            IsEncrypted      = false,
        };
    }

    private AssetInfo FileToInfo(TigerEntry entry, TigerReader owner)
    {
        string path = FileVirtualPath(entry);
        return new AssetInfo
        {
            VirtualPath      = path,
            Name             = Path.GetFileNameWithoutExtension(path),
            Type             = AssetType.Other,
            CompressedSize   = entry.CompressedSize > 0 ? entry.CompressedSize : entry.UncompressedSize,
            UncompressedSize = entry.UncompressedSize,
            ArchivePath      = owner.IndexPath,
            EngineClassName  = path.StartsWith("/collections/", StringComparison.Ordinal)
                                   ? "ResourceCollection"
                                   : "TigerFile",
            IsEncrypted      = false,
        };
    }

    private string ResourceVirtualPath(IndexedResource indexed)
    {
        var resource   = indexed.Resource;
        string folder  = TypeFolder(resource);
        string owner   = StripCollectionSuffix(ResolveName(indexed.CollectionHash));
        string name    = $"{resource.Type}_{resource.Id}{LocaleSuffix(resource.Locale)}{Extension(resource)}";
        return $"/{folder}/{owner}/{name}";
    }

    private string FileVirtualPath(TigerEntry entry)
    {
        string name   = ResolveName(entry.NameHash);
        string suffix = LocaleSuffix(entry.Locale);

        if (suffix.Length > 0)
        {
            string ext = Path.GetExtension(name);
            name = name[..^ext.Length] + suffix + ext;
        }

        return name.EndsWith(".drm", StringComparison.OrdinalIgnoreCase)
            ? $"/collections/{name}"
            : $"/files/{name}";
    }

    /// <summary>
    /// Filename tag distinguishing localised variants of the same asset. The same resource id
    /// exists once per language, so without this every localisation collapses onto one path and
    /// all but the last would be unreachable. Mirrors ShadowArchiveSet.MakeLocaleSuffix.
    /// </summary>
    private static string LocaleSuffix(ulong locale)
    {
        if (locale == ulong.MaxValue) return string.Empty;

        uint text  = (uint)(locale & 0xFFFFFFF);
        uint voice = (uint)((locale >> 28) & 0xFFFFFFF);
        int  plat  = (int)(locale >> 56);

        string textName  = text  == 0xFFFFFFF ? "alltxt" : LanguageName(text);
        string voiceName = voice == 0xFFFFFFF ? "allvo"  : LanguageName(voice);
        string platName  = plat switch { 0xFF => "allplt", 0x20 => "neutral", _ => $"{plat:X2}" };

        return $"_{textName}_{voiceName}_{platName}";
    }

    private string ResolveName(ulong hash)
    {
        if (!_nameCache.TryGetValue(hash, out string? raw)) return $"unnamed/{hash:X16}";

        if (raw.StartsWith(SotrDrmReader.PlatformPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[SotrDrmReader.PlatformPrefix.Length..];
        return raw.Replace('\\', '/');
    }

    private static string StripCollectionSuffix(string path)
        => path.EndsWith(".drm", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    /// <summary>File extensions matching TrRebootModTools' ShadowResourceNaming.</summary>
    private static string Extension(SotrResource r) => r.Type switch
    {
        SotrResourceType.Animation              => ".tr11anim",
        SotrResourceType.AnimationLib           => ".tr11animlib",
        SotrResourceType.CollisionModel         => ".tr11cmodel",
        SotrResourceType.Dtp                    => ".tr11dtp",
        SotrResourceType.GlobalContentReference => ".tr11contentref",
        SotrResourceType.Material               => ".tr11material",
        SotrResourceType.ObjectReference        => ".tr11objectref",
        SotrResourceType.BlendShapeDriver       => ".tr11drivers",
        SotrResourceType.Script                 => ".tr11script",
        SotrResourceType.ShaderLib              => ".tr11shaderlib",
        SotrResourceType.SoundBank              => ".bnk",
        SotrResourceType.Texture                => ".dds",
        SotrResourceType.Model => r.SubType switch
        {
            SotrResourceSubType.Model      => ".tr11model",
            SotrResourceSubType.ModelData  => ".tr11modeldata",
            SotrResourceSubType.ShResource => ".tr11shresource",
            SotrResourceSubType.CubeLut    => ".tr11cubelut",
            _                              => ".tr11model",
        },
        _ => ".bin",
    };

    private static string TypeFolder(SotrResource r) => r.Type switch
    {
        SotrResourceType.Texture        => "textures",
        SotrResourceType.Model          => "models",
        SotrResourceType.Animation
            or SotrResourceType.AnimationLib => "animations",
        SotrResourceType.SoundBank      => "audio",
        SotrResourceType.Material       => "materials",
        SotrResourceType.CollisionModel => "collision",
        SotrResourceType.Dtp            => "data",
        SotrResourceType.LocalString    => "strings",
        SotrResourceType.Script
            or SotrResourceType.ShaderLib    => "scripts",
        _ => "other",
    };

    private static AssetType MapAssetType(SotrResource r) => r.Type switch
    {
        SotrResourceType.Texture      => AssetType.Texture,
        SotrResourceType.Animation
            or SotrResourceType.AnimationLib => AssetType.Animation,
        SotrResourceType.SoundBank    => AssetType.Audio,
        SotrResourceType.Material     => AssetType.Material,
        SotrResourceType.Dtp          => AssetType.DataTable,
        SotrResourceType.LocalString  => AssetType.StringTable,
        SotrResourceType.Model        => r.SubType == SotrResourceSubType.ModelData
                                            ? AssetType.StaticMesh
                                            : AssetType.Other,
        _ => AssetType.Other,
    };

    private static string DescribeSubType(SotrResource r) => r.SubType switch
    {
        SotrResourceSubType.Texture    => "Texture",
        SotrResourceSubType.Model      => "Model",
        SotrResourceSubType.ModelData  => "ModelData",
        SotrResourceSubType.ShResource => "ShResource",
        SotrResourceSubType.CubeLut    => "CubeLut",
        0                              => r.Type.ToString(),
        _                              => $"{r.Type}:{r.SubType}",
    };

    private static string DescribeResource(SotrResource r)
        => $"{r.Type}:{r.Id}{(r.SubType != 0 ? $" ({DescribeSubType(r)})" : "")}";

    // ── Locale ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shadow packs three fields into the 64-bit locale: text language in bits 0-27,
    /// voice language in bits 28-55, platform in bits 56-63.
    /// </summary>
    private static string DescribeLocale(ulong locale)
    {
        if (locale == ulong.MaxValue) return "all locales";
        if (locale == 0)              return "none";
        return LocaleSuffix(locale).TrimStart('_').Replace("_", " / ");
    }

    private static string LanguageName(uint flags) => flags switch
    {
        0x0001 => "en",    0x0002 => "fr",    0x0004 => "de",
        0x0008 => "it",    0x0010 => "es",    0x0020 => "nl",
        0x0040 => "pl",    0x0080 => "pt-br", 0x0100 => "ru",
        0x0200 => "ja",    0x0400 => "ko",    0x0800 => "zh-tw",
        0x1000 => "zh-cn", 0x2000 => "ar",    0      => "none",
        _      => $"0x{flags:X}",
    };

    // ── Resource index cache ──────────────────────────────────────────────────

    private const string CACHE_MAGIC   = "SOTRIDX1";
    private const int    CACHE_VERSION = 1;

    private string CachePath(string gameDir)
    {
        ulong key = CdcHash64(gameDir.ToLowerInvariant());
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "GameAssetExplorer", $"sotr-{key:X16}-resources.bin");
    }

    /// <summary>
    /// Fingerprint of the mounted archive set. Any archive added, removed, patched or resized
    /// changes this, which invalidates the cache rather than serving a stale index.
    /// </summary>
    private long ArchiveSignature()
    {
        long signature = _archives.Archives.Count;
        foreach (var a in _archives.Archives.OrderBy(a => a.IndexPath, StringComparer.OrdinalIgnoreCase))
        {
            signature = signature * 31 + a.NumFiles;
            try { signature = signature * 31 + new FileInfo(a.IndexPath).Length; } catch { }
        }
        return signature;
    }

    private bool LoadResourceCache(string gameDir)
    {
        try
        {
            string path = CachePath(gameDir);
            if (!File.Exists(path)) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8);

            if (br.ReadString() != CACHE_MAGIC) return false;
            if (br.ReadInt32()  != CACHE_VERSION) return false;
            if (br.ReadInt64()  != ArchiveSignature()) return false;

            int count = br.ReadInt32();
            if (count < 0 || count > 20_000_000) return false;

            for (int i = 0; i < count; i++)
            {
                var resource = new SotrResource
                {
                    Type               = (SotrResourceType)br.ReadByte(),
                    SubType            = br.ReadInt32(),
                    Id                 = br.ReadInt32(),
                    Locale             = br.ReadUInt64(),
                    BodySize           = br.ReadUInt32(),
                    RefDefinitionsSize = br.ReadUInt32(),
                    ArchiveId          = br.ReadByte(),
                    ArchiveSubId       = br.ReadByte(),
                    ArchivePart        = br.ReadInt16(),
                    Offset             = br.ReadUInt32(),
                    Length             = br.ReadUInt32(),
                    Enabled            = true,
                };
                ulong collectionHash = br.ReadUInt64();

                _resources.TryAdd(resource.Key, new IndexedResource
                {
                    Resource       = resource,
                    CollectionHash = collectionHash,
                });
            }

            return _resources.Count > 0;
        }
        catch
        {
            _resources.Clear();
            return false;
        }
    }

    private void SaveResourceCache(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || _resources.IsEmpty) return;

        try
        {
            string path = CachePath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(CACHE_MAGIC);
                bw.Write(CACHE_VERSION);
                bw.Write(ArchiveSignature());

                var entries = _resources.Values.ToArray();
                bw.Write(entries.Length);

                foreach (var indexed in entries)
                {
                    var r = indexed.Resource;
                    bw.Write((byte)r.Type);
                    bw.Write(r.SubType);
                    bw.Write(r.Id);
                    bw.Write(r.Locale);
                    bw.Write(r.BodySize);
                    bw.Write(r.RefDefinitionsSize);
                    bw.Write((byte)r.ArchiveId);
                    bw.Write((byte)r.ArchiveSubId);
                    bw.Write((short)r.ArchivePart);
                    bw.Write(r.Offset);
                    bw.Write(r.Length);
                    bw.Write(indexed.CollectionHash);
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOTR] Could not save resource cache: {ex.Message}");
        }
    }

    // ── Hash list ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the 335K-path list bundled as an embedded resource. The hash is FNV-1 64 over the
    /// path's UTF-16 chars with no lowercasing and no slash conversion — the paths must be
    /// hashed exactly as written, matching ShadowHash.Calculate64.
    /// </summary>
    private void LoadBundledHashList()
    {
        var asm = Assembly.GetExecutingAssembly();
        const string res = "GameAssetExplorer.SotrEngine.SOTR_PC_Release.list";

        using var stream = asm.GetManifestResourceStream(res);
        if (stream == null)
        {
            Console.WriteLine("[SOTR] Embedded hash list not found — assets will show as hex hashes.");
            return;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            _nameCache[CdcHash64(line)] = line;
        }
    }

    private static ulong CdcHash64(string str)
    {
        ulong hash = 0xCBF29CE484222325;
        foreach (char c in str)
            hash = (hash ^ c) * 0x100000001B3;
        return hash;
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

    // ── Index entry ───────────────────────────────────────────────────────────

    private sealed class IndexedResource
    {
        public SotrResource Resource       { get; init; } = null!;
        /// <summary>Path hash of the .drm that referenced this resource — used for naming.</summary>
        public ulong        CollectionHash { get; init; }
    }
}

/// <summary>Raw resource bytes for types that have no dedicated decoder yet.</summary>
public class SotrRawAssetData : RawAssetData
{
}
