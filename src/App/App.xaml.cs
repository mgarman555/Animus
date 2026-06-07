using GameAssetExplorer.App.ViewModels;
using GameAssetExplorer.App.Views;
using GameAssetExplorer.Core.Services;
using GameAssetExplorer.Engines.UnrealEngine;
using GameAssetExplorer.Engines.NaughtyDog;
using GameAssetExplorer.Engines.RageEngine;
using GameAssetExplorer.Engines.SotrEngine;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace GameAssetExplorer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 0. Crash handlers (log everything) ────────────────────────────
        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Error("Unhandled UI exception", ev.Exception);
            // Don't mark handled — let WPF show the standard crash dialog so user notices
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex)
                Log.Error("Unhandled domain exception", ex);
            else
                Log.Error($"Unhandled domain exception: {ev.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("Unobserved task exception", ev.Exception);
            ev.SetObserved();
        };

        Log.Info($"App starting — version {typeof(App).Assembly.GetName().Version}");
        Log.Info($"Log file: {Log.LogFile}");

        // ── 1. Core services ──────────────────────────────────────────────
        // One-time cover-art cache migration to portrait posters
        var coversDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameAssetExplorer", "covers");
        CoverArtService.MigrateCacheIfNeeded(coversDir);

        var configManager = new ConfigManager();
        configManager.Load();
        Log.Info($"Loaded config — {configManager.Games.Count} games in library");

        // ── 2. Engine plugins ─────────────────────────────────────────────
        var enginesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engines");
        var pluginLoader = new PluginLoader(enginesDir);

        // Built-in engine plugins
        pluginLoader.RegisterBuiltIn(new UnrealEnginePlugin());   // UE4 / UE5
        pluginLoader.RegisterBuiltIn(new NaughtyDogPlugin());     // TLOU1, TLOU2, Uncharted 4 PC
        pluginLoader.RegisterBuiltIn(new RageEnginePlugin());     // GTA V, RDR 2
        pluginLoader.RegisterBuiltIn(new SotrEnginePlugin());    // Shadow of the Tomb Raider

        // External plugins from /Engines/*.dll (future: Decima, etc.)
        pluginLoader.LoadAll();
        Log.Info($"Loaded {pluginLoader.Engines.Count} engine plugin(s): {string.Join(", ", pluginLoader.Engines.Select(p => p.EngineId))}");

        // ── 3. Main window ────────────────────────────────────────────────
        var mainViewModel = new MainViewModel(configManager, pluginLoader);
        var mainWindow    = new MainWindow { DataContext = mainViewModel };
        mainWindow.Show();
    }
}
