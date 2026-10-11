using System.Runtime.CompilerServices;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using Box.Sdk.Gen.Schemas;
using Task = System.Threading.Tasks.Task;

namespace WriteableFileProviders.Box;

/// <summary>
/// A writeable file provider over a Box folder, on the Box .NET SDK (<c>Box.Sdk.Gen</c>). Paths are folders and files under
/// <see cref="BoxFileProviderOptions.RootFolderId"/>, followed by the root folder of the options.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in.</b> With the options alone, the provider signs in with the app's JWT credentials as its service account, on
/// the first call that needs Box: the constructors do no network I/O. Box's tokens last an hour; the SDK fetches a new one
/// when Box answers 401. With a ready client, the client's own authentication is used: a client that is exactly a
/// <see cref="BoxClient"/> through a copy that shares its authentication and its session's settings, with the session's
/// network client wrapped (see Errors); any other <see cref="IBoxClient"/> as it is. The provider never disposes a client
/// it was given.
/// </para>
/// <para>
/// <b>Paths.</b> Box has real folders, which exist while empty, and compares names without regard to case: a folder cannot
/// hold <c>a.txt</c> and <c>A.txt</c>, and moving <c>a.txt</c> to <c>A.txt</c> renames it. Every call resolves its path
/// from the root folder down, listing each folder with marker paging: nothing is cached between calls. Box allows some
/// names that a path cannot name, such as <c>...</c> (only dots) or <c>%2e%2e</c> (a percent-encoded <c>..</c>):
/// listings leave those files and folders out, and never walk into such a folder, so everything listed can be read back.
/// </para>
/// <para>
/// <b>Writes.</b> The caller's stream is copied to a local temp file from its position first, so a failing stream leaves
/// the file as it was. Files below 20 MiB go up in one request, larger ones through a chunked upload session; Box keeps
/// either as a new version only once it is complete. An append rewrites the file (Box has no append), on the version it
/// read. Box keeps no content type, so <see cref="UpdateFileOptions.ContentType"/> is ignored and
/// <see cref="IWriteableFileInfo.ContentType"/> is null. A deleted file or folder goes to the Box trash.
/// </para>
/// <para>
/// <b>ETags.</b> A file's <see cref="IWriteableFileInfo.ETag"/> is the id of its current Box file version, which no other
/// version of any file shares: a file that is deleted and created again under the same name has another ETag. (Box's own
/// <c>etag</c> counts the versions of one item, so it repeats across items.)
/// </para>
/// <para>
/// <b>Conditions.</b> <see cref="UpdateFileOptions.IfNotExists"/> is atomic: Box refuses to create a name that is taken.
/// <see cref="UpdateFileOptions.IfMatch"/> is checked against the file's ETag just before the write, which is not atomic:
/// a write that lands between the check and the upload is replaced. The same holds for the version an append rewrites.
/// </para>
/// <para>
/// <b>Reads.</b> Reads stream the download; ranged and seekable reads fetch byte ranges of the file version they were
/// pinned to, and throw <see cref="PreconditionFailedException"/> once the file has changed. A failed download throws
/// <see cref="FileNotFoundException"/> or <see cref="IOException"/> with its HTTP status only, never the download URL,
/// which is a credential.
/// </para>
/// <para>
/// <b>Errors.</b> The SDK's <see cref="BoxApiException"/> keeps the request it sent: the access token, any extra header
/// the client was built with, the query string and the body (a token request's holds the client secret). When the
/// provider built its client, or was given exactly a <see cref="BoxClient"/>, every <see cref="BoxApiException"/> it
/// wraps keeps Box's status, code and message, and of the request only its method, its URL without the query,
/// and the headers and parameters that diagnose it (<c>As-User</c>, <c>If-Match</c>, <c>Range</c>, the digests, the
/// content type; the listing and version parameters). A provider given any other <see cref="IBoxClient"/>, a class
/// derived from <see cref="BoxClient"/> included, uses that client as it is: its errors are the client's own, and may
/// carry its credentials.
/// </para>
/// <para>
/// <b>Waits.</b> Box can answer that a file just uploaded is not ready to download yet, or that a large upload is still
/// being processed. The provider waits as Box asks and tries again: up to two minutes for a download, and two minutes
/// plus two more for every whole GiB for an upload; then it throws <see cref="IOException"/>.
/// </para>
/// <para>
/// <b>Signed URLs.</b> Read only: Box's own pre-authenticated download URL, which lives about 15 minutes whatever is asked.
/// A shorter validity throws <see cref="NotSupportedException"/>, since the URL would outlive it; a validity of 15 minutes
/// or more is honoured only with <see cref="SignedUrlOptions.AllowShorterValidity"/>, and the URL's
/// <see cref="SignedUrl.ExpiresOn"/> is 15 minutes after the call. The URL cannot set a download file name or a content
/// type.
/// </para>
/// </remarks>
public sealed partial class WriteableBoxFileProvider : WriteableFileProvider<BoxFileProviderOptions>
{
    /// <summary>The lifetime Box gives its download URLs, "15 minutes in most cases".</summary>
    internal static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(15);

