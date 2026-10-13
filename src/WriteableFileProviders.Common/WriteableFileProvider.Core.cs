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

    /// <summary>
    /// Whether the store treats names that differ only in case as the same name. When true, moving a file to a name that
    /// differs only in case is a rename: the base class skips the destination check, and the default
    /// <see cref="MoveCoreAsync"/> goes through a temporary name.
    /// </summary>
    protected virtual bool IsCaseInsensitive => false;

    /// <summary>
    /// The longest validity the provider signs natively; 7 days unless the provider says otherwise. A longer one throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual TimeSpan MaxSignedUrlValidity => TimeSpan.FromDays(7);

    /// <summary>Whether the provider issues signed URLs with this access natively.</summary>
    /// <param name="access">The access.</param>
    protected virtual bool SupportsSignedUrl(SignedUrlAccess access) => false;

    /// <summary>
    /// Whether the provider honours <see cref="SignedUrlOptions.DownloadFileName"/> and
    /// <see cref="SignedUrlOptions.ContentType"/> on a URL with this access. When false (the default), a request that
    /// sets either one throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="access">The access.</param>
    protected virtual bool SupportsSignedUrlContentHeaders(SignedUrlAccess access) => false;

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
        var slice = new SliceReadStream(await ReadFileCoreAsync(path, cancellationToken).ConfigureAwait(false), offset, length);
        try
        {
            await slice.SkipAsync(cancellationToken).ConfigureAwait(false);
            return slice;
        }
        catch
        {
            await slice.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens a seekable read-only stream. The default reads ranges through <see cref="ReadRangeCoreAsync"/> as it is read,
    /// at least <see cref="SeekableReadOptions.BufferSize"/> bytes a fetch, pinned to the file's <see cref="IWriteableFileInfo.ETag"/> unless modifications
    /// are allowed; without an ETag a pinned read throws <see cref="NotSupportedException"/>. A store with its own
    /// seekable stream overrides it.
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
        if (!options.AllowModifications && info.ETag is null)
            throw new NotSupportedException($"{GetType().Name} keeps no version of '{path}', so a read cannot be pinned to one; allow modifications to read it unpinned.");
        var pin = options.AllowModifications ? null : info.ETag;
        return new RangeReadStream(info.Length, (offset, count, token) => ReadRangeCoreAsync(path, offset, count, pin, token), options.BufferSize);
    }

    /// <summary>
    /// Writes a file, atomically: a write that fails leaves the old content. Reads <paramref name="contents"/> from its
    /// position to its end and leaves it open. Never called with the root.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content.</param>
    /// <param name="options">
    /// How to write, conditions included. They always reach here: when <see cref="SupportsAtomicPreconditions"/> is
    /// true the store must check them as it writes; when it is false the base class has already checked them, just
    /// before this call (not atomic), and the provider may still send them for the store to enforce where it can. A
    /// store that refuses an <see cref="UpdateFileOptions.IfNotExists"/> write because a file is there throws
    /// <see cref="FileAlreadyExistsException"/>; any other failed condition, <see cref="PreconditionFailedException"/>.
    /// A folder at the path is not a file: it gets the answer a write without conditions gets.
    /// </param>
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
    /// destination with <see cref="UpdateFileOptions.IfNotExists"/>, so a destination created meanwhile throws
    /// <see cref="FileExistsException"/>; a store that copies on the server overrides it.
    /// </summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        var contents = await ReadFileCoreAsync(source, cancellationToken).ConfigureAwait(false);
        await using (contents.ConfigureAwait(false))
        {
            try
            {
                await UpdateWithPreconditionsAsync(destination, contents, new UpdateFileOptions { IfNotExists = true }, cancellationToken).ConfigureAwait(false);
            }
            // Only the create-only failure: the source's own stream may throw the base type (a pinned read whose file
            // changed), which passes as it is.
            catch (FileAlreadyExistsException e)
            {
                throw new FileExistsException($"The destination '{destination}' already exists.", e);
            }
        }
    }

    /// <summary>
    /// Moves a file whose source exists and whose destination does not, or that differs only in case on a store that
    /// ignores case (<see cref="IsCaseInsensitive"/>). The default copies with <see cref="CopyCoreAsync"/>, then deletes
    /// the source with <see cref="DeleteFileCoreAsync"/>. A case-only rename goes through a temporary copy next to the
    /// source: when a step fails, the file is put back under its old name and the temporary copy removed; only when
    /// that also fails does the temporary copy stay, and the <see cref="IOException"/> thrown names it.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual async Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsCaseOnlyRename(source, destination))
        {
            await RenameCaseOnlyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            return;
        }
        await CopyCoreAsync(source, destination, cancellationToken).ConfigureAwait(false);
        await DeleteFileCoreAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async Task RenameCaseOnlyAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        // Short whatever the file's name, so it fits a store's name limit (255 characters on most) whenever the name does.
        // Distinct from Physical's write temp, ".wfp-<guid>.tmp", which its listings hide: this copy holds a file's content.
        var temp = source.Parent.Child($".wfp-rename-{Guid.NewGuid():N}.tmp");
        await CopyCoreAsync(source, temp, cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteFileCoreAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The source is still there: drop the temporary copy and report the original failure.
            await TryDeleteFileAsync(temp).ConfigureAwait(false);
            throw;
        }

        try
        {
            await CopyCoreAsync(temp, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The source is gone: put the file back under its old name, or say where its content is.
            try
            {
                await CopyCoreAsync(temp, source, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception restoreFailure)
            {
                throw new IOException(
                    $"Renaming '{source}' to '{destination}' failed, and so did putting it back; its content is kept at '{temp}'.",
                    new AggregateException(failure, restoreFailure));
            }
            await TryDeleteFileAsync(temp).ConfigureAwait(false);
            throw;
        }

        try
        {
            await DeleteFileCoreAsync(temp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            throw new IOException($"'{source}' was renamed to '{destination}', but its temporary copy '{temp}' could not be removed.", failure);
        }
    }

    private async Task TryDeleteFileAsync(ResolvedPath path)
    {
        try
        {
            await DeleteFileCoreAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the caller is already reporting the failure that matters.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // Copies the file to a local temp file and opens it, uploading on commit unless read-only: spooled through
    // ReadFileCoreAsync, committed through UpdateFileCoreAsync (its failures mapped as a write's), keeping the content type
    // the file had when it was opened. Every provider's local stream is made here; it is not an extension point.
    private async Task<LocalFileStream> CreateLocalCopyAsync(ResolvedPath path, bool readOnly, CancellationToken cancellationToken)
    {
        var temp = StreamUtilities.CreateTempFile();
        try
        {
            var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsDirectory)
                throw readOnly ? NotFound(path) : new IOException($"The path '{path}' is a folder.");
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
            // Editing a file does not change what it is: the commit keeps its content type.
            var commit = info.ContentType is { } contentType ? new UpdateFileOptions { ContentType = contentType } : UpdateFileOptions.Overwrite;
            return new LocalFileStream(temp, this, path.Path, readOnly, readOnly ? null : (content, token) => CommitLocalCopyAsync(path, content, commit, token));
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Issues a signed URL; called only when <see cref="SupportsSignedUrl"/> is true for the access.</summary>
    /// <param name="path">The file; for a read URL it exists.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected virtual Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} cannot issue signed URLs.");

    /// <summary>Watches for changes. The default throws <see cref="NotSupportedException"/>.</summary>
    /// <param name="filter">The files to watch.</param>
    protected virtual IChangeToken WatchCore(string filter) => throw new NotSupportedException($"{GetType().Name} cannot watch for changes.");

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
    /// Appends by rewriting, for a store with no native append: the existing content and <paramref name="contents"/>
    /// are copied to a temp file, which then replaces the file with <see cref="UpdateFileOptions.IfMatch"/> on the
    /// version that was read, so a concurrent change is never lost. Where no file was read (a missing path, or a
    /// folder), it writes with <see cref="UpdateFileOptions.IfNotExists"/>, and a folder gets the store's answer. When
    /// the caller gave no condition, a concurrent change or create, or a file deleted since it was read, makes it read
    /// the file again and append after what is there now, up to five attempts in all, as Azure Blob's native append
    /// does; then it throws <see cref="PreconditionFailedException"/> with the last conflict inside. With the caller's
    /// <see cref="UpdateFileOptions.IfMatch"/> or <see cref="UpdateFileOptions.IfNotExists"/>, the first conflict
    /// throws: <see cref="FileAlreadyExistsException"/> for a file created meanwhile under
    /// <see cref="UpdateFileOptions.IfNotExists"/>, the store's "not found" (<see cref="FileNotFoundException"/> to the
    /// caller) for a file deleted before its read, <see cref="PreconditionFailedException"/> otherwise.
    /// <paramref name="contents"/> is read once: a retry copies the caller's bytes from the previous attempt's temp file,
    /// and a failure of the caller's stream ends the append, whatever its type. It keeps the file's content type unless
    /// the caller gives one. It needs the file's <see cref="IWriteableFileInfo.ETag"/>: an existing file without one
    /// throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content to add.</param>
    /// <param name="options">The caller's options; their conditions are checked against what was read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected async Task AppendByRewriteAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        // A caller's condition names one version, or none: its failure is the caller's answer, never read past.
        var retries = !options.IfNotExists && options.IfMatch is null;
        var appended = new AppendedBytes();
        await using (appended.ConfigureAwait(false))
        {
            for (var attempt = 1; ; attempt++)
            {
                var conflict = await TryAppendByRewriteAsync(path, contents, options, appended, retries, cancellationToken).ConfigureAwait(false);
                if (conflict is null)
                    return;
                if (attempt == AppendAttempts)
                    throw new PreconditionFailedException($"The file '{path}' kept changing while it was appended to; nothing was appended after {AppendAttempts} attempts.", conflict);
            }
        }
    }

    // One attempt of an append by rewrite: null once the file is written, or the concurrent change the next attempt reads
    // past. Only an append that retries returns one; any other failure is thrown.
    private async Task<Exception?> TryAppendByRewriteAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, AppendedBytes appended, bool retries, CancellationToken cancellationToken)
    {
        var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        // Only a file is appended to; a folder at the path gets the store's own answer to the rewrite below.
        var isFile = info.Exists && !info.IsDirectory;
        if (options.IfNotExists && isFile)
            throw new FileAlreadyExistsException($"The file '{path}' already exists.");
        if (options.IfMatch is not null && !string.Equals(info.ETag, options.IfMatch, StringComparison.Ordinal))
            throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{options.IfMatch}'.");
        if (isFile && info.ETag is null)
            throw new NotSupportedException($"{GetType().Name} keeps no version of '{path}', so an append could lose a concurrent write.");
        var temp = StreamUtilities.CreateTempFile();
        var held = false;
        try
        {
            if (isFile)
            {
                try
                {
                    var existing = await ReadFileCoreAsync(path, cancellationToken).ConfigureAwait(false);
                    await using (existing.ConfigureAwait(false))
                        await existing.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (retries && (e is FileNotFoundException || Recognises(IsNotFound, e)))
                {
                    // Deleted since its info was read: the next attempt creates it.
                    return e;
                }
            }
            // The caller's bytes: from the caller's stream the first time, from the temp file that holds them after.
            var start = temp.Position;
            if (appended.File is { } previous)
            {
                previous.Position = appended.Offset;
                await previous.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await contents.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            }
            await appended.HoldAsync(temp, start).ConfigureAwait(false);
            held = true;
            temp.Position = 0;
            // The rewrite replaces exactly the file that was read, or creates one where there was none: a file written
            // meanwhile makes it fail rather than be lost.
            var rewrite = options with
            {
                Mode = UpdateFileMode.Overwrite,
                IfMatch = isFile ? info.ETag : null,
                IfNotExists = !isFile,
                // An append does not change what the file is.
                ContentType = options.ContentType ?? info.ContentType,
            };
            try
            {
                await UpdateWithPreconditionsAsync(path, temp, rewrite, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (PreconditionFailedException e) when (retries)
            {
                return e;
            }
        }
        finally
        {
            if (!held)
                await temp.DisposeAsync().ConfigureAwait(false);
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

    private async Task CommitLocalCopyAsync(ResolvedPath path, Stream content, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await UpdateWithPreconditionsAsync(path, content, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Write, path, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    private bool IsCaseOnlyRename(ResolvedPath from, ResolvedPath to) =>
        IsCaseInsensitive && from.Path != to.Path
        && string.Equals(from.Path.ToString(), to.Path.ToString(), StringComparison.OrdinalIgnoreCase);

    private async Task UpdateWithPreconditionsAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        if (!SupportsAtomicPreconditions && (options.IfMatch is not null || options.IfNotExists))
        {
            var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
            // Only a file counts: a folder at the path gets the store's own answer to the write, as without conditions.
            if (options.IfNotExists && info.Exists && !info.IsDirectory)
                throw new FileAlreadyExistsException($"The file '{path}' already exists.");
            if (options.IfMatch is not null && !string.Equals(info.ETag, options.IfMatch, StringComparison.Ordinal))
                throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{options.IfMatch}'.");
        }
        await UpdateFileCoreAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
    }

    // How many attempts an append by rewrite with no condition of the caller's makes in all: as many as Azure Blob's
    // native append.
    private const int AppendAttempts = 5;

    // The caller's bytes of an append by rewrite, held in the latest attempt's temp file from Offset on, so that a retry
    // never reads the caller's stream again.
    private sealed class AppendedBytes : IAsyncDisposable
    {
        public FileStream? File { get; private set; }

        public long Offset { get; private set; }

        // Takes the temp file that now holds the bytes, from 'offset' on, and lets the previous one go.
        public async ValueTask HoldAsync(FileStream file, long offset)
        {
            if (File is { } previous)
                await previous.DisposeAsync().ConfigureAwait(false);
            File = file;
            Offset = offset;
        }

        public ValueTask DisposeAsync() => File?.DisposeAsync() ?? default;
    }
}
