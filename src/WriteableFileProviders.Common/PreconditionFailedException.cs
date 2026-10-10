namespace WriteableFileProviders;

/// <summary>
/// A condition on a read or a write did not hold: the file's version is not the one given, the file already exists, or it
/// changed while it was read.
/// </summary>
public class PreconditionFailedException : IOException
{
    /// <summary>Creates the exception.</summary>
    public PreconditionFailedException() : base("The file's version did not match the condition.") { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PreconditionFailedException(string? message) : base(message) { }

    /// <summary>Creates the exception with a message and the error it wraps.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The store's error.</param>
    public PreconditionFailedException(string? message, Exception? innerException) : base(message, innerException) { }
}
