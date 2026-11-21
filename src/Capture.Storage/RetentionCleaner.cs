using System.Globalization;

namespace Capture.Storage;

/// <summary>
/// Deletes screenshot files older than the retention period. Whole day folders are removed, the
/// database rows stay with their file path set to null, and the days' encryption keys are deleted,
/// so encrypted images are unrecoverable even where the disk still holds their old sectors. Recognized text has a retention
/// period of its own. Window titles and events are kept.
/// </summary>
public sealed class RetentionCleaner(string root, CaptureDatabase database, int retentionDays)
{
    /// <summary>Returns the number of day folders removed.</summary>
    public int Run(DateOnly today)
    {
        DateOnly firstDayToKeep = today.AddDays(-Math.Max(0, retentionDays));
        string capturesFolder = Path.Combine(root, FileImageStore.CapturesFolderName);
        int removed = 0;

        if (Directory.Exists(capturesFolder))
        {
            foreach (string folder in Directory.GetDirectories(capturesFolder))
            {
                // Only folders this app created (named as a date) are ever touched.
                if (DateOnly.TryParseExact(Path.GetFileName(folder), FileImageStore.DayFolderFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                    && day < firstDayToKeep)
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
            }
        }

        database.ClearFilePathsBefore(firstDayToKeep);
        database.DeleteDayKeysBefore(firstDayToKeep);
        return removed;
    }
}
