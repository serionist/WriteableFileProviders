namespace WriteableFileProviders;

/// <summary>Conditions on a read.</summary>
public sealed record ReadOptions
{
    /// <summary>
    /// Read only if the file's current <see cref="IWriteableFileInfo.ETag"/> is this value; otherwise throw
    /// <see cref="PreconditionFailedException"/>.
    /// </summary>
    public string? IfMatch { get; init; }
}
