using System.Text.Json;
using Capture.Core.Config;

namespace Capture.App;

/// <summary>Where things live on disk, and loading of config.json.</summary>
public sealed class AppPaths
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private AppPaths(string configFile, string root, IReadOnlyList<string> warnings)
    {
        ConfigFile = configFile;
        Root = root;
        Warnings = warnings;
    }

    /// <summary>Problems found while protecting the storage root, for the log and the console.</summary>
    public IReadOnlyList<string> Warnings { get; }

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
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeCapture");

    /// <summary>Loads config.json. Throws if the file is missing or invalid.</summary>
    public static (AppPaths Paths, CaptureConfig Config) Load()
    {
        string configFile = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(configFile))
        {
            throw new FileNotFoundException($"{configFile} does not exist.");
        }

        CaptureConfig config = JsonSerializer.Deserialize<CaptureConfig>(File.ReadAllText(configFile), JsonOptions)
                               ?? throw new InvalidDataException("config.json is empty.");

        string root = string.IsNullOrWhiteSpace(config.StorageRoot)
            ? DefaultRoot
            : Environment.ExpandEnvironmentVariables(config.StorageRoot);
        bool justCreated = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        IReadOnlyList<string> warnings = StorageProtection.Apply(root, justCreated);

        return (new AppPaths(configFile, root, warnings), config);
    }
}
