using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.App.ViewModels;

/// <summary>
/// Top-level ViewModel — owns the navigation stack.
/// The MainWindow just binds its ContentControl to CurrentView;
/// DataTemplates in App.xaml map HomeViewModel→HomeView and AssetBrowserViewModel→AssetBrowserView.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly PluginLoader  _plugins;

    [ObservableProperty]
    private object currentView;

    [ObservableProperty]
    private string windowTitle = "Game Asset Explorer";

    public MainViewModel(ConfigManager config, PluginLoader plugins)
    {
        _config  = config;
        _plugins = plugins;

        // Start on the home screen
        var homeVm = CreateHomeViewModel();
        currentView = homeVm;
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private HomeViewModel CreateHomeViewModel()
    {
        var vm = new HomeViewModel(_config, _plugins);
        vm.GameOpened        += OnGameOpened;
        vm.AddGameRequested  += OnAddGameRequested;
        return vm;
    }

    private void OnGameOpened(object? sender, GameOpenedEventArgs e)
    {
        WindowTitle  = $"Game Asset Explorer — {e.Config.DisplayName}";
        var browserVm = new AssetBrowserViewModel(e.Engine, e.Config);
        browserVm.BackRequested += OnBackToHome;
        CurrentView = browserVm;
    }

    private void OnAddGameRequested(object? sender, EventArgs e)
    {
        // The HomeView code-behind handles opening the dialog.
        // This event exists so the MainViewModel can also react if needed.
    }

    private void OnBackToHome(object? sender, EventArgs e)
    {
        WindowTitle = "Game Asset Explorer";
        CurrentView = CreateHomeViewModel();
    }
}

/// <summary>Passed when the user opens a game from the library.</summary>
public class GameOpenedEventArgs : EventArgs
{
    public IGameEngine Engine { get; init; } = null!;
    public GameConfig  Config { get; init; } = null!;
}
