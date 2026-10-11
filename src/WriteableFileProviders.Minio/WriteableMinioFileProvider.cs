using Amazon.Runtime;
using Amazon.S3;
using WriteableFileProviders.S3;

namespace WriteableFileProviders.Minio;

/// <summary>
/// A writeable file provider over a MinIO bucket, on the AWS SDK for .NET v4 (the S3 protocol MinIO speaks). It shares
/// its implementation with WriteableFileProviders.AmazonS3.
/// </summary>
/// <remarks>
/// <para>
/// Folders are key prefixes: a folder exists while some key is under it, and a file and a folder may share a name
/// (MinIO then lists only the file, and hides what is under the folder while the file exists). Names are
/// case-sensitive. Files up to 8 MiB are written in one request, larger ones in a multipart upload; either way MinIO
/// replaces a file only once the whole content arrived, so a failed write leaves the old content.
/// <see cref="UpdateFileOptions.IfMatch"/> and <see cref="UpdateFileOptions.IfNotExists"/> are checked by the provider
/// first, which is not atomic, and sent with the write, which MinIO releases with conditional writes then enforce; an
/// append (a rewrite of the whole file) writes with <c>If-Match</c> on the version it read. A move or copy checks that
/// the destination is free first, and a move checks the source's version before deleting it, undoing the copy when it
/// changed; neither is atomic, as MinIO ignores <c>If-None-Match</c> on copies and <c>If-Match</c> on deletes.
/// </para>
/// <para>
/// Nothing touches the network until the first call, which asks the server for the bucket's region (unless
/// <see cref="MinioFileProviderOptions.Region"/> names it) and checks the bucket once. Presigned read and write URLs
/// live at most 7 days. A create URL (only if the file does not exist yet) throws <see cref="NotSupportedException"/>:
/// its guard would be the <c>If-None-Match: *</c> it signs, which older MinIO releases ignore, so
/// WriteableFileProviders.AspNetCore issues a gateway URL instead, which checks the condition before the write.
/// </para>
/// </remarks>
public sealed class WriteableMinioFileProvider : WriteableFileProvider<MinioFileProviderOptions>
{
    private readonly S3Core _core;

    /// <summary>Creates the provider; it connects on first use.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentException">
    /// The options are incomplete, <see cref="MinioFileProviderOptions.HostAndPort"/> is not <c>host:port</c>, or plain
    /// HTTP toward a host that is not loopback was not allowed (<see cref="MinioFileProviderOptions.AllowInsecureHttp"/>).
    /// </exception>
    public WriteableMinioFileProvider(MinioFileProviderOptions options)
        : base(options) =>
        _core = new S3Core(this, SettingsFor(Options, client: null), null, IsAddressable);

