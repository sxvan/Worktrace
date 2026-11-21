using Capture.Core.Config;

namespace Capture.App;

/// <summary>Where things live on disk, and loading of config.json.</summary>
public sealed class AppPaths
{
    private AppPaths(string configFile, string root)
    {
        ConfigFile = configFile;
        Root = root;
    }

    /// <summary>
    /// config.json next to the executable. Its source is src/Capture.App/config.json; the build
    /// and the publish copy it there.
    /// </summary>
    public string ConfigFile { get; }
    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "capture.db");
    public string CapturesFolder => Path.Combine(Root, "captures");
    public string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>Where the database, captures and logs go when the config names no storage root.</summary>
    public static string DefaultRoot => ConfigLoader.DefaultRoot;

    /// <summary>Loads config.json. Throws if the file is missing or invalid.</summary>
    public static (AppPaths Paths, CaptureConfig Config) Load()
    {
        string configFile = Path.Combine(AppContext.BaseDirectory, "config.json");
        CaptureConfig config = ConfigLoader.Load(configFile);
        string root = ConfigLoader.ResolveRoot(config);
        Directory.CreateDirectory(root);

        return (new AppPaths(configFile, root), config);
    }
}
