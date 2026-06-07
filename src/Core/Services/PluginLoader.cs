using GameAssetExplorer.Core.Interfaces;
using System.Reflection;

namespace GameAssetExplorer.Core.Services;

/// <summary>
/// Discovers and loads engine plugins at runtime.
///
/// Plugins are just .dll files that implement IGameEngine.
/// Drop a new engine DLL in the /Engines folder and it gets picked up automatically.
/// This is how we'll eventually add RAGE, Decima, etc. without touching the main app.
/// </summary>
public class PluginLoader
{
    private readonly List<IGameEngine> _loadedEngines = new();
    private readonly string _pluginsDirectory;

    public PluginLoader(string pluginsDirectory)
    {
        _pluginsDirectory = pluginsDirectory;
    }

    /// <summary>All engine plugins currently loaded</summary>
    public IReadOnlyList<IGameEngine> Engines => _loadedEngines.AsReadOnly();

    /// <summary>
    /// Scan the plugins directory and load all valid engine plugins.
    /// Called once at app startup.
    /// </summary>
    public void LoadAll()
    {
        // Do NOT clear here — built-in engines registered via RegisterBuiltIn() must survive
        if (!Directory.Exists(_pluginsDirectory))
        {
            Directory.CreateDirectory(_pluginsDirectory);
            return;
        }

        foreach (var dllPath in Directory.GetFiles(_pluginsDirectory, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dllPath);
                var engineTypes = assembly.GetTypes()
                    .Where(t => typeof(IGameEngine).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                foreach (var engineType in engineTypes)
                {
                    if (Activator.CreateInstance(engineType) is IGameEngine engine)
                    {
                        _loadedEngines.Add(engine);
                        Log.Info($"PluginLoader: loaded external engine {engine.EngineName} ({engine.EngineId})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"PluginLoader: failed to load {dllPath}", ex);
            }
        }
    }

    /// <summary>
    /// Register a plugin instance directly (used for built-in plugins like UnrealEngine
    /// that ship with the app rather than being dropped in as external DLLs).
    /// </summary>
    public void RegisterBuiltIn(IGameEngine engine)
    {
        _loadedEngines.Add(engine);
        Log.Info($"PluginLoader: registered built-in {engine.EngineName} ({engine.EngineId})");
    }

    /// <summary>
    /// Find the best engine for a given game directory.
    /// Runs DetectEngine() on all loaded plugins and returns the highest-confidence match.
    /// Returns null if nothing scores above 0.3.
    /// </summary>
    public IGameEngine? AutoDetectEngine(string gameDirectory)
    {
        IGameEngine? best = null;
        float bestScore = 0.3f; // Minimum confidence threshold

        foreach (var engine in _loadedEngines)
        {
            var score = engine.DetectEngine(gameDirectory);
            if (score > bestScore)
            {
                bestScore = score;
                best = engine;
            }
        }

        return best;
    }

    /// <summary>Find a specific engine by its ID string</summary>
    public IGameEngine? GetEngineById(string engineId)
    {
        return _loadedEngines.FirstOrDefault(e =>
            string.Equals(e.EngineId, engineId, StringComparison.OrdinalIgnoreCase));
    }
}
