using System.Text.Json;
using Capture.Core.Config;

namespace Capture.App;

/// <summary>Where things live on disk, and loading of config.json.</summary>
public sealed class AppPaths
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private AppPaths(string configFile, string root)
    {
        ConfigFile = configFile;
        Root = root;
    }

    /// <summary>The config file always lives in the default folder, even if the storage root is moved.</summary>
    public string ConfigFile { get; }
    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "capture.db");
    public string CapturesFolder => Path.Combine(Root, "captures");
    public string LogsFolder => Path.Combine(Root, "logs");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeCapture");

    /// <summary>Loads config.json, creating it with defaults on first run. Throws on an invalid file.</summary>
    public static (AppPaths Paths, CaptureConfig Config) Load()
    {
        string configFile = Path.Combine(DefaultRoot, "config.json");
        CaptureConfig config;

        if (File.Exists(configFile))
        {
            config = JsonSerializer.Deserialize<CaptureConfig>(File.ReadAllText(configFile), JsonOptions)
                     ?? throw new InvalidDataException("config.json is empty.");
        }
        else
        {
            config = new CaptureConfig();
            Directory.CreateDirectory(DefaultRoot);
            File.WriteAllText(configFile, JsonSerializer.Serialize(config, JsonOptions));
        }

        string root = string.IsNullOrWhiteSpace(config.StorageRoot)
            ? DefaultRoot
            : Environment.ExpandEnvironmentVariables(config.StorageRoot);
        Directory.CreateDirectory(root);

        return (new AppPaths(configFile, root), config);
    }
}
