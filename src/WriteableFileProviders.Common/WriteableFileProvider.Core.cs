using System.Runtime.CompilerServices;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders;

public abstract partial class WriteableFileProvider<TOptions>
{
    /// <summary>
    /// Whether the store checks <see cref="UpdateFileOptions.IfMatch"/> and <see cref="UpdateFileOptions.IfNotExists"/>
    /// atomically in <see cref="UpdateFileCoreAsync"/>. When false (the default), the base class checks them just before
    /// the write, which is not atomic.
    /// </summary>
    protected virtual bool SupportsAtomicPreconditions => false;

    /// <summary>Whether the store treats names that differ only in case as the same name.</summary>
    protected virtual bool IsCaseInsensitive => false;

    /// <summary>
    /// The longest validity the provider signs natively; 7 days unless the provider says otherwise. A longer one throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual TimeSpan MaxSignedUrlValidity => TimeSpan.FromDays(7);

    /// <summary>Whether the provider issues signed URLs with this permission natively.</summary>
    /// <param name="permission">The permission.</param>
    protected virtual bool SupportsSignedUrl(SignedUrlAccess permission) => false;

    /// <summary>Gets a file, a folder or a missing path; never called with the root.</summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected abstract Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken);

    /// <summary>Streams one level of a folder's entries; a missing folder yields nothing.</summary>
    /// <param name="path">The folder; may be the root.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    protected abstract IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Streams every level of a folder's entries, each folder before its contents. The default walks
    /// <see cref="EnumerateCoreAsync"/> level by level; a store that lists recursively in one call can override it.
    /// </summary>
    /// <param name="path">The folder; may be the root.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    protected virtual async IAsyncEnumerable<IWriteableFileInfo> EnumerateRecursiveCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var folders = new Queue<ResolvedPath>();
        folders.Enqueue(path);
        while (folders.TryDequeue(out var folder))
        {
            await foreach (var entry in EnumerateCoreAsync(folder, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
                if (entry.IsDirectory)
                    folders.Enqueue(folder.Child(entry.Path.Name));
            }
        }
    }

    /// <summary>
    /// Whether an empty listing is an existing, empty folder rather than a missing one. The default asks
    /// <see cref="GetFileInfoCoreAsync"/> whether the path is a folder; on an object store, where a folder exists only
    /// while something is in it, that is false.
    /// </summary>
    /// <param name="path">The folder, not the root.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task<bool> FolderExistsCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        (await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false)).IsDirectory;

