using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Exporters.TextureExporter;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Standalone window for previewing a single texture with zoom/pan and PNG export.
/// </summary>
public partial class TexturePreviewWindow : Window
{
    private readonly TextureAssetData _texture;
    private readonly BitmapSource? _bitmap;

    private bool _dragging;
    private System.Windows.Point _dragStart;
    private Matrix _dragStartMatrix;

    public TexturePreviewWindow(TextureAssetData texture)
    {
        InitializeComponent();

        _texture = texture;
        _bitmap = TextureDecodeHelper.TryDecode(texture);

        Title = $"Texture — {texture.Info.Name}";
        LblName.Text = texture.Info.Name;
        LblMeta.Text = $"{texture.Width} × {texture.Height}  ·  {texture.SourceFormat}" +
                       (texture.IsSrgb ? "  ·  sRGB" : "");

        if (_bitmap == null)
        {
            LblEmpty.Visibility = Visibility.Visible;
            LblStatus.Text = "Decode failed — texture format may not be supported.";
            return;
        }

        Img.Source = _bitmap;
        Img.Width  = _bitmap.PixelWidth;
        Img.Height = _bitmap.PixelHeight;
    }

    // ── Initial fit ───────────────────────────────────────────────────────

    private bool _initialFit;
    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_initialFit || _bitmap == null) return;
        if (ImageHost.ActualWidth <= 0 || ImageHost.ActualHeight <= 0) return;

        FitToWindow();
        _initialFit = true;
    }

    private void FitToWindow()
    {
        if (_bitmap == null) return;

        double sx = ImageHost.ActualWidth  / _bitmap.PixelWidth;
        double sy = ImageHost.ActualHeight / _bitmap.PixelHeight;
        double scale = Math.Min(sx, sy) * 0.95;
        if (scale <= 0) return;

        double tx = (ImageHost.ActualWidth  - _bitmap.PixelWidth  * scale) / 2;
        double ty = (ImageHost.ActualHeight - _bitmap.PixelHeight * scale) / 2;

        Xform.Matrix = new Matrix(scale, 0, 0, scale, tx, ty);
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        LblZoom.Text = $"{Xform.Matrix.M11 * 100:F0}%";
    }

    // ── Zoom (mouse wheel, around cursor) ─────────────────────────────────

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bitmap == null) return;

        var pos = e.GetPosition(ImageHost);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;

        var m = Xform.Matrix;
        double newScale = m.M11 * factor;
        if (newScale < 0.05 || newScale > 64) return;

        m.ScaleAtPrepend(factor, factor, pos.X, pos.Y);
        Xform.Matrix = m;
        UpdateZoomLabel();
    }

    // ── Pan (left-click drag) ─────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap == null) return;

        if (e.ClickCount >= 2)
        {
            FitToWindow();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(ImageHost);
        _dragStartMatrix = Xform.Matrix;
        ImageHost.CaptureMouse();
        ImageHost.Cursor = System.Windows.Input.Cursors.SizeAll;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ImageHost.ReleaseMouseCapture();
        ImageHost.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(ImageHost);
        var m = _dragStartMatrix;
        m.OffsetX += p.X - _dragStart.X;
        m.OffsetY += p.Y - _dragStart.Y;
        Xform.Matrix = m;
    }

    // ── Save PNG ──────────────────────────────────────────────────────────

    private async void OnSavePng(object sender, RoutedEventArgs e)
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "GameAssetExplorer_Exports");

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose folder to save PNG",
            SelectedPath = Directory.Exists(defaultDir) ? defaultDir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        try
        {
            LblStatus.Text = "Exporting…";
            var exporter = new PngTextureExporter();
            var result = await exporter.ExportAsync(_texture, dlg.SelectedPath, new ExportSettings());

            LblStatus.Text = result.Success
                ? $"Saved: {result.OutputPath}"
                : $"Export failed: {result.ErrorMessage}";

            if (!result.Success)
                MessageBox.Show(this, result.ErrorMessage ?? "Unknown error", "Export Failed");
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"Export failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Export Failed");
        }
    }
}
