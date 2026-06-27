using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.UnrealEngine;

/// <summary>
/// Unreal Engine 4/5 plugin built on top of CUE4Parse — the same library FModel uses.
/// Supports .pak archives (UE4) and .utoc/.ucas (UE5 IO Store format).
/// Handles AES encryption, which most modern UE games use.
/// </summary>
public class UnrealEnginePlugin : IGameEngine
{
    private DefaultFileProvider? _provider;
    private GameConfig? _currentConfig;

    public string EngineName => "Unreal Engine";
    public string EngineId   => "UE";
    public bool   IsMounted  => _provider != null;

    public IReadOnlyList<string> SupportedVersions => new[]
    {
        // UE4 full range — 4.0 is ancient but Hellblade 1 uses ~4.16
        "4.0",  "4.1",  "4.2",  "4.3",  "4.4",  "4.5",
        "4.6",  "4.7",  "4.8",  "4.9",  "4.10", "4.11",
        "4.12", "4.13", "4.14", "4.15", "4.16", "4.17",
        "4.18", "4.19", "4.20", "4.21", "4.22", "4.23",
        "4.24", "4.25", "4.26", "4.27",
        // UE5
        "5.0",  "5.1",  "5.2",  "5.3",  "5.4",  "5.5",  "5.6"
    };

    public IReadOnlyList<string> ArchiveExtensions => new[] { ".pak", ".utoc", ".ucas" };

    public float DetectEngine(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return 0f;

        try
        {
            // Use safe enumeration to avoid UnauthorizedAccessException on Program Files subdirs
            bool hasPak  = SafeEnumerateFiles(gameDirectory, "*.pak").Any();
            bool hasUtoc = !hasPak && SafeEnumerateFiles(gameDirectory, "*.utoc").Any();
            return (hasPak || hasUtoc) ? 0.95f : 0f;
        }
        catch { return 0f; }
    }

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

    // ── Native compression codecs (Oodle / Zlib) ───────────────────────────────
    // Initialized once per process. Required to decompress packaged UE assets.

    private static bool _compressionInit;
    private static readonly SemaphoreSlim _compressionLock = new(1, 1);

    private static async Task EnsureCompressionAsync(string gameDirectory, IProgress<string>? progress)
    {
        if (_compressionInit) return;
        await _compressionLock.WaitAsync();
        try
        {
            if (_compressionInit) return;

            // ── Oodle (the Jedi titles and most modern UE games use it) ──
            // Prefer the game's own oo2core DLL (offline, exact-version match); otherwise
            // download the CUE4Parse-expected build into our app data, exactly like FModel.
            string? oodle = FindGameOodle(gameDirectory);
            if (oodle == null)
            {
                var dl = Path.Combine(DataDir(), OodleHelper.OODLE_DLL_NAME);
                if (!File.Exists(dl))
                {
                    progress?.Report("Downloading Oodle decompressor…");
                    await OodleHelper.DownloadOodleDllAsync(dl);
                }
                oodle = dl;
            }
            OodleHelper.Initialize(oodle);
            Console.WriteLine($"[UE] Oodle initialized: {oodle}");

            // ── Zlib (best-effort — some UE paks use it; non-fatal if unavailable offline) ──
            try
            {
                var zlib = Path.Combine(DataDir(), ZlibHelper.DLL_NAME);
                if (!File.Exists(zlib)) await ZlibHelper.DownloadDllAsync(zlib);
                ZlibHelper.Initialize(zlib);
            }
            catch (Exception ex) { Console.WriteLine($"[UE] Zlib init skipped: {ex.Message}"); }

            _compressionInit = true;
        }
        catch (Exception ex) { Console.WriteLine($"[UE] Compression init failed: {ex.Message}"); }
        finally { _compressionLock.Release(); }
    }

