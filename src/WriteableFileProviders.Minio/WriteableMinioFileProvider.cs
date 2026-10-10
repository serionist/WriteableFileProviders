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
/// Folders are key prefixes: a folder exists while some key is under it. Names are case-sensitive. Files up to 8 MiB are
/// written in one request, larger ones in a multipart upload; either way MinIO replaces a file only once the whole
/// content arrived, so a failed write leaves the old content. <see cref="UpdateFileOptions.IfMatch"/> and
/// <see cref="UpdateFileOptions.IfNotExists"/> are checked atomically by MinIO releases that support conditional writes,
/// and an append (a rewrite of the whole file) writes with <c>If-Match</c> on the version it read. A move or copy checks
/// that the destination is free first, which is not atomic: MinIO ignores <c>If-None-Match</c> on copies.
/// </para>
/// <para>
/// Nothing touches the network until the first call, which asks the server for the bucket's region (unless
/// <see cref="MinioFileProviderOptions.Region"/> names it) and checks the bucket once. Presigned read, create and write
/// URLs live at most 7 days.
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
        _core = new S3Core(this, SettingsFor(Options, clientGiven: false), null, IsAddressable);

    /// <summary>
    /// Creates the provider over an S3 client the caller built and owns, such as an <see cref="AmazonS3Client"/> with a
    /// <c>ServiceURL</c> and <c>ForcePathStyle</c>: it is never disposed by the provider. The options give the bucket,
    /// the root and <see cref="MinioFileProviderOptions.CreateBucketIfMissing"/>; the server, keys and region are not
    /// used. The provider cannot see when the client's credentials expire, so its presigned URLs need
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/>.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="client">The client.</param>
    /// <exception cref="ArgumentException">The options name no bucket.</exception>
    public WriteableMinioFileProvider(MinioFileProviderOptions options, IAmazonS3 client)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _core = new S3Core(this, SettingsFor(Options, clientGiven: true), client, IsAddressable);
    }

    /// <summary>The shared S3 core, for tests that tune its part sizes.</summary>
    internal S3Core Core => _core;

    /// <inheritdoc />
    protected override bool SupportsAtomicPreconditions => true;

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
            var keep = await _core.KeepContentTypeAsync(path, options, cancellationToken).ConfigureAwait(false);
            await AppendByRewriteAsync(path, contents, keep, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    protected override bool SupportsSignedUrl(SignedUrlAccess permission) => _core.CanPresign;

    /// <inheritdoc />
    protected override bool SupportsSignedUrlContentHeaders(SignedUrlAccess permission) => true;

    /// <inheritdoc />
    protected override Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken) =>
        _core.PresignAsync(path, options, TimeProvider.GetUtcNow(), cancellationToken);

    /// <inheritdoc />
    protected override bool IsNotFound(Exception exception) => S3Core.IsNotFound(exception);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _core.Dispose();
        base.Dispose(disposing);
    }

    // A listed name that the path validation would refuse, or would split into other segments, cannot be addressed.
    private static bool IsAddressable(string name)
    {
        try
        {
            return ValidateSegments([name]) is [var only] && only == name;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static S3CoreSettings SettingsFor(MinioFileProviderOptions options, bool clientGiven)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new ArgumentException("BucketName is required.", nameof(options));
        if (clientGiven)
            return new S3CoreSettings { Bucket = options.BucketName, CreateBucketIfMissing = options.CreateBucketIfMissing };
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
