namespace WriteableFileProviders;

/// <summary>A file, a folder or a missing path of an <see cref="IWriteableFileProvider"/>.</summary>
/// <param name="provider">The provider the entry belongs to.</param>
/// <param name="path">The entry's path, relative to the provider root.</param>
/// <param name="exists">Whether the entry exists.</param>
/// <param name="isDirectory">Whether the entry is a folder.</param>
/// <param name="length">The file's length in bytes, or -1 for a folder or a missing path.</param>
/// <param name="lastModified">When the entry last changed.</param>
/// <param name="physicalPath">The path on the local disk, or null when the entry is not a local file.</param>
/// <param name="etag">The version of the file's content, if the store keeps one.</param>
/// <param name="contentType">The file's content type, if the store keeps one.</param>
public sealed class WriteableFileInfo(
    IWriteableFileProvider provider,
    ProviderPath path,
    bool exists,
    bool isDirectory,
    long length,
    DateTimeOffset lastModified,
    string? physicalPath = null,
    string? etag = null,
    string? contentType = null) : IWriteableFileInfo
{
    private static readonly DateTimeOffset _unknownTime = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The provider the entry belongs to.</summary>
    public IWriteableFileProvider Provider { get; } = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <inheritdoc />
    public ProviderPath Path { get; } = path;

    /// <inheritdoc />
    public bool Exists { get; } = exists;

    /// <inheritdoc />
    public bool IsDirectory { get; } = isDirectory;

    /// <inheritdoc />
    public long Length { get; } = length;

    /// <inheritdoc />
    public DateTimeOffset LastModified { get; } = lastModified;

    /// <inheritdoc />
    public string? PhysicalPath { get; } = physicalPath;

    /// <inheritdoc />
    public string? ETag { get; } = etag;

    /// <inheritdoc />
    public string? ContentType { get; } = contentType;

    /// <summary>The last segment of <see cref="Path"/>; empty for the root.</summary>
    public string Name => Path.Name;

    /// <summary>A path that does not exist.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The path.</param>
    public static WriteableFileInfo Missing(IWriteableFileProvider provider, ProviderPath path) =>
        new(provider, path, false, false, -1, _unknownTime);

    /// <summary>A folder.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The folder's path.</param>
    /// <param name="lastModified">When it last changed, if known.</param>
    public static WriteableFileInfo Folder(IWriteableFileProvider provider, ProviderPath path, DateTimeOffset? lastModified = null) =>
        new(provider, path, true, true, -1, lastModified ?? _unknownTime);

    /// <summary>Opens the file for reading; the same as <see cref="IWriteableFileProvider.ReadFileAsync"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist, or is a folder.</exception>
    public Stream CreateReadStream() => Provider.ReadFileAsync(Path).GetAwaiter().GetResult();
}