    /// <summary>Opens a file for reading; never called with the root.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The file does not exist, or is a folder.</exception>
    protected abstract Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a byte range. The default checks <paramref name="ifMatch"/> against <see cref="GetFileInfoCoreAsync"/> (not
    /// atomic), opens the whole file and skips to the offset; a store that reads ranges natively overrides it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="offset">The first byte, not negative.</param>
    /// <param name="length">How many bytes, not negative, or null to the end.</param>
    /// <param name="ifMatch">The version the file must have, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PreconditionFailedException">The file's version is not <paramref name="ifMatch"/>.</exception>
    protected virtual async Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is not null)
        {
            var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
            if (!info.Exists || info.IsDirectory)
                throw NotFound(path);
            if (!string.Equals(info.ETag, ifMatch, StringComparison.Ordinal))
                throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{ifMatch}'.");
        }
        var stream = await ReadFileCoreAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            await StreamUtilities.SkipAsync(stream, offset, cancellationToken).ConfigureAwait(false);
            return length is { } count ? new BoundedReadStream(stream, count) : stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens a seekable read-only stream. The default is a <see cref="RangeReadStream"/> over
    /// <see cref="ReadRangeCoreAsync"/>, pinned to the file's <see cref="IWriteableFileInfo.ETag"/> unless modifications
    /// are allowed; a store with its own seekable stream overrides it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How the stream reads.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task<Stream> OpenReadSeekableCoreAsync(ResolvedPath path, SeekableReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.Exists || info.IsDirectory)
            throw NotFound(path);
        var pin = options.AllowModifications ? null : info.ETag;
        return new RangeReadStream(info.Length, (offset, count, token) => ReadRangeCoreAsync(path, offset, count, pin, token), options.BufferSize);
    }

    /// <summary>
    /// Writes a file, atomically: a write that fails leaves the old content. Reads <paramref name="contents"/> from its
    /// position to its end and leaves it open. Never called with the root.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content.</param>
    /// <param name="options">How to write. The conditions reach here only when <see cref="SupportsAtomicPreconditions"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected abstract Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or a folder recursively; never called with the root.</summary>
    /// <param name="path">The file or folder.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether something was deleted.</returns>
    protected abstract Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes exactly one file, never a folder or a prefix: what a move removes. The default calls
    /// <see cref="DeleteCoreAsync"/>; an object store whose folder delete removes a prefix overrides it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task DeleteFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        await DeleteCoreAsync(path, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Copies a file whose source exists and whose destination does not. The default reads the source and writes the
    /// destination with <see cref="UpdateFileOptions.IfNotExists"/>; a store that copies on the server overrides it.
    /// </summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        var contents = await ReadFileCoreAsync(source, cancellationToken).ConfigureAwait(false);
        await using (contents.ConfigureAwait(false))
            await UpdateWithPreconditionsAsync(destination, contents, new UpdateFileOptions { IfNotExists = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a file whose source exists and whose destination does not. The default copies with
    /// <see cref="CopyCoreAsync"/>, then deletes the source with <see cref="DeleteFileCoreAsync"/>.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        await CopyCoreAsync(source, destination, cancellationToken).ConfigureAwait(false);
        await DeleteFileCoreAsync(source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the file to a local temp file and opens it, uploading on commit unless read-only. The default spools through
    /// <see cref="ReadFileCoreAsync"/> and commits with <see cref="UpdateFileCoreAsync"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task<LocalFileStream> CreateLocalFileStreamCoreAsync(ResolvedPath path, bool readOnly, CancellationToken cancellationToken)
    {
        var temp = StreamUtilities.CreateTempFile();
        try
        {
            var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsDirectory)
                throw new IOException($"The path '{path}' is a folder.");
            if (info.Exists)
            {
                var source = await ReadFileCoreAsync(path, cancellationToken).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                    await source.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
                temp.Position = 0;
            }
            else if (readOnly)
            {
                throw NotFound(path);
            }
            return new LocalFileStream(temp, this, path.Path, readOnly,
                readOnly ? null : (content, token) => UpdateWithPreconditionsAsync(path, content, UpdateFileOptions.Overwrite, token));
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Issues a signed URL; called only when <see cref="SupportsSignedUrl"/> is true for the permission.</summary>
    /// <param name="path">The file; for a read URL it exists.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} cannot issue signed URLs.");

    /// <summary>Watches for changes. The default throws <see cref="NotSupportedException"/>.</summary>
    /// <param name="filter">The files to watch.</param>
    protected virtual IChangeToken WatchCore(string filter) => throw new NotSupportedException($"{GetType().Name} cannot watch for changes.");

    /// <summary>Whether an exception of the store means "not found"; read members turn it into a <see cref="FileNotFoundException"/>.</summary>
    /// <param name="exception">The store's exception.</param>
    protected virtual bool IsNotFound(Exception exception) => false;

    /// <summary>Releases the clients the provider owns. The default does nothing.</summary>
    /// <param name="disposing">True from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Releases the clients the provider owns, asynchronously. The default calls <see cref="Dispose(bool)"/>.</summary>
    protected virtual ValueTask DisposeAsyncCore()
    {
        Dispose(true);
        return default;
    }

    /// <summary>
    /// Appends by rewriting, for a store with no native append: the existing content and <paramref name="contents"/> are
    /// spooled to a temp file, which then replaces the file with <see cref="UpdateFileOptions.IfMatch"/> on the version
    /// that was read, so a concurrent change throws <see cref="PreconditionFailedException"/> instead of being lost.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content to add.</param>
    /// <param name="options">The caller's options; their conditions are checked against what was read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected async Task AppendByRewriteAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        if (options.IfNotExists && info.Exists)
            throw new PreconditionFailedException($"The file '{path}' already exists.");
        if (options.IfMatch is not null && !string.Equals(info.ETag, options.IfMatch, StringComparison.Ordinal))
            throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{options.IfMatch}'.");
        var temp = StreamUtilities.CreateTempFile();
        await using (temp.ConfigureAwait(false))
        {
            if (info.Exists && !info.IsDirectory)
            {
                var existing = await ReadFileCoreAsync(path, cancellationToken).ConfigureAwait(false);
                await using (existing.ConfigureAwait(false))
                    await existing.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            }
            await contents.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            temp.Position = 0;
            var rewrite = options with
            {
                Mode = UpdateFileMode.Overwrite,
                IfMatch = info.Exists ? info.ETag : null,
                IfNotExists = !info.Exists,
            };
            await UpdateWithPreconditionsAsync(path, temp, rewrite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies <paramref name="source"/> from its position to a new temp file, deleted when it is disposed, positioned at 0.</summary>
    /// <param name="source">The stream to copy.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected static async Task<FileStream> SpoolToTempFileAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var temp = StreamUtilities.CreateTempFile();
        try
        {
            await source.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            temp.Position = 0;
            return temp;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A <see cref="FileNotFoundException"/> for the path, wrapping the store's error.</summary>
    /// <param name="path">The path.</param>
    /// <param name="innerException">The store's error, if any.</param>
    protected static FileNotFoundException NotFound(ResolvedPath path, Exception? innerException = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new FileNotFoundException($"The file '{path}' does not exist.", path.ToString(), innerException);
    }

    private async Task UpdateWithPreconditionsAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        if (!SupportsAtomicPreconditions && (options.IfMatch is not null || options.IfNotExists))
        {
            var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
            if (options.IfNotExists && info.Exists)
                throw new PreconditionFailedException($"The file '{path}' already exists.");
            if (options.IfMatch is not null && !string.Equals(info.ETag, options.IfMatch, StringComparison.Ordinal))
                throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{options.IfMatch}'.");
        }
        await UpdateFileCoreAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
    }
}
