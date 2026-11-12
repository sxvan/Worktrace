namespace Capture.Core.Pipeline;

/// <summary>Result of the redaction stage: either a composite that may be saved, or "drop this frame".</summary>
public sealed class RedactionResult
{
    private RedactionResult(RedactedComposite? composite, string? dropReason)
    {
        Composite = composite;
        DropReason = dropReason;
    }

    public RedactedComposite? Composite { get; }

    /// <summary>Technical reason; never contains screen content.</summary>
    public string? DropReason { get; }

    public bool IsDropped => Composite is null;

    internal static RedactionResult Redacted(RedactedComposite composite) => new(composite, null);

    public static RedactionResult Drop(string reason) => new(null, reason);
}

/// <summary>
/// Removes sensitive content from a composite before it can be saved. Takes ownership of the raw
/// composite. Async, and working only on plain pixel and metadata values, so a later
/// implementation can run OCR and secret detection in a separate process.
///
/// Later milestone: the password-field check (UI Automation IsPassword on the focused element)
/// does not belong here. It runs before capture, in the capture step, so nothing is captured at all.
/// </summary>
public interface IRedactor
{
    ValueTask<RedactionResult> RedactAsync(RawComposite composite, CancellationToken cancellationToken);
}

/// <summary>First implementation: returns its input unchanged.</summary>
public sealed class PassThroughRedactor : IRedactor
{
    public ValueTask<RedactionResult> RedactAsync(RawComposite composite, CancellationToken cancellationToken)
    {
        var redacted = new RedactedComposite(
            composite.Timestamp, composite.Monitor, composite.Width, composite.Height,
            composite.DetachPixels(), composite.Windows);
        return ValueTask.FromResult(RedactionResult.Redacted(redacted));
    }
}

/// <summary>
/// The only way image data reaches the disk. Accepts nothing but redacted composites.
/// Encryption at rest will be added behind this interface.
/// </summary>
public interface IImageStore
{
    /// <summary>Encodes and saves the composite. Returns the file path relative to the storage root.</summary>
    Task<string> SaveAsync(RedactedComposite composite, CancellationToken cancellationToken);
}
