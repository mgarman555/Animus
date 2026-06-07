using GameAssetExplorer.App.ViewModels;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace GameAssetExplorer.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private HomeViewModel? _vm;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.AddGameRequested -= OnAddGameRequested;
            _vm.LoadError        -= OnLoadError;
        }

        _vm = DataContext as HomeViewModel;

        if (_vm != null)
        {
            _vm.AddGameRequested += OnAddGameRequested;
            _vm.LoadError        += OnLoadError;
        }
    }

    // ── Logs button ──────────────────────────────────────────────────────────

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Opens File Explorer at the log folder with app.log selected
            Process.Start("explorer.exe", $"/select,\"{Log.LogFile}\"");
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Could not open log folder:\n\n{ex.Message}\n\nLog path: {Log.LogFile}",
                "Open Logs");
        }
    }

    // ── Error dialog ─────────────────────────────────────────────────────────

    private void OnLoadError(object? sender, LoadErrorArgs e)
    {
        Dispatcher.Invoke(() =>
            MessageBox.Show(e.Message, e.Title,
                MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    // ── Add game dialog (triggered by "Load Game" button) ────────────────────

    private void OnAddGameRequested(object? sender, EventArgs e)
    {
        if (_vm == null) return;

        var dialog = new AddGameDialog(null, _vm.AvailableEngines)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            bool added = _vm.AddGameFromDialog(dialog.Result);
            if (!added)
            {
                MessageBox.Show(
                    $"A game with the directory\n\n\"{dialog.Result.GameDirectory}\"\n\nis already in your library.",
                    "Duplicate Game",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    // ── Info / Edit button on card ────────────────────────────────────────────

    private void OnCardInfoButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is GameLibraryEntry entry)
            OpenEditDialog(entry);
    }

    // ── ⋮ menu button on cards (left-click opens the same context menu) ──────

    private void OnCardMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;

        // Walk up to the Card border (identified by its Tag = GameLibraryEntry binding,
        // not the Button's own template Border)
        DependencyObject? cur = btn;
        Border? card = null;
        while (cur != null)
        {
            if (cur is Border b && b.Tag is GameLibraryEntry) { card = b; break; }
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }

        if (card?.ContextMenu != null)
        {
            card.ContextMenu.PlacementTarget = btn;
            card.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            card.ContextMenu.IsOpen = true;
        }
        e.Handled = true;
    }

    // ── Right-click context menu on cards ────────────────────────────────────

    private void OnCardEditMenuClick(object sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromMenuSender(sender);
        if (entry != null) OpenEditDialog(entry);
    }

    private void OnCardDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromMenuSender(sender);
        if (entry == null) return;

        var result = MessageBox.Show(
            $"Remove \"{entry.DisplayName}\" from the library?\n\nThis does not delete any game files.",
            "Remove Game",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            _vm?.RemoveGameCommand.Execute(entry);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void OpenEditDialog(GameLibraryEntry entry)
    {
        if (_vm == null) return;

        var dialog = new AddGameDialog(entry.Config, _vm.AvailableEngines)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true && dialog.Result != null)
            _vm.UpdateGameFromDialog(dialog.Result);
    }

    private static GameLibraryEntry? GetEntryFromMenuSender(object sender)
    {
        if (sender is MenuItem item &&
            item.Parent is ContextMenu cm &&
            cm.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as GameLibraryEntry;
        return null;
    }
}
