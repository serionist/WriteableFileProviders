namespace WriteableFileProviders;

/// <summary>How <see cref="IWriteableFileProvider.OpenReadSeekableAsync"/> reads.</summary>
public sealed record SeekableReadOptions
{
    /// <summary>The least number of bytes each fetch reads ahead; 1 MiB by default.</summary>
    public int BufferSize { get; init; } = 1 << 20;

    /// <summary>
    /// When false (the default), the stream pins the version it opened and throws <see cref="IOException"/> if the file
    /// changes while it is read; a file with no <see cref="IWriteableFileInfo.ETag"/> cannot be pinned and throws
    /// <see cref="NotSupportedException"/>. When true, each fetch reads whatever the file holds then.
    /// </summary>
    public bool AllowModifications { get; init; }
}
