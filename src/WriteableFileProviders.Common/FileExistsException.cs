namespace WriteableFileProviders;

/// <summary>
/// The destination of a move or a copy already exists; it was left unchanged. A write with
/// <see cref="UpdateFileOptions.IfNotExists"/> that finds its file throws <see cref="FileAlreadyExistsException"/>
/// instead.
/// </summary>
public class FileExistsException : IOException
{
    /// <summary>Creates the exception.</summary>
    public FileExistsException() : base("The destination already exists.") { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public FileExistsException(string? message) : base(message) { }

    /// <summary>Creates the exception with a message and the error it wraps.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The store's error.</param>
    public FileExistsException(string? message, Exception? innerException) : base(message, innerException) { }
}
