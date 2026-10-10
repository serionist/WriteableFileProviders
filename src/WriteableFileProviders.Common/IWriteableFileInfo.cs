using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders;

/// <summary>
/// A file or folder of an <see cref="IWriteableFileProvider"/>: the <see cref="IFileInfo"/> of ASP.NET Core, with its path
/// in the provider and the version and content type the store keeps.
/// </summary>
/// <remarks>
/// A file: <see cref="IFileInfo.Exists"/>, not a directory, its length and last-modified time. A folder: exists, a
/// directory, length -1. A missing path: does not exist, not a directory, length -1. <see cref="IFileInfo.PhysicalPath"/>
/// is null unless the entry is a file on the local disk.
/// </remarks>
public interface IWriteableFileInfo : IFileInfo
{
    /// <summary>The entry's path, relative to its provider's root.</summary>
    ProviderPath Path { get; }

    /// <summary>
    /// The version of the file's content, for <see cref="UpdateFileOptions.IfMatch"/> and
    /// <see cref="ReadOptions.IfMatch"/>; null for a folder or a missing path. Every file has one: a pinned seekable read
    /// or an append by rewrite of a file without one throws <see cref="NotSupportedException"/>.
    /// </summary>
    string? ETag { get; }

    /// <summary>The file's content type, where the store keeps one; otherwise null.</summary>
    string? ContentType { get; }
}
