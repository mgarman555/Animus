using GameAssetExplorer.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Always-available log drawer. Click the header bar to expand/collapse.
/// Uses a RichTextBox so the user can select + copy text across lines using normal
/// keyboard shortcuts (Ctrl+A, Ctrl+C) or right-click → Copy.
/// Capped at the most recent 1000 lines so memory stays bounded.
/// </summary>
public partial class LogPanel : System.Windows.Controls.UserControl
{
    private const int MaxLines = 1000;
    private const double ExpandedHeight = 240;

    private bool _expanded;
    private int _lineCount;

    public LogPanel()
    {
        InitializeComponent();

        Log.OnLine += OnLogLine;
        Unloaded += (_, _) => Log.OnLine -= OnLogLine;
    }

    private void OnLogLine(string level, string formattedLine)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogLine(level, formattedLine));
            return;
        }

        var brush = ColorForLevel(level);

        var para = new Paragraph(new Run(formattedLine) { Foreground = brush })
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            LineHeight = 14,
        };

        LogRtb.Document.Blocks.Add(para);
        _lineCount++;

        // Trim oldest paragraphs once we exceed the cap
        while (_lineCount > MaxLines && LogRtb.Document.Blocks.FirstBlock is { } first)
        {
            LogRtb.Document.Blocks.Remove(first);
            _lineCount--;
        }

        // Tail preview in the collapsed header
        LblTail.Text = formattedLine;
        LblTail.Foreground = brush;

        if (_expanded)
            LogRtb.ScrollToEnd();
    }

    private static Brush ColorForLevel(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x47, 0x47)),
        "WARN"  => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x20)),
        _       => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
    };

    // ── Expand/collapse via header click ────────────────────────────────

    private void OnHeaderClick(object sender, MouseButtonEventArgs e) => Toggle();

    private void Toggle()
    {
        _expanded = !_expanded;
        BodyRow.Height = _expanded ? new GridLength(ExpandedHeight) : new GridLength(0);
        ChevronText.Text = _expanded ? "▾" : "▸";

        if (_expanded)
            Dispatcher.BeginInvoke(() => LogRtb.ScrollToEnd(),
                System.Windows.Threading.DispatcherPriority.Background);
    }

    // Stop button clicks from bubbling up to the header (which would toggle the panel)
    private void OnButtonStopBubble(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ── Toolbar actions ─────────────────────────────────────────────────

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = BuildLogText();

        // Win32 clipboard sometimes refuses immediate ownership when another app is
        // holding it. Retry with SetDataObject(copy=true) so the text persists in the
        // clipboard even after our process exits.
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                LblTail.Text = $"Copied {_lineCount} line(s) to clipboard.";
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                System.Threading.Thread.Sleep(80);
            }
        }

        // Fallback: dump to a sibling text file
        try
        {
            var fallback = Path.Combine(Path.GetDirectoryName(Log.LogFile)!, "log-snapshot.txt");
            File.WriteAllText(fallback, text);
            LblTail.Text = $"Clipboard busy — saved to {fallback}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Copy and fallback save both failed:\n\n{lastError?.Message}\n{ex.Message}",
                "Logs");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title           = "Save log snapshot",
            Filter          = "Text file (*.txt)|*.txt",
            FileName        = $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            InitialDirectory = Path.GetDirectoryName(Log.LogFile),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, BuildLogText());
            LblTail.Text = $"Saved log to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}", "Logs");
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogRtb.Document.Blocks.Clear();
        _lineCount = 0;
        LblTail.Text = "";
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{Log.LogFile}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log folder:\n\n{ex.Message}\n\nLog path: {Log.LogFile}",
                "Open Logs");
        }
    }

    // Extract all lines from the RichTextBox as plain text. Walks paragraphs in order
    // and appends each Run's text plus a newline.
    private string BuildLogText()
    {
        var sb = new StringBuilder();
        foreach (var block in LogRtb.Document.Blocks)
        {
            if (block is Paragraph p)
            {
                foreach (var inline in p.Inlines)
                    if (inline is Run r) sb.Append(r.Text);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
