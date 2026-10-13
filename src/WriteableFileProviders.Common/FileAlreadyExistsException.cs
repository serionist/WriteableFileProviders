namespace WriteableFileProviders;

/// <summary>
/// A write with <see cref="UpdateFileOptions.IfNotExists"/> found a file at its path; nothing was written. Physical on
/// Linux and macOS also throws it for an append that lost a new name to a file still empty, since the rename that fills
/// such a file would undo an append made to it. It is a <see cref="PreconditionFailedException"/>, so a <c>catch</c> of
/// that type catches it. Not to be confused with <see cref="FileExistsException"/>, which a copy or a move throws when
/// its destination exists.
/// </summary>
public class FileAlreadyExistsException : PreconditionFailedException
{
    /// <summary>Creates the exception.</summary>
    public FileAlreadyExistsException() : base("The file already exists.") { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public FileAlreadyExistsException(string? message) : base(message) { }

    /// <summary>Creates the exception with a message and the error it wraps.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The store's error.</param>
    public FileAlreadyExistsException(string? message, Exception? innerException) : base(message, innerException) { }
}
