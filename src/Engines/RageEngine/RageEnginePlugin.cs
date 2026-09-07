using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// Engine plugin for GTA V (PC), RAGE / RPF7.
///
/// RDR 2 is deliberately not claimed here. RPF8 is a different container: a longer header with a
/// decryption tag, a platform id and a 256-byte RSA signature, 24-byte entries, hash-only
/// filenames, and a cipher (TFIT) whose keys no public tool holds. Nothing in this file reads it.
///
/// Archive tables of contents are NG-encrypted and NG is not AES, so there is no key you can type
/// into a text box. Point EngineSpecificSettings["KeysDirectory"] at a CodeWalker key dump, or
/// drop the four .dat files in %AppData%\GameAssetExplorer\RageKeys and it is found automatically.
/// See <see cref="GtaKeys"/>.
/// </summary>
public class RageEnginePlugin : IGameEngine
{
    private readonly List<RpfArchive> _roots = new();
    private readonly List<AssetInfo>  _assets = new();

    /// <summary>Virtual path (forward slashes, lowercase) to the entry that produced it.</summary>
    private readonly Dictionary<string, RpfFileEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private GtaKeys? _keys;

    public string EngineName => "RAGE Engine";
    public string EngineId   => "RAGE";
    public bool   IsMounted  => _assets.Count > 0;

    public IReadOnlyList<string> SupportedVersions => new[] { "GTA5-RPF7" };
    public IReadOnlyList<string> ArchiveExtensions => new[] { ".rpf" };

    /// <summary>Encryption type counts from the last mount. The first thing to check when a mount looks wrong.</summary>
    public IReadOnlyDictionary<RpfEncryption, int> EncryptionHistogram => _encryptionHistogram;
    private readonly Dictionary<RpfEncryption, int> _encryptionHistogram = new();

    // ── Detection ─────────────────────────────────────────────────────────────

    public float DetectEngine(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return 0f;

        var names = SafeEnumerateFiles(gameDirectory, "*.rpf")
                        .Select(f => Path.GetFileName(f).ToLowerInvariant())
                        .ToHashSet();
        if (names.Count == 0) return 0f;

        bool gta5 = names.Contains("common.rpf") || names.Contains("update.rpf")
                 || names.Any(n => n.Length == 8 && n.StartsWith("x64") && n.EndsWith(".rpf"));

        return gta5 ? 0.95f : 0.50f;
    }

    // ── Mount ─────────────────────────────────────────────────────────────────

