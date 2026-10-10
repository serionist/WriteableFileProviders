using System.Runtime.CompilerServices;
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
/// A provider needs a public constructor that takes only <typeparamref name="TOptions"/>, so
/// <see cref="WriteableFileProviderSerializer"/> can build it from configuration, and it does no network I/O there.
/// </remarks>
/// <param name="options">The provider's options.</param>
public abstract partial class WriteableFileProvider<TOptions>(TOptions options) : IWriteableFileProvider
    where TOptions : FileProviderOptions, new()
{
    private int _disposed;

    /// <summary>The provider's options, as they were given.</summary>
    public TOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    FileProviderOptions IWriteableFileProvider.Options => Options;

    /// <summary>The clock for expiry times; the system clock unless a provider or a test sets another.</summary>
    protected TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// The provider as JSON, in the shape <see cref="WriteableFileProviderSerializer"/> reads back. Warning: it contains
    /// the options' secrets (keys, passwords, tokens) and must never be logged.
    /// </summary>
    public JsonObject ToJson() => WriteableFileProviderSerializer.ToJson(this);

    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath) => GetFileInfoAsync(subpath).GetAwaiter().GetResult();

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath) => GetDirectoryContentsAsync(subpath).GetAwaiter().GetResult();

    /// <summary>Watches for changes.</summary>
    /// <param name="filter">The files to watch, such as <c>**/*.json</c>.</param>
    /// <exception cref="NotSupportedException">The provider cannot watch for changes.</exception>
    public IChangeToken Watch(string filter) => WatchCore(filter);

    /// <inheritdoc />
    public async Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (resolved.IsRoot)
            return WriteableFileInfo.Folder(this, ProviderPath.Root);
        return await GetFileInfoCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        var entries = new List<IFileInfo>();
        await foreach (var entry in EnumerateCoreAsync(resolved, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            entries.Add(entry);
        if (entries.Count > 0 || resolved.IsRoot)
            return new DirectoryContents(true, entries);
        return await FolderExistsCoreAsync(resolved, cancellationToken).ConfigureAwait(false)
            ? new DirectoryContents(true, [])
            : DirectoryContents.NotFound;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        return EnumerateResolvedAsync(resolved, recursive, cancellationToken);
    }

    private async IAsyncEnumerable<IWriteableFileInfo> EnumerateResolvedAsync(ResolvedPath path, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var source = recursive ? EnumerateRecursiveCoreAsync(path, cancellationToken) : EnumerateCoreAsync(path, cancellationToken);
        await foreach (var entry in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return entry;
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveReadable(path);
        try
        {
            return await ReadFileCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not FileNotFoundException && IsNotFound(e))
        {
            throw NotFound(resolved, e);
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
            return await ReadRangeCoreAsync(resolved, offset, length, options?.IfMatch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not FileNotFoundException && IsNotFound(e))
        {
            throw NotFound(resolved, e);
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
            return await OpenReadSeekableCoreAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not FileNotFoundException && IsNotFound(e))
        {
            throw NotFound(resolved, e);
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
        await UpdateWithPreconditionsAsync(resolved, contents, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveModifiable(path);
        return await DeleteCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default)
    {
        var (from, to, same) = await PrepareTransferAsync(source, destination, move: true, cancellationToken).ConfigureAwait(false);
        if (!same)
            await MoveCoreAsync(from, to, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CopyAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default)
    {
        var (from, to, _) = await PrepareTransferAsync(source, destination, move: false, cancellationToken).ConfigureAwait(false);
        await CopyCoreAsync(from, to, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly, CancellationToken cancellationToken = default)
    {
        var resolved = readOnly ? ResolveReadable(path) : ResolveModifiable(path);
        return await CreateLocalFileStreamCoreAsync(resolved, readOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.Validity, "The validity must be positive.");
        var resolved = options.Permission == SignedUrlAccess.Read ? ResolveReadable(path) : ResolveModifiable(path);
        if (!SupportsSignedUrl(options.Permission))
            throw new NotSupportedException($"{GetType().Name} cannot issue a {options.Permission} URL.");
        if (options.Validity > MaxSignedUrlValidity)
            throw new NotSupportedException($"{GetType().Name} signs URLs that live at most {MaxSignedUrlValidity}; {options.Validity} was asked.");
        if (options.Permission == SignedUrlAccess.Read)
        {
            var info = await GetFileInfoCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (!info.Exists || info.IsDirectory)
                throw NotFound(resolved);
        }
        return await GetSignedUrlCoreAsync(resolved, options, cancellationToken).ConfigureAwait(false);
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

    private async Task<(ResolvedPath From, ResolvedPath To, bool Same)> PrepareTransferAsync(
        ProviderPath source, ProviderPath destination, bool move, CancellationToken cancellationToken)
    {
        var from = ResolveModifiable(source);
        var to = ResolveModifiable(destination);
        var info = await GetFileInfoCoreAsync(from, cancellationToken).ConfigureAwait(false);
        if (!info.Exists || info.IsDirectory)
            throw NotFound(from);
        var same = from.Path == to.Path;
        if (move && same)
            return (from, to, true);
        // On a store that ignores case, moving a file to a name that differs only in case is a rename: the destination
        // "exists" because it is the source.
        var caseOnlyRename = move && IsCaseInsensitive
            && string.Equals(from.Path.ToString(), to.Path.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!caseOnlyRename && (await GetFileInfoCoreAsync(to, cancellationToken).ConfigureAwait(false)).Exists)
            throw new FileExistsException($"The destination '{to}' already exists.");
        return (from, to, false);
    }
}