    /// <summary>
    /// Creates the provider over an S3 client the caller built and owns, such as an <see cref="AmazonS3Client"/> with a
    /// <c>ServiceURL</c> and <c>ForcePathStyle</c>: it is never disposed by the provider. The options give the bucket,
    /// the root and <see cref="MinioFileProviderOptions.CreateBucketIfMissing"/>; the server, keys and region are not
    /// used. The provider cannot see when the client's credentials expire, so its presigned URLs need
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/>. A client whose endpoint is plain http toward a host that is
    /// not loopback needs <see cref="MinioFileProviderOptions.AllowInsecureHttp"/>.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="client">The client.</param>
    /// <exception cref="ArgumentException">
    /// The options name no bucket, or the client's endpoint is plain http toward a host that is not loopback without
    /// <see cref="MinioFileProviderOptions.AllowInsecureHttp"/>.
    /// </exception>
    public WriteableMinioFileProvider(MinioFileProviderOptions options, IAmazonS3 client)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _core = new S3Core(this, SettingsFor(Options, client), client, IsAddressable);
    }

    /// <summary>The shared S3 core, for tests that tune its part sizes.</summary>
    internal S3Core Core => _core;

    /// <summary>
    /// Always false: MinIO may ignore <c>If-Match</c> and <c>If-None-Match</c> (on copies and deletes it does), so the
    /// provider checks conditions first, which is not atomic, and still sends them for MinIO to enforce where it can.
    /// </summary>
    protected override bool SupportsAtomicPreconditions => _core.ChecksAtomically;

    /// <inheritdoc />
    protected override TimeSpan MaxSignedUrlValidity => S3Core.MaxPresignValidity;

    /// <inheritdoc />
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        _core.GetFileInfoAsync(path, cancellationToken);

    /// <inheritdoc />
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        _core.EnumerateAsync(path, cancellationToken);

    /// <inheritdoc />
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateRecursiveCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        _core.EnumerateRecursiveAsync(path, cancellationToken);

    /// <inheritdoc />
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        _core.ReadAsync(path, null, cancellationToken);

    /// <inheritdoc />
    protected override Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken) =>
        _core.ReadRangeAsync(path, offset, length, ifMatch, cancellationToken);

    /// <inheritdoc />
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == UpdateFileMode.Append)
        {
            // The base keeps the file's content type when the caller names none.
            await AppendByRewriteAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _core.WriteAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        _core.DeleteAsync(path, cancellationToken);

    /// <inheritdoc />
    protected override Task DeleteFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        _core.DeleteFileAsync(path, cancellationToken);

    /// <inheritdoc />
    protected override Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken) =>
        _core.CopyAsync(source, destination, cancellationToken);

    /// <summary>
    /// Copies the file, then deletes the source with <c>If-Match</c> on the version copied. A source deleted meanwhile
    /// completes the move; a source changed meanwhile removes the copy (only while it is still the copy) and throws
    /// <see cref="PreconditionFailedException"/>.
    /// </summary>
    protected override Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken) =>
        _core.MoveAsync(source, destination, cancellationToken);

    /// <summary>
    /// Read and write URLs whenever the credential can sign. Never a create URL (only if the file does not exist yet):
    /// its guard is the <c>If-None-Match: *</c> it signs, which older MinIO releases ignore, so such a URL could
    /// overwrite a file; a create URL is <see cref="NotSupportedException"/>, and WriteableFileProviders.AspNetCore
    /// issues a gateway URL, which checks the condition before the write.
    /// </summary>
    /// <param name="access">The access.</param>
    protected override bool SupportsSignedUrl(SignedUrlAccess access) =>
        _core.CanPresign && (access != SignedUrlAccess.Create || _core.ChecksAtomically);

    /// <inheritdoc />
    protected override bool SupportsSignedUrlContentHeaders(SignedUrlAccess access) => true;

    /// <inheritdoc />
    protected override Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken) =>
        _core.PresignAsync(path, options, TimeProvider.GetUtcNow(), cancellationToken);

    /// <inheritdoc />
    protected override bool IsNotFound(Exception exception) => S3Core.IsNotFound(exception);

    /// <inheritdoc />
    /// <remarks>
    /// The store's 401 and 403, whatever the code, but an archived object (<c>InvalidObjectState</c>,
    /// <c>ObjectNotInActiveTierError</c>) and a skewed clock (<c>RequestTimeTooSkewed</c>), which are the store's failures
    /// (<see cref="IOException"/>); and a session token that expired or is not valid (<c>ExpiredToken</c>,
    /// <c>InvalidToken</c>), which S3 answers with a 400.
    /// </remarks>
    protected override bool IsAccessDenied(Exception exception) => S3Core.IsAccessDenied(exception);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _core.Dispose();
        base.Dispose(disposing);
    }

    private static S3CoreSettings SettingsFor(MinioFileProviderOptions options, IAmazonS3? client)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new ArgumentException("BucketName is required.", nameof(options));
        if (client is not null)
        {
            // The rule of UseSSL, for the client's own endpoint: plain HTTP only toward loopback, unless allowed.
            if (S3Core.PlainHttpEndpointOf(client.Config) is { IsLoopback: false } plainHttp && !options.AllowInsecureHttp)
            {
                throw new ArgumentException(
                    $"The client's endpoint is plain http, so files would cross the network to '{plainHttp.Host}' in clear text. Use https, or set AllowInsecureHttp to accept plain HTTP.",
                    nameof(options));
            }
            return new S3CoreSettings { Bucket = options.BucketName, CreateBucketIfMissing = options.CreateBucketIfMissing };
        }
        var serviceUrl = ServiceUrlFor(options);
        if (string.IsNullOrEmpty(options.AccessKey) != string.IsNullOrEmpty(options.SecretKey))
            throw new ArgumentException("AccessKey and SecretKey go together: set both, or neither for anonymous access.", nameof(options));
        return new S3CoreSettings
        {
            Bucket = options.BucketName,
            ServiceUrl = serviceUrl,
            Region = S3Core.NormalizeRegion(string.IsNullOrWhiteSpace(options.Region) ? null : options.Region),
            DiscoverRegion = true,
            ForcePathStyle = true,
            Credentials = string.IsNullOrEmpty(options.AccessKey)
                ? new AnonymousAWSCredentials()
                : new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            // MinIO refuses storage classes it does not know (STANDARD_IA: 400 InvalidStorageClass).
            StorageClass = null,
            CreateBucketIfMissing = options.CreateBucketIfMissing,
        };
    }

    // The 2026 provider (on the Minio SDK) took host:port only, and rejected a scheme or a path.
    private static string ServiceUrlFor(MinioFileProviderOptions options)
    {
        var hostAndPort = options.HostAndPort?.Trim();
        if (string.IsNullOrEmpty(hostAndPort))
            throw new ArgumentException("HostAndPort is required.", nameof(options));
        if (hostAndPort.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate("http://" + hostAndPort, UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0 || hostAndPort.EndsWith('/'))
        {
            throw new ArgumentException("HostAndPort takes host:port, without a scheme (UseSSL chooses it), a path or a query.", nameof(options));
        }
        if (!options.UseSSL && !uri.IsLoopback && !options.AllowInsecureHttp)
        {
            throw new ArgumentException(
                $"UseSSL is false, so files would cross the network to '{uri.Host}' in clear text. Set UseSSL, or set AllowInsecureHttp to accept plain HTTP.",
                nameof(options));
        }
        return (options.UseSSL ? "https://" : "http://") + hostAndPort;
    }
}
