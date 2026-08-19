using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GameAssetExplorer.Core.Animation;
using GameAssetExplorer.Core.Models;
using Color = System.Windows.Media.Color;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Animation half of the skeletal-mesh viewer: bind-pose maths, clip selection, transport,
/// and the per-frame CPU skinning that actually moves the geometry.
///
/// WPF has no GPU skinning path, so posing means rewriting each submesh's position buffer
/// every frame. Two things make that affordable at character density:
///   • rest positions and normals are snapshotted once per LOD, so a frame is one pass over
///     the vertices rather than a re-parse of the source buffers;
///   • the new <see cref="Point3DCollection"/> is frozen before being assigned, which skips
///     WPF's per-item change notification — that is the difference between a few milliseconds
///     and a few seconds on a 50k-vertex mesh.
/// Only visible submeshes are skinned, so hiding parts in the outliner also buys frame time.
/// </summary>
public partial class SkeletalMeshViewerWindow
{
    // ── Bind pose ────────────────────────────────────────────────────────────
    private Matrix4x4[]? _worldBind;
    private Matrix4x4[]? _inverseBind;

    // ── Rest geometry snapshots, parallel to _submeshGeoms ───────────────────
    private readonly List<Point3D[]>  _restPositions = new();
    private readonly List<Vector3D[]> _restNormals   = new();

    // ── Playback ─────────────────────────────────────────────────────────────
    private readonly List<AnimationAssetData> _clips = new();
    private AnimationSampler? _sampler;
    private Matrix4x4[]? _localScratch;
    private bool  _playing;
    private float _time;
    private long  _lastTick;
    private bool  _renderHooked;
    private bool  _sliderSyncing;
    private bool  _clipComboSyncing;
    private bool  _skeletonComboSyncing;
    private bool  _decodingClip;

    /// <summary>One row of the clip dropdown.</summary>
    private sealed record ClipChoice(string Label, AnimationAssetData? Clip, AnimationSourceRef? Source)
    {
        public override string ToString() => Label;
    }

    // ── Setup ────────────────────────────────────────────────────────────────

    /// <summary>Called once per LOD load: rebuild bind pose, rest snapshots and the clip list.</summary>
    private void InitAnimation(LodData lod)
    {
        StopPlayback();
        _sampler = null;
        _time = 0;

        // Rest snapshots must be taken from the geometry we just built, so they match the
        // submesh order and the vertex ranges exactly.
        _restPositions.Clear();
        _restNormals.Clear();
        foreach (var geo in _submeshGeoms)
        {
            _restPositions.Add(geo.Positions.ToArray());
            _restNormals.Add(geo.Normals != null && geo.Normals.Count == geo.Positions.Count
                ? geo.Normals.ToArray()
                : Array.Empty<Vector3D>());
        }

        var skeleton = _meshData?.Skeleton;
        if (skeleton is { Bones.Count: > 0 })
        {
            _worldBind    = SkeletonMath.ComputeWorldBind(skeleton);
            _inverseBind  = SkeletonMath.ComputeInverseBind(_worldBind);
            _localScratch = new Matrix4x4[skeleton.Bones.Count];
        }
        else
        {
            _worldBind = null;
            _inverseBind = null;
            _localScratch = null;
        }

        BuildSkeletonList();
        BuildClipList(lod, skeleton);
    }

