using System.Collections.Concurrent;
using System.Diagnostics;
using Capture.Core.Pipeline;
using Capture.Core.Triggers;
using Capture.Storage;

namespace Capture.App;

/// <summary>
/// Everything after the capture itself, on one background thread at below-normal priority:
///
///   dedupe -> IRedactor -> encode -> IImageStore -> database
///
/// What is kept of a window follows the layers in the config: the recognized text (storeText)
/// and the image itself (storeImages). Window title and placement are always recorded.
///
/// Captures are queued so encoding never holds up event handling. If encoding falls behind,
/// new captures are dropped instead of piling up in memory.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private const int MaxQueuedCaptures = 3;

    private sealed record Job(CaptureDecision Decision, CaptureResult Capture, long CaptureMs);

    private readonly IRedactor _redactor;
    private readonly IImageStore _imageStore;
    private readonly CaptureDatabase _database;
    private readonly Deduplicator _deduplicator;
    private readonly bool _storeImages;
    private readonly bool _storeText;
    private readonly BlockingCollection<Job> _queue = new(MaxQueuedCaptures);
    private readonly Thread _thread;

    public CapturePipeline(
        IRedactor redactor, IImageStore imageStore, CaptureDatabase database, int dedupeThreshold, bool storeImages, bool storeText)
    {
        _storeImages = storeImages;
        _storeText = storeText;
        _redactor = redactor;
        _imageStore = imageStore;
        _database = database;
        _deduplicator = new Deduplicator(dedupeThreshold);
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
                    Process(job);
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

    private void Process(Job job)
    {
        var stopwatch = Stopwatch.StartNew();
        var records = new List<CapturedWindowRecord>();
        var writtenFiles = new List<string>();
        var windowSizes = new List<(int Width, int Height)>();
        int redactions = 0, dropped = 0;
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

                // Dedupe first: an unchanged window is not saved, so there is nothing to redact.
                // Only the 64-bit hash of the unredacted image is kept.
                ulong hash = DHash.Compute(raw.Pixels, width, height);
                long? duplicateOf = _deduplicator.FindDuplicate(handle, width, height, hash, day);
                if (duplicateOf is not null)
                {
                    records.Add(new CapturedWindowRecord(raw.Placement, focused, width, height, hash, null, duplicateOf));
                    windowSizes.Add((width, height));
                    continue;
                }

                if (!_storeImages && !_storeText)
                {
                    // Nothing of the content is kept, so there is nothing to check.
                    records.Add(new CapturedWindowRecord(raw.Placement, focused, width, height, hash, null, null));
                    windowSizes.Add((width, height));
                    continue;
                }

                RedactionResult redaction = _redactor.RedactAsync(raw, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (redaction.Image is null)
                {
                    dropped++;
                    CaptureReporter.WindowDroppedByRedaction(redaction.DropReason);
                    continue;
                }

                using RedactedWindowImage image = redaction.Image;
                SavedImage? saved = null;
                if (_storeImages)
                {
                    saved = _imageStore.Save(image, sequence);
                    writtenFiles.Add(saved.RelativePath);
                }
                redactions += image.RedactionCount;

                // Width and height describe the saved file, which can be scaled down.
                records.Add(new CapturedWindowRecord(
                    image.Placement, focused, saved?.Width ?? width, saved?.Height ?? height, hash, saved?.RelativePath, null,
                    image.RedactionCount, _storeText ? image.Text : null));
                windowSizes.Add((width, height));
            }

            if (records.Count == 0)
            {
                if (dropped > 0)
                {
                    CaptureReporter.NothingSaved(job.Decision, dropped);
                }
                return;
            }

            IReadOnlyList<long> ids = _database.InsertCapture(
                job.Decision.Timestamp, job.Decision.Trigger, job.Capture.FocusedWindowHandle, records);

            for (int i = 0; i < records.Count; i++)
            {
                // Later duplicates can point at a window whose image or text was kept.
                if (records[i].DedupeReferenceId is null && (records[i].FilePath is not null || records[i].Text is not null))
                {
                    _deduplicator.Remember(
                        records[i].Placement.Window.Handle, windowSizes[i].Width, windowSizes[i].Height, records[i].DHash, day, ids[i]);
                }
            }
        }
        catch
        {
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

        CaptureReporter.Stored(
            job.Decision, records.Count, writtenFiles.Count, redactions, dropped, job.CaptureMs, stopwatch.ElapsedMilliseconds);
    }

    private static void DisposeImages(CaptureResult capture)
    {
        foreach (RawWindowImage image in capture.Images)
        {
            image.Dispose();
        }
    }
}
