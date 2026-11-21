using System.Text.Json;

namespace Capture.Core.Config;

/// <summary>Reads config.json and works out the storage root. Shared by the tray app and the command line tool.</summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Where the database, captures and logs go when the config names no storage root.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeCapture");

    /// <summary>Loads a config file. Throws if it is missing or invalid.</summary>
    public static CaptureConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} does not exist.");
        }
        return JsonSerializer.Deserialize<CaptureConfig>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException("config.json is empty.");
    }

    public static string ResolveRoot(CaptureConfig config) =>
        string.IsNullOrWhiteSpace(config.StorageRoot) ? DefaultRoot : Environment.ExpandEnvironmentVariables(config.StorageRoot);
}