    /// <summary>
    /// Populate the rig picker. Automatic resolution can only verify that a skeleton has
    /// enough bones to cover the mesh's weights — a different rig of sufficient size passes
    /// that test and puts every weight on the wrong joint, so the correction has to be
    /// reachable.
    /// </summary>
    private void BuildSkeletonList()
    {
        _skeletonComboSyncing = true;
        SkeletonCombo.Items.Clear();

        var paths = _meshData?.AvailableSkeletonPaths ?? Array.Empty<string>();
        string current = _meshData?.Skeleton?.SourceName ?? string.Empty;
        int selected = 0;

        SkeletonCombo.Items.Add(new SkeletonChoice(
            string.IsNullOrEmpty(current) ? "(none resolved)" : $"{current}  (auto)", null));

        for (int i = 0; i < paths.Count; i++)
        {
            string label = Path.GetFileNameWithoutExtension(paths[i]);
            SkeletonCombo.Items.Add(new SkeletonChoice(label, paths[i]));
            if (!string.IsNullOrEmpty(current) &&
                label.Equals(Path.GetFileNameWithoutExtension(current), StringComparison.OrdinalIgnoreCase))
                selected = i + 1;
        }

        SkeletonCombo.SelectedIndex = selected;
        SkeletonCombo.IsEnabled = paths.Count > 0 && _meshData?.ResolveSkeletonOverride != null;
        _skeletonComboSyncing = false;
    }

    private sealed record SkeletonChoice(string Label, string? Path)
    {
        public override string ToString() => Label;
    }

