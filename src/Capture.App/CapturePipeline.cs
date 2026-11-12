using System.Collections.Concurrent;
using System.Diagnostics;
using Capture.Core.Pipeline;
using Capture.Core.Triggers;
using Capture.Storage;
using Serilog;

namespace Capture.App;

/// <summary>
/// Everything after the capture itself, on one background thread at below-normal priority:
///
///   IRedactor -> dedupe -> encode -> IImageStore -> database
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
    private readonly BlockingCollection<Job> _queue = new(MaxQueuedCaptures);
    private readonly Thread _thread;

    public CapturePipeline(IRedactor redactor, IImageStore imageStore, CaptureDatabase database, int dedupeThreshold)
    {
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
            Log.Warning("Capture dropped: encoding queue is full");
            Console.WriteLine($"{Prefix(decision)} -> dropped (encoding is behind)");
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
                    Log.Error("Capture processing failed: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
                    Console.WriteLine($"{Prefix(job.Decision)} -> failed ({ex.GetType().Name})");
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
        DateOnly day = DateOnly.FromDateTime(job.Decision.Timestamp.LocalDateTime);

        try
        {
            int sequence = 0;
            foreach (RawWindowImage raw in job.Capture.Images)
            {
                sequence++;
                RedactionResult redaction = _redactor.RedactAsync(raw, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (redaction.Image is null)
                {
                    Log.Information("Window image dropped by redaction: {Reason}", redaction.DropReason);
                    continue;
                }

                using RedactedWindowImage image = redaction.Image;
                long handle = image.Placement.Window.Handle;
                ulong hash = DHash.Compute(image.Pixels, image.Width, image.Height);
                long? duplicateOf = _deduplicator.FindDuplicate(handle, image.Width, image.Height, hash, day);

                string? path = null;
                if (duplicateOf is null)
                {
                    path = _imageStore.Save(image, sequence);
                    writtenFiles.Add(path);
                }

                records.Add(new CapturedWindowRecord(
                    image.Placement, handle == job.Capture.FocusedWindowHandle,
                    image.Width, image.Height, hash, path, duplicateOf));
            }

            if (records.Count == 0)
            {
                return;
            }

            IReadOnlyList<long> ids = _database.InsertCapture(
                job.Decision.Timestamp, job.Decision.Trigger, job.Capture.FocusedWindowHandle, records);

            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].FilePath is not null)
                {
                    _deduplicator.Remember(
                        records[i].Placement.Window.Handle, records[i].Width, records[i].Height, records[i].DHash, day, ids[i]);
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

        int saved = writtenFiles.Count, duplicates = records.Count - saved;
        Log.Information("Capture ({Trigger}): {Windows} window(s), {Saved} saved, {Duplicates} unchanged, capture {CaptureMs} ms, processing {ProcessMs} ms",
            job.Decision.Trigger.ToDbName(), records.Count, saved, duplicates, job.CaptureMs, stopwatch.ElapsedMilliseconds);
        Console.WriteLine($"{Prefix(job.Decision)} -> {records.Count} window(s): {saved} saved, {duplicates} unchanged (capture {job.CaptureMs} ms, encode {stopwatch.ElapsedMilliseconds} ms)");
    }

    private static void DisposeImages(CaptureResult capture)
    {
        foreach (RawWindowImage image in capture.Images)
        {
            image.Dispose();
        }
    }

    internal static string Prefix(CaptureDecision decision) =>
        $"{decision.Timestamp.ToLocalTime():HH:mm:ss}  >> capture   {decision.Trigger.ToDbName(),-13}";
}
