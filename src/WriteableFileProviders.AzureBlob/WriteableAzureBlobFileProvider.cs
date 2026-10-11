using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace WriteableFileProviders.AzureBlob;

/// <summary>
/// A writeable file provider over an Azure Blob Storage container. Files are block blobs; folders are name prefixes, so
/// a folder exists while some file is in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes</b> stage the content as blocks and commit them in one request, so a write that fails leaves the old content
/// and readers never see half of it. <see cref="UpdateFileOptions.IfMatch"/> and <see cref="UpdateFileOptions.IfNotExists"/>
/// are checked atomically by that commit. Azure keeps only the blocks a commit lists, so when another writer commits the
/// same file while an unconditional overwrite is staged, the overwrite's blocks are gone: content that fits in one block
/// (8 MiB) is staged again, up to five attempts in all; a longer write throws <see cref="IOException"/> and stores nothing
/// (with a condition, <see cref="PreconditionFailedException"/>). An append re-commits the file's block list with the new
/// blocks after it, on the condition that the file has not changed since it was read; after a concurrent change it reads
/// the file again, up to five attempts in all, then throws <see cref="PreconditionFailedException"/>, so it never loses a
/// concurrent write.
/// </para>
/// <para>
/// <b>Append blobs</b> written by 2026.10.10.x stay readable. Azure cannot turn one into a block blob in place, so the first
/// write or append to one goes through a hidden temporary blob under <c>.wfp-tmp/</c> in the provider root: the new content
/// is committed there, the append blob is deleted (with its snapshots) if it has not changed, the temporary blob is copied
/// to its name, and only then removed. Meanwhile the file is missing, or empty while the server copy runs; if the copy
/// fails, the exception names the temporary blob that holds the content. Page blobs can be read, not written.
/// </para>
/// <para>
/// <b>Moves and copies</b> copy on the server, refusing an existing destination atomically; a move then deletes the source
/// only if it has not changed since it was copied, and otherwise removes the copy and throws
/// <see cref="PreconditionFailedException"/>. A copy that does not finish within 10 minutes is aborted. Snapshots are
/// never deleted: deleting or moving a blob that has snapshots throws <see cref="IOException"/> and leaves it as it was.
/// </para>
/// <para>
/// <b>Signed URLs</b> are service SAS with a shared key, or user delegation SAS when the provider signs in with Microsoft
/// Entra ID, for reading, creating (only if absent) and writing, up to 7 days. Their start time is 15 minutes in the past,
/// so a clock that runs behind does not reject them, and they never live past the requested validity.
/// </para>
/// <para>
/// A missing container makes every member throw <see cref="InvalidOperationException"/>, unless
/// <see cref="AzureFileProviderOptions.CreateContainerIfMissing"/> lets the first write create it. Names are
/// case-sensitive. The provider owns no disposable resources, and never disposes a client it was given.
/// </para>
/// </remarks>
public sealed partial class WriteableAzureBlobFileProvider : WriteableFileProvider<AzureFileProviderOptions>
{
    private const string TempFolder = ".wfp-tmp";
    private const string HiddenPrefix = ".wfp-";
    private const string DefaultContentType = "application/octet-stream";
    private const int DeleteParallelism = 8;

    private readonly BlobContainerClient _container;
    private long _serverCopyRefusedUntil;

    /// <summary>Creates the provider from its options; it does no network I/O until it is used.</summary>
    /// <param name="options">The account, how to sign in, the container and the root folder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are incomplete or contradict each other; the message says which.</exception>
    public WriteableAzureBlobFileProvider(AzureFileProviderOptions options)
        : this(options, null, null)
    {
    }

