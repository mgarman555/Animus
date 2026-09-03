using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Engine plugin for Naughty Dog games (The Last of Us Part I &amp; II, Uncharted 4 PC).
///
/// Supports two loading modes:
///
/// 1. PSARC Mode (TLOU1-PC, Uncharted4-PC):
///    Game directory contains .psarc archives. All archives are mounted at startup
///    and their manifests merged into one virtual file index.
///
/// 2. PAK Directory Mode (TLOU2-PC):
///    TLOU2 PC ships .psarc files using the DSAR encrypted format, which requires
///    the user's Python extractor to pre-extract them into individual Naughty Dog
///    .pak asset files. Point the game directory at the root of those extracted files
///    and the plugin will scan and list all .pak files as assets.
///    Magic numbers: 0xA79 (U4/TLOU2), 0xA7D (TLOU Part I remaster)
///
/// No AES key needed — ND PC archives are unencrypted at the PSARC level.
/// </summary>
public class NaughtyDogPlugin : IGameEngine
{
    // PSARC mode
    private readonly List<PsarcReader>                        _readers      = new();
    private readonly List<(PsarcEntry Entry, string Archive)> _psarcIndex   = new();
    private readonly Dictionary<string, PsarcReader>          _readerByPath = new(StringComparer.OrdinalIgnoreCase);

    // PAK directory mode
    private readonly List<NdPakEntry>                         _pakIndex = new();

    // Skeleton resolution across part paks (ellie-body.pak → ellie-skel.pak)
    private NdSkeletonLocator? _skeletonLocator;

    // Full-resolution texture dictionary (texturedict3 paks)
    private readonly NdTextureDictionary _texDict = new();
    private Task? _texDictBuildTask;
    private IProgress<string>? _texDictExternalProgress;

    private GameConfig? _currentConfig;
    private bool _pakMode;

    public string EngineName => "Naughty Dog Engine";
    public string EngineId   => "ND";
    public bool   IsMounted  => _readers.Count > 0 || _pakIndex.Count > 0;

    /// <summary>Whether the full-res texture dictionary has finished loading or building.</summary>
    public bool IsTexDictLoaded    => _texDict.IsLoaded;

    /// <summary>Number of textures indexed in the texture dictionary (0 until loaded).</summary>
    public int  TexDictEntryCount  => _texDict.EntryCount;

    /// <summary>
    /// Attach an external progress reporter for the background texture-dictionary build.
    /// Call this after mounting so the asset browser can display build progress in the status bar.
    /// </summary>
    public void SetBackgroundProgress(IProgress<string>? progress)
        => _texDictExternalProgress = progress;

    public IReadOnlyList<string> SupportedVersions  => new[] { "TLOU1-PC", "TLOU2-PC", "Uncharted4-PC" };
    public IReadOnlyList<string> ArchiveExtensions  => new[] { ".psarc", ".pak" };

    // ── Detection ─────────────────────────────────────────────────────────────

    public float DetectEngine(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return 0f;

        // Strong signal: .psarc archives (TLOU1, Uncharted4)
        if (SafeEnumerateFiles(gameDirectory, "*.psarc").Any()) return 0.90f;

        // Also detect pre-extracted .pak directories (TLOU2 via Python extractor).
        // We look for the ND .pak magic (0xA79 / 0xA7D) in at least one file.
        var firstPak = SafeEnumerateFiles(gameDirectory, "*.pak").FirstOrDefault();
        if (firstPak != null && IsNdPak(firstPak)) return 0.88f;

        return 0f;
    }