    private void OnSkeletonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_skeletonComboSyncing || _meshData == null) return;
        if (SkeletonCombo.SelectedItem is not SkeletonChoice choice || choice.Path == null) return;
        if (_meshData.ResolveSkeletonOverride is not { } resolve) return;

        var replacement = resolve(choice.Path);
        if (replacement is not { Bones.Count: > 0 })
        {
            AnimStatusText.Text = $"{choice.Label}: no usable skeleton in that pak.";
            return;
        }

        StopPlayback();
        _meshData.Skeleton = replacement;
        _sampler = null;
        _time = 0;

        _worldBind    = SkeletonMath.ComputeWorldBind(replacement);
        _inverseBind  = SkeletonMath.ComputeInverseBind(_worldBind);
        _localScratch = new Matrix4x4[replacement.Bones.Count];

        ChkArmature.IsEnabled = true;
        ApplyRestPose();

        var lod = CurrentLod();
        if (lod != null) BuildClipList(lod, replacement);
        UpdateArmatureVisual();
    }

    private LodData? CurrentLod()
    {
        if (_meshData == null || _meshData.Lods.Count == 0) return null;
        int idx = LodCombo.SelectedIndex;
        if (idx < 0 || idx >= _meshData.Lods.Count) idx = 0;
        return _meshData.Lods[idx];
    }

    private void BuildClipList(LodData lod, SkeletonData? skeleton)
    {
        _clips.Clear();
        _clips.AddRange(_meshData?.Animations ?? Enumerable.Empty<AnimationAssetData>());

        _clipComboSyncing = true;
        AnimClipCombo.Items.Clear();
        AnimClipCombo.Items.Add(new ClipChoice("Bind pose", null, null));

        foreach (var c in _clips)
            AnimClipCombo.Items.Add(new ClipChoice(DescribeClip(c), c, null));

        var decoded = new HashSet<string>(
            _clips.Select(c => c.RawProperties.TryGetValue("Source", out var v) ? v?.ToString() ?? "" : ""),
            StringComparer.OrdinalIgnoreCase);

        foreach (var src in _meshData?.AnimationSources ?? new List<AnimationSourceRef>())
        {
            if (decoded.Contains(src.DisplayName)) continue;   // already read this pak
            AnimClipCombo.Items.Add(new ClipChoice(
                $"⬇  {src.DisplayName}  ({FormatSize(src.SizeBytes)})", null, src));
        }

        AnimClipCombo.SelectedIndex = 0;
        _clipComboSyncing = false;

        bool posable = skeleton is { Bones.Count: > 0 } && lod.Skin != null;
        AnimBar.Visibility = skeleton is { Bones.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        AnimStatusText.Text = DescribeRigWithCheck(lod, skeleton, posable);
        SetTransportEnabled(false);
    }

    /// <summary>
    /// Does the mesh actually belong to this skeleton? Measured, not assumed: a vertex sits
    /// close to the joints that drive it, so the average gap between the two — as a fraction
    /// of the rig — is small when they are paired correctly and large when they are not. This
    /// is the one cheap check that catches a plausible-but-wrong rig and geometry carrying a
    /// baked transform the bind pose does not know about; both render fine and then shear the
    /// moment the character moves.
    /// </summary>
    private string DescribeRestCheck(LodData lod, SkeletonData skeleton)
    {
        var a = SkeletonMath.MeasureRestAgreement(skeleton, lod, _worldBind);
        if (a.SampleCount == 0) return string.Empty;

        return a.Plausible
            ? $"  Rest-pose check: vertices sit {a.Ratio:P1} of rig size from their driving joints ✓"
            : $"  ⚠ Rest-pose check FAILED: vertices sit {a.Ratio:P1} of rig size from their driving " +
              "joints. The mesh and this skeleton are not in the same space — try another rig.";
    }

    /// <summary>
    /// One line saying exactly what the viewer is able to do with this asset — which skeleton
    /// it found, whether the mesh is actually skinned to it, and if not, why.
    /// </summary>
    private static string DescribeRig(LodData lod, SkeletonData? skeleton, bool posable)
    {
        if (skeleton is not { Bones.Count: > 0 })
            return "No skeleton resolved for this asset — nothing to pose.";

        string src = string.IsNullOrEmpty(skeleton.SourceName) ? "this pak" : skeleton.SourceName;

        if (lod.Skin is not { } skin)
            return $"Skeleton: {skeleton.Bones.Count} bones from {src}. " +
                   "This LOD has no skin weights, so it can only be shown in bind pose.";

        string coverage = skin.MaxBoneIndex >= skeleton.Bones.Count
            ? $"  ⚠ weights reference bone {skin.MaxBoneIndex} but the skeleton has only {skeleton.Bones.Count} — wrong skeleton."
            : "";

        return $"Skeleton: {skeleton.Bones.Count} bones from {src}. " +
               $"Skinned with up to {skin.InfluencesPerVertex} influences/vertex.{coverage}" +
               (posable ? "" : "  (not posable)");
    }

    /// <summary>Rig description plus the measured rest-pose check.</summary>
    private string DescribeRigWithCheck(LodData lod, SkeletonData? skeleton, bool posable)
    {
        string baseText = DescribeRig(lod, skeleton, posable);
        return skeleton is { Bones.Count: > 0 } && lod.Skin != null
            ? baseText + DescribeRestCheck(lod, skeleton)
            : baseText;
    }

    private static string DescribeClip(AnimationAssetData c)
    {
        string name = string.IsNullOrEmpty(c.ClipName) ? c.Info.Name : c.ClipName;
        if (c.FrameCount <= 0) return $"{name}  (no keyframes decoded)";
        return $"{name}  ({c.FrameCount} f @ {c.FrameRate:0.#} fps{(c.IsAdditive ? ", additive" : "")})";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _           => $"{bytes} B",
    };

    // ── Clip selection ───────────────────────────────────────────────────────

    private async void OnAnimClipChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clipComboSyncing || _decodingClip) return;
        if (AnimClipCombo.SelectedItem is not ClipChoice choice) return;

        StopPlayback();

        if (choice.Clip == null && choice.Source == null)
        {
            // Bind pose
            _sampler = null;
            _time = 0;
            SetTransportEnabled(false);
            ApplyRestPose();
            UpdateArmatureVisual();
            return;
        }

        if (choice.Source is { } src)
        {
            await LoadClipsFromSourceAsync(src);
            return;
        }

        BindSampler(choice.Clip!);
    }

    private async Task LoadClipsFromSourceAsync(AnimationSourceRef src)
    {
        if (_meshData?.ResolveAnimations is not { } resolver)
        {
            AnimStatusText.Text = $"{src.DisplayName}: this engine has no animation decoder wired up.";
            return;
        }

        _decodingClip = true;
        AnimStatusText.Text = $"Reading {src.DisplayName} …";
        AnimClipCombo.IsEnabled = false;

        try
        {
            var decoded = await resolver(src);

            var usable = decoded.Where(c => c.FrameCount > 0 && c.Tracks.Count > 0).ToList();

            if (usable.Count == 0)
            {
                string why = decoded.Count > 0 && decoded[0].RawProperties.TryGetValue("Outcome", out var o)
                    ? o?.ToString() ?? ""
                    : "";
                AnimStatusText.Text = $"{src.DisplayName}: no playable clips. " +
                    (string.IsNullOrEmpty(why) ? "" : why);
                return;
            }

            // Splice the decoded clips into the dropdown in place of the source row.
            _clipComboSyncing = true;
            int insertAt = AnimClipCombo.SelectedIndex;
            AnimClipCombo.Items.RemoveAt(insertAt);
            for (int i = 0; i < usable.Count; i++)
            {
                _clips.Add(usable[i]);
                // Park them on the asset too, so switching LOD or reopening the viewer does not
                // throw away a decode that just cost seconds of I/O.
                if (_meshData != null && !_meshData.Animations.Contains(usable[i]))
                    _meshData.Animations.Add(usable[i]);
                AnimClipCombo.Items.Insert(insertAt + i, new ClipChoice(DescribeClip(usable[i]), usable[i], null));
            }
            AnimClipCombo.SelectedIndex = insertAt;
            _clipComboSyncing = false;

            BindSampler(usable[0]);
        }
        catch (Exception ex)
        {
            AnimStatusText.Text = $"{src.DisplayName}: {ex.Message}";
        }
        finally
        {
            _decodingClip = false;
            AnimClipCombo.IsEnabled = true;
        }
    }

    private void BindSampler(AnimationAssetData clip)
    {
        var skeleton = _meshData?.Skeleton;
        if (skeleton is not { Bones.Count: > 0 }) return;

        _sampler = new AnimationSampler(skeleton, clip);
        _time = 0;

        _sliderSyncing = true;
        AnimSlider.Minimum = 0;
        AnimSlider.Maximum = Math.Max(_sampler.Duration, 0.0001);
        AnimSlider.Value   = 0;
        _sliderSyncing = false;

        SetTransportEnabled(true);

        string bind = $"{_sampler.BoundTrackCount}/{clip.Tracks.Count} tracks bound to " +
                      $"{skeleton.Bones.Count} bones";
        string unbound = _sampler.UnboundTracks.Count > 0
            ? $"  ({_sampler.UnboundTracks.Count} track(s) had no matching bone)"
            : "";
        string additive = clip.IsAdditive
            ? "  ⚠ additive clip — layered on the bind pose; it is meant to be combined with a base animation."
            : "";
        string mapping = clip.Tracks.All(t => t.BoneIndex >= 0) && clip.JointNames.Count > 0
                         && clip.Tracks.Count != skeleton.Bones.Count
            ? "  ⚠ tracks were mapped to bones positionally, not by name — verify before trusting."
            : "";

        AnimStatusText.Text = $"{DescribeClip(clip)} — {bind}{unbound}{additive}{mapping}";

        ApplyPose(0);
        UpdateArmatureVisual();
    }

    private void SetTransportEnabled(bool on)
    {
        BtnPlayPause.IsEnabled = on;
        AnimSlider.IsEnabled   = on;
        if (!on)
        {
            BtnPlayPause.Content = "▶";
            AnimFrameText.Text   = "—";
        }
        else
        {
            UpdateFrameLabel();
        }
    }

    // ── Transport ────────────────────────────────────────────────────────────

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_sampler == null) return;
        if (_playing) StopPlayback();
        else StartPlayback();
    }

    private void StartPlayback()
    {
        if (_sampler == null || _sampler.Duration <= 0) return;
        _playing = true;
        BtnPlayPause.Content = "⏸";
        _lastTick = DateTime.UtcNow.Ticks;
        if (!_renderHooked)
        {
            CompositionTarget.Rendering += OnRenderTick;
            _renderHooked = true;
        }
    }

    private void StopPlayback()
    {
        _playing = false;
        if (_renderHooked)
        {
            CompositionTarget.Rendering -= OnRenderTick;
            _renderHooked = false;
        }
        // BtnPlayPause is null while the window is still being constructed.
        if (BtnPlayPause != null) BtnPlayPause.Content = "▶";
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!_playing || _sampler == null) return;

        long now = DateTime.UtcNow.Ticks;
        float dt = (float)((now - _lastTick) / (double)TimeSpan.TicksPerSecond);
        _lastTick = now;

        // A long stall (window drag, a slow decode) must not fast-forward the clip.
        if (dt > 0.25f) dt = 0.25f;

        float duration = _sampler.Duration;
        _time += dt;

        if (_time > duration)
        {
            if (ChkLoop.IsChecked == true) _time = duration > 0 ? _time % duration : 0;
            else { _time = duration; StopPlayback(); }
        }

        _sliderSyncing = true;
        AnimSlider.Value = _time;
        _sliderSyncing = false;

        ApplyPose(_time);
        UpdateArmatureVisual();
        UpdateFrameLabel();
    }

    private void OnAnimSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderSyncing || _sampler == null) return;
        _time = (float)e.NewValue;
        ApplyPose(_time);
        UpdateArmatureVisual();
        UpdateFrameLabel();
    }

    private void UpdateFrameLabel()
    {
        if (_sampler == null) { AnimFrameText.Text = "—"; return; }
        int frames = Math.Max(_sampler.FrameCount, 1);
        int cur = _sampler.Duration > 0
            ? (int)Math.Round(_time / _sampler.Duration * (frames - 1))
            : 0;
        AnimFrameText.Text = $"{cur} / {frames - 1}   {_time:0.00}s";
    }

    // ── Posing ───────────────────────────────────────────────────────────────

    /// <summary>Put every submesh back to the geometry as authored.</summary>
    private void ApplyRestPose()
    {
        for (int i = 0; i < _submeshGeoms.Count && i < _restPositions.Count; i++)
        {
            var pos = new Point3DCollection(_restPositions[i]);
            pos.Freeze();
            _submeshGeoms[i].Positions = pos;

            if (i < _restNormals.Count && _restNormals[i].Length > 0)
            {
                var nrm = new Vector3DCollection(_restNormals[i]);
                nrm.Freeze();
                _submeshGeoms[i].Normals = nrm;
            }
        }
    }

    /// <summary>Skin every visible submesh into the pose at <paramref name="time"/>.</summary>
    private void ApplyPose(float time)
    {
        if (_sampler == null || _inverseBind == null) { ApplyRestPose(); return; }
        var skin = CurrentSkin();
        if (skin == null) { ApplyRestPose(); return; }

        var palette = _sampler.SamplePalette(time, _inverseBind, _localScratch);

        for (int i = 0; i < _submeshGeoms.Count; i++)
        {
            if (i >= _submeshVisible.Count || !_submeshVisible[i]) continue;
            if (i >= _restPositions.Count || i >= _submeshInfos.Count) continue;

            var sm   = _submeshInfos[i];
            var rest = _restPositions[i];
            var restN = i < _restNormals.Count ? _restNormals[i] : Array.Empty<Vector3D>();

            var outPos = new Point3D[rest.Length];
            var outNrm = restN.Length == rest.Length ? new Vector3D[rest.Length] : null;

            int inf = Math.Max(skin.InfluencesPerVertex, 1);

            for (int k = 0; k < rest.Length; k++)
            {
                int v = sm.VertexStart + k;
                if (v >= skin.VertexCount)
                {
                    outPos[k] = rest[k];
                    if (outNrm != null) outNrm[k] = restN[k];
                    continue;
                }

                var p = new Vector3((float)rest[k].X, (float)rest[k].Y, (float)rest[k].Z);
                var n = outNrm != null
                    ? new Vector3((float)restN[k].X, (float)restN[k].Y, (float)restN[k].Z)
                    : Vector3.Zero;

                var accP = Vector3.Zero;
                var accN = Vector3.Zero;
                float total = 0f;
                int row = v * inf;

                for (int j = 0; j < inf; j++)
                {
                    float w = skin.BoneWeights[row + j];
                    if (w <= 0f) continue;
                    int b = skin.BoneIndices[row + j];
                    if (b >= palette.Length) continue;

                    accP += Vector3.Transform(p, palette[b]) * w;
                    if (outNrm != null) accN += Vector3.TransformNormal(n, palette[b]) * w;
                    total += w;
                }

                if (total > 1e-6f)
                {
                    var fp = accP / total;
                    outPos[k] = new Point3D(fp.X, fp.Y, fp.Z);
                    if (outNrm != null)
                    {
                        var fn = accN / total;
                        float len = fn.Length();
                        outNrm[k] = len > 1e-6f
                            ? new Vector3D(fn.X / len, fn.Y / len, fn.Z / len)
                            : restN[k];
                    }
                }
                else
                {
                    // No usable influence: leave the vertex where it was authored rather than
                    // collapsing it to the origin.
                    outPos[k] = rest[k];
                    if (outNrm != null) outNrm[k] = restN[k];
                }
            }

            var posColl = new Point3DCollection(outPos);
            posColl.Freeze();
            _submeshGeoms[i].Positions = posColl;

            if (outNrm != null)
            {
                var nrmColl = new Vector3DCollection(outNrm);
                nrmColl.Freeze();
                _submeshGeoms[i].Normals = nrmColl;
            }
        }
    }

    private SkinBinding? CurrentSkin()
    {
        if (_meshData == null) return null;
        int idx = LodCombo.SelectedIndex;
        if (idx < 0 || idx >= _meshData.Lods.Count) idx = 0;
        return _meshData.Lods.Count > idx ? _meshData.Lods[idx].Skin : null;
    }

    // ── Armature overlay ─────────────────────────────────────────────────────

    /// <summary>
    /// Draw the real skeleton: full world transforms (rotation included), in the current pose
    /// when a clip is playing and in the bind pose otherwise. The previous version summed
    /// parent translations only, which drew a plausible-looking but incorrect rig.
    /// </summary>
    private void UpdateArmatureVisual()
    {
        if (ChkArmature.IsChecked != true || _meshData?.Skeleton?.Bones is not { Count: > 0 } bones)
        {
            ArmatureVisual.Content = null;
            return;
        }

        Vector3[] world;
        if (_sampler != null)
        {
            world = _sampler.SampleJointPositions(_time, _localScratch);
        }
        else
        {
            _worldBind ??= SkeletonMath.ComputeWorldBind(_meshData.Skeleton);
            world = new Vector3[_worldBind.Length];
            for (int i = 0; i < _worldBind.Length; i++) world[i] = _worldBind[i].Translation;
        }

        if (world.Length == 0) { ArmatureVisual.Content = null; return; }

        var pts = new Point3D[world.Length];
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        for (int i = 0; i < world.Length; i++)
        {
            var w = world[i];
            pts[i] = new Point3D(w.X, w.Y, w.Z);
            if (w.X < minX) minX = w.X; if (w.X > maxX) maxX = w.X;
            if (w.Y < minY) minY = w.Y; if (w.Y > maxY) maxY = w.Y;
            if (w.Z < minZ) minZ = w.Z; if (w.Z > maxZ) maxZ = w.Z;
        }

        double extent = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
        if (extent <= 0 || double.IsNaN(extent)) extent = 1;
        double joint = Math.Max(extent * 0.012, 0.001);

        var group = new Model3DGroup();
        var jointMat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x40)));
        var boneMat  = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xC0, 0xA0, 0x40)));

        foreach (var p in pts)
            group.Children.Add(new GeometryModel3D { Geometry = MakeBox(p, joint), Material = jointMat });

        for (int i = 0; i < bones.Count && i < pts.Length; i++)
        {
            int p = bones[i].ParentIndex;
            if (p < 0 || p >= pts.Length) continue;
            var seg = MakeBoneSegment(pts[p], pts[i], joint * 0.5);
            if (seg != null)
                group.Children.Add(new GeometryModel3D { Geometry = seg, Material = boneMat });
        }

        ArmatureVisual.Content = group;
    }
}
