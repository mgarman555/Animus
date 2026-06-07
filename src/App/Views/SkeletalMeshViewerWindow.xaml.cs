using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using GameAssetExplorer.Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using CheckBox = System.Windows.Controls.CheckBox;

namespace GameAssetExplorer.App.Views;

public partial class SkeletalMeshViewerWindow : Window
{
    private readonly AssetData? _data;
    private readonly AssetInfo  _info;
    private MeshAssetData? _meshData;
    private bool _lodSwitching;

    // Per-submesh state (parallel lists)
    private readonly List<MeshGeometry3D> _submeshGeoms = new();
    private readonly List<string>         _submeshNames = new();
    private readonly List<bool>           _submeshVisible = new();
    private readonly List<CheckBox>       _submeshChecks = new();

    // Material brushes (texture decoded once per LOD)
    private ImageBrush? _textureBrush;
    private readonly SolidColorBrush _solidBrush =
        new(Color.FromRgb(0xC8, 0xC0, 0xB8));
    // Neutral dark grey for back-faces. (Used to be brown #403A38, which made BC1
    // punch-through-alpha bleed-through look like "the texture is red" rather than
    // "you can see the inside of the mesh".)
    private readonly SolidColorBrush _backBrush =
        new(Color.FromRgb(0x2A, 0x2A, 0x2A));

    // Session 3 texture debug state
    private string? _forcedFormat;

    // Camera orbit state
    private WpfPoint _lastMouse;
    private bool _orbiting;
    private double _yaw   = 0;
    private double _pitch = 15;
    private double _dist  = 4;
    private Point3D _target = new(0, 0, 0);