    /// <summary>
    /// Creates the provider over a container client the caller built, such as one signed in with a managed identity, with
    /// custom retries or a custom transport. The provider never disposes it. Only the root folder and
    /// <see cref="AzureFileProviderOptions.CreateContainerIfMissing"/> are read from <paramref name="options"/>; the account
    /// and sign-in options may stay empty, and <see cref="WriteableFileProvider{TOptions}.ToJson"/> writes only the
    /// options.
    /// </summary>
    /// <param name="options">The root folder and the container option; null for the container's root.</param>
    /// <param name="container">The container client. Its credential decides how signed URLs are signed: a shared key signs a
    /// service SAS, a token credential a user delegation SAS; with neither, signed URLs throw
    /// <see cref="NotSupportedException"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="AzureFileProviderOptions.ContainerName"/> names another container.</exception>
    public WriteableAzureBlobFileProvider(AzureFileProviderOptions? options, BlobContainerClient container)
        : base(options ?? new AzureFileProviderOptions())
    {
        ArgumentNullException.ThrowIfNull(container);
        if (!string.IsNullOrEmpty(Options.ContainerName) && !string.Equals(Options.ContainerName, container.Name, StringComparison.Ordinal))
            throw new ArgumentException($"AzureFileProviderOptions.ContainerName is '{Options.ContainerName}', but the client is for container '{container.Name}'.", nameof(options));
        _container = container;
    }

    /// <summary>The options constructor, with the client options and the credential a test can give.</summary>
    internal WriteableAzureBlobFileProvider(AzureFileProviderOptions options, BlobClientOptions? clientOptions, TokenCredential? credential)
        : base(options)
    {
        _container = CreateContainerClient(Options, clientOptions, credential);
    }

    /// <summary>The size of each staged block: 8 MiB. A write from a seekable stream uses larger blocks when it needs them.</summary>
    internal int BlockSize { get; set; } = 8 << 20;

    /// <summary>The largest block a write stages: 256 MiB.</summary>
    internal int MaxBlockSize { get; set; } = 256 << 20;

    /// <summary>How many blocks a write stages at the same time.</summary>
    internal int MaxConcurrency { get; set; } = 4;

    /// <summary>Azure's limit of committed blocks per blob: 50,000.</summary>
    internal int MaxCommittedBlocks { get; set; } = 50_000;

    /// <summary>The largest range one server-side block copy (Put Block From URL) takes: 100 MiB.</summary>
    internal long CopyRangeSize { get; set; } = 100L << 20;

    /// <summary>
    /// How many times an append, a replace or a copy reads the file again after a concurrent change, and how many times an
    /// overwrite of one block stages it again after another writer's commit dropped it.
    /// </summary>
    internal int MaxAttempts { get; set; } = 5;

    /// <summary>How often a pending server-side copy is polled.</summary>
    internal TimeSpan CopyPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a server-side copy may stay pending before it is aborted: 10 minutes, as in 2026.</summary>
    internal TimeSpan CopyTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Until when the provider stages restaged content through the client, after the service refused to read it.</summary>
    internal DateTimeOffset ServerCopyRefusedUntil
    {
        get => new(Interlocked.Read(ref _serverCopyRefusedUntil), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _serverCopyRefusedUntil, value.UtcTicks);
    }

    /// <summary>The clock for signed URL times and for how long a refusal is remembered, for the tests.</summary>
    internal TimeProvider Clock
    {
        get => TimeProvider;
        set => TimeProvider = value;
    }

    /// <summary>The container client the provider works through.</summary>
    internal BlobContainerClient Container => _container;

    /// <inheritdoc />
    protected override bool SupportsAtomicPreconditions => true;

