using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Web;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;
using Google.Apis.Storage.v1;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace WriteableFileProviders.GoogleCloudStorage;

/// <summary>
/// A writeable file provider over a Google Cloud Storage bucket. Object names are paths: the provider shows the
/// <c>/</c> in them as folders, a folder exists while some object has its prefix, and a file and a folder may share a
/// name.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Versions:</b> a file's <see cref="IWriteableFileInfo.ETag"/> is its generation. <see cref="UpdateFileOptions.IfMatch"/>
/// and <see cref="UpdateFileOptions.IfNotExists"/> are generation preconditions that Cloud Storage checks atomically, and
/// an append, which rewrites the object, is conditional on the generation it read, so it never loses a concurrent write.
/// </description></item>
/// <item><description>
/// <b>Reads</b> stream the object as it downloads; ranged and seekable reads fetch only the bytes asked for, pinned to
/// the generation they started from. The download is not checked against the object's hash.
/// </description></item>
/// <item><description>
/// <b>Move and copy</b> copy on the server, only if the destination does not exist; a move then deletes exactly the
/// generation it copied, and puts things back if the source was written meanwhile. The copy is not retried: after one
/// that fails with a server error or a timeout, the copy may still have landed, so check the destination before trying
/// again.
/// </description></item>
/// <item><description>
/// <b>Signed URLs</b> are V4 signed URLs, for read, create and write, valid from one second to seven days. A credential
/// that cannot sign, or an identity whose signing through IAM's <c>signBlob</c> is refused (403), answers another error
/// than 401 or cannot be reached, throws <see cref="NotSupportedException"/>, so WriteableFileProviders.AspNetCore
/// issues a gateway URL instead; a refused credential and a timeout fail as on every other member
/// (<see cref="UnauthorizedAccessException"/>, <see cref="IOException"/>).
/// </description></item>
/// <item><description>
/// <b>Credentials</b> are resolved on the first call, never in a constructor; see
/// <see cref="GoogleCloudStorageFileProviderOptions"/>. Without a key in the options or a client of yours, the first
/// call looks up Application Default Credentials, and where there are none it throws
/// <see cref="InvalidOperationException"/>, as for a store that is not set up.
/// </description></item>
/// </list>
/// </remarks>
public sealed class WriteableGoogleCloudStorageFileProvider : WriteableFileProvider<GoogleCloudStorageFileProviderOptions>
{
    private const string DefaultContentType = "application/octet-stream";
    private const string IfGenerationMatchHeader = "x-goog-if-generation-match";
    private const int DeletePageSize = 1000;
    private const int DeleteParallelism = 8;

    private readonly Lock _stateLock = new();
    private readonly string _bucket;
    private readonly StorageClient? _givenClient;
    private readonly UrlSigner? _givenClientSigner;
    private readonly GoogleCredential? _credential;
    private readonly string? _baseUri;
    private Task<State>? _state;
    private bool _disposed;

    /// <summary>
    /// Creates the provider from its options, as <see cref="WriteableFileProviderSerializer"/> does. It does no network
    /// I/O: the client is built, and Application Default Credentials are looked up, on the first call.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentException">
    /// The bucket is missing; <see cref="GoogleCloudStorageFileProviderOptions.Endpoint"/> is not a root URL, or is plain
    /// <c>http</c> toward a host that is not loopback without
    /// <see cref="GoogleCloudStorageFileProviderOptions.AllowInsecureHttp"/>; or the service account key cannot be read.
    /// </exception>
    public WriteableGoogleCloudStorageFileProvider(GoogleCloudStorageFileProviderOptions options) : base(options)
    {
        _bucket = RequireBucket(Options);
        _baseUri = BaseUriFor(Options);
        _credential = CredentialFrom(Options);
    }

