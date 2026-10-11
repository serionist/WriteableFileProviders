using Amazon.Runtime;
using Amazon.S3;
using WriteableFileProviders.S3;

namespace WriteableFileProviders.AmazonS3;

/// <summary>
/// A writeable file provider over an Amazon S3 bucket, or a bucket on an S3-compatible server
/// (<see cref="AmazonS3FileProviderOptions.ServiceUrl"/>), on the AWS SDK for .NET v4.
/// </summary>
/// <remarks>
/// <para>
/// Folders are key prefixes: a folder exists while some key is under it. Names are case-sensitive. Files up to 8 MiB are
/// written in one request, larger ones in a multipart upload; either way S3 replaces a file only once the whole content
/// arrived, so a failed write leaves the old content. <see cref="UpdateFileOptions.IfMatch"/> and
/// <see cref="UpdateFileOptions.IfNotExists"/> go to the store with the write, and an append (a rewrite of the whole file)
/// writes with <c>If-Match</c> on the version it read. A copy refuses an existing destination with <c>If-None-Match: *</c>,
/// and a move deletes the source with <c>If-Match</c> on the version it copied, undoing the copy when the source changed.
/// AWS checks all of these atomically. An S3-compatible server (<see cref="AmazonS3FileProviderOptions.ServiceUrl"/>) may
/// ignore some of them, so there the provider also checks them first, which is not atomic.
/// </para>
/// <para>
/// Nothing touches the network until the first call, which resolves the credentials of the default chain if asked and
/// checks the bucket once. Presigned read, create and write URLs live at most 7 days, and never longer than the
/// credential that signs them.
/// </para>
/// </remarks>
public sealed class WriteableAmazonS3FileProvider : WriteableFileProvider<AmazonS3FileProviderOptions>
{
    private readonly S3Core _core;

    /// <summary>Creates the provider; it connects on first use.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentException">The options are incomplete or contradict each other.</exception>
    public WriteableAmazonS3FileProvider(AmazonS3FileProviderOptions options)
        : this(options, null, null)
    {
    }

    /// <summary>
    /// Creates the provider over a client the caller built and owns (for its own credentials, endpoint or retries): it is
    /// never disposed by the provider. The options give the bucket, the root, the storage class and
    /// <see cref="AmazonS3FileProviderOptions.CreateBucketIfMissing"/>; their credentials, region and endpoint are not
    /// used. The provider cannot see when the client's credentials expire, so its presigned URLs need
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/>.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="client">The client.</param>
    /// <exception cref="ArgumentException">The options name no bucket.</exception>
    public WriteableAmazonS3FileProvider(AmazonS3FileProviderOptions options, IAmazonS3 client)
        : this(options, client ?? throw new ArgumentNullException(nameof(client)), null)
    {
    }

    /// <summary>Creates the provider with credentials the options cannot express; for tests.</summary>
    /// <param name="options">The options.</param>
    /// <param name="credentials">The credentials that sign.</param>
    internal WriteableAmazonS3FileProvider(AmazonS3FileProviderOptions options, AWSCredentials credentials)
        : this(options, null, credentials ?? throw new ArgumentNullException(nameof(credentials)))
    {
    }

    private WriteableAmazonS3FileProvider(AmazonS3FileProviderOptions options, IAmazonS3? client, AWSCredentials? credentials)
        : base(options) =>
        _core = new S3Core(this, SettingsFor(Options, client, credentials), client, IsAddressable);

    /// <summary>The shared S3 core, for tests that tune its part sizes.</summary>
    internal S3Core Core => _core;

    /// <summary>
    /// True on AWS, which checks <c>If-Match</c> and <c>If-None-Match</c> atomically. On an S3-compatible server the
    /// provider checks them first too (not atomic), and still sends them.
    /// </summary>
    protected override bool SupportsAtomicPreconditions => _core.ChecksAtomically;

    /// <inheritdoc />
    protected override TimeSpan MaxSignedUrlValidity => S3Core.MaxPresignValidity;

    /// <summary>
    /// Reads a version of a file in a bucket with versioning.
    /// </summary>
    /// <param name="versionId">The S3 version id; null or empty for the current version.</param>
    /// <param name="path">The file, relative to the provider root.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The content, as a network stream that reads forward only.</returns>
    /// <exception cref="ArgumentException">A segment of the path is not allowed, or the path is the provider root.</exception>
    /// <exception cref="FileNotFoundException">The file or the version does not exist.</exception>
    public async Task<Stream> ReadFileVersionAsync(string? versionId, ProviderPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (resolved.IsRoot)
            throw new ArgumentException("The path must contain at least one segment.", nameof(path));
        return await _core.ReadAsync(resolved, string.IsNullOrEmpty(versionId) ? null : versionId, cancellationToken).ConfigureAwait(false);
    }

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
    /// Copies the file, then deletes the source with <c>If-Match</c> on the version copied; when the source changed
    /// meanwhile, the copy is removed and <see cref="PreconditionFailedException"/> is thrown.
    /// </summary>
    protected override Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken) =>
        _core.MoveAsync(source, destination, cancellationToken);

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

    private static S3CoreSettings SettingsFor(AmazonS3FileProviderOptions options, IAmazonS3? client, AWSCredentials? credentials)
    {
        var clientGiven = client is not null;
        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new ArgumentException("BucketName is required.", nameof(options));
        string? serviceUrl = null;
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("ServiceUrl is not an absolute http or https URL.", nameof(options));
            serviceUrl = options.ServiceUrl;
        }
        var region = S3Core.NormalizeRegion(options.BucketRegion);
        if (!clientGiven && serviceUrl is null && region is null)
            throw new ArgumentException("BucketRegion is required unless ServiceUrl is set.", nameof(options));
        return new S3CoreSettings
        {
            Bucket = options.BucketName,
            ServiceUrl = serviceUrl,
            Region = region,
            ForcePathStyle = options.ForcePathStyle,
            Credentials = clientGiven ? null : credentials ?? CredentialsFor(options),
            StorageClass = options.StorageClassForNewFiles,
            // A requester-pays bucket on AWS needs every request to accept the charge; it costs nothing elsewhere.
            RequesterPays = serviceUrl is null,
            // AWS checks conditions atomically; an S3-compatible server, ours or the given client's, may ignore some.
            ChecksAtomically = serviceUrl is null && client?.Config.ServiceURL is null,
            // Outside us-east-1, AWS needs the region as a new bucket's location; other servers use their own.
            BucketLocation = serviceUrl is null && region is not null and not "us-east-1" ? region : null,
            CreateBucketIfMissing = options.CreateBucketIfMissing,
        };
    }

    // Null: the default credential chain, resolved on first use.
    private static AWSCredentials? CredentialsFor(AmazonS3FileProviderOptions options)
    {
        if (options.UseDefaultCredentials)
        {
            return string.IsNullOrEmpty(options.AccessKey)
                ? null
                : throw new ArgumentException("UseDefaultCredentials and AccessKey cannot both be set.", nameof(options));
        }
        if (options.IsAnonymus)
            return new AnonymousAWSCredentials();
        if (string.IsNullOrEmpty(options.AccessKey))
            throw new ArgumentException("Set AccessKey and SecretKey, IsAnonymus, or UseDefaultCredentials.", nameof(options));
        if (string.IsNullOrEmpty(options.SecretKey))
            throw new ArgumentException("AccessKey needs SecretKey.", nameof(options));
        return new BasicAWSCredentials(options.AccessKey, options.SecretKey);
    }
}
