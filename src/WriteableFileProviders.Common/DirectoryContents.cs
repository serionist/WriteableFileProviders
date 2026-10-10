using System.Collections;
using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders;

/// <summary>The entries of one folder, as <see cref="IWriteableFileProvider.GetDirectoryContentsAsync"/> lists them.</summary>
/// <param name="exists">Whether the folder exists.</param>
/// <param name="entries">The folder's files and folders, one level.</param>
public sealed class DirectoryContents(bool exists, IEnumerable<IFileInfo> entries) : IDirectoryContents
{
    private readonly IFileInfo[] _entries = [.. entries ?? throw new ArgumentNullException(nameof(entries))];

    /// <summary>A folder that does not exist: no entries.</summary>
    public static DirectoryContents NotFound { get; } = new(false, []);

    /// <inheritdoc />
    public bool Exists { get; } = exists;

    /// <inheritdoc />
    public IEnumerator<IFileInfo> GetEnumerator() => ((IEnumerable<IFileInfo>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