    /// <summary>Peek at a file's first 4 bytes to check for Naughty Dog .pak magic.</summary>
    private static bool IsNdPak(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            if (f.Length < 4) return false;
            var buf = new byte[4];
            _ = f.Read(buf, 0, 4);
            uint magic = BitConverter.ToUInt32(buf, 0);
            // Magic values: 0xA79 (2681 – U4/TLOU2), 0xA7D (2685 – TLOUP1),
            //               0x10A79 (68217 – large pak), 0x80000A79 (streaming pak)
            return magic == 0xA79 || magic == 0xA7D || magic == 0x10A79 || magic == 0x80000A79;
        }
        catch { return false; }
    }

    // ── Mount / Unmount ───────────────────────────────────────────────────────

    public async Task<bool> MountGameAsync(GameConfig config, IProgress<string>? progress = null)
    {
        _currentConfig = config;

        // ── Try PSARC mode first ───────────────────────────────────────────────
        var archives = SafeEnumerateFiles(config.GameDirectory, "*.psarc")
                            .OrderBy(f => f).ToArray();

        if (archives.Length > 0)
        {
            _pakMode = false;
            try
            {
                bool psarcResult = await MountPsarcArchivesAsync(archives, config, progress);
                if (psarcResult) return true;
                // PSARC found but yielded nothing — fall through to PAK scan below
            }
            catch (NotSupportedException)
            {
                // DSAR encrypted .psarc — can't read natively.
                // Clear any partial state and fall through to PAK directory mode.
                foreach (var r in _readers) r.Dispose();
                _readers.Clear();
                _psarcIndex.Clear();
                progress?.Report("DSAR .psarc detected — switching to PAK directory scan…");
            }
        }

        // ── Fall back to PAK directory mode (pre-extracted TLOU2 files) ───────
        // Scan .pak plus common image/audio formats from the extracted directory.
        // Don't filter by magic bytes — user explicitly chose this directory.
        var collectedFiles = SafeEnumerateFiles(config.GameDirectory, "*.pak").ToList();
        foreach (var ext in new[] { "*.png", "*.jpg", "*.dds", "*.tga", "*.wav", "*.ogg" })
            collectedFiles.AddRange(SafeEnumerateFiles(config.GameDirectory, ext));
        collectedFiles.Sort(StringComparer.OrdinalIgnoreCase);

        if (collectedFiles.Count > 0)
        {
            _pakMode = true;
            return await MountPakDirectoryAsync(collectedFiles.ToArray(), config, progress);
        }

        progress?.Report("No .psarc or .pak files found. " +
                          "For TLOU2: use your Python extractor to convert .psarc → .pak first, " +
                          "then point the game directory at the extracted folder.");
        Console.WriteLine($"[ND] Nothing to mount in: {config.GameDirectory}");
        return false;
    }

    private async Task<bool> MountPsarcArchivesAsync(
        string[] archives, GameConfig config, IProgress<string>? progress)
    {
        progress?.Report($"Found {archives.Length} .psarc archive(s). Reading manifests…");

        await Task.Run(() =>
        {
            for (int i = 0; i < archives.Length; i++)
            {
                string file = archives[i];
                progress?.Report($"[{i + 1}/{archives.Length}] {Path.GetFileName(file)}");
                try
                {
                    var reader = new PsarcReader(file);
                    reader.Open();
                    _readers.Add(reader);
                    _readerByPath[file] = reader;
                    foreach (var entry in reader.Entries)
                        _psarcIndex.Add((entry, file));
                }
                catch (NotSupportedException ex)
                {
                    // DSAR (TLOU2 encrypted variant) — guide user to PAK mode
                    progress?.Report(
                        $"DSAR encrypted archive detected ({Path.GetFileName(file)}). " +
                        "TLOU2 requires pre-extraction: run your Python .psarc→.pak converter " +
                        "and point the game directory at the extracted .pak files.");
                    throw new NotSupportedException(
                        $"{Path.GetFileName(file)}: {ex.Message}\n\n" +
                        "Tip: run your Python extractor on the .psarc files, then configure " +
                        "the game directory to point at the folder containing the extracted .pak files.",
                        ex);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ND] Skipped {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        });

        var total = _psarcIndex.Count;
        progress?.Report($"Mounted {total:N0} files from {_readers.Count} archive(s).");
        Console.WriteLine($"[ND] PSARC: {config.DisplayName} ({total:N0} files)");
        return total > 0;
    }

    private Task<bool> MountPakDirectoryAsync(
        string[] pakFiles, GameConfig config, IProgress<string>? progress)
    {
        progress?.Report($"PAK directory mode: found {pakFiles.Length} .pak file(s)…");

        var root = config.GameDirectory.TrimEnd('\\', '/');

        foreach (var pak in pakFiles)
        {
            // Virtual path = relative path from game directory, forward-slashes
            var relative = pak.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? pak[(root.Length + 1)..].Replace('\\', '/')
                : Path.GetFileName(pak);

            long size = 0;
            try { size = new FileInfo(pak).Length; } catch { }

            _pakIndex.Add(new NdPakEntry
            {
                FilePath    = pak,
                VirtualPath = relative,
                FileSize    = size,
            });
        }

        _skeletonLocator = new NdSkeletonLocator(_pakIndex);

        progress?.Report($"Indexed {_pakIndex.Count:N0} .pak assets.");
        Console.WriteLine($"[ND] PAK dir: {config.DisplayName} ({_pakIndex.Count:N0} files)");

        // Kick off texture dictionary build in the background (non-blocking)
        var dictPaks = DiscoverTextureDictPaks(config.GameDirectory);
        if (dictPaks.Count > 0)
        {
            string cacheKey = SanitiseName(config.DisplayName);
            _texDictBuildTask = Task.Run(async () =>
            {
                try
                {
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GameAssetExplorer", "texdict-build.log");

                    try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }
                    var progress = new Progress<string>(msg =>
                    {
                        Console.WriteLine($"[ND-TexDict] {msg}");
                        try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {msg}\n"); }
                        catch { }
                        _texDictExternalProgress?.Report(msg);
                    });

                    await _texDict.EnsureBuiltAsync(dictPaks, cacheKey, progress)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ND-TexDict] Build failed: {ex.Message}");
                }
            });
        }

        return Task.FromResult(_pakIndex.Count > 0);
    }

    public async Task UnmountGameAsync()
    {
        foreach (var r in _readers) r.Dispose();
        _readers.Clear();
        _readerByPath.Clear();
        _psarcIndex.Clear();
        _pakIndex.Clear();
        _skeletonLocator = null;
        _pakMode = false;
        _currentConfig = null;
        _texDictBuildTask = null;
        await Task.CompletedTask;
    }

    // ── Asset listing ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync()
    {
        if (_pakMode)
        {
            var results = new List<AssetInfo>(_pakIndex.Count);
            foreach (var entry in _pakIndex)
                results.Add(PakEntryToInfo(entry));
            return Task.FromResult<IReadOnlyList<AssetInfo>>(results);
        }
        else
        {
            var results = new List<AssetInfo>(_psarcIndex.Count);
            foreach (var (entry, archive) in _psarcIndex)
                results.Add(EntryToInfo(entry, archive));
            return Task.FromResult<IReadOnlyList<AssetInfo>>(results);
        }
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
        if (_pakMode)
            return await LoadPakFileAssetAsync(asset);
        else
            return await LoadPsarcAssetAsync(asset);
    }

    private async Task<AssetData> LoadPakFileAssetAsync(AssetInfo asset)
    {
        var entry = _pakIndex.FirstOrDefault(e => e.VirtualPath == asset.VirtualPath);
        if (entry == null)
            throw new Exception($"PAK asset not found: {asset.VirtualPath}");

        var rawData = await Task.Run(() => File.ReadAllBytes(entry.FilePath));

        var ext = Path.GetExtension(asset.VirtualPath).ToLowerInvariant();

        // DDS textures
        if (ext == ".dds")
            return BuildTextureAssetData(asset, rawData);

        // Animation paks: decode clips directly. There is no mesh in these, so the viewer
        // shows them as a clip list until one is retargeted onto a character.
        if (ext == ".pak" && asset.Type == AssetType.Animation)
        {
            var animReader = new NdPakReader(rawData);
            if (animReader.ReadHeader())
            {
                var clips = NdAnimParser.DecodeAll(animReader, skeleton: null, asset.Name, out var animReport);
                var result = clips.Count > 0
                    ? clips[0]
                    : new AnimationAssetData
                    {
                        Info = asset, ClipName = asset.Name, FrameCount = 0,
                    };
                result.Info = asset;
                result.RawProperties["Scan"]    = animReport.Summarise();
                result.RawProperties["Outcome"] = animReport.Outcome;
                foreach (var r in animReport.Resources.Take(200))
                    result.RawProperties[$"Resource[{result.RawProperties.Count}]"] = $"{r.Type}: {r.Name}";
                if (clips.Count > 1)
                    result.RawProperties["AdditionalClips"] = clips.Count - 1;
                return result;
            }
        }

        // Try to parse ND .pak as mesh geometry (actors, levels, or any unidentified pak)
        if (ext == ".pak" && asset.Type is AssetType.SkeletalMesh or AssetType.StaticMesh
                                          or AssetType.Level or AssetType.Unknown)
        {
            // Legacy parser is the primary mesh source. The new fmt_nd_pak-style port
            // (NdPakReader + NdMeshExtractor) runs alongside as research-only — its output
            // is logged for comparison but NEVER replaces the legacy mesh until it reliably
            // produces ≥ legacy's submesh count.
            NdPakReader? reader = null;
            try
            {
                reader = new NdPakReader(rawData);
                if (reader.ReadHeader())
                {
                    reader.LogDiagnostics(asset.Name);
                    var newMesh = NdMeshExtractor.TryExtract(reader, asset);
                    if (newMesh != null)
                    {
                        int s = newMesh.Lods.Sum(l => l.Submeshes.Count);
                        int v = newMesh.Lods.Sum(l => l.VertexCount);
                        GameAssetExplorer.Core.Services.Log.Info(
                            $"NdMeshExtractor[{asset.Name}]: research path produced {s} submeshes / {v:N0} verts (advisory only)");
                    }
                }
                else
                {
                    reader = null;
                    GameAssetExplorer.Core.Services.Log.Warn($"NdPakReader: ReadHeader returned false for {asset.Name}");
                }
            }
            catch (Exception ex)
            {
                reader = null;
                GameAssetExplorer.Core.Services.Log.Error($"NdPakReader threw on {asset.Name}", ex);
            }

            // Legacy parser does the actual mesh extraction
            var mesh = NdPakMeshParser.TryParse(rawData, asset);

            // If the new reader is healthy and we have a legacy mesh, apply the m_papTransform
            // matrices it discovered to the legacy parser's vertex buffers. This is the part
            // of the new path we can use safely — it's a non-destructive post-process and
            // explains the "parts look split" complaint (each submesh has a world transform
            // we were ignoring).
            if (reader != null && mesh != null)
            {
                try
                {
                    var applied = NdTransformApplier.ApplyTo(reader, mesh, asset.Name);
                    if (applied > 0)
                        GameAssetExplorer.Core.Services.Log.Info(
                            $"NdTransformApplier[{asset.Name}]: applied {applied} m_papTransform matrix(es) to legacy mesh");
                }
                catch (Exception ex)
                {
                    GameAssetExplorer.Core.Services.Log.Warn($"NdTransformApplier failed on {asset.Name}: {ex.Message}");
                }
            }
            if (mesh != null)
            {
                // ── Skeleton ──────────────────────────────────────────────────
                // Character part paks carry skin weights but no joints; the joints live once
                // in a shared <name>-skel.pak. Without this link the weights index bones that
                // do not exist locally and nothing can be posed.
                try
                {
                    mesh.Skeleton = _skeletonLocator?.Resolve(asset, reader, mesh);
                    if (mesh.Skeleton is { Bones.Count: > 0 } sk)
                    {
                        mesh.RawProperties["Skeleton"] =
                            $"{sk.Bones.Count} bones (from {(string.IsNullOrEmpty(sk.SourceName) ? "this pak" : sk.SourceName)})";
                    }
                    else if (mesh.Lods.Any(l => l.Skin != null))
                    {
                        mesh.RawProperties["Skeleton"] = "not found — mesh is skinned but no matching *-skel.pak was located";
                    }
                }
                catch (Exception ex)
                {
                    GameAssetExplorer.Core.Services.Log.Warn($"Skeleton resolve failed for {asset.Name}: {ex.Message}");
                }

                // Offer every rig in the game so a mis-resolved skeleton can be corrected in
                // the viewer rather than silently deforming the character wrongly.
                if (_skeletonLocator is { } locator)
                {
                    mesh.AvailableSkeletonPaths = locator.AllSkeletonPaks.Select(p => p.FilePath).ToList();
                    var meshRef = mesh;
                    mesh.ResolveSkeletonOverride = path => locator.LoadFrom(path, meshRef);
                }

                // ── Animations ────────────────────────────────────────────────
                AttachAnimationSources(mesh, asset);

                // Merge full metadata (all VRAM_DESC records, resource type summary) into props
                foreach (var (k, v) in NdPakMeshParser.ParseMetadata(rawData))
                    mesh.RawProperties.TryAdd(k, v);

                // If the dict is still building, give it up to 5 s before proceeding
                if (!_texDict.IsLoaded && _texDictBuildTask != null)
                    await Task.WhenAny(_texDictBuildTask, Task.Delay(5000)).ConfigureAwait(false);

                // Upgrade to full-resolution texture if the dictionary is ready
                if (_texDict.IsLoaded && !string.IsNullOrEmpty(mesh.DiffuseTexturePath))
                {
                    var fullRes = await _texDict.LookupAsync(mesh.DiffuseTexturePath)
                        .ConfigureAwait(false);
                    if (fullRes.HasValue)
                    {
                        mesh.DiffuseTextureData   = fullRes.Value.Data;
                        mesh.DiffuseTextureWidth  = fullRes.Value.Width;
                        mesh.DiffuseTextureHeight = fullRes.Value.Height;
                        mesh.DiffuseTextureFormat = fullRes.Value.Format;
                        mesh.RawProperties["Texture"] =
                            $"{fullRes.Value.Width}×{fullRes.Value.Height} {fullRes.Value.Format} [full-res]";
                        Console.WriteLine(
                            $"[ND] Full-res texture {fullRes.Value.Width}×{fullRes.Value.Height}" +
                            $" {fullRes.Value.Format} loaded for {asset.Name}");
                    }
                    else
                    {
                        // Dict loaded but hash not found — show thumbnail + hint
                        string existingTex = mesh.RawProperties.TryGetValue("Texture", out var tv)
                            ? tv?.ToString() ?? "" : "";
                        mesh.RawProperties["Texture"] = $"{existingTex} [thumbnail only — hash not in dict]";
                    }
                }

                // Resolve each submesh's own diffuse to full-res so the viewer can texture
                // submeshes individually (body = tank top + pants + shoes, each its own map).
                // Many submeshes share a texPath (e.g. head partition0/1), so cache per call.
                if (_texDict.IsLoaded)
                {
                    var perPath = new Dictionary<string, (byte[] Data, int W, int H, string Fmt)?>(
                        StringComparer.OrdinalIgnoreCase);
                    int resolved = 0;
                    foreach (var lod in mesh.Lods)
                    foreach (var sm in lod.Submeshes)
                    {
                        if (string.IsNullOrEmpty(sm.DiffuseTexturePath)) continue;
                        if (!perPath.TryGetValue(sm.DiffuseTexturePath, out var hit))
                            perPath[sm.DiffuseTexturePath] = hit =
                                await _texDict.LookupAsync(sm.DiffuseTexturePath).ConfigureAwait(false);
                        if (hit is { } t)
                        {
                            sm.DiffuseTextureData   = t.Data;
                            sm.DiffuseTextureWidth  = t.W;
                            sm.DiffuseTextureHeight = t.H;
                            sm.DiffuseTextureFormat = t.Fmt;
                            resolved++;
                        }
                    }
                    if (resolved > 0)
                        Console.WriteLine($"[ND] Resolved full-res diffuse for {resolved} submesh(es) of {asset.Name}");
                }
                return mesh;
            }
        }

        // Fallback: raw blob — run metadata scan so the info panel shows something useful
        var meta = ext == ".pak"
            ? await Task.Run(() => NdPakMeshParser.ParseMetadata(rawData)).ConfigureAwait(false)
            : new Dictionary<string, object?>();

        meta["_FileSize"] = entry.FileSize;
        meta["_FilePath"] = entry.FilePath;

        return new NdRawAssetData
        {
            Info          = asset,
            RawData       = rawData,
            RawProperties = meta,
        };
    }

    // ── Animation wiring ──────────────────────────────────────────────────────

    /// <summary>
    /// List the animation paks that belong to this character and hand the mesh a callback that
    /// decodes one on demand. Naughty Dog names them <c>anim-&lt;character&gt;-*.pak</c>, and a
    /// full set is far too large to decode eagerly, so nothing is read here beyond the index.
    /// </summary>
    private void AttachAnimationSources(MeshAssetData mesh, AssetInfo asset)
    {
        if (_pakIndex.Count == 0) return;

        var stems = NameStems(asset.Name).ToList();
        var matches = new List<NdPakEntry>();

        foreach (var entry in _pakIndex)
        {
            string file = Path.GetFileNameWithoutExtension(entry.VirtualPath);
            var tokens = file.Split('-', StringSplitOptions.RemoveEmptyEntries);

            // Match on the "anim" TOKEN rather than an "anim-" prefix: the Remastered build
            // names its packages "t2r-anim-ellie-workbench", which a prefix test misses.
            if (!IsAnimPakName(tokens)) continue;

            // Likewise, match the character on a token boundary so "ellie" cannot match
            // inside an unrelated word.
            if (stems.Any(stem => ContainsTokenRun(tokens, stem.Split('-', StringSplitOptions.RemoveEmptyEntries))))
                matches.Add(entry);
        }

        foreach (var m in matches.OrderBy(m => m.VirtualPath, StringComparer.OrdinalIgnoreCase))
        {
            mesh.AnimationSources.Add(new AnimationSourceRef
            {
                DisplayName = Path.GetFileNameWithoutExtension(m.VirtualPath),
                Locator     = m.FilePath,
                EngineId    = EngineId,
                SizeBytes   = m.FileSize,
            });
        }

        if (mesh.AnimationSources.Count > 0)
            mesh.RawProperties["Animations"] = $"{mesh.AnimationSources.Count} anim pak(s) found for this character";

        var skeleton = mesh.Skeleton;
        mesh.ResolveAnimations = src => Task.Run<IReadOnlyList<AnimationAssetData>>(
            () => DecodeAnimationPak(src, skeleton));
    }

    /// <summary>Read one anim pak and decode whatever clips it yields.</summary>
    private static IReadOnlyList<AnimationAssetData> DecodeAnimationPak(
        AnimationSourceRef src, SkeletonData? skeleton)
    {
        try
        {
            var bytes = File.ReadAllBytes(src.Locator);
            var reader = new NdPakReader(bytes);
            if (!reader.ReadHeader())
            {
                GameAssetExplorer.Core.Services.Log.Warn(
                    $"NdAnimParser[{src.DisplayName}]: not a readable ND pak");
                return Array.Empty<AnimationAssetData>();
            }

            var clips = NdAnimParser.DecodeAll(reader, skeleton, src.DisplayName, out var report);
            foreach (var c in clips)
            {
                c.SkeletonPath = skeleton?.SourceName ?? string.Empty;
                c.RawProperties["Source"]   = src.DisplayName;
                c.RawProperties["Decoder"]  = report.Layout;
                c.RawProperties["Outcome"]  = report.Outcome;
            }

            // Even when nothing decodes, surface what the pak contains so the UI can say why.
            if (clips.Count == 0 && report.Resources.Count > 0)
            {
                var placeholder = new AnimationAssetData
                {
                    Info       = new AssetInfo { Name = src.DisplayName, Type = AssetType.Animation },
                    ClipName   = src.DisplayName,
                    FrameCount = 0,
                };
                placeholder.RawProperties["Outcome"] = report.Outcome;
                placeholder.RawProperties["Scan"]    = report.Summarise();
                foreach (var r in report.Resources.Take(64))
                    placeholder.JointNames.Add($"{r.Type}: {r.Name}");
                return new[] { placeholder };
            }

            return clips;
        }
        catch (Exception ex)
        {
            GameAssetExplorer.Core.Services.Log.Warn(
                $"NdAnimParser[{src.DisplayName}]: {ex.Message}");
            return Array.Empty<AnimationAssetData>();
        }
    }

    /// <summary>"abby-prisoner-hair" → "abby-prisoner-hair", "abby-prisoner", "abby".</summary>
    private static IEnumerable<string> NameStems(string assetName)
    {
        var parts = assetName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (int keep = parts.Length; keep >= 1; keep--)
            yield return string.Join('-', parts.Take(keep));
    }

    /// <summary>
    /// True when one of a filename's '-'-separated tokens is "anim". Covers both the base
    /// game's <c>anim-melee-ellie-npc-t2</c> and the Remastered build's
    /// <c>t2r-anim-ellie-workbench</c>.
    /// </summary>
    private static bool IsAnimPakName(string[] tokens)
        => tokens.Any(t => t.Equals("anim", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="needle"/> appears as a consecutive token run.</summary>
    private static bool ContainsTokenRun(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < needle.Length; k++)
                if (!haystack[i + k].Equals(needle[k], StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    private async Task<AssetData> LoadPsarcAssetAsync(AssetInfo asset)
    {
        var match = _psarcIndex.FirstOrDefault(e => e.Entry.Path == asset.VirtualPath);
        if (match.Entry == null)
            throw new Exception($"Asset not found in index: {asset.VirtualPath}");

        var reader = FindReader(match.Archive)
            ?? throw new Exception($"Archive reader not open: {match.Archive}");

        var rawData = await Task.Run(() => reader.ExtractFile(match.Entry));

        return Path.GetExtension(asset.VirtualPath).ToLowerInvariant() switch
        {
            ".dds" => BuildTextureAssetData(asset, rawData),
            _      => new NdRawAssetData
            {
                Info          = asset,
                RawData       = rawData,
                RawProperties = new Dictionary<string, object?>
                {
                    ["_Archive"]          = Path.GetFileName(match.Archive),
                    ["_UncompressedSize"] = match.Entry.UncompressedSize,
                }
            }
        };
    }

    // ── Texture (DDS) loader ─────────────────────────────────────────────────

    private static TextureAssetData BuildTextureAssetData(AssetInfo info, byte[] ddsData)
    {
        // DDS header layout (reference: https://docs.microsoft.com/en-us/windows/win32/direct3ddds/dds-header)
        // Bytes 0-3:   magic "DDS "
        // Bytes 4-7:   header size (124)
        // Bytes 12-15: height
        // Bytes 16-19: width
        // Bytes 84-87: FourCC (pixel format)

        var tex = new TextureAssetData { Info = info };

        if (ddsData.Length >= 128 &&
            ddsData[0] == 'D' && ddsData[1] == 'D' && ddsData[2] == 'S' && ddsData[3] == ' ')
        {
            tex.Height = (int)BitConverter.ToUInt32(ddsData, 12);
            tex.Width  = (int)BitConverter.ToUInt32(ddsData, 16);

            uint fourCC = BitConverter.ToUInt32(ddsData, 84);
            tex.SourceFormat = fourCC switch
            {
                0x31545844 => "DXT1",
                0x33545844 => "DXT3",
                0x35545844 => "DXT5",
                0x30315844 => ReadDx10Format(ddsData),  // DX10 extended header
                0           => "RGBA",
                _           => $"FourCC:0x{fourCC:X8}"
            };

            // Raw DDS data (minus the 128-byte header) as mip 0
            tex.Mips.Add(new MipData
            {
                Width  = tex.Width,
                Height = tex.Height,
                Data   = ddsData.Length > 128 ? ddsData[128..] : Array.Empty<byte>()
            });
        }

        tex.RawProperties = new Dictionary<string, object?>
        {
            ["Width"]  = tex.Width,
            ["Height"] = tex.Height,
            ["Format"] = tex.SourceFormat,
        };

        return tex;
    }

    private static string ReadDx10Format(byte[] dds)
    {
        // DX10 header starts at byte 148 (after standard 128-byte header + 20-byte DX10 extension)
        if (dds.Length < 132) return "DX10";
        uint dxgiFormat = BitConverter.ToUInt32(dds, 128);
        return dxgiFormat switch
        {
            71  => "BC1",
            74  => "BC2",
            77  => "BC3",
            80  => "BC4",
            83  => "BC5",
            95  => "BC6H",
            98  => "BC7",
            87  => "BGRA8",
            28  => "RGBA8",
            _   => $"DXGI:{dxgiFormat}"
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AssetInfo EntryToInfo(PsarcEntry entry, string archivePath) => new()
    {
        VirtualPath      = entry.Path,
        Name             = Path.GetFileNameWithoutExtension(entry.Path),
        Type             = InferAssetType(entry.Path),
        CompressedSize   = (long)entry.UncompressedSize,   // exact size not stored per-entry in PSARC
        UncompressedSize = (long)entry.UncompressedSize,
        ArchivePath      = archivePath,
        EngineClassName  = string.Empty,
        IsEncrypted      = false
    };

    private PsarcReader? FindReader(string archivePath)
        => _readerByPath.TryGetValue(archivePath, out var r) ? r : null;

    private static AssetInfo PakEntryToInfo(NdPakEntry entry) => new()
    {
        VirtualPath      = entry.VirtualPath,
        Name             = Path.GetFileNameWithoutExtension(entry.VirtualPath),
        Type             = InferAssetType(entry.VirtualPath),
        CompressedSize   = entry.FileSize,
        UncompressedSize = entry.FileSize,
        ArchivePath      = Path.GetDirectoryName(entry.FilePath) ?? string.Empty,
        EngineClassName  = "NdPak",
        IsEncrypted      = false
    };

    /// <summary>
    /// Enumerate files recursively, skipping any directories that throw access-denied.
    /// Directory.GetFiles with AllDirectories crashes on Program Files subdirectories.
    /// </summary>
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

    /// <summary>
    /// Finds all texturedict3 pak files under the game directory.
    /// TLOU2 layout: {root}/*/texturedict3/*.pak
    /// </summary>
    private static IReadOnlyList<string> DiscoverTextureDictPaks(string gameDirectory)
    {
        var results = new List<string>();
        foreach (var pak in SafeEnumerateFiles(gameDirectory, "*.pak"))
        {
            var lower = pak.ToLowerInvariant();
            if (lower.Contains("texturedict") && (lower.EndsWith("-dict.pak") || lower.Contains("-dict")))
                results.Add(pak);
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static string SanitiseName(string name)
        => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    private static AssetType InferAssetType(string path)
    {
        var ext   = Path.GetExtension(path).ToLowerInvariant();
        var lower = path.ToLowerInvariant();

        return ext switch
        {
            ".dds" or ".png" or ".tga"            => AssetType.Texture,
            ".wav" or ".ogg" or ".mp3" or ".opus" => AssetType.Audio,
            // ND .pak files — infer type from folder/name convention
            ".pak" when lower.Contains("texturedict") || lower.Contains("vram")
                                                  => AssetType.Texture,
            // Naughty Dog's clip container. The test is on the FILE name's tokens — so an
            // actor pak merely living under a path containing "anim" is not misfiled, and a
            // Remastered "t2r-anim-*.pak" under actorNN/ is not missed. It has to come before
            // the "actor" case below, which matches the whole path.
            ".pak" when IsAnimPakName(Path.GetFileNameWithoutExtension(path)
                            .Split('-', StringSplitOptions.RemoveEmptyEntries))
                                                  => AssetType.Animation,
            ".pak" when lower.Contains("actor")   => AssetType.SkeletalMesh,
            ".pak" when lower.Contains("anim")    => AssetType.Animation,
            ".pak" when lower.Contains("sound") || lower.Contains("audio")
                                                  => AssetType.Audio,
            // pak68 = environment/level geometry (res-*, san-*, wat-*, afm-*, sle-*, lev-cin-*, part-level-*)
            ".pak" when lower.Contains("/pak68/") || lower.Contains("\\pak68\\")
                                                  => AssetType.Level,
            ".pak"                                => AssetType.Unknown,
            _ when lower.Contains("/texture")     => AssetType.Texture,
            _ when lower.Contains("/model")
                || lower.Contains("/mesh")        => AssetType.StaticMesh,
            _ when lower.Contains("/anim")        => AssetType.Animation,
            _ when lower.Contains("/sound")
                || lower.Contains("/audio")       => AssetType.Audio,
            _ when lower.Contains("/material")    => AssetType.Material,
            _                                     => AssetType.Unknown
        };
    }
}

/// <summary>Represents one .pak file in the pre-extracted PAK directory (TLOU2 mode).</summary>
public sealed class NdPakEntry
{
    /// <summary>Absolute path to the .pak file on disk.</summary>
    public string FilePath    { get; init; } = string.Empty;
    /// <summary>Virtual path shown in the asset browser (relative to game directory).</summary>
    public string VirtualPath { get; init; } = string.Empty;
    /// <summary>File size in bytes.</summary>
    public long   FileSize    { get; init; }
}

/// <summary>Raw asset data for Naughty Dog formats we can't yet decode further.</summary>
public class NdRawAssetData : AssetData
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
