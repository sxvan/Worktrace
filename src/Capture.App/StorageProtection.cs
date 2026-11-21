using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Capture.App;

/// <summary>
/// Protects the storage root on disk: only the current user and SYSTEM may access it, its
/// content is not indexed by Windows Search, and a warning is given if it lies in a folder that
/// a cloud service syncs.
/// </summary>
internal static class StorageProtection
{
    /// <summary>Entries this app creates in the storage root. A folder with anything else is not locked down.</summary>
    private static readonly string[] OwnEntries = ["capture.db", "capture.db-wal", "capture.db-shm", "capture.db-journal", "captures", "logs"];

    /// <summary>Applies the protections and returns warnings for the user and the log.</summary>
    public static IReadOnlyList<string> Apply(string root, bool justCreated)
    {
        var warnings = new List<string>();

        try
        {
            if (justCreated || OnlyOwnEntries(root))
            {
                RestrictToCurrentUser(root);
            }
            else
            {
                warnings.Add($"The storage root {root} contains other files, so its permissions were left as they are. "
                             + "Use an empty folder to have them restricted to your account.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            warnings.Add($"Could not restrict the permissions of {root} ({ex.GetType().Name}).");
        }

        try
        {
            // New files and folders inherit the attribute from their folder.
            string captures = Path.Combine(root, "captures");
            Directory.CreateDirectory(captures);
            MarkNotIndexed(root);
            MarkNotIndexed(captures);
            foreach (string day in Directory.GetDirectories(captures))
            {
                MarkNotIndexed(day);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            warnings.Add($"Could not exclude {root} from Windows Search indexing ({ex.GetType().Name}).");
        }

        if (SyncProvider(root) is string provider)
        {
            warnings.Add($"The storage root {root} is inside a folder synced by {provider}. "
                         + "Screenshots are encrypted, but the database (window titles, recognized text) is not: "
                         + "it is copied to the cloud. Set storageRoot to a folder outside it.");
        }
        return warnings;
    }

    private static bool OnlyOwnEntries(string root) =>
        Directory.EnumerateFileSystemEntries(root)
            .All(e => OwnEntries.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces the folder's permissions: no inherited entries, full control for the current
    /// user and SYSTEM only, inherited by everything inside. Skipped if already so.
    /// </summary>
    private static void RestrictToCurrentUser(string root)
    {
        var directory = new DirectoryInfo(root);
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
                                  ?? throw new InvalidOperationException("No user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        DirectorySecurity current = directory.GetAccessControl(AccessControlSections.Access);
        var rules = current.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        bool alreadyDone = current.AreAccessRulesProtected
                           && rules.Count == 2
                           && rules.All(r => r.AccessControlType == AccessControlType.Allow
                                             && !r.IsInherited
                                             && r.FileSystemRights.HasFlag(FileSystemRights.FullControl)
                                             && r.InheritanceFlags == inherit
                                             && (r.IdentityReference.Equals(user) || r.IdentityReference.Equals(system)))
                           && rules.Select(r => r.IdentityReference).Distinct().Count() == 2;
        if (alreadyDone)
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in new[] { user, system })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        // Windows passes the new inheritable entries on to everything already inside the folder.
        directory.SetAccessControl(security);
    }

    private static void MarkNotIndexed(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Attributes.HasFlag(FileAttributes.NotContentIndexed))
        {
            info.Attributes |= FileAttributes.NotContentIndexed;
        }
    }

    /// <summary>The name of the sync service whose folder contains <paramref name="root"/>, or null.</summary>
    internal static string? SyncProvider(string root)
    {
        string full = WithSeparator(Path.GetFullPath(root));

        foreach ((string provider, string? folder) in SyncFolders())
        {
            if (!string.IsNullOrWhiteSpace(folder) && full.StartsWith(WithSeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        // Folder names the clients use, wherever they are (also redirected known folders such as
        // Documents or Desktop, which then live inside the OneDrive folder).
        foreach (string part in full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
            if (part.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) || part.StartsWith("Dropbox (", StringComparison.OrdinalIgnoreCase)) return "Dropbox";
            if (part.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) || part.Equals("My Drive", StringComparison.OrdinalIgnoreCase)) return "Google Drive";
            if (part.Equals("iCloudDrive", StringComparison.OrdinalIgnoreCase)) return "iCloud Drive";
        }

        // Google Drive for desktop mounts a drive of its own.
        try
        {
            string? driveRoot = Path.GetPathRoot(full);
            if (driveRoot is { Length: > 0 } && !driveRoot.StartsWith(@"\\", StringComparison.Ordinal)
                && new DriveInfo(driveRoot) is { IsReady: true } drive
                && drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Drive";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown drive: no verdict.
        }
        return null;
    }

    private static IEnumerable<(string Provider, string? Folder)> SyncFolders()
    {
        foreach (string variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            yield return ("OneDrive", Environment.GetEnvironmentVariable(variable));
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return ("Google Drive", Path.Combine(home, "Google Drive"));
        yield return ("iCloud Drive", Path.Combine(home, "iCloudDrive"));

        // Dropbox writes its folders to info.json.
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string info = Path.Combine(Environment.GetFolderPath(folder), "Dropbox", "info.json");
            foreach (string path in DropboxPaths(info))
            {
                yield return ("Dropbox", path);
            }
        }
    }

    private static List<string> DropboxPaths(string infoFile)
    {
        var paths = new List<string>();
        try
        {
            if (File.Exists(infoFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoFile));
                foreach (JsonProperty account in doc.RootElement.EnumerateObject())
                {
                    if (account.Value.ValueKind == JsonValueKind.Object
                        && account.Value.TryGetProperty("path", out JsonElement path)
                        && path.GetString() is { Length: > 0 } p)
                    {
                        paths.Add(p);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No verdict from this file.
        }
        return paths;
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