    public SkeletalMeshViewerWindow(AssetData? data, AssetInfo info)
    {
        InitializeComponent();
        _data = data;
        _info = info;

        TitleText.Text    = info.Name;
        SubtitleText.Text = info.VirtualPath;
        Title             = $"Skeletal Mesh — {info.Name}";

        ViewportContainer.MouseLeftButtonDown += (_, e) =>
        {
            _orbiting = true; _lastMouse = e.GetPosition(this);
            ViewportContainer.CaptureMouse();
        };
        ViewportContainer.MouseLeftButtonUp += (_, _) =>
        {
            _orbiting = false; ViewportContainer.ReleaseMouseCapture();
        };
        ViewportContainer.MouseMove  += OnMouseMove;
        ViewportContainer.MouseWheel += OnMouseWheel;

        Populate();
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    private void Populate()
    {
        if (_data is MeshAssetData mesh && TryLoadMeshAssetData(mesh))
            return;

        if (!string.IsNullOrEmpty(_info.ArchivePath))
        {
            var objPath = Path.ChangeExtension(_info.ArchivePath, ".obj");
            if (File.Exists(objPath) && TryLoadObj(objPath))
                return;
        }

        ShowPlaceholder();
    }

    private bool TryLoadMeshAssetData(MeshAssetData mesh)
    {
        if (mesh.Lods.Count == 0) return false;

        _meshData = mesh;

        _lodSwitching = true;
        LodCombo.Items.Clear();
        int bestIdx = 0, bestVerts = 0;
        for (int i = 0; i < mesh.Lods.Count; i++)
        {
            var l = mesh.Lods[i];
            LodCombo.Items.Add($"{l.LodIndex} ({l.VertexCount:N0}v)");
            if (l.VertexCount > bestVerts) { bestVerts = l.VertexCount; bestIdx = i; }
        }
        LodCombo.SelectedIndex = bestIdx;
        LodCombo.Visibility = mesh.Lods.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _lodSwitching = false;

        return LoadLod(mesh.Lods[bestIdx]);
    }

    private bool LoadLod(LodData lod)
    {
        if (lod.VertexBuffer == null || lod.IndexBuffer == null) return false;
        if (lod.VertexCount <= 0 || lod.TriangleCount <= 0)     return false;

        try
        {
            // Reset per-submesh state
            _submeshGeoms.Clear();
            _submeshNames.Clear();
            _submeshVisible.Clear();

            // Use the parser-emitted submesh boundaries; if absent, treat the whole LOD as one
            var submeshes = lod.Submeshes.Count > 0
                ? lod.Submeshes
                : new List<SubmeshInfo>
                {
                    new() {
                        Name = "Mesh",
                        VertexStart = 0, VertexCount = lod.VertexCount,
                        IndexStart  = 0, IndexCount  = lod.TriangleCount * 3,
                    }
                };

            int stride = lod.VertexBuffer.Length / lod.VertexCount;
            if (stride < 12) return false;
            bool indices32 = lod.IndexBuffer.Length == lod.TriangleCount * 3 * 4;

            foreach (var sm in submeshes)
            {
                var geo = BuildSubmeshGeometry(lod, sm, stride, indices32);
                if (geo == null) continue;
                _submeshGeoms.Add(geo);
                _submeshNames.Add(string.IsNullOrEmpty(sm.Name) ? $"Shape{_submeshGeoms.Count - 1}" : sm.Name);
                _submeshVisible.Add(true);
            }

            if (_submeshGeoms.Count == 0) return false;

            // Texture loading: auto-detect the best decode settings if we have texture data.
            _textureBrush = null;
            bool hasTextureData = _meshData?.DiffuseTextureData != null
                                  && _meshData.DiffuseTextureWidth  > 0
                                  && _meshData.DiffuseTextureHeight > 0;
            if (hasTextureData)
            {
                // Run the auto-detect probe. It updates the UI toggles and sets _textureBrush
                // to the best candidate. If everything's broken, falls back to the manual decode.
                var picked = AutoDetectTextureSettings(applyImmediately: true);
                if (picked == null)
                    _textureBrush = TryDecodeMeshTexture(_meshData!);
            }

            // Reset toggle state — enabled whenever there's texture data (even if decode failed)
            ChkTexture.IsEnabled = hasTextureData;
            ChkTexture.IsChecked = _textureBrush != null;
            ChkTexture.ToolTip   = hasTextureData && _textureBrush == null
                ? "Texture decode failed — toggle on to see the broken result"
                : null;
            ChkArmature.IsEnabled = (_meshData?.Skeleton?.Bones?.Count ?? 0) > 0;
            ChkArmature.IsChecked = false;

            BuildSubmeshList();
            UpdateMeshVisual();
            UpdateArmatureVisual();
            FitCameraToVisible();

            PlaceholderPanel.Visibility = Visibility.Collapsed;
            Viewport3D.Visibility       = Visibility.Visible;
            DisplayPanel.Visibility     = Visibility.Visible;
            BtnReset.Visibility         = Visibility.Visible;
            return true;
        }
        catch { return false; }
    }

    private MeshGeometry3D? BuildSubmeshGeometry(LodData lod, SubmeshInfo sm, int stride, bool indices32)
    {
        if (sm.VertexCount <= 0 || sm.IndexCount <= 0) return null;
        if (lod.VertexBuffer == null || lod.IndexBuffer == null) return null;

        // Vertex positions for this submesh
        var positions = new Point3DCollection(sm.VertexCount);
        for (int i = 0; i < sm.VertexCount; i++)
        {
            int off = (sm.VertexStart + i) * stride;
            if (off + 12 > lod.VertexBuffer.Length) return null;
            positions.Add(new Point3D(
                BitConverter.ToSingle(lod.VertexBuffer, off),
                BitConverter.ToSingle(lod.VertexBuffer, off + 4),
                BitConverter.ToSingle(lod.VertexBuffer, off + 8)));
        }

        // Indices: rebase from global LOD-space to local submesh-space (subtract VertexStart)
        var indices = new Int32Collection(sm.IndexCount);
        for (int i = 0; i < sm.IndexCount; i++)
        {
            int globalIdx;
            if (indices32)
            {
                int byteOff = (sm.IndexStart + i) * 4;
                if (byteOff + 4 > lod.IndexBuffer.Length) return null;
                globalIdx = BitConverter.ToInt32(lod.IndexBuffer, byteOff);
            }
            else
            {
                int byteOff = (sm.IndexStart + i) * 2;
                if (byteOff + 2 > lod.IndexBuffer.Length) return null;
                globalIdx = BitConverter.ToUInt16(lod.IndexBuffer, byteOff);
            }
            int localIdx = globalIdx - sm.VertexStart;
            if (localIdx < 0 || localIdx >= sm.VertexCount) continue;
            indices.Add(localIdx);
        }

        var normals = ComputeNormals(positions, indices);

        // UVs (if parsed): pull the same vertex range out of the merged UV buffer
        PointCollection? uvs = null;
        if (lod.UvBuffer != null && lod.UvBuffer.Length >= (sm.VertexStart + sm.VertexCount) * 8)
        {
            uvs = new PointCollection(sm.VertexCount);
            for (int i = 0; i < sm.VertexCount; i++)
            {
                int o = (sm.VertexStart + i) * 8;
                uvs.Add(new WpfPoint(
                    BitConverter.ToSingle(lod.UvBuffer, o),
                    BitConverter.ToSingle(lod.UvBuffer, o + 4)));
            }
        }
        else if (_meshData?.DiffuseTextureData != null)
        {
            uvs = GeneratePlanarUvs(positions);
        }

        var geo = new MeshGeometry3D
        {
            Positions       = positions,
            TriangleIndices = indices,
            Normals         = normals,
        };
        if (uvs != null) geo.TextureCoordinates = uvs;
        return geo;
    }

    // ── Display panel: submeshes list ─────────────────────────────────────────

    private void BuildSubmeshList()
    {
        SubmeshList.Items.Clear();
        _submeshChecks.Clear();

        for (int i = 0; i < _submeshNames.Count; i++)
        {
            int idx = i;
            var cb = new CheckBox
            {
                Content    = _submeshNames[i],
                FontSize   = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                IsChecked  = true,
                Margin     = new Thickness(8, 1, 0, 1),
            };
            cb.Click += (_, _) =>
            {
                _submeshVisible[idx] = cb.IsChecked == true;
                UpdateMeshVisual();
                FitCameraToVisible();
            };
            _submeshChecks.Add(cb);
            SubmeshList.Items.Add(cb);
        }

        LblNoSubmeshes.Visibility = _submeshNames.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Mesh visual rebuild (called on any toggle) ────────────────────────────

    private void UpdateMeshVisual()
    {
        var group = new Model3DGroup();
        var material = BuildMaterial();

        for (int i = 0; i < _submeshGeoms.Count; i++)
        {
            if (!_submeshVisible[i]) continue;
            group.Children.Add(new GeometryModel3D
            {
                Geometry     = _submeshGeoms[i],
                Material     = material,
                BackMaterial = new DiffuseMaterial(_backBrush),
            });
        }

        MeshVisual.Content = group;
    }

    private Material BuildMaterial()
    {
        var mg = new MaterialGroup();
        Brush brush = (ChkTexture.IsChecked == true && _textureBrush != null)
            ? _textureBrush
            : _solidBrush;
        mg.Children.Add(new DiffuseMaterial(brush));
        mg.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 60));
        return mg;
    }

