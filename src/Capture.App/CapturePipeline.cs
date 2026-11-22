using System.Collections.Concurrent;
using System.Diagnostics;
using Capture.Core.Pipeline;
using Capture.Core.Triggers;
using Capture.Storage;

namespace Capture.App;

/// <summary>
/// Everything after the capture itself, on one background thread at below-normal priority:
///
///   dedupe -> encode and encrypt (IImageStore) -> database
///
/// Window title and placement are always recorded; the image only if storeImages is on. No text
/// is recognized and nothing is redacted here: that happens on demand when an image is read.
///
/// Captures are queued so encoding never holds up event handling. If encoding falls behind,
/// new captures are dropped instead of piling up in memory.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private const int MaxQueuedCaptures = 3;

    private sealed record Job(CaptureDecision Decision, CaptureResult Capture, long CaptureMs);

    private readonly IImageStore _imageStore;
    private readonly CaptureDatabase _database;
    private readonly Deduplicator _deduplicator;
    private readonly bool _storeImages;
    private readonly BlockingCollection<Job> _queue = new(MaxQueuedCaptures);
    private readonly Thread _thread;

    // Held while a capture is processed and while recent captures are deleted, so a delete never
    // races a capture that is half stored.
    private readonly Lock _processing = new();

    // Captures taken before this were deleted while they still sat in the queue: drop them.
    private DateTimeOffset _discardBefore = DateTimeOffset.MinValue;

    public CapturePipeline(IImageStore imageStore, CaptureDatabase database, bool storeImages)
    {
        _storeImages = storeImages;
        _imageStore = imageStore;
        _database = database;
        _deduplicator = new Deduplicator();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CapturePipeline",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Hands a capture over for processing. Takes ownership of its images in every case.</summary>
    public void Enqueue(CaptureDecision decision, CaptureResult capture, long captureMs)
    {
        bool queued = false;
        try
        {
            queued = _queue.TryAdd(new Job(decision, capture, captureMs));
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }

        if (!queued)
        {
            DisposeImages(capture);
            CaptureReporter.DroppedBecauseQueueIsFull(decision);
        }
    }

    /// <summary>Finishes the capture in progress, discards queued ones and stops the thread.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(15));
        while (_queue.TryTake(out Job? job))
        {
            DisposeImages(job.Capture);
        }
        _queue.Dispose();
    }

    private void Run()
    {
        try
        {
            foreach (Job job in _queue.GetConsumingEnumerable())
            {
                try
                {
                    lock (_processing)
                    {
                        if (job.Decision.Timestamp < _discardBefore)
                        {
                            CaptureReporter.Discarded(job.Decision, "deleted by the user");
                        }
                        else
                        {
                            Process(job);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Fail closed: nothing of this capture is kept.
                    CaptureReporter.Failed(job.Decision, "Capture processing", ex);
                }
                finally
                {
                    DisposeImages(job.Capture);
                }

                // Each capture leaves tens of megabytes of pixel buffers behind. The app allocates
                // so little otherwise that the runtime would not collect them for a long time, so
                // ask for it here, where it costs a few milliseconds on this background thread.
                if (_queue.Count == 0)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed during shutdown.
        }
    }

    /// <summary>
    /// Deletes everything captured from <paramref name="since"/> up to <paramref name="now"/>: the
    /// database rows, their image files, and captures still waiting in the queue. With
    /// <paramref name="includeEvents"/>, also the activity events of that span. Safe to call from
    /// any thread. Returns the number of image files deleted.
    /// </summary>
    public int DeleteRecent(DateTimeOffset since, DateTimeOffset now, bool includeEvents)
    {
        lock (_processing)
        {
            _discardBefore = now;
            IReadOnlyList<string> files = _database.DeleteCapturesSince(since);
            // The remembered images may be among the deleted ones; a later duplicate must not point at them.
            _deduplicator.Clear();
            if (includeEvents)
            {
                _database.DeleteEventsSince(since);
            }

            int deleted = 0;
            foreach (string path in files)
            {
                try
                {
                    _imageStore.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Without its row the file is unreachable; retention removes the day folder eventually.
                }
            }
            return deleted;
        }
    }

    private void Process(Job job)
    {
        var stopwatch = Stopwatch.StartNew();
        var records = new List<CapturedWindowRecord>();
        var writtenFiles = new List<string>();
        var windowSizes = new List<(int Width, int Height)>();
        // Grayscale copies of the windows whose image is saved, for comparing later captures.
        var grays = new List<byte[]?>();
        DateOnly day = DateOnly.FromDateTime(job.Decision.Timestamp.LocalDateTime);

        try
        {
            int sequence = 0;
            foreach (RawWindowImage raw in job.Capture.Images)
            {
                sequence++;
                long handle = raw.Placement.Window.Handle;
                bool focused = handle == job.Capture.FocusedWindowHandle;
                int width = raw.Width, height = raw.Height;

                // Dedupe first: an unchanged window is not saved again. It is compared with the last
                // saved image of the window at full resolution.
                byte[] gray = Deduplicator.Grayscale(raw.Pixels, width, height);
                long? duplicateOf = _deduplicator.FindDuplicate(handle, raw.Placement.Window.Title, width, height, gray, day);
                if (duplicateOf is not null)
                {
                    Array.Clear(gray);
                    records.Add(new CapturedWindowRecord(raw.Placement, focused, null, duplicateOf));
                    windowSizes.Add((width, height));
                    grays.Add(null);
                    continue;
                }
                grays.Add(gray);

                string? savedPath = null;
                if (_storeImages)
                {
                    savedPath = _imageStore.Save(raw, sequence);
                    writtenFiles.Add(savedPath);
                }

                records.Add(new CapturedWindowRecord(raw.Placement, focused, savedPath, null));
                windowSizes.Add((width, height));
            }

            if (records.Count == 0)
            {
                return;
            }

            IReadOnlyList<long> ids = _database.InsertCapture(
                job.Decision.Timestamp, job.Decision.Trigger, records);

            for (int i = 0; i < records.Count; i++)
            {
                // Later duplicates can point at a window whose image was kept.
                if (records[i].DedupeReferenceId is null && records[i].FilePath is not null && grays[i] is { } gray)
                {
                    _deduplicator.Remember(
                        records[i].Placement.Window.Handle, records[i].Placement.Window.Title,
                        windowSizes[i].Width, windowSizes[i].Height, gray, day, ids[i]);
                    grays[i] = null;
                }
            }
        }
        catch
        {
            ClearAll(grays);
            // Files without database rows would be orphans: remove them again.
            foreach (string path in writtenFiles)
            {
                try
                {
                    _imageStore.Delete(path);
                }
                catch (Exception)
                {
                    // Retention removes the day folder eventually.
                }
            }
            throw;
        }

        // Copies of windows that were not remembered (images not stored) are no longer needed.
        ClearAll(grays);
        CaptureReporter.Stored(job.Decision, records.Count, writtenFiles.Count, job.CaptureMs, stopwatch.ElapsedMilliseconds);
    }

    private static void ClearAll(List<byte[]?> buffers)
    {
        foreach (byte[]? buffer in buffers)
        {
            if (buffer is not null)
            {
                Array.Clear(buffer);
            }
        }
    }

    private static void DisposeImages(CaptureResult capture)
    {
        foreach (RawWindowImage image in capture.Images)
        {
            image.Dispose();
        }
    }
}
