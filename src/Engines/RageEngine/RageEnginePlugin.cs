using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// Engine plugin for Rockstar Games titles using the RAGE (Rockstar Advanced Game Engine).
///
/// Supported games:
///   GTA V (PC)   — RPF7, AES-256 encrypted (key required for most archives)
///   RDR 2 (PC)   — RPF8, unencrypted
///
/// Asset formats inside RPF archives:
///   .ytd  — texture dictionary (DDS textures bundled together)
///   .ydr  — drawable / static mesh
///   .ydd  — drawable dictionary (multiple meshes)
///   .yft  — fragment (destructible/rigged mesh)
///   .ycd  — clip dictionary (animations)
///   .awc  — audio wave container
///   .ysc  — script
///   .ymap — map placement data
///
/// For GTA V: supply the AES key in EngineSpecificSettings["AesKey"] (64 hex chars, no 0x).
/// The key is publicly documented in community tools (OpenIV, CodeWalker).
/// RDR 2 requires no key.
/// </summary>
public class RageEnginePlugin : IGameEngine
{
    private readonly List<RpfReader>                        _readers      = new();
    private readonly List<(RpfEntry Entry, string Archive)> _index        = new();
    private readonly Dictionary<string, RpfReader>          _readerByPath = new(StringComparer.OrdinalIgnoreCase);
    private GameConfig? _currentConfig;

    public string EngineName => "RAGE Engine";
    public string EngineId   => "RAGE";
    public bool   IsMounted  => _readers.Count > 0;

    public IReadOnlyList<string> SupportedVersions  => new[] { "GTA5-RPF7", "RDR2-RPF8" };
    public IReadOnlyList<string> ArchiveExtensions  => new[] { ".rpf" };

    // ── Detection ─────────────────────────────────────────────────────────────

    public float DetectEngine(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return 0f;

        var rpfs = SafeEnumerateFiles(gameDirectory, "*.rpf").ToArray();
        if (rpfs.Length == 0) return 0f;

        bool hasGta5 = rpfs.Any(f => Path.GetFileName(f).Equals("update.rpf", StringComparison.OrdinalIgnoreCase)
                                  || Path.GetFileName(f).Equals("common.rpf", StringComparison.OrdinalIgnoreCase));
        bool hasRdr2 = rpfs.Any(f => Path.GetFileName(f).StartsWith("appdata0_", StringComparison.OrdinalIgnoreCase));

        return (hasGta5 || hasRdr2) ? 0.95f : 0.80f;
    }

    // ── Mount / Unmount ───────────────────────────────────────────────────────

