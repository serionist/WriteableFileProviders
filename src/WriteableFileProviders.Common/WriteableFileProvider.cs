using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders;

/// <summary>
/// The base class of every provider. Its public members validate the path, place it under the root, apply the contract
/// every provider shares (<see cref="IWriteableFileProvider"/>), and call the protected <c>...CoreAsync</c> members a
/// provider implements.
/// </summary>
/// <typeparam name="TOptions">The provider's options. Their property names are the configuration shape consumers store.</typeparam>
/// <remarks>
/// <para>
/// A provider needs a public constructor that takes only <typeparamref name="TOptions"/>, so
/// <see cref="WriteableFileProviderSerializer"/> can build it from configuration, and it does no network I/O there.
/// </para>
/// <para>
/// What a public member throws is the contract's (<see cref="IWriteableFileProvider"/>): an exception of another type
/// that escapes a <c>...CoreAsync</c> member, or a stream or an enumeration a public member returns, is mapped here, once
/// for every provider. What <see cref="IsNotFound"/> recognises becomes <see cref="FileNotFoundException"/>, what
/// <see cref="IsAccessDenied"/> recognises <see cref="UnauthorizedAccessException"/>, a cancellation the caller's token
/// did not ask for (a timeout) <see cref="IOException"/>, a failure after the caller cancelled
/// <see cref="OperationCanceledException"/>, and anything else <see cref="IOException"/> naming the operation and the
/// path; the original exception is always the <see cref="Exception.InnerException"/>. One of the contract's types passes
/// through unchanged, so a provider maps what it knows better by throwing it.
/// </para>
/// </remarks>
public abstract partial class WriteableFileProvider<TOptions> : IWriteableFileProvider
    where TOptions : FileProviderOptions, new()
{
    private readonly string[] _rootSegments;
    private int _disposed;

    /// <summary>
    /// Creates the provider over its options, and checks the root they name (see <see cref="ValidatesRootDirectory"/>). The
    /// provider keeps a copy of the root: changing the options afterwards does not move it.
    /// </summary>
    /// <param name="options">The provider's options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="FileProviderOptions.RootDirectoryPath"/> holds a segment a path may not hold, such as <c>..</c>.
    /// </exception>
    protected WriteableFileProvider(TOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _rootSegments = [.. Options.RootDirectorySegments];
        if (RootRefusal(_rootSegments) is { } refusal)
            throw new ArgumentException(refusal, nameof(options));
    }

    /// <summary>The provider's options, as they were given.</summary>
    public TOptions Options { get; }

    FileProviderOptions IWriteableFileProvider.Options => Options;

    /// <summary>The clock for expiry times; the system clock unless a provider or a test sets another.</summary>
    protected TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// The provider as JSON, in the shape <see cref="WriteableFileProviderSerializer"/> reads back. Warning: it contains
    /// the options' secrets (keys, passwords, tokens) and must never be logged.
    /// </summary>
    public JsonObject ToJson() => WriteableFileProviderSerializer.ToJson(this);

    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath) => WriteableFileProviderExtensions.Wait(GetFileInfoAsync(subpath));

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath) => WriteableFileProviderExtensions.Wait(GetDirectoryContentsAsync(subpath));

    /// <summary>Watches for changes.</summary>
    /// <param name="filter">The files to watch, such as <c>**/*.json</c>.</param>
    /// <exception cref="NotSupportedException">The provider cannot watch for changes.</exception>
    public IChangeToken Watch(string filter)
    {
        try
        {
            return WatchCore(filter);
        }
        catch (Exception e) when (Map(e, Operation.Watch, null, null, CancellationToken.None) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (resolved.IsRoot)
            return WriteableFileInfo.Folder(this, ProviderPath.Root);
        try
        {
            return await GetFileInfoCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Info, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        try
        {
            var entries = new List<IFileInfo>();
            await foreach (var entry in EnumerateCoreAsync(resolved, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                entries.Add(entry);
            if (entries.Count > 0 || resolved.IsRoot)
                return new DirectoryContents(true, entries);
            return await FolderExistsCoreAsync(resolved, cancellationToken).ConfigureAwait(false)
                ? new DirectoryContents(true, [])
                : DirectoryContents.NotFound;
        }
        catch (Exception e) when (Map(e, Operation.List, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        return EnumerateResolvedAsync(resolved, recursive, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveReadable(path);
        try
        {
            return Contract(await ReadFileCoreAsync(resolved, cancellationToken).ConfigureAwait(false), Operation.Read, resolved);
        }
        catch (Exception e) when (Map(e, Operation.Read, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length cannot be negative.");
        var resolved = ResolveReadable(path);
        try
        {
            return Contract(await ReadRangeCoreAsync(resolved, offset, length, options?.IfMatch, cancellationToken).ConfigureAwait(false), Operation.Read, resolved);
        }
        catch (Exception e) when (Map(e, Operation.Read, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SeekableReadOptions();
        if (options.BufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.BufferSize, "The buffer size must be positive.");
        var resolved = ResolveReadable(path);
        try
        {
            return Contract(await OpenReadSeekableCoreAsync(resolved, options, cancellationToken).ConfigureAwait(false), Operation.Read, resolved);
        }
        catch (Exception e) when (Map(e, Operation.Read, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task UpdateFileAsync(ProviderPath path, Stream contents, UpdateFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        options ??= UpdateFileOptions.Overwrite;
        if (options.IfMatch is not null && options.IfNotExists)
            throw new ArgumentException("IfMatch and IfNotExists cannot both be set.", nameof(options));
        var resolved = ResolveModifiable(path);
        try
        {
            await UpdateWithPreconditionsAsync(resolved, contents, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Write, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveModifiable(path);
        try
        {
            return await DeleteCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Delete, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task MoveAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default)
    {
        var from = ResolveModifiable(source);
        var to = ResolveModifiable(destination);
        try
        {
            if (!await PrepareTransferAsync(from, to, move: true, cancellationToken).ConfigureAwait(false))
                await MoveCoreAsync(from, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Move, from, to, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task CopyAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default)
    {
        var from = ResolveModifiable(source);
        var to = ResolveModifiable(destination);
        try
        {
            await PrepareTransferAsync(from, to, move: false, cancellationToken).ConfigureAwait(false);
            await CopyCoreAsync(from, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Copy, from, to, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly, CancellationToken cancellationToken = default)
    {
        var resolved = readOnly ? ResolveReadable(path) : ResolveModifiable(path);
        try
        {
            return await CreateLocalCopyAsync(resolved, readOnly, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.LocalCopy, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc />
    public async Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.Validity, "The validity must be positive.");
        var resolved = options.Access == SignedUrlAccess.Read ? ResolveReadable(path) : ResolveModifiable(path);
        if (!SupportsSignedUrl(options.Access))
            throw new NotSupportedException($"{GetType().Name} cannot issue a {options.Access} URL.");
        if ((options.DownloadFileName is not null || options.ContentType is not null) && !SupportsSignedUrlContentHeaders(options.Access))
            throw new NotSupportedException($"{GetType().Name} cannot set the download file name or the content type of a {options.Access} URL.");
        if (options.Validity > MaxSignedUrlValidity)
            throw new NotSupportedException($"{GetType().Name} signs URLs that live at most {MaxSignedUrlValidity}; {options.Validity} was asked.");
        try
        {
            if (options.Access == SignedUrlAccess.Read)
            {
                var info = await GetFileInfoCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
                if (!info.Exists || info.IsDirectory)
                    throw NotFound(resolved);
            }
            return await GetSignedUrlCoreAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, Operation.Sign, resolved, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Disposes the provider and the clients it owns.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the provider and the clients it owns.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // Checks the source and the destination of a move or a copy; true when a move has nothing to do (onto itself).
    private async Task<bool> PrepareTransferAsync(ResolvedPath from, ResolvedPath to, bool move, CancellationToken cancellationToken)
    {
        var info = await GetFileInfoCoreAsync(from, cancellationToken).ConfigureAwait(false);
        if (!info.Exists || info.IsDirectory)
            throw NotFound(from);
        if (move && from.Path == to.Path)
            return true;
        // On a store that ignores case, moving a file to a name that differs only in case is a rename: the destination
        // "exists" because it is the source.
        if (!(move && IsCaseOnlyRename(from, to)) && (await GetFileInfoCoreAsync(to, cancellationToken).ConfigureAwait(false)).Exists)
            throw new FileExistsException($"The destination '{to}' already exists.");
        return false;
    }
}
