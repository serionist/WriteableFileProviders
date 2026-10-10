using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders;

/// <summary>
/// A writeable <see cref="IFileProvider"/>: one storage backend (the local disk, Azure Blob Storage, Amazon S3, MinIO,
/// Google Cloud Storage, Box, SharePoint) behind one API that means the same thing on every provider.
/// </summary>
/// <remarks>
/// <para>
/// Every path is a <see cref="ProviderPath"/> relative to the provider's root (<see cref="FileProviderOptions"/>); nothing
/// outside the root can be reached. A path segment that is empty, only dots and spaces, holds a control character, or
/// becomes one of those when percent-decoded throws <see cref="ArgumentException"/>. The root itself cannot be written,
/// deleted or moved (<see cref="InvalidOperationException"/>), nor read as a file (<see cref="ArgumentException"/>).
/// </para>
/// <para>
/// A provider whose bucket or container does not exist throws <see cref="InvalidOperationException"/> from every member,
/// naming it and the option that creates it; it never reports that as a missing path. An operation a provider cannot do
/// throws <see cref="NotSupportedException"/>. The synchronous forms of the members are
/// extension methods (<see cref="WriteableFileProviderExtensions"/>), except the three <see cref="IFileProvider"/> needs.
/// </para>
/// </remarks>
public interface IWriteableFileProvider : IFileProvider, IDisposable, IAsyncDisposable
{
    /// <summary>The provider's options, as they were given.</summary>
    FileProviderOptions Options { get; }

    /// <summary>Gets a file, a folder or a missing path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// A file: exists, not a directory, with its length. A folder: exists, a directory, length -1. A missing path: does not
    /// exist, not a directory, length -1. The root is a folder.
    /// </returns>
    Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken cancellationToken = default);

    /// <summary>Lists one level of a folder.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The folder's entries; a missing folder lists as not existing and empty.</returns>
    Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken cancellationToken = default);

    /// <summary>Streams the entries of a folder, one level or every level.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="recursive">Whether to walk every level: each folder is yielded once, before its contents.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>Opens a file for reading. The stream may be a network stream that can only be read forward.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The file does not exist, or is a folder.</exception>
    Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken cancellationToken = default);

    /// <summary>Reads a byte range of a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="offset">The first byte to read.</param>
    /// <param name="length">How many bytes to read, or null to read to the end.</param>
    /// <param name="options">Conditions on the read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes from <paramref name="offset"/>: empty past the end of the file, or when the length is 0.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The offset or the length is negative.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist, or is a folder.</exception>
    /// <exception cref="PreconditionFailedException">The file's version is not <see cref="ReadOptions.IfMatch"/>.</exception>
    Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file as a read-only stream that can seek: it knows its length, and fetches ranges as it is read.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How the stream reads.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The file does not exist, or is a folder.</exception>
    /// <exception cref="NotSupportedException">The read is pinned and the file has no <see cref="IWriteableFileInfo.ETag"/>.</exception>
    Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a file: overwrites or appends, atomically, so a write that fails leaves the old content. Missing parent
    /// folders are created.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">
    /// The content: read from its current position to its end, and left open. Its position afterwards is not promised.
    /// </param>
    /// <param name="options">How to write; overwrite with no conditions by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PreconditionFailedException">A condition in <paramref name="options"/> did not hold.</exception>
    /// <exception cref="ArgumentException">Both <see cref="UpdateFileOptions.IfMatch"/> and <see cref="UpdateFileOptions.IfNotExists"/> are set.</exception>
    /// <exception cref="InvalidOperationException">The path is the provider root.</exception>
    Task UpdateFileAsync(ProviderPath path, Stream contents, UpdateFileOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file, or a folder with everything in it. A folder delete never touches a sibling that only shares its
    /// name's prefix.
    /// </summary>
    /// <param name="path">The file or folder.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when something was deleted; false when the path did not exist.</returns>
    /// <exception cref="InvalidOperationException">The path is the provider root.</exception>
    Task<bool> DeleteAsync(ProviderPath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a file. Missing parent folders of the destination are created; moving a file onto itself does nothing.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where to move it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The source does not exist, or is a folder.</exception>
    /// <exception cref="FileExistsException">The destination exists; it is left unchanged.</exception>
    Task MoveAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default);

    /// <summary>Copies a file. Missing parent folders of the destination are created.</summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">Where to copy it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The source does not exist, or is a folder.</exception>
    /// <exception cref="FileExistsException">The destination exists; it is left unchanged.</exception>
    Task CopyAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file to a local temp file and opens it. A writeable stream uploads its content when it is disposed (or
    /// when <see cref="LocalFileStream.CommitAsync"/> is called), once and atomically, unless
    /// <see cref="LocalFileStream.SkipReUploadOnDispose"/> is set. A dispose after an exception uploads too, so partially
    /// written content is uploaded; set <see cref="LocalFileStream.SkipReUploadOnDispose"/> to prevent that.
    /// </summary>
    /// <param name="path">The file. A writeable stream of a missing file starts empty.</param>
    /// <param name="readOnly">Whether the stream is read-only and never uploads.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">A read-only stream of a file that does not exist, or of a folder.</exception>
    /// <exception cref="IOException">A writeable stream of a folder.</exception>
    Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a URL that lets its holder read or write one file for a limited time, without the provider's credentials.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">What the URL allows and how long it lives.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentOutOfRangeException">The validity is not positive.</exception>
    /// <exception cref="FileNotFoundException">A read URL for a file that does not exist, or for a folder.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider cannot issue such a URL natively: not for this permission, not for this long, not with an option it
    /// cannot honour (such as <see cref="SignedUrlOptions.DownloadFileName"/>), or only one that lives shorter than asked.
    /// WriteableFileProviders.AspNetCore issues a gateway URL instead.
    /// </exception>
    Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default);
}