    public async Task<bool> MountGameAsync(GameConfig config, IProgress<string>? progress = null)
    {
        _currentConfig = config;

        byte[]? aesKey = null;
        if (config.EngineSpecificSettings.TryGetValue("AesKey", out var keyHex) &&
            !string.IsNullOrEmpty(keyHex))
        {
            try { aesKey = HexToBytes(keyHex.Replace("0x", "").Replace(" ", "")); }
            catch { Console.WriteLine("[RAGE] AES key is invalid hex — ignoring."); }
        }

        progress?.Report("Scanning for .rpf archives…");
        var archives = SafeEnumerateFiles(config.GameDirectory, "*.rpf")
                                .OrderBy(f => f)
                                .ToArray();

        if (archives.Length == 0)
        {
            Console.WriteLine($"[RAGE] No .rpf files found in {config.GameDirectory}");
            return false;
        }

        progress?.Report($"Found {archives.Length} archive(s). Reading file tables…");

        await Task.Run(() =>
        {
            int i = 0;
            foreach (var file in archives)
            {
                i++;
                progress?.Report($"[{i}/{archives.Length}] {Path.GetFileName(file)}");
                try
                {
                    var reader = new RpfReader(file);
                    reader.Open(aesKey);
                    _readers.Add(reader);
                    _readerByPath[file] = reader;
                    foreach (var entry in reader.Entries)
                        _index.Add((entry, file));
                }
                catch (Exception ex)
                {
                    // Encrypted archive without key, or corrupt file — skip silently
                    Console.WriteLine($"[RAGE] Skipped {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        });

        progress?.Report($"Mounted {_index.Count:N0} files from {_readers.Count} archive(s).");
        Console.WriteLine($"[RAGE] Mounted: {config.DisplayName} ({_index.Count:N0} files)");
        return _index.Count > 0;
    }

    public async Task UnmountGameAsync()
    {
        foreach (var r in _readers) r.Dispose();
        _readers.Clear();
        _readerByPath.Clear();
        _index.Clear();
        _currentConfig = null;
        await Task.CompletedTask;
    }

    // ── Asset listing ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync()
    {
        var results = new List<AssetInfo>(_index.Count);
        foreach (var (entry, archive) in _index)
            results.Add(EntryToInfo(entry, archive));
        return Task.FromResult<IReadOnlyList<AssetInfo>>(results);
    }

    public async Task<IReadOnlyList<AssetInfo>> GetAssetsAtPathAsync(string virtualPath)
    {
        var all        = await GetAllAssetsAsync();
        var normalized = virtualPath.TrimEnd('/').ToLowerInvariant();
        return all.Where(a =>
        {
            var dir = Path.GetDirectoryName(a.VirtualPath)?.Replace('\\', '/').ToLowerInvariant() ?? "";
            return dir.StartsWith(normalized);
        }).ToList();
    }

    // ── Asset loading ─────────────────────────────────────────────────────────

    public async Task<AssetData> LoadAssetAsync(AssetInfo asset)
    {
        var match = _index.FirstOrDefault(e => e.Entry.VirtualPath == asset.VirtualPath);
        if (match.Entry == null)
            throw new Exception($"Asset not found: {asset.VirtualPath}");

        var reader = FindReaderForArchive(match.Archive)
            ?? throw new Exception($"Reader not found for: {match.Archive}");

        var rawData = await Task.Run(() => reader.ExtractFile(match.Entry));

        return new RageRawAssetData
        {
            Info       = asset,
            RawData    = rawData,
            RawProperties = new Dictionary<string, object?>
            {
                ["_Archive"]    = Path.GetFileName(match.Archive),
                ["_FileSize"]   = match.Entry.FileSize,
                ["_OnDiskSize"] = match.Entry.OnDiskSize,
                ["_IsResource"] = match.Entry.IsResource,
            }
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AssetInfo EntryToInfo(RpfEntry entry, string archivePath) => new()
    {
        VirtualPath      = entry.VirtualPath,
        Name             = Path.GetFileNameWithoutExtension(entry.Name),
        Type             = InferAssetType(entry.Name),
        CompressedSize   = entry.OnDiskSize,
        UncompressedSize = entry.FileSize,
        ArchivePath      = archivePath,
        EngineClassName  = string.Empty,
        IsEncrypted      = false   // per-file encryption not supported yet
    };

    private RpfReader? FindReaderForArchive(string archivePath)
        => _readerByPath.TryGetValue(archivePath, out var r) ? r : _readers.FirstOrDefault();

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            IEnumerable<string> files = Enumerable.Empty<string>();
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            foreach (var f in files) yield return f;

            IEnumerable<string> subdirs = Enumerable.Empty<string>();
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            foreach (var s in subdirs) queue.Enqueue(s);
        }
    }

    private static AssetType InferAssetType(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".ytd"              => AssetType.Texture,       // texture dictionary
            ".ydr" or ".ydd"
                or ".yft"       => AssetType.StaticMesh,    // mesh types
            ".ycd"              => AssetType.Animation,     // clip dictionary
            ".awc"              => AssetType.Audio,         // audio wave container
            ".dds"              => AssetType.Texture,
            _                   => AssetType.Unknown
        };
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) throw new ArgumentException("Hex string must have even length.");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}

/// <summary>Raw RPF file data for formats we haven't decoded further yet.</summary>
public class RageRawAssetData : AssetData
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