    /// <inheritdoc />
    protected override async Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        return await GuardAsync(async () =>
        {
            var properties = await GetPropertiesOrNullAsync(Blob(path), cancellationToken).ConfigureAwait(false);
            if (properties is not null)
                return FileInfo(path.Path, properties.ContentLength, properties.LastModified, properties.ETag, properties.ContentType);
            return await HasBlobsUnderAsync(path, cancellationToken).ConfigureAwait(false)
                ? WriteableFileInfo.Folder(this, path.Path)
                : WriteableFileInfo.Missing(this, path.Path);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<bool> FolderExistsCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        await GuardAsync(() => HasBlobsUnderAsync(path, cancellationToken)).ConfigureAwait(false);

    /// <inheritdoc />
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        GuardAsync(EnumerateLevelAsync(path, cancellationToken), cancellationToken);

    /// <summary>
    /// Every level of a folder from one flat listing of the blobs under it, in name order: each folder is made up from the
    /// first blob below it, and yielded once, before it.
    /// </summary>
    /// <param name="path">The folder; may be the root.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateRecursiveCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        GuardAsync(EnumerateTreeAsync(path, cancellationToken), cancellationToken);

    /// <inheritdoc />
    protected override async Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GuardAsync(async () =>
        {
            var download = await Blob(path).DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken).ConfigureAwait(false);
            return download.Value.Content;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blob = Blob(path);
        var conditions = ifMatch is null ? null : new BlobRequestConditions { IfMatch = new ETag(ifMatch) };
        return await GuardAsync(async () =>
        {
            try
            {
                return await DownloadRangeAsync(blob, offset, length, conditions, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (IsConditionNotMet(e))
            {
                throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{ifMatch}'.", e);
            }
        }).ConfigureAwait(false);
    }

    private static async Task<Stream> DownloadRangeAsync(BlobClient blob, long offset, long? length, BlobRequestConditions? conditions, CancellationToken cancellationToken)
    {
        try
        {
            // A range of length 0 cannot be asked for; the file must still exist and match.
            if (length == 0)
            {
                await blob.GetPropertiesAsync(conditions, cancellationToken).ConfigureAwait(false);
                return Stream.Null;
            }
            var download = await blob.DownloadStreamingAsync(new BlobDownloadOptions
            {
                Range = new HttpRange(offset, length),
                Conditions = conditions,
            }, cancellationToken).ConfigureAwait(false);
            return download.Value.Content;
        }
        catch (RequestFailedException e) when (e.Status == 416)
        {
            // Past the end, or any range of an empty file. The version still has to match.
            if (conditions is not null)
                await blob.GetPropertiesAsync(conditions, cancellationToken).ConfigureAwait(false);
            return Stream.Null;
        }
    }

    /// <summary>
    /// Opens the blob's own seekable stream, which reads ranges as it goes. Unless modifications are allowed, it is pinned
    /// to the version it opened, atomically: a read after the file changed throws <see cref="PreconditionFailedException"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How the stream reads.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task<Stream> OpenReadSeekableCoreAsync(ResolvedPath path, SeekableReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        return await GuardAsync(async () =>
        {
            var stream = await Blob(path).OpenReadAsync(new BlobOpenReadOptions(options.AllowModifications) { BufferSize = options.BufferSize }, cancellationToken).ConfigureAwait(false);
            return (Stream)new BlobReadStream(stream, path.ToString());
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a blob, or every blob under a folder. A blob with snapshots is not deleted: it throws
    /// <see cref="IOException"/>, as the provider never deletes snapshots.
    /// </summary>
    /// <param name="path">The file or folder.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GuardAsync(async () =>
        {
            if (await DeleteBlobAsync(Blob(path), cancellationToken).ConfigureAwait(false))
                return true;
            var deleted = 0;
            var pages = _container.GetBlobsAsync(new GetBlobsOptions { Prefix = FolderPrefix(path) }, cancellationToken).AsPages();
            await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await Parallel.ForEachAsync(page.Values, new ParallelOptions { MaxDegreeOfParallelism = DeleteParallelism, CancellationToken = cancellationToken }, async (item, token) =>
                {
                    if (await DeleteBlobAsync(_container.GetBlobClient(item.Name), token).ConfigureAwait(false))
                        Interlocked.Increment(ref deleted);
                }).ConfigureAwait(false);
            }
            return deleted > 0;
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes exactly the one blob, never what is under its name as a folder.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task DeleteFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        await GuardAsync(() => DeleteBlobAsync(Blob(path), cancellationToken)).ConfigureAwait(false);

    /// <summary>A store's "blob not found": the public members turn it into a <see cref="FileNotFoundException"/>.</summary>
    /// <param name="exception">The store's exception.</param>
    protected override bool IsNotFound(Exception exception) =>
        exception is RequestFailedException { Status: 404 } e && !Is(e, BlobErrorCode.ContainerNotFound);

    /// <summary>
    /// Azure's 401 and 403, and Microsoft Entra ID refusing the credential when the provider signs in with
    /// <see cref="AzureFileProviderOptions.UseDefaultAzureCredential"/> (Azure.Identity's
    /// <see cref="AuthenticationFailedException"/>, thrown inside each request): the public members turn them into an
    /// <see cref="UnauthorizedAccessException"/>. A sign-in that finds no credential at all
    /// (<see cref="CredentialUnavailableException"/>) is not a refusal: the provider throws
    /// <see cref="InvalidOperationException"/> for it, as for a missing container.
    /// </summary>
    /// <param name="exception">The store's exception.</param>
    protected override bool IsAccessDenied(Exception exception) =>
        exception is RequestFailedException { Status: 401 or 403 } or (AuthenticationFailedException and not CredentialUnavailableException);

    private static BlobContainerClient CreateContainerClient(AzureFileProviderOptions options, BlobClientOptions? clientOptions, TokenCredential? credential)
    {
        if (string.IsNullOrWhiteSpace(options.ContainerName))
            throw new ArgumentException("AzureFileProviderOptions.ContainerName is required.", nameof(options));
        var hasKey = !string.IsNullOrEmpty(options.AccountKey);
        if (options.UseDefaultAzureCredential && hasKey)
            throw new ArgumentException("Set AzureFileProviderOptions.AccountKey or UseDefaultAzureCredential, not both.", nameof(options));
        if (!options.UseDefaultAzureCredential && !hasKey)
            throw new ArgumentException("AzureFileProviderOptions.AccountKey is required, unless UseDefaultAzureCredential is set.", nameof(options));
        if (!options.UseDefaultAzureCredential && string.IsNullOrWhiteSpace(options.AccountName))
            throw new ArgumentException("AzureFileProviderOptions.AccountName is required with a shared key.", nameof(options));

        var serviceUri = ServiceUri(options);
        if (!options.UseDefaultAzureCredential)
        {
            StorageSharedKeyCredential sharedKey;
            try
            {
                sharedKey = new StorageSharedKeyCredential(options.AccountName, options.AccountKey);
            }
            catch (FormatException e)
            {
                throw new ArgumentException("AzureFileProviderOptions.AccountKey is not a base64 key.", nameof(options), e);
            }
            return new BlobServiceClient(serviceUri, sharedKey, clientOptions).GetBlobContainerClient(options.ContainerName);
        }
        if (serviceUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("UseDefaultAzureCredential needs an https endpoint: AzureFileProviderOptions.Protocol cannot be http.", nameof(options));
        return new BlobServiceClient(serviceUri, credential ?? new DefaultAzureCredential(), clientOptions).GetBlobContainerClient(options.ContainerName);
    }

    private static Uri ServiceUri(AzureFileProviderOptions options)
    {
        var scheme = string.IsNullOrEmpty(options.Protocol) ? Uri.UriSchemeHttps : options.Protocol.ToLowerInvariant();
        if (scheme != Uri.UriSchemeHttps && scheme != Uri.UriSchemeHttp)
            throw new ArgumentException($"AzureFileProviderOptions.Protocol must be http or https, not '{options.Protocol}'.", nameof(options));
        string url;
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            if (string.IsNullOrWhiteSpace(options.AccountName))
                throw new ArgumentException("AzureFileProviderOptions.AccountName or Url is required.", nameof(options));
            url = $"{scheme}://{options.AccountName}.blob.core.windows.net";
        }
        else if (options.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || options.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = options.Url;
            if (!string.IsNullOrEmpty(options.Protocol) && !url.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"AzureFileProviderOptions.Url '{options.Url}' does not use the scheme of Protocol '{options.Protocol}'.", nameof(options));
        }
        else
        {
            url = $"{scheme}://{options.Url}";
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri
            : throw new ArgumentException($"AzureFileProviderOptions.Url does not make a valid endpoint: '{url}'.", nameof(options));
    }

    private async IAsyncEnumerable<IWriteableFileInfo> EnumerateLevelAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = FolderPrefix(path);
        var items = _container.GetBlobsByHierarchyAsync(new GetBlobsByHierarchyOptions { Delimiter = "/", Prefix = prefix }, cancellationToken);
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (item.IsPrefix)
            {
                var name = item.Prefix[prefix.Length..];
                name = name.EndsWith('/') ? name[..^1] : name;
                if (IsListable(name))
                    yield return WriteableFileInfo.Folder(this, path.Child(name).Path);
            }
            else if (item.IsBlob)
            {
                var name = item.Blob.Name[prefix.Length..];
                if (IsListable(name))
                    yield return FileInfo(path.Child(name).Path, item.Blob.Properties);
            }
        }
    }

    private async IAsyncEnumerable<IWriteableFileInfo> EnumerateTreeAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = FolderPrefix(path);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var items = _container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken);
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var parts = item.Name[prefix.Length..].Split('/');
            // The folders above the blob, up to its first name the provider cannot address, as the one-level listing shows
            // them; the blob itself only when every name is addressable. A blob named "dir/" (a folder marker, as the Azure
            // portal makes them) ends in an empty name: a folder, not a file.
            var addressable = Array.FindIndex(parts, part => !IsListable(part));
            if (addressable < 0)
                addressable = parts.Length;
            for (var depth = 1; depth <= Math.Min(addressable, parts.Length - 1); depth++)
            {
                if (folders.Add(string.Join('/', parts[..depth])))
                    yield return WriteableFileInfo.Folder(this, new ProviderPath([.. path.RelativeSegments, .. parts[..depth]]));
            }
            if (addressable == parts.Length)
                yield return FileInfo(new ProviderPath([.. path.RelativeSegments, .. parts]), item.Properties);
        }
    }

    // What a listing shows: a name a path can name again (IsAddressable), but not the provider's own hidden .wfp- entries.
    private bool IsListable(string name) => !name.StartsWith(HiddenPrefix, StringComparison.Ordinal) && IsAddressable(name);

    private async Task<bool> HasBlobsUnderAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var pages = _container.GetBlobsAsync(new GetBlobsOptions { Prefix = FolderPrefix(path) }, cancellationToken).AsPages(pageSizeHint: 1);
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (page.Values.Count > 0)
                return true;
        }
        return false;
    }

    private static async Task<BlobProperties?> GetPropertiesOrNullAsync(BlobBaseClient blob, CancellationToken cancellationToken)
    {
        try
        {
            return (await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.BlobNotFound))
        {
            return null;
        }
    }

    // Deletes one blob, never its snapshots: a blob that has some stays, and the caller learns why.
    private async Task<bool> DeleteBlobAsync(BlobBaseClient blob, CancellationToken cancellationToken)
    {
        try
        {
            return (await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.SnapshotsPresent))
        {
            throw new IOException($"The file '{ProviderPathOf(blob)}' has snapshots, which {nameof(WriteableAzureBlobFileProvider)} never deletes; delete them first.", e);
        }
    }

    // The root the provider was built with, which a later change to the options does not move.
    private string[] RootSegments => Resolve(ProviderPath.Root).AbsoluteSegments;

    // A blob's name as a path of this provider, for messages.
    private string ProviderPathOf(BlobBaseClient blob) => PathUnderRoot(blob.Name);

    // A blob name under the root as a path of this provider.
    private string PathUnderRoot(string blobName)
    {
        var root = RootSegments;
        return root.Length == 0 ? blobName : blobName[(string.Join('/', root).Length + 1)..];
    }

    /// <summary>
    /// Deletes exactly the one blob at <paramref name="path"/> through <see cref="DeleteFileCoreAsync"/>, the hook the base
    /// class's own moves use: a test's way in, as the class is sealed.
    /// </summary>
    internal Task DeleteFileForTestsAsync(ProviderPath path) => DeleteFileCoreAsync(Resolve(path), CancellationToken.None);

    private WriteableFileInfo FileInfo(ProviderPath path, long length, DateTimeOffset lastModified, ETag etag, string? contentType) =>
        new(this, path, true, false, length, lastModified, etag: Tag(etag), contentType: contentType);

    private WriteableFileInfo FileInfo(ProviderPath path, BlobItemProperties properties) =>
        new(this, path, true, false, properties.ContentLength ?? 0, properties.LastModified ?? DateTimeOffset.UnixEpoch,
            etag: properties.ETag is { } etag ? Tag(etag) : null, contentType: properties.ContentType);

    // One form of an ETag everywhere: a HEAD returns it quoted, a listing bare.
    private static string Tag(ETag etag) => etag.ToString("H");

    private static bool SameTag(ETag etag, string other) => string.Equals(Tag(etag), Tag(new ETag(other)), StringComparison.Ordinal);

    private BlobClient Blob(ResolvedPath path) => _container.GetBlobClient(BlobName(path));

    private BlockBlobClient BlockBlob(ResolvedPath path) => _container.GetBlockBlobClient(BlobName(path));

    private static string BlobName(ResolvedPath path) => string.Join('/', path.AbsoluteSegments);

    private static string FolderPrefix(ResolvedPath path) => path.AbsoluteSegments.Length == 0 ? "" : BlobName(path) + "/";

    private static bool Is(RequestFailedException exception, BlobErrorCode code) =>
        string.Equals(exception.ErrorCode, code.ToString(), StringComparison.Ordinal);

    // A 412 that says a condition of the request did not hold; a lease's 412 (LeaseIdMissing and the like) is not one.
    internal static bool IsConditionNotMet(RequestFailedException exception) =>
        exception.Status == 412
        && (string.IsNullOrEmpty(exception.ErrorCode) || Is(exception, BlobErrorCode.ConditionNotMet)
            || Is(exception, BlobErrorCode.SourceConditionNotMet) || Is(exception, BlobErrorCode.TargetConditionNotMet));

    private InvalidOperationException ContainerMissing(Exception? innerException) =>
        new($"The blob container '{_container.Name}' does not exist. Create it, or set AzureFileProviderOptions.{nameof(AzureFileProviderOptions.CreateContainerIfMissing)} so the first write creates it.", innerException);

    // Microsoft Entra ID sign-in found no credential to use (Azure.Identity raises it inside each request): the store is
    // not set up as configured, as a missing container is.
    private static InvalidOperationException NoCredential(CredentialUnavailableException innerException) =>
        new($"UseDefaultAzureCredential is set, but the Microsoft Entra ID sign-in found no credential to use (environment, workload or managed identity, developer sign-in). Configure one, or set AzureFileProviderOptions.{nameof(AzureFileProviderOptions.AccountKey)}.", innerException);

    // Every member: a missing container is an InvalidOperationException, never a missing path (P18), and so is a sign-in
    // that finds no credential.
    private async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.ContainerNotFound))
        {
            throw ContainerMissing(e);
        }
        catch (CredentialUnavailableException e)
        {
            throw NoCredential(e);
        }
    }

    private async Task GuardAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.ContainerNotFound))
        {
            throw ContainerMissing(e);
        }
        catch (CredentialUnavailableException e)
        {
            throw NoCredential(e);
        }
    }

    private async IAsyncEnumerable<T> GuardAsync<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (RequestFailedException e) when (Is(e, BlobErrorCode.ContainerNotFound))
                {
                    throw ContainerMissing(e);
                }
                catch (CredentialUnavailableException e)
                {
                    throw NoCredential(e);
                }
                if (!hasNext)
                    yield break;
                yield return enumerator.Current;
            }
        }
    }

    // A write's first request: on a missing container, create it when the options allow, then try once more.
    private async Task<T> WithContainerAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.ContainerNotFound) && Options.CreateContainerIfMissing)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
    }
}