    // ── Armature overlay ──────────────────────────────────────────────────────

    private void UpdateArmatureVisual()
    {
        if (ChkArmature.IsChecked != true || _meshData?.Skeleton?.Bones is not { Count: > 0 } bones)
        {
            ArmatureVisual.Content = null;
            return;
        }

        // Compute world-ish positions by walking the parent chain (translation-only —
        // ignores bone rotations, fine for v1 visualization in bind pose)
        var worldPos = new Point3D[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            var local = new Point3D(b.Position[0], b.Position[1], b.Position[2]);
            if (b.ParentIndex >= 0 && b.ParentIndex < i)
                worldPos[i] = new Point3D(
                    worldPos[b.ParentIndex].X + local.X,
                    worldPos[b.ParentIndex].Y + local.Y,
                    worldPos[b.ParentIndex].Z + local.Z);
            else
                worldPos[i] = local;
        }

        // Pick a small joint size relative to skeleton bounds
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var p in worldPos)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        double extent = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
        if (extent <= 0) extent = 1;
        double joint = extent * 0.012;
        if (joint < 0.001) joint = 0.001;

        var group = new Model3DGroup();
        var jointMat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x40)));
        var boneMat  = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xC0, 0xA0, 0x40)));

        // Joint markers
        foreach (var p in worldPos)
            group.Children.Add(new GeometryModel3D
            {
                Geometry = MakeBox(p, joint),
                Material = jointMat,
            });

        // Bone segments (as thin elongated boxes parent → child)
        for (int i = 0; i < bones.Count; i++)
        {
            int p = bones[i].ParentIndex;
            if (p < 0 || p >= bones.Count) continue;
            var seg = MakeBoneSegment(worldPos[p], worldPos[i], joint * 0.5);
            if (seg != null)
                group.Children.Add(new GeometryModel3D { Geometry = seg, Material = boneMat });
        }

        ArmatureVisual.Content = group;
    }

    private static MeshGeometry3D MakeBox(Point3D center, double size)
    {
        double s = size;
        var g = new MeshGeometry3D();
        var p = new[]
        {
            new Point3D(center.X - s, center.Y - s, center.Z - s),
            new Point3D(center.X + s, center.Y - s, center.Z - s),
            new Point3D(center.X + s, center.Y + s, center.Z - s),
            new Point3D(center.X - s, center.Y + s, center.Z - s),
            new Point3D(center.X - s, center.Y - s, center.Z + s),
            new Point3D(center.X + s, center.Y - s, center.Z + s),
            new Point3D(center.X + s, center.Y + s, center.Z + s),
            new Point3D(center.X - s, center.Y + s, center.Z + s),
        };
        foreach (var pt in p) g.Positions.Add(pt);
        int[] idx = {
            0,1,2, 0,2,3,
            4,6,5, 4,7,6,
            0,4,5, 0,5,1,
            3,2,6, 3,6,7,
            1,5,6, 1,6,2,
            0,3,7, 0,7,4,
        };
        foreach (var i in idx) g.TriangleIndices.Add(i);
        return g;
    }

    private static MeshGeometry3D? MakeBoneSegment(Point3D a, Point3D b, double thickness)
    {
        var d = b - a;
        if (d.LengthSquared < 1e-9) return null;
        var len = d.Length;
        var dir = d / len;

        // Build orthonormal basis around dir
        var up = Math.Abs(dir.Y) < 0.95 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        var right = Vector3D.CrossProduct(dir, up); right.Normalize();
        var forward = Vector3D.CrossProduct(right, dir); forward.Normalize();

        var rOff = right   * thickness;
        var fOff = forward * thickness;

        var g = new MeshGeometry3D();
        var pts = new[]
        {
            a - rOff - fOff, a + rOff - fOff, a + rOff + fOff, a - rOff + fOff,
            b - rOff - fOff, b + rOff - fOff, b + rOff + fOff, b - rOff + fOff,
        };
        foreach (var p in pts) g.Positions.Add(p);
        int[] idx = {
            0,1,2, 0,2,3,
            4,6,5, 4,7,6,
            0,4,5, 0,5,1,
            3,2,6, 3,6,7,
            1,5,6, 1,6,2,
            0,3,7, 0,7,4,
        };
        foreach (var i in idx) g.TriangleIndices.Add(i);
        return g;
    }

    // ── Display toggle handlers ──────────────────────────────────────────────

    private void OnToggleTexture(object sender, RoutedEventArgs e) => UpdateMeshVisual();

    private void OnToggleArmature(object sender, RoutedEventArgs e) => UpdateArmatureVisual();

    private void OnToggleWireframe(object sender, RoutedEventArgs e)
    {
        // Reserved for future wireframe overlay; WPF 3D doesn't natively support
        // line rendering and triangle-edge expansion is expensive on large meshes.
        // For now this checkbox is a no-op — kept so the UI matches the layout
        // intent and we can wire it up later.
    }

    // ── Session 3 texture debug handlers ────────────────────────────────────

    private void OnToggleSkipUntile (object sender, RoutedEventArgs e) => ReDecodeAndApply();
    private void OnToggleSwapRB     (object sender, RoutedEventArgs e) => ReDecodeAndApply();
    private void OnToggleFlipV      (object sender, RoutedEventArgs e) => ReDecodeAndApply();
    private void OnToggleHonorAlpha (object sender, RoutedEventArgs e) => ReDecodeAndApply();

    /// <summary>
    /// Probe every combination of decode settings and pick the one with the lowest
    /// Total Variation (TV) score — natural images have moderate TV, scrambled images
    /// have very high TV. Auto-detects the right skip-untile / swap-RB / flip-V / format
    /// for the current asset.
    /// </summary>
    private void OnAutoDetectTexture(object sender, RoutedEventArgs e)
    {
        AutoDetectTextureSettings(applyImmediately: true);
    }

    private record DecodeConfig(bool SkipUntile, bool SwapRB, bool FlipV, string? Format);
    private record DecodeAttempt(DecodeConfig Config, double Score, BitmapSource Bitmap);

    /// <summary>
    /// Returns the best-scoring config or null if no decode succeeded. Optionally
    /// updates the UI toggles to match the chosen config and re-renders the mesh.
    /// </summary>
    private DecodeConfig? AutoDetectTextureSettings(bool applyImmediately)
    {
        if (_meshData?.DiffuseTextureData == null) return null;

        string reportedFormat = _meshData.DiffuseTextureFormat;
        var formats = new List<string?> { null /* = reported */ };

        // If the reported format is BC1/BC4 (8 bytes/block), also probe BC7 (16). And vice-versa.
        // Wrong-format decodes produce huge TV so they get rejected naturally — this just
        // gives the probe a way to recover if VRAM_DESC reports the format incorrectly.
        if (reportedFormat is "BC1" or "BC4")
        {
            formats.Add("BC7");
        }
        else
        {
            formats.Add("BC1");
        }

        var attempts = new List<DecodeAttempt>();

        foreach (var skip in new[] { true, false })
        foreach (var swap in new[] { true, false })
        foreach (var flip in new[] { false, true })
        foreach (var fmt  in formats)
        {
            var cfg = new DecodeConfig(skip, swap, flip, fmt);
            try
            {
                var bmp = DecodeMeshTextureWithConfig(_meshData, skip, swap, flip,
                    honorAlpha: false, forcedFormat: fmt);
                if (bmp == null) continue;
                double score = ComputeTotalVariation(bmp);
                attempts.Add(new DecodeAttempt(cfg, score, bmp));
            }
            catch { /* skip configs that throw (wrong format size etc.) */ }
        }

        if (attempts.Count == 0)
        {
            GameAssetExplorer.Core.Services.Log.Warn($"Auto-detect[{_info.Name}]: no decode candidates succeeded");
            return null;
        }

        // Lowest TV wins
        var ranked = attempts.OrderBy(a => a.Score).ToList();
        var best   = ranked[0];

        // Log top 4 candidates so we can see how confident the pick is
        GameAssetExplorer.Core.Services.Log.Info(
            $"Auto-detect[{_info.Name}]: probed {attempts.Count} combinations");
        foreach (var a in ranked.Take(4))
        {
            string winner = ReferenceEquals(a, best) ? " ← chosen" : "";
            GameAssetExplorer.Core.Services.Log.Info(
                $"  TV={a.Score,12:N0}  skip={a.Config.SkipUntile,-5} swap={a.Config.SwapRB,-5} flip={a.Config.FlipV,-5} fmt={(a.Config.Format ?? reportedFormat + " (auto)"),-12}{winner}");
        }

        if (applyImmediately)
        {
            // Update UI without firing the toggle handlers (would re-decode redundantly)
            ChkSkipUntile.IsChecked = best.Config.SkipUntile;
            ChkSwapRB.IsChecked     = best.Config.SwapRB;
            ChkFlipV.IsChecked      = best.Config.FlipV;
            _forcedFormat           = best.Config.Format;

            // Reflect format in the combo box (Auto = item 0)
            if (CmbForceFormat != null)
            {
                CmbForceFormat.SelectedIndex = best.Config.Format switch
                {
                    "BC1" => 1, "BC3" => 2, "BC4" => 3, "BC5" => 4, "BC7" => 5,
                    _ => 0,
                };
            }

            _textureBrush = new ImageBrush(best.Bitmap) { Stretch = Stretch.Fill };
            UpdateMeshVisual();
        }

        return best.Config;
    }

    /// <summary>
    /// Total Variation: sum of absolute pixel-to-neighbour differences. Sampled every
    /// 4th pixel for speed. Lower = more visually coherent (natural image), higher =
    /// scrambled / wrong decode.
    /// </summary>
    private static double ComputeTotalVariation(BitmapSource bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w < 8 || h < 8) return double.MaxValue;

        int stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        long total = 0;
        long count = 0;
        const int step = 4;   // sample every 4th pixel — fast enough for 1024×1024 in ~15ms

        for (int y = 0; y < h - step; y += step)
        {
            int row    = y * stride;
            int rowDn  = (y + step) * stride;
            for (int x = 0; x < w - step; x += step)
            {
                int p  = row + x * 4;
                int pR = row + (x + step) * 4;
                int pD = rowDn + x * 4;

                // Sum |Δ| across B, G, R channels (skip alpha — it's usually 255 after our fix)
                total += Math.Abs(pixels[p    ] - pixels[pR    ]);
                total += Math.Abs(pixels[p + 1] - pixels[pR + 1]);
                total += Math.Abs(pixels[p + 2] - pixels[pR + 2]);
                total += Math.Abs(pixels[p    ] - pixels[pD    ]);
                total += Math.Abs(pixels[p + 1] - pixels[pD + 1]);
                total += Math.Abs(pixels[p + 2] - pixels[pD + 2]);
                count += 6;
            }
        }

        return count == 0 ? double.MaxValue : (double)total / count;
    }

    private void ReDecodeAndApply()
    {
        if (_meshData?.DiffuseTextureData == null) return;
        _textureBrush = TryDecodeMeshTexture(_meshData);
        UpdateMeshVisual();
    }

    private void OnForceFormatChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbForceFormat == null || _meshData?.DiffuseTextureData == null) return;
        int idx = CmbForceFormat.SelectedIndex;
        _forcedFormat = idx switch
        {
            1 => "BC1",
            2 => "BC3",
            3 => "BC4",
            4 => "BC5",
            5 => "BC7",
            _ => null,   // auto = use MeshData's reported format
        };
        ReDecodeAndApply();
    }

    private void OnDumpTextureBytes(object sender, RoutedEventArgs e)
    {
        if (_meshData?.DiffuseTextureData is not { } raw)
        {
            System.Windows.MessageBox.Show("No texture data on this mesh.", "Dump");
            return;
        }
        int w = _meshData.DiffuseTextureWidth;
        int h = _meshData.DiffuseTextureHeight;
        string format = _forcedFormat ?? _meshData.DiffuseTextureFormat;
        if (w <= 0 || h <= 0)
        {
            System.Windows.MessageBox.Show($"Invalid texture dimensions ({w}×{h}).", "Dump");
            return;
        }

        try
        {
            // Dump folder lives next to the app log so the user can find it via the Logs button
            var dumpRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameAssetExplorer", "texture-dumps");
            Directory.CreateDirectory(dumpRoot);

            string stem = $"{_info.Name}-{DateTime.Now:yyyyMMdd-HHmmss}";
            string rawPath      = Path.Combine(dumpRoot, $"{stem}-1-raw.bin");
            string untiledPath  = Path.Combine(dumpRoot, $"{stem}-2-untiled.bin");
            string decodedPath  = Path.Combine(dumpRoot, $"{stem}-3-decoded.rgba");
            string pngPath      = Path.Combine(dumpRoot, $"{stem}-4-final.png");
            string metaPath     = Path.Combine(dumpRoot, $"{stem}-meta.txt");

            // 1. Raw bytes
            File.WriteAllBytes(rawPath, raw);

            // 2. After GOB untile (always, even if skip-untile is on, so we can compare)
            int bpb = (format == "BC1" || format == "BC4") ? 8 : 16;
            int expectedBase = (w / 4) * (h / 4) * bpb;
            byte[] baseLevel = raw;
            if (raw.Length > expectedBase)
            {
                baseLevel = new byte[expectedBase];
                Buffer.BlockCopy(raw, 0, baseLevel, 0, expectedBase);
            }
            byte[] untiled = UntileGobBC(baseLevel, w / 4, h / 4, bpb);
            File.WriteAllBytes(untiledPath, untiled);

            // 3. Decoded RGBA (using current toggles)
            byte[]? decoded = DecodeBC(
                ChkSkipUntile.IsChecked == true ? baseLevel : untiled,
                w, h, format);
            if (decoded != null)
                File.WriteAllBytes(decodedPath, decoded);

            // 4. Write the FINAL bitmap as a PNG so we can actually see the result.
            // This honours every active toggle (skip-untile, swap-rb, flip-v, force-format).
            var bmp = DecodeMeshTextureToBitmap(_meshData);
            if (bmp != null)
            {
                using var fs = File.Create(pngPath);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                encoder.Save(fs);
            }

            // 4. Metadata + a quick byte-pattern report
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"asset                : {_info.Name}");
            sb.AppendLine($"virtual path         : {_info.VirtualPath}");
            sb.AppendLine($"format (reported)    : {_meshData.DiffuseTextureFormat}");
            sb.AppendLine($"format (effective)   : {format}");
            sb.AppendLine($"dimensions           : {w} × {h}");
            sb.AppendLine($"bytesPerBlock (BCn)  : {bpb}");
            sb.AppendLine($"skip untile flag     : {ChkSkipUntile.IsChecked}");
            sb.AppendLine($"swap R/B flag        : {ChkSwapRB.IsChecked}");
            sb.AppendLine($"flip V flag          : {ChkFlipV.IsChecked}");
            sb.AppendLine($"raw size             : {raw.Length} bytes");
            sb.AppendLine($"expected base level  : {expectedBase} bytes  (mip chain ratio: {raw.Length / (double)expectedBase:F2}×)");
            sb.AppendLine($"diffuse texture path : {_meshData.DiffuseTexturePath}");
            sb.AppendLine();
            sb.AppendLine("first 64 bytes (hex):");
            sb.Append("  ");
            for (int i = 0; i < Math.Min(64, raw.Length); i++)
            {
                sb.Append(raw[i].ToString("X2"));
                if ((i + 1) % 16 == 0) sb.AppendLine().Append("  ");
                else sb.Append(' ');
            }
            File.WriteAllText(metaPath, sb.ToString());

            // Open the dump folder so the user can grab the files
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{rawPath}\""); } catch { }

            GameAssetExplorer.Core.Services.Log.Info(
                $"Texture dump[{_info.Name}]: wrote {(decoded != null ? 4 : 3)} file(s) to {dumpRoot}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Dump failed: {ex.Message}", "Dump");
        }
    }

    private void OnSubmeshShowAll(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _submeshVisible.Count; i++)
        {
            _submeshVisible[i] = true;
            if (i < _submeshChecks.Count) _submeshChecks[i].IsChecked = true;
        }
        UpdateMeshVisual();
        FitCameraToVisible();
    }

    private void OnSubmeshHideAll(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _submeshVisible.Count; i++)
        {
            _submeshVisible[i] = false;
            if (i < _submeshChecks.Count) _submeshChecks[i].IsChecked = false;
        }
        UpdateMeshVisual();
        // Don't re-fit when nothing's visible — keep camera where it was
    }

    /// <summary>Deserialise float32 U/V pairs from UvBuffer (8 bytes per vertex).</summary>
    private static PointCollection BuildUvsFromBuffer(byte[] uvBuf, int vertCount)
    {
        var uvs = new PointCollection(vertCount);
        for (int i = 0; i < vertCount; i++)
        {
            int off = i * 8;
            float u = BitConverter.ToSingle(uvBuf, off);
            float v = BitConverter.ToSingle(uvBuf, off + 4);
            uvs.Add(new WpfPoint(u, v));
        }
        return uvs;
    }

    private static PointCollection GeneratePlanarUvs(Point3DCollection positions)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var p in positions)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        double rngX = maxX - minX;
        double rngY = maxY - minY;
        double rngZ = maxZ - minZ;

        double uMin, uRng, vMin, vRng;
        Func<Point3D, double> getU, getV;
        if (rngX >= rngY && rngX >= rngZ)
        {
            getU = p => p.X; uMin = minX; uRng = rngX;
            if (rngY >= rngZ) { getV = p => p.Y; vMin = minY; vRng = rngY; }
            else               { getV = p => p.Z; vMin = minZ; vRng = rngZ; }
        }
        else if (rngY >= rngX && rngY >= rngZ)
        {
            getU = p => p.Y; uMin = minY; uRng = rngY;
            if (rngX >= rngZ) { getV = p => p.X; vMin = minX; vRng = rngX; }
            else               { getV = p => p.Z; vMin = minZ; vRng = rngZ; }
        }
        else
        {
            getU = p => p.Z; uMin = minZ; uRng = rngZ;
            if (rngX >= rngY) { getV = p => p.X; vMin = minX; vRng = rngX; }
            else               { getV = p => p.Y; vMin = minY; vRng = rngY; }
        }

        if (uRng < 1e-6) uRng = 1;
        if (vRng < 1e-6) vRng = 1;

        var uvs = new PointCollection(positions.Count);
        foreach (var p in positions)
            uvs.Add(new WpfPoint((getU(p) - uMin) / uRng, (getV(p) - vMin) / vRng));
        return uvs;
    }

    private void OnLodChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_lodSwitching || _meshData == null || LodCombo.SelectedIndex < 0) return;
        if (LodCombo.SelectedIndex < _meshData.Lods.Count)
            LoadLod(_meshData.Lods[LodCombo.SelectedIndex]);
    }

    private bool TryLoadObj(string path)
    {
        try
        {
            var geo = ObjLoader.Load(path);
            if (geo == null) return false;

            _submeshGeoms.Clear();
            _submeshNames.Clear();
            _submeshVisible.Clear();
            _submeshGeoms.Add(geo);
            _submeshNames.Add(Path.GetFileNameWithoutExtension(path));
            _submeshVisible.Add(true);
            _textureBrush = null;

            ChkTexture.IsChecked = false;
            ChkArmature.IsEnabled = false;
            ChkArmature.IsChecked = false;

            BuildSubmeshList();
            UpdateMeshVisual();
            UpdateArmatureVisual();
            FitCameraToVisible();

            PlaceholderPanel.Visibility = Visibility.Collapsed;
            Viewport3D.Visibility       = Visibility.Visible;
            DisplayPanel.Visibility     = Visibility.Visible;
            BtnReset.Visibility         = Visibility.Visible;
            return true;
        }
        catch { return false; }
    }

    private static Vector3DCollection ComputeNormals(Point3DCollection positions, Int32Collection indices)
    {
        int vCount = positions.Count;
        var acc = new Vector3D[vCount];

        for (int t = 0; t < indices.Count; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if (i0 >= vCount || i1 >= vCount || i2 >= vCount) continue;

            var p0 = positions[i0];
            var e1 = positions[i1] - p0;
            var e2 = positions[i2] - p0;
            var fn = Vector3D.CrossProduct(e1, e2);

            acc[i0] += fn; acc[i1] += fn; acc[i2] += fn;
        }

        var normals = new Vector3DCollection(vCount);
        for (int i = 0; i < vCount; i++)
        {
            var n = acc[i];
            if (n.LengthSquared > 0) n.Normalize();
            normals.Add(n);
        }
        return normals;
    }

    private ImageBrush? TryDecodeMeshTexture(MeshAssetData meshData)
    {
        try
        {
            var bmp = DecodeMeshTextureToBitmap(meshData);
            if (bmp == null) return null;
            return new ImageBrush(bmp) { Stretch = Stretch.Fill };
        }
        catch { return null; }
    }

    /// <summary>
    /// Full decode path with all current debug toggles applied. Returns a frozen
    /// BitmapSource (Bgra32) or null. Used both for the live mesh material and
    /// the Dump-to-PNG export.
    /// </summary>
    private BitmapSource? DecodeMeshTextureToBitmap(MeshAssetData meshData)
    {
        bool skipUntile = ChkSkipUntile?.IsChecked == true;
        bool swapRB    = ChkSwapRB?.IsChecked    == true;
        bool flipV     = ChkFlipV?.IsChecked     == true;
        bool honorAlpha = ChkHonorAlpha?.IsChecked == true;
        return DecodeMeshTextureWithConfig(meshData, skipUntile, swapRB, flipV, honorAlpha, _forcedFormat);
    }

    private BitmapSource? DecodeMeshTextureWithConfig(
        MeshAssetData meshData, bool skipUntile, bool swapRB, bool flipV,
        bool honorAlpha, string? forcedFormat)
    {
        byte[]? texData = meshData.DiffuseTextureData;
        int w = meshData.DiffuseTextureWidth;
        int h = meshData.DiffuseTextureHeight;
        string format = meshData.DiffuseTextureFormat;
        if (texData == null || w <= 0 || h <= 0) return null;

        // Debug overrides
        string effectiveFormat = forcedFormat ?? format;

        int bpb = (effectiveFormat == "BC1" || effectiveFormat == "BC4") ? 8 : 16;

        // Trim to exactly one mip level if the buffer holds more than that
        int expectedBaseSize = (w / 4) * (h / 4) * bpb;
        byte[] baseLevel = texData;
        if (texData.Length > expectedBaseSize)
        {
            baseLevel = new byte[expectedBaseSize];
            Buffer.BlockCopy(texData, 0, baseLevel, 0, expectedBaseSize);
        }

        byte[] toDecode = skipUntile
            ? baseLevel
            : UntileGobBC(baseLevel, w / 4, h / 4, bpb);

        byte[]? rgba = DecodeBC(toDecode, w, h, effectiveFormat);
        if (rgba == null) return null;

        // BC1 punch-through alpha (color0 ≤ color1 in a block) yields alpha=0 pixels.
        // For diffuse color textures this is almost never meaningful — the BC1 encoder
        // just chose that mode for compression. Without forcing alpha opaque, the mesh's
        // BackMaterial bleeds through transparent pixels and looks brown/red.
        if (!honorAlpha && effectiveFormat is "BC1" or "DXT1")
        {
            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = 0xFF;
        }

        byte[] display = swapRB ? SwapRB(rgba) : rgba;

        if (flipV) display = FlipVertically(display, w, h);

        var bmp = BitmapSource.Create(w, h,
            dpiX: 96, dpiY: 96,
            pixelFormat: PixelFormats.Bgra32,
            palette: null,
            pixels: display,
            stride: w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] FlipVertically(byte[] pixels, int width, int height)
    {
        var output = new byte[pixels.Length];
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * rowBytes, output, (height - 1 - y) * rowBytes, rowBytes);
        return output;
    }

    // ── NVidia 1D-thin GOB deswizzle ─────────────────────────────────────────
    private static byte[] UntileGobBC(byte[] tiled, int blockW, int blockH, int bytesPerBlock)
    {
        int gobBW    = Math.Max(1, 64 / bytesPerBlock);
        int gobsWide = (blockW + gobBW - 1) / gobBW;
        int gobsTall = (blockH + 7)         / 8;

        var linear = new byte[blockW * blockH * bytesPerBlock];

        for (int gy = 0; gy < gobsTall; gy++)
        {
            for (int gx = 0; gx < gobsWide; gx++)
            {
                int gobBase = (gy * gobsWide + gx) * 512;

                for (int swiz = 0; swiz < 512; swiz++)
                {
                    int xByte = (swiz & 0x0F) | ((swiz >> 1) & 0x30);
                    int yRow  = ((swiz >> 4) & 0x1) | ((swiz >> 6) & 0x6);

                    int blockCol    = xByte / bytesPerBlock;
                    int byteInBlock = xByte % bytesPerBlock;

                    int gbx = gx * gobBW + blockCol;
                    int gby = gy * 8     + yRow;
                    if (gbx >= blockW || gby >= blockH) continue;

                    int linearOff = (gby * blockW + gbx) * bytesPerBlock + byteInBlock;
                    int tiledOff  = gobBase + swiz;
                    if ((uint)tiledOff  < (uint)tiled.Length &&
                        (uint)linearOff < (uint)linear.Length)
                        linear[linearOff] = tiled[tiledOff];
                }
            }
        }
        return linear;
    }

    private static byte[]? DecodeBC(byte[] data, int w, int h, string format)
    {
        var fmt = format.ToUpperInvariant() switch
        {
            "DXT1" or "BC1" => CompressionFormat.Bc1,
            "DXT3" or "BC2" => CompressionFormat.Bc2,
            "DXT5" or "BC3" => CompressionFormat.Bc3,
            "BC4"            => CompressionFormat.Bc4,
            "BC5"            => CompressionFormat.Bc5,
            "BC6H"           => CompressionFormat.Bc6S,
            "BC7"            => CompressionFormat.Bc7,
            _ => CompressionFormat.Unknown
        };
        if (fmt == CompressionFormat.Unknown) return null;

        var decoder = new BcDecoder();
        var colors = decoder.DecodeRaw(data, w, h, fmt);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            rgba[i * 4 + 0] = colors[i].r;
            rgba[i * 4 + 1] = colors[i].g;
            rgba[i * 4 + 2] = colors[i].b;
            rgba[i * 4 + 3] = colors[i].a;
        }
        return rgba;
    }

    private static byte[] SwapRB(byte[] rgba)
    {
        var bgra = (byte[])rgba.Clone();
        for (int i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    // ── Camera ───────────────────────────────────────────────────────────────

    private void FitCameraToVisible()
    {
        bool any = false;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        for (int i = 0; i < _submeshGeoms.Count; i++)
        {
            if (!_submeshVisible[i]) continue;
            foreach (var p in _submeshGeoms[i].Positions)
            {
                any = true;
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
        }
        if (!any) return;

        _target = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        _dist   = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ) * 1.6;
        if (_dist < 0.1) _dist = 0.1;
        _yaw    = 0;
        _pitch  = 15;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double yawRad   = _yaw   * Math.PI / 180;
        double pitchRad = _pitch * Math.PI / 180;

        double x = _dist * Math.Cos(pitchRad) * Math.Sin(yawRad);
        double y = _dist * Math.Sin(pitchRad);
        double z = _dist * Math.Cos(pitchRad) * Math.Cos(yawRad);

        Camera.Position      = new Point3D(_target.X + x, _target.Y + y, _target.Z + z);
        Camera.LookDirection = new Vector3D(_target.X - Camera.Position.X,
                                            _target.Y - Camera.Position.Y,
                                            _target.Z - Camera.Position.Z);
    }

    private void OnMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_orbiting) return;
        var pos = e.GetPosition(this);
        _yaw   += (pos.X - _lastMouse.X) * 0.4;
        _pitch -= (pos.Y - _lastMouse.Y) * 0.4;
        _pitch  = Math.Clamp(_pitch, -89, 89);
        _lastMouse = pos;
        UpdateCamera();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _dist *= e.Delta > 0 ? 0.9 : 1.1;
        _dist  = Math.Max(0.1, _dist);
        UpdateCamera();
    }

    private void OnResetCamera(object sender, RoutedEventArgs e) => FitCameraToVisible();

    // ── Placeholder ───────────────────────────────────────────────────────────

    private void ShowPlaceholder()
    {
        var expectedObj = !string.IsNullOrEmpty(_info.ArchivePath)
            ? Path.ChangeExtension(_info.ArchivePath, ".obj")
            : null;

        PlaceholderTitle.Text = _info.Name;
        PlaceholderSub.Text   = expectedObj != null
            ? $"Export from Noesis as .obj and save to:\n{expectedObj}"
            : "Export this asset from Noesis as .obj then use Browse.";

        PlaceholderPanel.Visibility = Visibility.Visible;
        Viewport3D.Visibility       = Visibility.Collapsed;
        DisplayPanel.Visibility     = Visibility.Collapsed;
        BtnReset.Visibility         = Visibility.Collapsed;
    }

    private void OnBrowseObj(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Open exported .obj file",
            Filter           = "Wavefront OBJ (*.obj)|*.obj",
            InitialDirectory = !string.IsNullOrEmpty(_info.ArchivePath)
                ? Path.GetDirectoryName(_info.ArchivePath) : null
        };

        if (dlg.ShowDialog(this) == true)
        {
            if (!TryLoadObj(dlg.FileName))
                System.Windows.MessageBox.Show("Could not parse the selected .obj file.",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