    public async Task<bool> MountGameAsync(GameConfig config, IProgress<string>? progress = null)
    {
        Reset();

        config.EngineSpecificSettings.TryGetValue("KeysDirectory", out var keysDir);
        if (!GtaKeys.TryLoadFromKnownLocations(keysDir, out _keys, out string keyError))
        {
            // Not fatal on its own: an all-OPEN install still mounts. Encrypted archives will
            // report per-archive below, and the histogram makes the situation obvious.
            progress?.Report("No RAGE key set loaded — encrypted archives will be skipped.");
            Console.WriteLine($"[RAGE] {keyError}");
        }
        else
        {
            Console.WriteLine($"[RAGE] Keys loaded from {_keys!.SourceDirectory}");
        }

        progress?.Report("Scanning for .rpf archives…");

        // Deterministic order. It decides which DLC or mod wins when two archives define the same
        // archetype, so it must be reproducible run to run.
        var archives = SafeEnumerateFiles(config.GameDirectory, "*.rpf")
                          .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                          .ToArray();

        if (archives.Length == 0)
        {
            Console.WriteLine($"[RAGE] No .rpf files under {config.GameDirectory}");
            progress?.Report("No .rpf archives found.");
            return false;
        }

        if (archives.Any(f => f.Replace('/', '\\').Contains("\\mods\\", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("[RAGE] WARNING: a mods folder is present. Modded archives override base "
                            + "game assets by name hash, so exports may not be vanilla geometry.");
            progress?.Report("Warning: mods folder detected — exports may not be vanilla.");
        }

        var failures = new List<string>();

        await Task.Run(() =>
        {
            for (int i = 0; i < archives.Length; i++)
            {
                string file = archives[i];
                progress?.Report($"[{i + 1}/{archives.Length}] {Path.GetFileName(file)}");

                try
                {
                    var root = new RpfArchive(file);
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var br = new BinaryReader(fs))
                    {
                        root.ScanStructure(br, _keys, err => failures.Add(err));
                    }
                    _roots.Add(root);
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            BuildIndex();
        });

        foreach (var (enc, count) in _encryptionHistogram.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"[RAGE] encryption {enc,-5} : {count,5} archive(s)");

        if (failures.Count > 0)
        {
            Console.WriteLine($"[RAGE] {failures.Count} archive(s) failed to open. First few:");
            foreach (var f in failures.Take(5)) Console.WriteLine($"[RAGE]   {f}");
        }

        int rpfCount = _roots.Sum(r => r.EnumerateArchives().Count());
        Console.WriteLine($"[RAGE] Mounted {config.DisplayName}: {_assets.Count:N0} files across "
                        + $"{rpfCount:N0} archive(s) ({_roots.Count} on disk).");
        progress?.Report($"Mounted {_assets.Count:N0} files from {rpfCount:N0} archive(s).");

        return _assets.Count > 0;
    }

    private void BuildIndex()
    {
        foreach (var root in _roots)
        {
            foreach (var archive in root.EnumerateArchives())
            {
                _encryptionHistogram[archive.Encryption] =
                    _encryptionHistogram.GetValueOrDefault(archive.Encryption) + 1;

                foreach (var entry in archive.Files)
                {
                    string vpath = entry.Path.Replace('\\', '/');

                    // Two archives can legitimately define the same path (base game vs DLC vs mods).
                    // Later in mount order wins, matching how the game resolves overrides.
                    _byPath[vpath] = entry;

                    _assets.Add(new AssetInfo
                    {
                        VirtualPath      = vpath,
                        Name             = Path.GetFileNameWithoutExtension(entry.Name),
                        Type             = InferAssetType(entry.NameLower),
                        CompressedSize   = entry.FileSize,
                        UncompressedSize = entry.GetFileSize(),
                        ArchivePath      = archive.PhysicalPath,
                        EngineClassName  = Path.GetExtension(entry.NameLower).TrimStart('.'),
                        IsEncrypted      = entry.IsEncrypted,
                    });
                }
            }
        }
    }

    public Task UnmountGameAsync()
    {
        Reset();
        _keys = null;
        return Task.CompletedTask;
    }

    private void Reset()
    {
        _roots.Clear();
        _assets.Clear();
        _byPath.Clear();
        _encryptionHistogram.Clear();
    }

    // ── Asset listing ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync()
        => Task.FromResult<IReadOnlyList<AssetInfo>>(_assets);

    public Task<IReadOnlyList<AssetInfo>> GetAssetsAtPathAsync(string virtualPath)
    {
        string prefix = virtualPath.Replace('\\', '/').TrimEnd('/');
        var results = _assets
            .Where(a => a.VirtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult<IReadOnlyList<AssetInfo>>(results);
    }

    // ── Asset loading ─────────────────────────────────────────────────────────

    public async Task<AssetData> LoadAssetAsync(AssetInfo asset)
    {
        if (!_byPath.TryGetValue(asset.VirtualPath, out var entry))
            throw new FileNotFoundException($"Asset not mounted: {asset.VirtualPath}");

        var archive = entry.Archive
            ?? throw new InvalidOperationException($"Entry has no owning archive: {entry.Path}");

        byte[] data = await Task.Run(() => archive.ExtractFile(entry, _keys))
            ?? throw new InvalidDataException($"Could not extract {entry.Path}.");

        var props = new Dictionary<string, object?>
        {
            ["_Archive"]     = archive.Path,
            ["_Physical"]    = archive.PhysicalPath,
            ["_Encryption"]  = archive.Encryption.ToString(),
            ["_FileOffset"]  = entry.FileOffset,
            ["_FileSize"]    = entry.FileSize,
            ["_IsResource"]  = entry is RpfResourceFileEntry,
        };

        if (entry is RpfResourceFileEntry res)
        {
            props["_ResourceVersion"] = res.Version;
            props["_SystemSize"]      = res.SystemSize;
            props["_GraphicsSize"]    = res.GraphicsSize;
        }

        return new RageRawAssetData
        {
            Info          = asset,
            RawData       = data,
            RawProperties = props,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();

            IEnumerable<string> files = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            foreach (var f in files) yield return f;

            IEnumerable<string> subdirs = Array.Empty<string>();
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            foreach (var s in subdirs) queue.Enqueue(s);
        }
    }

    private static AssetType InferAssetType(string nameLower) => Path.GetExtension(nameLower) switch
    {
        ".ytd"                        => AssetType.Texture,        // texture dictionary
        ".ydr" or ".ydd" or ".yft"
             or ".ypt" or ".yld"      => AssetType.StaticMesh,     // drawable / dictionary / fragment
        ".ycd"                        => AssetType.Animation,      // clip dictionary
        ".awc"                        => AssetType.Audio,
        ".ymap" or ".ytyp" or ".ymt"  => AssetType.Level,          // placement / archetypes / metadata
        ".ybn" or ".ynv" or ".yed"    => AssetType.Other,          // bounds / navmesh / expressions
        ".ysc"                        => AssetType.Blueprint,      // script
        ".dds"                        => AssetType.Texture,
        ".xml" or ".meta" or ".dat"   => AssetType.DataTable,
        _                             => AssetType.Unknown,
    };
}

/// <summary>Raw bytes out of an RPF, decrypted and inflated. Format decoding comes later.</summary>
public class RageRawAssetData : AssetData
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