    /// <summary>
    /// Creates the provider over a client the caller built, for credentials, endpoints, retries or HTTP handling the
    /// options do not cover. The provider never disposes the client. Of the options, only the bucket, the root,
    /// <see cref="GoogleCloudStorageFileProviderOptions.CreateBucketIfMissing"/> and
    /// <see cref="GoogleCloudStorageFileProviderOptions.ProjectId"/> are used; the credential properties and the endpoint
    /// are the client's own. Signed URLs need a client whose credential can sign.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="client">The client; the caller keeps it and disposes it.</param>
    /// <exception cref="ArgumentException">The bucket is missing.</exception>
    public WriteableGoogleCloudStorageFileProvider(GoogleCloudStorageFileProviderOptions options, StorageClient client) : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _bucket = RequireBucket(Options);
        _givenClient = client;
        _givenClientSigner = SignerOf(client);
    }

    /// <summary>Cloud Storage checks generation preconditions atomically.</summary>
    protected override bool SupportsAtomicPreconditions => true;

    /// <inheritdoc />
    /// <remarks>
    /// False only for a client given to the constructor whose credential cannot sign; a credential found on the first
    /// call (Application Default Credentials) is checked when the URL is signed, and throws
    /// <see cref="NotSupportedException"/> there if it cannot sign.
    /// </remarks>
    protected override bool SupportsSignedUrl(SignedUrlAccess access) => _givenClient is null || _givenClientSigner is not null;

    /// <summary>A read URL carries the download name and content type; an upload URL carries the content type.</summary>
    /// <param name="access">The access.</param>
    protected override bool SupportsSignedUrlContentHeaders(SignedUrlAccess access) => true;

    /// <summary>
    /// Cloud Storage's 401 and 403, and Google's token endpoint refusing the credential (a 400, 401 or 403 answer to the
    /// token request, such as <c>invalid_grant</c> for a key that was deleted): the public members turn them into an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    /// <param name="exception">The store's exception.</param>
    protected override bool IsAccessDenied(Exception exception) =>
        exception is GoogleApiException { HttpStatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            or TokenResponseException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    /// <inheritdoc />
    protected override async Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (await GetObjectOrNullAsync(state, Key(path), cancellationToken).ConfigureAwait(false) is { } item)
            return ToInfo(path.Path, item);
        return await HasObjectsUnderAsync(state, path, cancellationToken).ConfigureAwait(false)
            ? WriteableFileInfo.Folder(this, path.Path)
            : WriteableFileInfo.Missing(this, path.Path);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        var prefix = FolderPrefix(path);
        // The raw pages: the flattened listing drops the prefixes, which are the folders.
        var pages = state.Client.ListObjectsAsync(_bucket, prefix, new ListObjectsOptions { Delimiter = "/" }).AsRawResponses().GetAsyncEnumerator(cancellationToken);
        await using (pages.ConfigureAwait(false))
        {
            while (await NextPageAsync(pages).ConfigureAwait(false) is { } page)
            {
                foreach (var folder in page.Prefixes ?? [])
                {
                    if (EntryName(folder[prefix.Length..^1]) is { } name)
                        yield return WriteableFileInfo.Folder(this, path.Child(name).Path);
                }
                foreach (var item in page.Items ?? [])
                {
                    if (EntryName(item.Name[prefix.Length..]) is { } name)
                        yield return ToInfo(path.Child(name).Path, item);
                }
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> FolderExistsCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        return await HasObjectsUnderAsync(state, path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        return await DownloadAsync(state, path, 0, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a byte range through one ranged download. With <paramref name="ifMatch"/>, the download names that
    /// generation and is conditional on it, so a file that changed throws <see cref="PreconditionFailedException"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="offset">The first byte.</param>
    /// <param name="length">How many bytes, or null to the end.</param>
    /// <param name="ifMatch">The generation the file must have, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        long? generation = null;
        if (ifMatch is not null)
            generation = ParseGeneration(ifMatch) ?? throw Changed(path, ifMatch);
        if (length == 0)
        {
            // Nothing to download, but the file must exist, at the version asked for.
            var item = await GetObjectOrNullAsync(state, Key(path), cancellationToken).ConfigureAwait(false)
                ?? throw await NotFoundAsync(state, path, null, cancellationToken).ConfigureAwait(false);
            if (generation is not null && item.Generation != generation)
                throw Changed(path, ifMatch!);
            return Stream.Null;
        }
        return await DownloadAsync(state, path, offset, length, generation, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == UpdateFileMode.Append)
        {
            // No native append: rewrite the object, conditional on the generation read, so a concurrent write is never
            // lost.
            await AppendByRewriteAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
            return;
        }
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        long? ifGenerationMatch = options.IfNotExists ? 0 : null;
        if (options.IfMatch is { } ifMatch)
            ifGenerationMatch = ParseGeneration(ifMatch) ?? throw Changed(path, ifMatch);
        var destination = new StorageObject { Bucket = _bucket, Name = Key(path), ContentType = options.ContentType ?? DefaultContentType };
        // The upload is resumable: Cloud Storage creates the object only when every byte arrived, so a source that fails
        // leaves the old content. The caller's stream is read forward and left open.
        var source = new ForwardReadStream(contents);
        await using (source.ConfigureAwait(false))
        {
            try
            {
                await state.Client.UploadObjectAsync(destination, source, new UploadObjectOptions { IfGenerationMatch = ifGenerationMatch }, cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw options.IfNotExists ? new FileAlreadyExistsException($"The file '{path}' already exists.", e) : Changed(path, options.IfMatch!, e);
            }
            catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
            {
                if (await BucketMissingAsync(state.Client, cancellationToken).ConfigureAwait(false) is not null)
                    throw MissingBucket(e);
                throw;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await state.Client.DeleteObjectAsync(_bucket, Key(path), null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Not a file: a folder, or nothing.
        }
        // Every object under the folder's prefix, with its trailing '/', so a sibling such as 'ab.txt' is never touched.
        var deleted = false;
        var pages = state.Client.ListObjectsAsync(_bucket, FolderPrefix(path), new ListObjectsOptions { PageSize = DeletePageSize }).AsRawResponses().GetAsyncEnumerator(cancellationToken);
        await using (pages.ConfigureAwait(false))
        {
            while (await NextPageAsync(pages).ConfigureAwait(false) is { } page)
            {
                if (page.Items is not { Count: > 0 } items)
                    continue;
                deleted = true;
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = DeleteParallelism, CancellationToken = cancellationToken };
                await Parallel.ForEachAsync(items, parallel, async (item, token) =>
                {
                    try
                    {
                        await state.Client.DeleteObjectAsync(_bucket, item.Name, null, token).ConfigureAwait(false);
                    }
                    catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
                    {
                        // Deleted meanwhile.
                    }
                }).ConfigureAwait(false);
            }
        }
        return deleted;
    }

    /// <summary>
    /// Copies on the server, only if the destination does not exist (<c>ifGenerationMatch=0</c>), so a destination
    /// written after the base class checked it throws <see cref="FileExistsException"/> and is left unchanged. The copy
    /// is not retried: when it fails with a server error or a timeout, it may still have landed, so the destination may
    /// already hold it.
    /// </summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        await CopyAsync(state, source, destination, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves by a server copy of the source's current generation, only if the destination does not exist, then deletes
    /// exactly that generation of the source. If the source was written in between, the newer content stays, the copy is
    /// removed, and the move throws <see cref="PreconditionFailedException"/>. When the copy fails with a server error or
    /// a timeout, it is not retried and may still have landed: the destination may then hold the copy while the source
    /// is kept.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="cancellationToken">Cancels the call; once the copy is made, the source is deleted regardless.</param>
    protected override async Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        var item = await GetObjectOrNullAsync(state, Key(source), cancellationToken).ConfigureAwait(false)
            ?? throw await NotFoundAsync(state, source, null, cancellationToken).ConfigureAwait(false);
        var copy = await CopyAsync(state, source, destination, item.Generation, cancellationToken).ConfigureAwait(false);
        try
        {
            // The generation names the version (the emulator honours only this); the precondition makes it atomic.
            var exact = new DeleteObjectOptions { Generation = item.Generation, IfGenerationMatch = item.Generation };
            await state.Client.DeleteObjectAsync(_bucket, Key(source), exact, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            // Deleted meanwhile: the move is complete. Written meanwhile: keep that write and undo the copy.
            if (await GetObjectOrNullAsync(state, Key(source), CancellationToken.None).ConfigureAwait(false) is null)
                return;
            await DeleteGenerationAsync(state, Key(destination), copy.Generation).ConfigureAwait(false);
            throw new PreconditionFailedException($"The file '{source}' was written while it was moved: the newer content stays, and nothing was moved.", e);
        }
    }

    /// <summary>
    /// Issues a V4 signed URL. A read URL carries the download name and content type as response overrides; a create URL
    /// signs <c>x-goog-if-generation-match: 0</c>, so it uploads only a file that does not exist yet; an upload URL with a
    /// content type signs it. The request must send every header in <see cref="SignedUrl.Headers"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="NotSupportedException">
    /// The credential cannot sign; IAM's <c>signBlob</c> refused the signing (403) or answered another error than 401,
    /// or could not be reached (the cause is the <see cref="Exception.InnerException"/>); the validity is under one
    /// second; or a download name was asked for an upload URL. A refused credential (401, a token error) and a timeout
    /// are not turned into this exception.
    /// </exception>
    protected override async Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        var signer = state.Signer ?? throw new NotSupportedException(
            $"{GetType().Name} cannot sign URLs: its credential cannot sign. Use a service account key, or an identity that holds iam.serviceAccounts.signBlob.");
        // V4 URLs live whole seconds: rounding down keeps the URL within the validity asked.
        var seconds = Math.Floor(options.Validity.TotalSeconds);
        if (seconds < 1)
            throw new NotSupportedException($"Cloud Storage signs URLs that live at least one second; {options.Validity} was asked.");
        var read = options.Access == SignedUrlAccess.Read;
        if (!read && options.DownloadFileName is not null)
            throw new NotSupportedException("A download file name applies to read URLs only.");

        var template = UrlSigner.RequestTemplate.FromBucket(_bucket).WithObjectName(Key(path)).WithHttpMethod(read ? HttpMethod.Get : HttpMethod.Put);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (read)
        {
            var query = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
            if (options.DownloadFileName is { } fileName)
                query["response-content-disposition"] = [ContentDisposition(fileName)];
            if (options.ContentType is { } contentType)
                query["response-content-type"] = [contentType];
            if (query.Count > 0)
                template = template.WithQueryParameters(query);
        }
        else
        {
            if (options.Access == SignedUrlAccess.Create)
            {
                headers[IfGenerationMatchHeader] = "0";
                template = template.WithRequestHeaders(new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal) { [IfGenerationMatchHeader] = ["0"] });
            }
            if (options.ContentType is { } contentType)
            {
                headers["Content-Type"] = contentType;
                template = template.WithContentHeaders(new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal) { ["Content-Type"] = [contentType] });
            }
        }
        string signed;
        try
        {
            signed = await signer.SignAsync(template, UrlSigner.Options.FromDuration(TimeSpan.FromSeconds(seconds)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException || (e is GoogleApiException api && api.HttpStatusCode != HttpStatusCode.Unauthorized))
        {
            // An identity that signs through IAM's signBlob (Compute Engine, Cloud Run, Workload Identity,
            // impersonation) may be refused it (403, without iam.serviceAccounts.signBlob), get another error answer,
            // or not reach it: the URL cannot be signed natively, so the gateway can serve it. A refused credential
            // (401, or a token error) and a timeout are not this; they fail as on every other member.
            throw new NotSupportedException(
                $"{GetType().Name} could not sign the URL: IAM's signBlob refused it or could not be reached. Grant iam.serviceAccounts.signBlob, or use a service account key.", e);
        }
        var url = new Uri(signed);
        return new SignedUrl
        {
            Url = url,
            Method = read ? "GET" : "PUT",
            // The SDK signs with its own clock and offers no way to give it another, so the URL lives the validity on
            // Cloud Storage's clock whatever the provider's says. ExpiresOn is read from the URL, or, where the caller
            // gave the provider a clock, is that clock's now plus the validity, as on S3.
            ExpiresOn = ReferenceEquals(TimeProvider, TimeProvider.System) ? ExpiryOf(url) : TimeProvider.GetUtcNow().AddSeconds(seconds),
            Headers = headers,
            Kind = SignedUrlKind.Presigned,
        };
    }

    /// <summary>Disposes the client the provider built; a client given to the constructor stays the caller's.</summary>
    /// <param name="disposing">True from <see cref="WriteableFileProvider{TOptions}.Dispose()"/>.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Task<State>? state;
            lock (_stateLock)
            {
                _disposed = true;
                state = _state;
            }
            // A client still being built is disposed when it is ready.
            state?.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.OwnsClient)
                    t.Result.Client.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Turns escaped newlines (a key pasted into an environment variable or a secret store) into real ones. A PEM body
    /// never holds a backslash, so a valid key is returned unchanged.
    /// </summary>
    /// <param name="pem">The key.</param>
    internal static string? NormalizePrivateKey(string? pem) =>
        string.IsNullOrEmpty(pem) || !pem.Contains('\\', StringComparison.Ordinal)
            ? pem
            : pem.Replace("\\r\\n", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\n", StringComparison.Ordinal);

    /// <summary>
    /// The client's base URI for <see cref="GoogleCloudStorageFileProviderOptions.Endpoint"/>: its root followed by
    /// <c>storage/v1/</c>; null for Google's.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentException">
    /// The endpoint is not an http or https root URL, or is plain http toward a host that is not loopback without
    /// <see cref="GoogleCloudStorageFileProviderOptions.AllowInsecureHttp"/>.
    /// </exception>
    internal static string? BaseUriFor(GoogleCloudStorageFileProviderOptions options)
    {
        var endpoint = options.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;
        // The value is never echoed: a URL may carry user information.
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Endpoint must be an http or https URL without user information or a query, such as http://localhost:4443.", nameof(options));
        // The credentials' tokens travel with every request: in clear text only toward this machine, unless allowed.
        if (uri.Scheme == "http" && !uri.IsLoopback && !options.AllowInsecureHttp)
            throw new ArgumentException(
                $"Endpoint uses plain http toward a host that is not loopback, which would send the credentials in clear text: use https, or set {nameof(GoogleCloudStorageFileProviderOptions.AllowInsecureHttp)}.",
                nameof(options));
        // Uploads go to /upload/storage/v1 on the same host whatever the base path, so only the root is meaningful.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 && !path.Equals("/storage/v1", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Endpoint must be the server's root URL, such as http://localhost:4443, or end in /storage/v1/.", nameof(options));
        return uri.GetLeftPart(UriPartial.Authority) + "/storage/v1/";
    }

    private Task<State> GetStateAsync(CancellationToken cancellationToken)
    {
        Task<State> state;
        lock (_stateLock)
        {
            // A call that began before the provider was disposed builds no client that nothing would dispose.
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Built once, by the first call, for every caller; a failed build is tried again by the next call.
            if (_state is null || _state.IsFaulted || _state.IsCanceled)
                _state = CreateStateAsync();
            state = _state;
        }
        return state.WaitAsync(cancellationToken);
    }

    private async Task<State> CreateStateAsync()
    {
        if (_givenClient is { } given)
        {
            await EnsureBucketAsync(given, null).ConfigureAwait(false);
            return new State(given, _givenClientSigner, OwnsClient: false);
        }
        // Application Default Credentials only now, on the first call and asynchronously: the lookup may ask the
        // metadata server, which a constructor must not wait for.
        var credential = _credential ?? await GoogleCredential.GetApplicationDefaultAsync().ConfigureAwait(false);
        var builder = new StorageClientBuilder { GoogleCredential = credential };
        if (_baseUri is not null)
            builder.BaseUri = _baseUri;
        var client = await builder.BuildAsync().ConfigureAwait(false);
        try
        {
            await EnsureBucketAsync(client, credential).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new State(client, SignerOf(client), OwnsClient: true);
    }

    private async Task EnsureBucketAsync(StorageClient client, GoogleCredential? credential)
    {
        if (!Options.CreateBucketIfMissing || await BucketMissingAsync(client, CancellationToken.None).ConfigureAwait(false) is null)
            return;
        var project = !string.IsNullOrEmpty(Options.ProjectId) ? Options.ProjectId : (credential?.UnderlyingCredential as ServiceAccountCredential)?.ProjectId;
        if (string.IsNullOrEmpty(project))
            throw new InvalidOperationException($"The bucket '{_bucket}' does not exist, and creating it needs the project: set {nameof(GoogleCloudStorageFileProviderOptions.ProjectId)}.");
        try
        {
            await client.CreateBucketAsync(project, _bucket).ConfigureAwait(false);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Conflict)
        {
            // Created meanwhile.
        }
    }

    // The error that says the bucket does not exist, or null when it does or when that cannot be told. It lists one
    // object, which needs only storage.objects.list, unlike a bucket lookup; a credential that may read objects but not
    // list them (403) cannot tell, and a missing file then stays a missing file.
    private async Task<GoogleApiException?> BucketMissingAsync(StorageClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.ListObjectsAsync(_bucket, null, new ListObjectsOptions { PageSize = 1 }).ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return e;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private async Task<bool> HasObjectsUnderAsync(State state, ResolvedPath folder, CancellationToken cancellationToken)
    {
        try
        {
            var page = await state.Client.ListObjectsAsync(_bucket, FolderPrefix(folder), new ListObjectsOptions { PageSize = 1 }).ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
            return page.Any();
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw MissingBucket(e);
        }
    }

    private async Task<Objects?> NextPageAsync(IAsyncEnumerator<Objects> pages)
    {
        try
        {
            return await pages.MoveNextAsync().ConfigureAwait(false) ? pages.Current : null;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw MissingBucket(e);
        }
    }

    private async Task<StorageObject?> GetObjectOrNullAsync(State state, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await state.Client.GetObjectAsync(_bucket, key, null, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // A media download through the client's own HTTP client, which carries its credential: the SDK's download writes
    // into a stream the caller provides and checks a whole-object hash, which a range cannot match.
    private async Task<Stream> DownloadAsync(State state, ResolvedPath path, long offset, long? length, long? generation, CancellationToken cancellationToken)
    {
        var service = state.Client.Service;
        var pin = generation is { } g ? string.Create(CultureInfo.InvariantCulture, $"&generation={g}&ifGenerationMatch={g}") : "";
        var uri = new Uri($"{service.BaseUri}b/{Uri.EscapeDataString(_bucket)}/o/{Uri.EscapeDataString(Key(path))}?alt=media{pin}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // A client with a customer-supplied encryption key sends it with every object read, as the SDK's own download does.
        state.Client.EncryptionKey?.ModifyRequest(request);
        var ranged = offset > 0 || length is not null;
        if (ranged)
            request.Headers.Range = new RangeHeaderValue(offset, length is { } count && count <= long.MaxValue - offset ? offset + count - 1 : null);
        var response = await service.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var owned = false;
        try
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK or HttpStatusCode.PartialContent:
                    var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    // A whole object for a ranged request: the server ignored the range (a transcoded object), so the
                    // stream skips to the offset and stops after the length.
                    var ignored = ranged && response.StatusCode == HttpStatusCode.OK;
                    var skip = ignored ? offset : 0;
                    var limit = ignored ? length : null;
                    owned = true;
                    return new SliceReadStream(body, skip, limit, response, KnownLength(response, skip, limit));
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    return Stream.Null;
                case HttpStatusCode.PreconditionFailed when generation is { } pinned:
                    throw Changed(path, pinned.ToString(CultureInfo.InvariantCulture), await ErrorAsync(service, response).ConfigureAwait(false));
                case HttpStatusCode.NotFound:
                    throw await MissingFileAsync(state, path, generation, await ErrorAsync(service, response).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                default:
                    throw await ErrorAsync(service, response).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!owned)
                response.Dispose();
        }
    }

    // How many bytes a download returns, when the response says its length: what is left after the skip, up to the limit.
    private static long? KnownLength(HttpResponseMessage response, long skip, long? limit)
    {
        if (response.Content.Headers.ContentLength is not { } contentLength)
            return null;
        var available = Math.Max(0, contentLength - skip);
        return limit is { } max ? Math.Min(max, available) : available;
    }

    private async Task<StorageObject> CopyAsync(State state, ResolvedPath source, ResolvedPath destination, long? sourceGeneration, CancellationToken cancellationToken)
    {
        // Never retried: a retry after a lost answer would find the copy this call made and report the destination as
        // taken. A failure the SDK would have retried reaches the caller, whose copy may have landed.
        var options = new CopyObjectOptions
        {
            IfGenerationMatch = 0,
            SourceGeneration = sourceGeneration,
            IfSourceGenerationMatch = sourceGeneration,
            RetryOptions = RetryOptions.Never,
        };
        try
        {
            return await state.Client.CopyObjectAsync(_bucket, Key(source), _bucket, Key(destination), options, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // The destination appeared since the base class checked it, or the pinned source changed.
            if (sourceGeneration is null || await GetObjectOrNullAsync(state, Key(destination), cancellationToken).ConfigureAwait(false) is not null)
                throw new FileExistsException($"The destination '{destination}' already exists.", e);
            throw Changed(source, sourceGeneration.Value.ToString(CultureInfo.InvariantCulture), e);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw await MissingFileAsync(state, source, sourceGeneration, e, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteGenerationAsync(State state, string key, long? generation)
    {
        try
        {
            await state.Client.DeleteObjectAsync(_bucket, key, new DeleteObjectOptions { Generation = generation, IfGenerationMatch = generation }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            // Written or deleted by someone else since: theirs to keep.
        }
    }

    // A 404 for a file: the file changed (when pinned to a generation it no longer has), is missing, or the bucket is.
    private async Task<Exception> MissingFileAsync(State state, ResolvedPath path, long? generation, Exception inner, CancellationToken cancellationToken)
    {
        if (generation is { } pinned && await GetObjectOrNullAsync(state, Key(path), cancellationToken).ConfigureAwait(false) is not null)
            return Changed(path, pinned.ToString(CultureInfo.InvariantCulture), inner);
        return await NotFoundAsync(state, path, inner, cancellationToken).ConfigureAwait(false);
    }

    // Cloud Storage answers the same 404 for a missing object and a missing bucket: a list tells them apart, so a
    // missing bucket is reported as such (InvalidOperationException), never as a missing file.
    private async Task<Exception> NotFoundAsync(State state, ResolvedPath path, Exception? inner, CancellationToken cancellationToken) =>
        await BucketMissingAsync(state.Client, cancellationToken).ConfigureAwait(false) is { } missing ? MissingBucket(inner ?? missing) : NotFound(path, inner);

    private InvalidOperationException MissingBucket(Exception inner) =>
        new($"The bucket '{_bucket}' does not exist. Create it, or set {nameof(GoogleCloudStorageFileProviderOptions.CreateBucketIfMissing)} to have the provider create it.", inner);

    private static PreconditionFailedException Changed(ResolvedPath path, string version, Exception? inner = null) =>
        new($"The file '{path}' changed: its version is no longer '{version}'.", inner);

    private static async Task<GoogleApiException> ErrorAsync(StorageService service, HttpResponseMessage response)
    {
        RequestError? error = null;
        try
        {
            error = await service.DeserializeError(response).ConfigureAwait(false);
        }
        catch (GoogleApiException)
        {
            // Not a JSON error body: the status says enough.
        }
        return new GoogleApiException(service.Name, error?.Message is { Length: > 0 } message ? message : $"{(int)response.StatusCode} {response.ReasonPhrase}")
        {
            HttpStatusCode = response.StatusCode,
            Error = error,
        };
    }

    private WriteableFileInfo ToInfo(ProviderPath path, StorageObject item) =>
        new(this, path, exists: true, isDirectory: false, length: checked((long)(item.Size ?? 0)), lastModified: UpdatedOf(item),
            etag: item.Generation?.ToString(CultureInfo.InvariantCulture), contentType: item.ContentType);

    // The SDK's own parse (UpdatedDateTimeOffset) throws on RFC 3339 times with other than 3, 6 or 9 fractional digits,
    // which fake-gcs-server sends; this one takes any.
    private static DateTimeOffset UpdatedOf(StorageObject item) =>
        DateTimeOffset.TryParse(item.UpdatedRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated)
            ? updated
            : DateTimeOffset.UnixEpoch;

    private static string Key(ResolvedPath path) => string.Join('/', path.AbsoluteSegments);

    private static string FolderPrefix(ResolvedPath path) => path.AbsoluteSegments.Length == 0 ? "" : Key(path) + "/";

    // An entry the API can address again (IsAddressable). Objects named otherwise (such as 'a//b' or '..') are left out of
    // listings.
    private string? EntryName(string name) => IsAddressable(name) ? name : null;

    // A generation is positive: 0 would turn "this version" into "no version yet".
    private static long? ParseGeneration(string etag) =>
        long.TryParse(etag, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation > 0 ? generation : null;

    private static DateTimeOffset? ExpiryOf(Uri url)
    {
        var query = HttpUtility.ParseQueryString(url.Query);
        return DateTimeOffset.TryParseExact(query["X-Goog-Date"], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt)
               && long.TryParse(query["X-Goog-Expires"], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? signedAt.AddSeconds(seconds)
            : null;
    }

    // RFC 6266: the UTF-8 name, and an ASCII fallback for clients that do not read filename*.
    private static string ContentDisposition(string fileName)
    {
        var fallback = new string([.. fileName.Select(c => c is >= ' ' and < '\u007f' and not '"' and not '\\' ? c : '_')]);
        return new ContentDispositionHeaderValue("attachment") { FileName = $"\"{fallback}\"", FileNameStar = fileName }.ToString();
    }

    private static string RequireBucket(GoogleCloudStorageFileProviderOptions options) =>
        string.IsNullOrWhiteSpace(options.BucketName)
            ? throw new ArgumentException($"{nameof(GoogleCloudStorageFileProviderOptions.BucketName)} is required.", nameof(options))
            : options.BucketName;

    private static GoogleCredential? CredentialFrom(GoogleCloudStorageFileProviderOptions options)
    {
        // Parsing a key is local work; nothing here touches the network.
        if (!string.IsNullOrEmpty(options.ClientEmail) && !string.IsNullOrEmpty(options.PrivateKey))
        {
            ServiceAccountCredential.Initializer initializer;
            try
            {
                initializer = new ServiceAccountCredential.Initializer(options.ClientEmail) { ProjectId = options.ProjectId }.FromPrivateKey(NormalizePrivateKey(options.PrivateKey));
            }
            catch (Exception e) when (e is FormatException or ArgumentException or CryptographicException)
            {
                throw new ArgumentException($"{nameof(GoogleCloudStorageFileProviderOptions.PrivateKey)} is not a PEM private key.", nameof(options), e);
            }
            return new ServiceAccountCredential(initializer).ToGoogleCredential();
        }
        if (!string.IsNullOrEmpty(options.CredentialsJson))
        {
            try
            {
                return CredentialFactory.FromJson<ServiceAccountCredential>(options.CredentialsJson).ToGoogleCredential();
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or CryptographicException)
            {
                // Not JSON, another kind of credential, or a key that cannot be read.
                throw new ArgumentException($"{nameof(GoogleCloudStorageFileProviderOptions.CredentialsJson)} is not a service account key.", nameof(options), e);
            }
        }
        return null;
    }

    private static UrlSigner? SignerOf(StorageClient client)
    {
        try
        {
            return client.CreateUrlSigner();
        }
        catch (InvalidOperationException)
        {
            return null; // A credential that cannot sign: a user, an external account.
        }
        catch (ArgumentNullException)
        {
            return null; // No credential at all: an unauthenticated client.
        }
    }

    private sealed record State(StorageClient Client, UrlSigner? Signer, bool OwnsClient);
}
