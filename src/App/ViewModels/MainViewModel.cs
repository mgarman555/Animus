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

    /// <summary>
    /// Games mounted right now. Opening a second game no longer unmounts the first, so the
    /// browser can switch between them instantly and search can span all of them.
    /// </summary>
    private readonly GameSession _session = new();

    /// <summary>One browser per game, kept so switching back does not re-index the archives.</summary>
    private readonly Dictionary<string, AssetBrowserViewModel> _browsers = new(StringComparer.OrdinalIgnoreCase);

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
        ShowBrowserFor(e.Config, e.Engine);
    }

    private void ShowBrowserFor(GameConfig config, IGameEngine engine)
    {
        WindowTitle = $"Game Asset Explorer — {config.DisplayName}";

        if (!_browsers.TryGetValue(config.Id, out var browserVm))
        {
            browserVm = new AssetBrowserViewModel(engine, config, _session);
            browserVm.BackRequested        += OnBackToHome;
            browserVm.SwitchGameRequested  += OnSwitchGame;
            _browsers[config.Id] = browserVm;
        }
        else
        {
            _session.SetActive(_session.Find(config.Id)!);
        }

        CurrentView = browserVm;
    }

    /// <summary>The game rail asked for a different mounted game.</summary>
    private void OnSwitchGame(object? sender, MountedGame game)
    {
        _session.SetActive(game);
        ShowBrowserFor(game.Config, game.Engine);
    }

    private void OnAddGameRequested(object? sender, EventArgs e)
    {
        // The HomeView code-behind handles opening the dialog.
        // This event exists so the MainViewModel can also react if needed.
    }

    private void OnBackToHome(object? sender, EventArgs e)
    {
        // Games stay mounted so reopening one is instant; the session is only torn down when
        // the user explicitly closes a game from the rail.
        WindowTitle = "Game Asset Explorer";
        CurrentView = CreateHomeViewModel();
    }

    /// <summary>Unmount everything — call on application shutdown.</summary>
    public Task ShutdownAsync()
    {
        _browsers.Clear();
        return _session.ClearAsync();
    }
}

/// <summary>Passed when the user opens a game from the library.</summary>
public class GameOpenedEventArgs : EventArgs
{
    public IGameEngine Engine { get; init; } = null!;
    public GameConfig  Config { get; init; } = null!;
}