    private static string DataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameAssetExplorer", ".data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Locate the oo2core_*_win64.dll the game ships (UE layout: Engine/Binaries/ThirdParty/Oodle/Win64).</summary>
    private static string? FindGameOodle(string gameDirectory)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory)) return null;

        var fast = new[]
        {
            Path.Combine(gameDirectory, "Engine", "Binaries", "ThirdParty", "Oodle", "Win64"),
            Path.Combine(gameDirectory, "Engine", "Binaries", "Win64"),
        };
        foreach (var d in fast)
            if (Directory.Exists(d))
                foreach (var f in Directory.EnumerateFiles(d, "oo2core_*_win64.dll"))
                    return f;

        // Bounded fallback: search only under Engine/Binaries so we don't walk the whole install.
        var eng = Path.Combine(gameDirectory, "Engine", "Binaries");
        if (Directory.Exists(eng))
            foreach (var f in SafeEnumerateFiles(eng, "oo2core_*_win64.dll"))
                return f;
        return null;
    }

    public async Task<bool> MountGameAsync(GameConfig config, IProgress<string>? progress = null)
    {
        _currentConfig = config;
        progress?.Report("Locating game archives…");

        // CUE4Parse can't decompress Oodle/Zlib-packed UE assets until the native codecs are
        // initialized — without this, mounting works but every LoadPackage throws
        // "Oodle decompression failed: not initialized". FModel does the same on startup.
        await EnsureCompressionAsync(config.GameDirectory, progress);

        var paksDir = FindPaksDirectory(config.GameDirectory);
        if (paksDir == null)
        {
            Console.WriteLine($"[UE] Could not find Paks directory in: {config.GameDirectory}");
            return false;
        }

        progress?.Report($"Found archives at: {paksDir}");

        var gameVersion = MapVersionToEGame(config.EngineVersion, config.EngineSpecificSettings);

        // Use overload (string, SearchOption, VersionContainer, StringComparer) to resolve constructor ambiguity
        _provider = new DefaultFileProvider(
            paksDir,
            SearchOption.AllDirectories,
            new VersionContainer(gameVersion),
            StringComparer.OrdinalIgnoreCase);

        progress?.Report("Reading archive headers…");
        _provider.Initialize();

        if (!string.IsNullOrEmpty(config.AesKey))
        {
            progress?.Report("Submitting AES encryption key…");
            await _provider.SubmitKeyAsync(new FGuid(), new FAesKey(config.AesKey));
        }

        foreach (var key in config.AdditionalAesKeys)
        {
            if (!string.IsNullOrEmpty(key))
                await _provider.SubmitKeyAsync(new FGuid(), new FAesKey(key));
        }

        progress?.Report("Mounting archives…");
        await _provider.MountAsync();

        var fileCount = _provider.Files.Count;
        progress?.Report($"Mounted {fileCount:N0} files.");
        Console.WriteLine($"[UE] Mounted: {config.DisplayName} ({fileCount:N0} files)");
        return true;
    }

    public async Task UnmountGameAsync()
    {
        _provider?.Dispose();
        _provider = null;
        _currentConfig = null;
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync()
    {
        if (_provider == null) return Array.Empty<AssetInfo>();

        var results = new List<AssetInfo>();
        await Task.Run(() =>
        {
            foreach (var (path, gameFile) in _provider.Files)
            {
                results.Add(new AssetInfo
                {
                    VirtualPath      = path,
                    Name             = Path.GetFileNameWithoutExtension(path),
                    Type             = InferAssetType(path),
                    CompressedSize   = gameFile.Size,
                    UncompressedSize = gameFile.Size,   // CUE4Parse 1.2.2: no separate uncompressed size exposed
                    ArchivePath      = gameFile.Directory ?? string.Empty,
                    EngineClassName  = string.Empty,
                    IsEncrypted      = gameFile.IsEncrypted
                });
            }
        });

        return results;
    }

    public async Task<IReadOnlyList<AssetInfo>> GetAssetsAtPathAsync(string virtualPath)
    {
        var all = await GetAllAssetsAsync();
        var normalizedPath = virtualPath.TrimEnd('/').ToLowerInvariant();

        return all.Where(a =>
        {
            var dir = Path.GetDirectoryName(a.VirtualPath)?.ToLowerInvariant() ?? "";
            return dir.StartsWith(normalizedPath);
        }).ToList();
    }

    public async Task<AssetData> LoadAssetAsync(AssetInfo asset)
    {
        if (_provider == null)
            throw new InvalidOperationException("No game is currently mounted.");

        var pkg     = await _provider.LoadPackageAsync(asset.VirtualPath);
        var exports = pkg?.GetExports()?.ToList();

        if (exports == null || exports.Count == 0)
            throw new Exception($"Could not load asset: {asset.VirtualPath}");

        // Pick the primary asset export — the first one we have a typed loader for.
        // Packages frequently list sub-objects first (e.g. a StaticMesh's BodySetup, a
        // mesh's Skeleton/PhysicsAsset), so exports[0] is often not the asset the user
        // picked. FModel resolves the main export the same way.
        var mainExport =
            exports.FirstOrDefault(e => e is UTexture2D or UStaticMesh or USkeletalMesh
                                          or UAnimSequence or USoundWave)
            ?? exports[0];

        return mainExport switch
        {
            UTexture2D    texture   => await LoadTextureAsync(texture, asset),
            UStaticMesh   staticMesh => await LoadStaticMeshAsync(staticMesh, asset),
            USkeletalMesh skelMesh   => await LoadSkeletalMeshAsync(skelMesh, asset),
            UAnimSequence anim       => await LoadAnimationAsync(anim, asset),
            USoundWave    sound      => await LoadAudioAsync(sound, asset),
            _                        => CreateGenericAssetData(mainExport, asset)
        };
    }

    // ─── Loaders ──────────────────────────────────────────────────────────────

    private async Task<TextureAssetData> LoadTextureAsync(UTexture2D texture, AssetInfo info)
    {
        // PlatformData holds the actual decoded mip data; ImportedSize is the canonical size
        var pd = texture.PlatformData;
        int w  = pd?.SizeX ?? (int)texture.ImportedSize.X;
        int h  = pd?.SizeY ?? (int)texture.ImportedSize.Y;

        var data = new TextureAssetData
        {
            Info         = info,
            Width        = w,
            Height       = h,
            SourceFormat = pd?.PixelFormat.ToString() ?? texture.Format.ToString(),
            TextureGroup = string.Empty, // In raw Properties list; read on demand
            IsSrgb       = texture.SRGB
        };

        if (pd?.Mips != null)
        {
            foreach (var mip in pd.Mips)
            {
                data.Mips.Add(new MipData
                {
                    Width  = mip.SizeX,
                    Height = mip.SizeY,
                    Data   = mip.BulkData.Data ?? Array.Empty<byte>()
                });
            }
        }

        data.RawProperties = ExtractProperties(texture);
        await Task.CompletedTask;
        return data;
    }

    private async Task<MeshAssetData> LoadStaticMeshAsync(UStaticMesh mesh, AssetInfo info)
    {
        var data = new MeshAssetData { Info = info, IsSkeletal = false };

        try
        {
            if (mesh.StaticMaterials != null)
            {
                for (int i = 0; i < mesh.StaticMaterials.Length; i++)
                {
                    var mat = mesh.StaticMaterials[i];
                    data.MaterialSlots.Add(new MaterialSlot
                    {
                        SlotIndex    = i,
                        MaterialName = mat.MaterialSlotName.ToString(),
                        MaterialPath = mat.MaterialInterface?.GetPathName(true) ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UE] Static mesh material read failed: {ex.Message}");
        }

        // Extract vertex/index data via CUE4Parse-Conversion
        try
        {
            if (MeshConverter.TryConvert(mesh, out var converted) && converted.LODs.Count > 0)
                ExtractMeshLods(converted.LODs, data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UE] Static mesh geometry extraction failed: {ex.Message}");
        }

        data.RawProperties = ExtractProperties(mesh);
        await Task.CompletedTask;
        return data;
    }

    private async Task<MeshAssetData> LoadSkeletalMeshAsync(USkeletalMesh mesh, AssetInfo info)
    {
        var data = new MeshAssetData { Info = info, IsSkeletal = true };

        // ReferenceSkeleton is directly on USkeletalMesh — no need to load the Skeleton asset
        try
        {
            var refSkel      = mesh.ReferenceSkeleton;
            var skeletonData = new SkeletonData();

            for (int i = 0; i < refSkel.FinalRefBoneInfo.Length; i++)
            {
                var boneInfo = refSkel.FinalRefBoneInfo[i];
                var bonePose = refSkel.FinalRefBonePose[i];

                skeletonData.Bones.Add(new BoneInfo
                {
                    Name        = boneInfo.Name.ToString(),
                    ParentIndex = boneInfo.ParentIndex,
                    Position    = new[] { bonePose.Translation.X, bonePose.Translation.Y, bonePose.Translation.Z },
                    Rotation    = new[] { bonePose.Rotation.X,    bonePose.Rotation.Y,    bonePose.Rotation.Z,    bonePose.Rotation.W },
                    Scale       = new[] { bonePose.Scale3D.X,     bonePose.Scale3D.Y,     bonePose.Scale3D.Z }
                });
            }

            data.Skeleton = skeletonData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UE] Skeleton build failed: {ex.Message}");
        }

        try
        {
            if (mesh.SkeletalMaterials != null)
            {
                for (int i = 0; i < mesh.SkeletalMaterials.Length; i++)
                {
                    var mat = mesh.SkeletalMaterials[i];
                    data.MaterialSlots.Add(new MaterialSlot
                    {
                        SlotIndex    = i,
                        MaterialName = mat.MaterialSlotName.ToString(),
                        MaterialPath = mat.Material?.GetPathName(true) ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UE] Skeletal mesh materials failed: {ex.Message}");
        }

        // Extract vertex/index data via CUE4Parse-Conversion
        try
        {
            if (MeshConverter.TryConvert(mesh, out var converted) && converted.LODs.Count > 0)
                ExtractSkelMeshLods(converted.LODs, data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UE] Skeletal mesh geometry extraction failed: {ex.Message}");
        }

        data.RawProperties = ExtractProperties(mesh);
        await Task.CompletedTask;
        return data;
    }

    private async Task<AnimationAssetData> LoadAnimationAsync(UAnimSequence anim, AssetInfo info)
    {
        var frameCount = anim.NumFrames;
        var rateScale  = anim.RateScale > 0 ? anim.RateScale : 1f;
        var seqLength  = anim.SequenceLength;
        float frameRate = (frameCount > 0 && seqLength > 0)
            ? (frameCount / seqLength * rateScale)
            : 30f * rateScale;

        var data = new AnimationAssetData
        {
            Info          = info,
            FrameRate     = frameRate,
            FrameCount    = frameCount,
            SkeletonPath  = anim.Skeleton.ToString(),
            RawProperties = ExtractProperties(anim)
        };

        await Task.CompletedTask;
        return data;
    }

    private async Task<AudioAssetData> LoadAudioAsync(USoundWave sound, AssetInfo info)
    {
        var data = new AudioAssetData
        {
            Info          = info,
            SourceFormat  = "Wwise/OGG",
            RawAudioData  = sound.RawData?.Data,
            RawProperties = ExtractProperties(sound)
        };

        try
        {
            foreach (var prop in sound.Properties)
            {
                switch (prop.Name.ToString())
                {
                    case "Duration":    data.Duration   = Convert.ToSingle(prop.Tag?.GetValue(typeof(float)) ?? 0f);  break;
                    case "SampleRate":  data.SampleRate = Convert.ToInt32(prop.Tag?.GetValue(typeof(int))   ?? 0);    break;
                    case "NumChannels": data.Channels   = Convert.ToInt32(prop.Tag?.GetValue(typeof(int))   ?? 0);    break;
                }
            }
        }
        catch { /* Audio metadata is non-critical */ }

        await Task.CompletedTask;
        return data;
    }

    // ─── Mesh geometry extraction ──────────────────────────────────────────

    private static void ExtractMeshLods(List<CStaticMeshLod> lods, MeshAssetData data)
    {
        for (int i = 0; i < lods.Count; i++)
        {
            var lod = lods[i];
            if (lod.SkipLod || lod.Verts == null || lod.Verts.Length == 0) continue;

            var indices  = UnpackIndices(lod.Indices.Value);
            var sections = lod.Sections?.Value;
            var lodData  = PackLodData(i, lod.Verts, lod.NumVerts, indices, sections, data.MaterialSlots);
            if (lodData != null) data.Lods.Add(lodData);
        }
    }

    private static void ExtractSkelMeshLods(List<CSkelMeshLod> lods, MeshAssetData data)
    {
        for (int i = 0; i < lods.Count; i++)
        {
            var lod = lods[i];
            if (lod.SkipLod || lod.Verts == null || lod.Verts.Length == 0) continue;

            var indices  = UnpackIndices(lod.Indices.Value);
            var sections = lod.Sections?.Value;
            // CSkelMeshVertex inherits CMeshVertex, so the array uplifts cleanly
            var lodData  = PackLodData(i, lod.Verts, lod.NumVerts, indices, sections, data.MaterialSlots);
            if (lodData != null) data.Lods.Add(lodData);
        }
    }

    private static int[] UnpackIndices(CUE4Parse.UE4.Assets.Exports.StaticMesh.FRawStaticIndexBuffer buf)
    {
        if (buf.Indices32 is { Length: > 0 })
            return Array.ConvertAll(buf.Indices32, i => (int)i);
        if (buf.Indices16 is { Length: > 0 })
            return Array.ConvertAll(buf.Indices16, i => (int)i);
        return Array.Empty<int>();
    }

    /// <summary>
    /// Pack a CUE4Parse LOD into our <see cref="LodData"/>:
    ///   - positions as 12 b/vertex float32 XYZ
    ///   - UV0 as 8 b/vertex float32 UV
    ///   - indices as 32-bit int (already global to the LOD vertex buffer)
    ///   - per-section <see cref="SubmeshInfo"/> derived from CBaseMeshLod.Sections —
    ///     each section's VertexStart/Count is computed by scanning the section's
    ///     index range for the min/max referenced vertex so the viewer can render
    ///     and toggle each material's geometry independently.
    /// </summary>
    private static LodData? PackLodData(
        int lodIndex,
        CMeshVertex[] verts,
        int numVerts,
        int[] indices,
        CMeshSection[]? sections,
        IReadOnlyList<MaterialSlot> matSlots)
    {
        if (verts == null || numVerts == 0 || indices.Length == 0) return null;

        // Positions
        var posBuf = new byte[numVerts * 12];
        for (int v = 0; v < numVerts; v++)
        {
            int off = v * 12;
            BitConverter.TryWriteBytes(posBuf.AsSpan(off,     4), verts[v].Position.X);
            BitConverter.TryWriteBytes(posBuf.AsSpan(off + 4, 4), verts[v].Position.Y);
            BitConverter.TryWriteBytes(posBuf.AsSpan(off + 8, 4), verts[v].Position.Z);
        }

        // UV0 (8 b/vertex float32 U,V) — required for texture mapping in the viewer
        var uvBuf = new byte[numVerts * 8];
        for (int v = 0; v < numVerts; v++)
        {
            int off = v * 8;
            BitConverter.TryWriteBytes(uvBuf.AsSpan(off,     4), verts[v].UV.U);
            BitConverter.TryWriteBytes(uvBuf.AsSpan(off + 4, 4), verts[v].UV.V);
        }

        // Indices (already 32-bit int)
        var idxBuf = new byte[indices.Length * 4];
        for (int j = 0; j < indices.Length; j++)
            BitConverter.TryWriteBytes(idxBuf.AsSpan(j * 4, 4), indices[j]);

        // Sections → SubmeshInfo. Each section spans indices [FirstIndex, FirstIndex + NumFaces*3).
        // We compute its VertexStart/Count by scanning the index range — sections often share
        // vertex ranges, so this isn't disjoint, but the viewer handles overlapping ranges
        // correctly (it only renders indices that fall inside each submesh's range).
        var submeshes = new List<SubmeshInfo>();
        if (sections != null)
        {
            for (int s = 0; s < sections.Length; s++)
            {
                var sec = sections[s];
                if (!sec.IsValid) continue;
                int idxStart = sec.FirstIndex;
                int idxCount = sec.NumFaces * 3;
                if (idxStart < 0 || idxStart + idxCount > indices.Length) continue;

                int minV = int.MaxValue, maxV = int.MinValue;
                for (int k = 0; k < idxCount; k++)
                {
                    int gi = indices[idxStart + k];
                    if (gi < minV) minV = gi;
                    if (gi > maxV) maxV = gi;
                }
                if (minV == int.MaxValue) continue;

                string name = (sec.MaterialIndex >= 0 && sec.MaterialIndex < matSlots.Count
                    && !string.IsNullOrEmpty(matSlots[sec.MaterialIndex].MaterialName))
                        ? matSlots[sec.MaterialIndex].MaterialName
                        : $"Section{s}";

                submeshes.Add(new SubmeshInfo
                {
                    Name        = name,
                    VertexStart = minV,
                    VertexCount = maxV - minV + 1,
                    IndexStart  = idxStart,
                    IndexCount  = idxCount,
                });
            }
        }

        return new LodData
        {
            LodIndex      = lodIndex,
            VertexCount   = numVerts,
            TriangleCount = indices.Length / 3,
            VertexBuffer  = posBuf,
            IndexBuffer   = idxBuf,
            UvBuffer      = uvBuf,
            Submeshes     = submeshes,
        };
    }

    private static AssetData CreateGenericAssetData(UObject obj, AssetInfo info) =>
        new GenericAssetData { Info = info, RawProperties = ExtractProperties(obj) };

    private static Dictionary<string, object?> ExtractProperties(UObject obj)
    {
        var props = new Dictionary<string, object?>();
        try
        {
            if (obj.Properties != null)
                foreach (var prop in obj.Properties)
                    props[prop.Name.ToString()] = prop.Tag?.GetValue(typeof(object));

            props["_ClassName"]  = obj.Class?.Name?.ToString() ?? obj.GetType().Name;
            props["_ObjectPath"] = obj.GetPathName();
        }
        catch (Exception ex)
        {
            props["_PropertyExtractionError"] = ex.Message;
        }
        return props;
    }

    private static string? FindPaksDirectory(string gameDirectory)
    {
        var pakDirs = Directory.GetDirectories(gameDirectory, "Paks", SearchOption.AllDirectories);

        foreach (var dir in pakDirs)
        {
            if ((dir.Contains("Content" + Path.DirectorySeparatorChar + "Paks") ||
                 dir.Contains("Content" + Path.AltDirectorySeparatorChar + "Paks")) &&
                (Directory.GetFiles(dir, "*.pak").Length > 0 ||
                 Directory.GetFiles(dir, "*.utoc").Length > 0))
            {
                return dir;
            }
        }
        return null;
    }

    private static EGame MapVersionToEGame(string version, Dictionary<string, string> settings)
    {
        if (settings.TryGetValue("EGame", out var gameOverride) &&
            Enum.TryParse<EGame>(gameOverride, out var specificGame))
            return specificGame;

        return version switch
        {
            // UE4 — full range so old games (Hellblade 1 ≈ 4.16, etc.) parse correctly
            "4.0"  => EGame.GAME_UE4_0,
            "4.1"  => EGame.GAME_UE4_1,
            "4.2"  => EGame.GAME_UE4_2,
            "4.3"  => EGame.GAME_UE4_3,
            "4.4"  => EGame.GAME_UE4_4,
            "4.5"  => EGame.GAME_UE4_5,
            "4.6"  => EGame.GAME_UE4_6,
            "4.7"  => EGame.GAME_UE4_7,
            "4.8"  => EGame.GAME_UE4_8,
            "4.9"  => EGame.GAME_UE4_9,
            "4.10" => EGame.GAME_UE4_10,
            "4.11" => EGame.GAME_UE4_11,
            "4.12" => EGame.GAME_UE4_12,
            "4.13" => EGame.GAME_UE4_13,
            "4.14" => EGame.GAME_UE4_14,
            "4.15" => EGame.GAME_UE4_15,
            "4.16" => EGame.GAME_UE4_16,   // Hellblade: Senua's Sacrifice
            "4.17" => EGame.GAME_UE4_17,
            "4.18" => EGame.GAME_UE4_18,
            "4.19" => EGame.GAME_UE4_19,
            "4.20" => EGame.GAME_UE4_20,
            "4.21" => EGame.GAME_UE4_21,
            "4.22" => EGame.GAME_UE4_22,
            "4.23" => EGame.GAME_UE4_23,
            "4.24" => EGame.GAME_UE4_24,
            "4.25" => EGame.GAME_UE4_25,
            "4.26" => EGame.GAME_UE4_26,
            "4.27" => EGame.GAME_UE4_27,
            // UE5
            "5.0"  => EGame.GAME_UE5_0,
            "5.1"  => EGame.GAME_UE5_1,   // Hellblade 2: Senua's Saga, Jedi Survivor
            "5.2"  => EGame.GAME_UE5_2,
            "5.3"  => EGame.GAME_UE5_3,
            "5.4"  => EGame.GAME_UE5_4,
            // 5.5 and 5.6 — try enum parse first (newer CUE4Parse versions expose these),
            // fall back to GAME_UE5_4 which is close enough for parsing
            "5.5"  => Enum.TryParse<EGame>("GAME_UE5_5", out var ue55) ? ue55 : EGame.GAME_UE5_4,
            "5.6"  => Enum.TryParse<EGame>("GAME_UE5_6", out var ue56) ? ue56 : EGame.GAME_UE5_4,
            _      => EGame.GAME_UE5_4   // unknown version — latest stable is the safest bet
        };
    }

    private static AssetType InferAssetType(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower.Contains("/textures/") || lower.Contains("_texture") ||
            lower.EndsWith("_d") || lower.EndsWith("_n") || lower.EndsWith("_r"))
            return AssetType.Texture;
        if (lower.Contains("/meshes/") || lower.Contains("/staticmesh") ||
            lower.Contains("_sm_") || lower.StartsWith("sm_"))
            return AssetType.StaticMesh;
        if (lower.Contains("/characters/") || lower.Contains("_skelmesh") ||
            lower.Contains("_sk_") || lower.StartsWith("sk_"))
            return AssetType.SkeletalMesh;
        if (lower.Contains("/animations/") || lower.Contains("_anim") ||
            lower.Contains("/anim/"))
            return AssetType.Animation;
        if (lower.Contains("/sounds/") || lower.Contains("/audio/") ||
            lower.Contains("/wwise/"))
            return AssetType.Audio;
        if (lower.Contains("/materials/") || lower.StartsWith("m_") ||
            lower.Contains("_mat"))
            return AssetType.Material;

        return AssetType.Unknown;
    }
}

/// <summary>Fallback for asset types we don't have a specific handler for yet</summary>
public class GenericAssetData : AssetData { }