    private const int PageSize = 1000;
    private const int MaxBlockedAttempts = 3;
    private static readonly TimeSpan _maxRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly string[] _itemFields = ["type", "id", "name", "etag", "size", "modified_at", "content_modified_at", "file_version"];

    private readonly IBoxClient _client;
    private readonly string _rootFolderId;

    /// <summary>
    /// Creates a provider that signs in with the app's JWT credentials in <paramref name="options"/>, on its first call.
    /// It does no network I/O here; it reads the private key, so a key or passphrase that is wrong fails now. A sign-in
    /// that Box refuses throws <see cref="UnauthorizedAccessException"/>, with the SDK's <see cref="BoxApiException"/>
    /// inside, without the request it sent.
    /// </summary>
    /// <param name="options">The credentials and the root folder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A credential is missing, or the private key cannot be read with the passphrase.</exception>
    public WriteableBoxFileProvider(BoxFileProviderOptions options)
        : this(options, CreateClient(options, new NetworkSession()))
    {
    }

    /// <summary>
    /// Creates a provider over a ready Box client, such as one built with another authentication, a proxy, custom retries
    /// or as-user headers. Only <see cref="BoxFileProviderOptions.RootFolderId"/> and the root folder are read from
    /// <paramref name="options"/>. The provider never disposes the client: the caller owns it. A client that is exactly a
    /// <see cref="BoxClient"/> is used through a copy that shares its authentication and its session's settings (base URLs,
    /// extra and as-user headers, retries, proxy, timeouts), with the session's network client wrapped so the errors carry
    /// no credentials. Any other <see cref="IBoxClient"/>, a class derived from <see cref="BoxClient"/> included, is used as
    /// it is, and its errors are its own: they may carry its credentials.
    /// </summary>
    /// <param name="options">The root folder.</param>
    /// <param name="client">The Box client, such as a <see cref="BoxClient"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="client"/> is null.</exception>
    public WriteableBoxFileProvider(BoxFileProviderOptions options, IBoxClient client)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client.GetType() == typeof(BoxClient) && client.NetworkSession.NetworkClient is not RedactingNetworkClient
            ? new BoxClient(client.Auth, client.NetworkSession.WithNetworkClient(new RedactingNetworkClient(client.NetworkSession.NetworkClient)))
            : client;
        _rootFolderId = string.IsNullOrWhiteSpace(Options.RootFolderId) ? "0" : Options.RootFolderId;
    }

    /// <summary>
    /// For the tests: the options' JWT sign-in, through the network session given (a fake Box behind a custom network
    /// client), so the options-to-JWT path and the token endpoint are exercised.
    /// </summary>
    internal WriteableBoxFileProvider(BoxFileProviderOptions options, NetworkSession networkSession)
        : this(options, CreateClient(options, networkSession))
    {
    }

    /// <summary>Uploads at or above this size go through a chunked upload session; the tests lower it.</summary>
    internal long ChunkedUploadThreshold { get; set; } = 20L * 1024 * 1024;

    /// <summary>How long to wait before asking again when Box is still processing an upload or a download.</summary>
    internal TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait for Box to make a download ready; an upload waits this long plus as long again for every whole GiB.
    /// </summary>
    internal TimeSpan NotReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether writes send <see cref="UpdateFileOptions.IfMatch"/> to Box as an <c>If-Match</c> header, which makes the
    /// condition atomic. Off until a live run confirms that Box matches the quoted ETag the SDK sends; until then the base
    /// class checks the condition just before the write.
    /// </summary>
    internal bool SendIfMatch { get; set; }

    /// <inheritdoc />
    protected override bool SupportsAtomicPreconditions => SendIfMatch;

    /// <inheritdoc />
    protected override bool IsCaseInsensitive => true;

    /// <inheritdoc />
    protected override bool SupportsSignedUrl(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    private NetworkSession Session => _client.NetworkSession;

    /// <inheritdoc />
    protected override async Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var entry = await FindAsync(path.AbsoluteSegments, cancellationToken).ConfigureAwait(false);
        return entry switch
        {
            null => WriteableFileInfo.Missing(this, path.Path),
            { IsFolder: true } => WriteableFileInfo.Folder(this, path.Path),
            _ => Info(path.Path, entry),
        };
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var folder = await FindAsync(path.AbsoluteSegments, cancellationToken).ConfigureAwait(false);
        if (folder is not { IsFolder: true })
            yield break;
        await foreach (var entry in ListAsync(folder.Id, cancellationToken).ConfigureAwait(false))
        {
            if (IsAddressable(entry.Name))
                yield return EntryInfo(path.Child(entry.Name), entry);
        }
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateRecursiveCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Walks by folder id within this one enumeration, rather than resolving every folder's path from the root again.
        ArgumentNullException.ThrowIfNull(path);
        var root = await FindAsync(path.AbsoluteSegments, cancellationToken).ConfigureAwait(false);
        if (root is not { IsFolder: true })
            yield break;
        var folders = new Queue<(ResolvedPath Path, string Id)>();
        folders.Enqueue((path, root.Id));
        while (folders.TryDequeue(out var folder))
        {
            await foreach (var entry in ListAsync(folder.Id, cancellationToken).ConfigureAwait(false))
            {
                if (!IsAddressable(entry.Name))
                    continue;
                var child = folder.Path.Child(entry.Name);
                yield return EntryInfo(child, entry);
                if (entry.IsFolder)
                    folders.Enqueue((child, entry.Id));
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var file = await FindFileAsync(path, cancellationToken).ConfigureAwait(false);
        return await DownloadAsync(path, file, 0, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        var file = await FindFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (ifMatch is not null && !string.Equals(file.ETag, ifMatch, StringComparison.Ordinal))
            throw Changed(path, ifMatch);
        if (length == 0 || offset >= file.Length)
            return new MemoryStream([], writable: false);
        return await DownloadAsync(path, file, offset, length, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var entry = await FindAsync(path.AbsoluteSegments, cancellationToken).ConfigureAwait(false);
        if (entry is null)
            return false;
        try
        {
            if (entry.IsFolder)
            {
                await RetryBlockedAsync(token => _client.Folders.DeleteFolderByIdAsync(entry.Id,
                    new DeleteFolderByIdQueryParams { Recursive = true }, cancellationToken: token), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RetryBlockedAsync(token => _client.Files.DeleteFileByIdAsync(entry.Id, cancellationToken: token), cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (BoxApiException e) when (e.ResponseInfo.StatusCode == 404)
        {
            // Deleted by someone else since it was found.
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var file = await FindFileAsync(source, cancellationToken).ConfigureAwait(false);
        var parentId = await EnsureFolderAsync(destination, cancellationToken).ConfigureAwait(false);
        try
        {
            await RenameAsync(file.Id, destination.Name, parentId, cancellationToken).ConfigureAwait(false);
        }
        catch (BoxApiException e) when (IsNameInUse(e) && IsCaseOnlyRename(source, destination))
        {
            // Box may count the file's own name as taken: rename through a temporary name.
            try
            {
                await RenameThroughTemporaryNameAsync(file.Id, source, destination, parentId, cancellationToken).ConfigureAwait(false);
            }
            catch (BoxApiException refused) when (IsNameRefused(refused))
            {
                throw NameRefused(destination, refused);
            }
            catch (BoxApiException missing) when (missing.ResponseInfo.StatusCode == 404)
            {
                throw await GoneAsync("Moving", source, destination, missing, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (BoxApiException e) when (IsNameInUse(e))
        {
            throw new FileExistsException($"The destination '{destination}' already exists.", e);
        }
        catch (BoxApiException e) when (IsNameRefused(e))
        {
            throw NameRefused(destination, e);
        }
        catch (BoxApiException e) when (e.ResponseInfo.StatusCode == 404)
        {
            throw await GoneAsync("Moving", source, destination, e, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var file = await FindFileAsync(source, cancellationToken).ConfigureAwait(false);
        var parentId = await EnsureFolderAsync(destination, cancellationToken).ConfigureAwait(false);
        try
        {
            // Copied on the server, from the version that was found.
            await RetryBlockedAsync(token => _client.Files.CopyFileAsync(file.Id,
                new CopyFileRequestBody(new CopyFileRequestBodyParentField(parentId)) { Name = destination.Name, Version = file.VersionId },
                cancellationToken: token), cancellationToken).ConfigureAwait(false);
        }
        catch (BoxApiException e) when (IsNameInUse(e))
        {
            throw new FileExistsException($"The destination '{destination}' already exists.", e);
        }
        catch (BoxApiException e) when (IsNameRefused(e))
        {
            throw NameRefused(destination, e);
        }
        catch (BoxApiException e) when (e.ResponseInfo.StatusCode == 404)
        {
            throw await GoneAsync("Copying", source, destination, e, cancellationToken).ConfigureAwait(false);
        }
    }

    // Box answered 404 to a move or copy of a file it had found. The file may have been deleted or moved meanwhile, or
    // the destination folder removed, or (for a copy) the version found deleted: asking again for the source tells
    // "not found" from the rest.
    private async Task<IOException> GoneAsync(string what, ResolvedPath source, ResolvedPath destination, BoxApiException e, CancellationToken cancellationToken)
    {
        var now = await FindAsync(source.AbsoluteSegments, cancellationToken).ConfigureAwait(false);
        return now is { IsFolder: false }
            ? new IOException($"{what} '{source}' to '{destination}' failed: the source changed or the destination folder was removed meanwhile.", e)
            : NotFound(source, e);
    }

    /// <inheritdoc />
    protected override async Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Box sets the lifetime. A URL never outlives what was asked, and lives shorter only when the caller accepts
        // it.
        if (options.Validity < DownloadUrlLifetime)
            throw new NotSupportedException($"Box's download URLs live about {DownloadUrlLifetime.TotalMinutes} minutes, longer than the {options.Validity} asked.");
        if (!options.AllowShorterValidity)
            throw new NotSupportedException($"Box's download URLs live about {DownloadUrlLifetime.TotalMinutes} minutes, shorter than the {options.Validity} asked; set AllowShorterValidity to accept one.");
        var issued = TimeProvider.GetUtcNow();
        var file = await FindFileAsync(path, cancellationToken).ConfigureAwait(false);
        var url = await GetDownloadUrlAsync(path, file, cancellationToken).ConfigureAwait(false);
        return new SignedUrl
        {
            Url = new Uri(url),
            Method = "GET",
            ExpiresOn = issued + DownloadUrlLifetime,
            Kind = SignedUrlKind.PreAuthenticated,
        };
    }

    /// <inheritdoc />
    protected override bool IsNotFound(Exception exception) => exception is BoxApiException { ResponseInfo.StatusCode: 404 };

    /// <inheritdoc />
    /// <remarks>
    /// Box's 401 and 403, and its token endpoint refusing the app's credentials (a 400 to <c>/oauth2/token</c>, such as
    /// <c>invalid_client</c> or <c>invalid_grant</c>, which is how OAuth says 401).
    /// </remarks>
    protected override bool IsAccessDenied(Exception exception) =>
        exception is BoxApiException { ResponseInfo.StatusCode: 401 or 403 }
        || (exception is BoxApiException { ResponseInfo.StatusCode: 400 } refused && IsTokenRequest(refused));

    private static bool IsTokenRequest(BoxApiException exception) =>
        Uri.TryCreate(exception.RequestInfo?.Url, UriKind.Absolute, out var url)
        && url.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal);

    /// <summary>Turns the escaped line breaks of a key pasted into JSON or an environment variable into real ones.</summary>
    internal static string NormalizePrivateKey(string pem) =>
        !pem.Contains('\\', StringComparison.Ordinal)
            ? pem
            : pem.Replace("\\r\\n", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\n", StringComparison.Ordinal);

    private static BoxClient CreateClient(BoxFileProviderOptions options, NetworkSession networkSession)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (value, name) in new[]
        {
            (options.ClientId, nameof(options.ClientId)),
            (options.ClientSecret, nameof(options.ClientSecret)),
            (options.EnterpriseId, nameof(options.EnterpriseId)),
            (options.PublicKeyId, nameof(options.PublicKeyId)),
            (options.PrivateKey, nameof(options.PrivateKey)),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"The Box option {name} is required.", nameof(options));
        }
        var config = new JwtConfig(
            clientId: options.ClientId!,
            clientSecret: options.ClientSecret!,
            jwtKeyId: options.PublicKeyId!,
            privateKey: NormalizePrivateKey(options.PrivateKey!),
            privateKeyPassphrase: options.Passphrase ?? "")
        {
            EnterpriseId = options.EnterpriseId,
        };
        try
        {
            // The SDK reads the key only when it first signs in; read it now, so a wrong key or passphrase fails here.
            config.PrivateKeyDecryptor.DecryptPrivateKey(config.PrivateKey, config.PrivateKeyPassphrase).Dispose();
        }
        catch (Exception e)
        {
            throw new ArgumentException("The Box PrivateKey cannot be read: it must be a PEM private key, and Passphrase must be its passphrase.", nameof(options), e);
        }
        return new BoxClient(new BoxJwtAuth(config), networkSession);
    }

    private IWriteableFileInfo EntryInfo(ResolvedPath path, Entry entry) =>
        entry.IsFolder ? WriteableFileInfo.Folder(this, path.Path) : Info(path.Path, entry);

    private WriteableFileInfo Info(ProviderPath path, Entry file) =>
        new(this, path, true, false, file.Length, file.LastModified ?? DateTimeOffset.UnixEpoch, etag: file.ETag);

    // Walks the segments from the root folder; null when a segment is missing, or a file is in the way.
    private async Task<Entry?> FindAsync(string[] segments, CancellationToken cancellationToken)
    {
        var current = new Entry(_rootFolderId, true, "", -1, null, null, null);
        foreach (var segment in segments)
        {
            if (!current.IsFolder)
                return null;
            var child = await FindChildAsync(current.Id, segment, cancellationToken).ConfigureAwait(false);
            if (child is null)
                return null;
            current = child;
        }
        return current;
    }

    private async Task<Entry> FindFileAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var entry = await FindAsync(path.AbsoluteSegments, cancellationToken).ConfigureAwait(false);
        return entry is { IsFolder: false } ? entry : throw NotFound(path);
    }

    // Box has no lookup by name: walk the folder's listing. Names are unique without regard to case.
    private async Task<Entry?> FindChildAsync(string folderId, string name, CancellationToken cancellationToken)
    {
        await foreach (var entry in ListAsync(folderId, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    // The files and folders of a folder, with marker paging; web links are neither, and are skipped. A folder that is gone
    // lists empty, except the root folder, which is a configuration error.
    private async IAsyncEnumerable<Entry> ListAsync(string folderId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        var markers = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            Items? page;
            try
            {
                page = await _client.Folders.GetFolderItemsAsync(folderId,
                    new GetFolderItemsQueryParams { Fields = _itemFields, Usemarker = true, Marker = marker, Limit = PageSize },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (BoxApiException e) when (e.ResponseInfo.StatusCode == 404)
            {
                if (folderId == _rootFolderId)
                    throw new InvalidOperationException($"The Box folder '{folderId}' (RootFolderId) does not exist, or the app cannot see it.", e);
                page = null;
            }
            if (page is null)
                yield break;
            foreach (var item in page.Entries ?? [])
            {
                if (Entry.From(item) is { } entry)
                    yield return entry;
            }
            marker = page.NextMarker;
            if (!string.IsNullOrEmpty(marker) && !markers.Add(marker))
                throw new IOException($"Box sent the same page marker twice while listing folder '{folderId}'.");
        }
        while (!string.IsNullOrEmpty(marker));
    }

    // Finds or creates the parent folders of the path; returns the id of its parent.
    private async Task<string> EnsureFolderAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var segments = path.AbsoluteSegments[..^1];
        var folderId = _rootFolderId;
        foreach (var segment in segments)
        {
            var parentId = folderId;
            var child = await FindChildAsync(parentId, segment, cancellationToken).ConfigureAwait(false);
            if (child is null)
            {
                try
                {
                    var created = await RetryBlockedAsync(token => _client.Folders.CreateFolderAsync(
                        new CreateFolderRequestBody(segment, new CreateFolderRequestBodyParentField(parentId)), cancellationToken: token),
                        cancellationToken).ConfigureAwait(false);
                    folderId = created.Id;
                    continue;
                }
                catch (BoxApiException e) when (IsNameInUse(e))
                {
                    // Created by someone else meanwhile, or the name is a file's.
                    child = await FindChildAsync(parentId, segment, cancellationToken).ConfigureAwait(false);
                }
                catch (BoxApiException e) when (IsNameRefused(e))
                {
                    throw NameRefused(path, e);
                }
            }
            if (child is null)
                throw new IOException($"The parent folders of '{path}' cannot be created: Box says the name '{segment}' is taken, by an item that is neither a file nor a folder, or that is gone again.");
            if (!child.IsFolder)
                throw new IOException($"The parent folders of '{path}' cannot be created: '{segment}' is a file.");
            folderId = child.Id;
        }
        return folderId;
    }

    private async Task RenameAsync(string fileId, string name, string parentId, CancellationToken cancellationToken) =>
        await RetryBlockedAsync(token => _client.Files.UpdateFileByIdAsync(fileId,
            new UpdateFileByIdRequestBody { Name = name, Parent = new UpdateFileByIdRequestBodyParentField { Id = parentId } },
            cancellationToken: token), cancellationToken).ConfigureAwait(false);

    private async Task RenameThroughTemporaryNameAsync(string fileId, ResolvedPath source, ResolvedPath destination, string parentId, CancellationToken cancellationToken)
    {
        // Short whatever the file's name, so it never exceeds Box's 255 characters.
        var temporary = $".wfp-rename-{Guid.NewGuid():N}.tmp";
        await RenameAsync(fileId, temporary, parentId, cancellationToken).ConfigureAwait(false);
        try
        {
            await RenameAsync(fileId, destination.Name, parentId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            try
            {
                await RenameAsync(fileId, source.Name, parentId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception restoreFailure)
            {
                throw new IOException($"Renaming '{source}' to '{destination}' failed, and so did putting it back; the file is named '{temporary}'.",
                    new AggregateException(failure, restoreFailure));
            }
            throw;
        }
    }

    // Box answers 409 operation_blocked_temporary while another operation holds the item: wait and try again, a few times.
    private Task RetryBlockedAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken) =>
        RetryBlockedAsync<bool>(async token =>
        {
            await call(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<T> RetryBlockedAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(cancellationToken).ConfigureAwait(false);
            }
            catch (BoxApiException e) when (attempt < MaxBlockedAttempts && e.ResponseInfo is { StatusCode: 409, Code: "operation_blocked_temporary" })
            {
                await Task.Delay(PollDelay * attempt, TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsCaseOnlyRename(ResolvedPath source, ResolvedPath destination) =>
        source.Path != destination.Path && string.Equals(source.ToString(), destination.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsNameInUse(BoxApiException e) => e.ResponseInfo is { StatusCode: 409, Code: "item_name_in_use" };

    private static bool IsNameRefused(BoxApiException e) =>
        e.ResponseInfo is { StatusCode: 400, Code: "item_name_invalid" or "item_name_too_long" };

    private static ArgumentException NameRefused(ResolvedPath path, BoxApiException e) =>
        new($"Box refused a name in '{path}': {e.ResponseInfo.Code}.", nameof(path), e);

    private static PreconditionFailedException Changed(ResolvedPath path, string ifMatch, Exception? innerException = null) =>
        new($"The file '{path}' changed: its version is no longer '{ifMatch}'.", innerException);

    /// <summary>A file or folder found by walking a path.</summary>
    /// <param name="Id">The item's id.</param>
    /// <param name="IsFolder">Whether it is a folder.</param>
    /// <param name="Name">Its name, as Box spells it.</param>
    /// <param name="Length">A file's size; -1 for a folder.</param>
    /// <param name="LastModified">When a file last changed, if Box says.</param>
    /// <param name="BoxETag">Box's own <c>etag</c>: it counts the versions of this item, so another item can have the same.</param>
    /// <param name="VersionId">The id of a file's current version.</param>
    private sealed record Entry(string Id, bool IsFolder, string Name, long Length, DateTimeOffset? LastModified, string? BoxETag, string? VersionId)
    {
        /// <summary>
        /// The ETag the provider exposes: the id of the file's current version, which no other version of any file shares,
        /// or, when Box does not say, the item's id with Box's etag. Null for a folder.
        /// </summary>
        public string? ETag => IsFolder ? null : VersionId ?? (BoxETag is null ? null : $"{Id}:{BoxETag}");

        public static Entry? From(Item item) => item switch
        {
            { FileFull: { } file } => new Entry(file.Id, false, file.Name ?? "", file.Size ?? 0, file.ModifiedAt ?? file.ContentModifiedAt, file.Etag, file.FileVersion?.Id),
            { FolderMini: { } folder } => new Entry(folder.Id, true, folder.Name ?? "", -1, null, folder.Etag, null),
            _ => null,
        };
    }
}
