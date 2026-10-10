namespace WriteableFileProviders;

/// <summary>How <see cref="IWriteableFileProvider.UpdateFileAsync"/> writes a file.</summary>
public sealed record UpdateFileOptions
{
    /// <summary>Overwrite, with no conditions.</summary>
    public static UpdateFileOptions Overwrite { get; } = new();

    /// <summary>Append, with no conditions.</summary>
    public static UpdateFileOptions Append { get; } = new() { Mode = UpdateFileMode.Append };

    /// <summary>Overwrite (the default) or append.</summary>
    public UpdateFileMode Mode { get; init; } = UpdateFileMode.Overwrite;

    /// <summary>
    /// A hint: the content type to store with the file where the store keeps one, ignored where it does not. Null keeps the
    /// store's default.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Write only if the file's current <see cref="IWriteableFileInfo.ETag"/> is this value; otherwise throw
    /// <see cref="PreconditionFailedException"/>. Each provider documents whether the check is atomic.
    /// </summary>
    public string? IfMatch { get; init; }

    /// <summary>Write only if the file does not exist yet; otherwise throw <see cref="PreconditionFailedException"/>.</summary>
    public bool IfNotExists { get; init; }
}
