using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.S3.Model;

namespace WriteableFileProviders.S3;

/// <summary>
/// The S3 protocol, shared by the Amazon S3 and MinIO providers: both translate their options into
/// <see cref="S3CoreSettings"/> and forward their <c>...CoreAsync</c> members here. The source is compiled into each
/// package, so neither depends on the other.
/// </summary>
/// <remarks>
/// Nothing touches the network before the first call: the credentials of the default chain, the MinIO region and the
/// bucket check all happen then, once, and are retried by the next call if they failed. A client given by the caller is
/// never disposed here.
/// </remarks>
/// <param name="owner">The provider the entries belong to.</param>
/// <param name="settings">The validated settings.</param>
/// <param name="client">A client the caller owns, or null to build one from the settings.</param>
/// <param name="isAddressable">Whether a listed name is a segment the provider accepts back.</param>
internal sealed partial class S3Core(IWriteableFileProvider owner, S3CoreSettings settings, IAmazonS3? client, Func<string, bool> isAddressable)
    : IDisposable
{
    /// <summary>S3's smallest part, except the last.</summary>
    public const long MinimumPartSize = 5L << 20;

    private const string UsEast1 = "us-east-1";

    private readonly IWriteableFileProvider _owner = owner;
    private readonly S3CoreSettings _settings = settings;
    private readonly IAmazonS3? _givenClient = client;
    private readonly Func<string, bool> _isAddressable = isAddressable;
    private readonly Lock _gate = new();
    private Task<Connection>? _connection;
    private Task? _bucketCheck;
    private bool _disposed;

    /// <summary>The size of each part of a multipart upload, and the largest file written in one request.</summary>
    public long PartSize { get; set; } = 8L << 20;

    /// <summary>The largest file copied in one request: S3 copies at most 5 GiB at once, and larger files in parts.</summary>
    public long MaxSingleCopySize { get; set; } = 5L << 30;

    /// <summary>The size of each part of a copy in parts.</summary>
    public long CopyPartSize { get; set; } = 512L << 20;

    /// <summary>The most parts an upload may have: S3 takes 10,000.</summary>
    public int MaxPartCount { get; set; } = 10_000;

    /// <summary>The HTTP layer of the clients this core builds; for tests that inject faults. Set it before first use.</summary>
    public HttpClientFactory? HttpClientFactory { get; set; }

    /// <summary>The bucket.</summary>
    public string Bucket => _settings.Bucket;

    /// <summary>
    /// Whether the store checks conditions atomically: AWS does. S3-compatible servers (MinIO, S3Mock, any
    /// <see cref="S3CoreSettings.ServiceUrl"/>) may ignore some headers, so the provider also checks first, which is not
    /// atomic; the headers are sent either way.
    /// </summary>
    public bool ChecksAtomically => _settings.ChecksAtomically;

    /// <summary>The location a bucket the provider creates is given, or null for none (the server's own).</summary>
    public string? BucketLocation => _settings.BucketLocation;

    /// <summary>Whether URLs can be presigned: never with anonymous credentials.</summary>
    public bool CanPresign => _settings.Credentials is not AnonymousAWSCredentials || _givenClient is not null;

    private RequestPayer? Payer => _settings.RequesterPays ? RequestPayer.Requester : null;

    /// <summary>The client configuration the settings stand for, in a given signing region.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="region">The region, or null for the settings' own.</param>
    public static AmazonS3Config CreateConfig(S3CoreSettings settings, string? region = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        region ??= settings.Region ?? UsEast1;
        if (settings.ServiceUrl is null)
            return new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region), ForcePathStyle = settings.ForcePathStyle };
        return new AmazonS3Config
        {
            // ServiceURL and RegionEndpoint exclude each other: the region only signs.
            ServiceURL = settings.ServiceUrl,
            AuthenticationRegion = region,
            ForcePathStyle = settings.ForcePathStyle,
            // S3-compatible servers do not all handle the SDK's default checksums: S3Mock returns the whole object's
            // checksum on a ranged read, which fails the default validation. Checksums go only where S3 requires them.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
    }

    /// <summary>A region name as S3 and the old options store it: <c>""</c> and <c>US</c> are us-east-1, <c>EU</c> is eu-west-1.</summary>
    /// <param name="region">The stored name.</param>
    public static string? NormalizeRegion(string? region) => region?.Trim() switch
    {
        null => null,
        "" or "US" or "null" => UsEast1,
        "EU" => "eu-west-1",
        var name => name,
    };

    /// <summary>Whether an exception of the SDK means the object or version does not exist.</summary>
    /// <param name="exception">The exception.</param>
    public static bool IsNotFound(Exception exception) =>
        exception is AmazonS3Exception { ErrorCode: "NoSuchKey" or "NoSuchVersion" };

    /// <summary>
    /// Whether an exception of the SDK means the store refused the credential or the permission: a 401 or a 403
    /// (<c>AccessDenied</c>, <c>InvalidAccessKeyId</c>, <c>SignatureDoesNotMatch</c>), or an <c>AccessDenied</c> code.
    /// </summary>
    /// <param name="exception">The exception.</param>
    public static bool IsAccessDenied(Exception exception) =>
        exception is AmazonS3Exception { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } or AmazonS3Exception { ErrorCode: "AccessDenied" };

    /// <summary>Disposes the client this core built; a client the caller gave is left alone. Called once, by the provider.</summary>
    public void Dispose()
    {
        Task<Connection>? connection;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
        }
        if (connection is { IsCompletedSuccessfully: true, Result: { Owned: true } built })
            built.Client.Dispose();
    }

    private async Task<IAmazonS3> ClientAsync(CancellationToken cancellationToken) =>
        (await ConnectedAsync(cancellationToken).ConfigureAwait(false)).Client;

    private async Task<Connection> ConnectedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await ConnectionTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        await BucketCheckTask(connection.Client).WaitAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private Task<Connection> ConnectionTask()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, _owner);
            if (_connection is null || _connection.IsFaulted || _connection.IsCanceled)
                _connection = ConnectAsync();
            return _connection;
        }
    }

    private Task BucketCheckTask(IAmazonS3 client)
    {
        lock (_gate)
        {
            if (_bucketCheck is null || _bucketCheck.IsFaulted || _bucketCheck.IsCanceled)
                _bucketCheck = CheckBucketAsync(client);
            return _bucketCheck;
        }
    }

    private async Task<Connection> ConnectAsync()
    {
        if (_givenClient is not null)
            return new Connection(_givenClient, null, false, IsHttp(_givenClient.Config));
        var credentials = _settings.Credentials ?? await DefaultCredentialsAsync(() => DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync()).ConfigureAwait(false);
        var region = _settings.Region;
        if (region is null && _settings.DiscoverRegion)
            region = await DiscoverRegionAsync(credentials).ConfigureAwait(false);
        var config = Configured(CreateConfig(_settings, region));
        var client = new AmazonS3Client(credentials, config);
        lock (_gate)
        {
            if (!_disposed)
                return new Connection(client, credentials, true, IsHttp(config));
        }
        client.Dispose();
        throw new ObjectDisposedException(_owner.GetType().FullName);
    }

    /// <summary>
    /// The credentials of the SDK's default chain (environment, profile, SSO, container or instance role). Finding none is a
    /// store that is not set up as configured, <see cref="InvalidOperationException"/>, which the next call asks again.
    /// </summary>
    /// <param name="resolve">Resolves the chain: the SDK's resolver, or a test's.</param>
    public static async Task<AWSCredentials> DefaultCredentialsAsync(Func<Task<AWSCredentials>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        try
        {
            return await resolve().ConfigureAwait(false);
        }
        catch (AmazonClientException e)
        {
            throw new InvalidOperationException(
                "UseDefaultCredentials is set, but the AWS SDK's default credential chain found no credentials (environment, profile, SSO, container or instance role).",
                e);
        }
    }

    // MinIO answers GetBucketLocation signed for us-east-1 even when it runs in another region, which rejects every other
    // request signed for the wrong one.
    private async Task<string> DiscoverRegionAsync(AWSCredentials credentials)
    {
        using var probe = new AmazonS3Client(credentials, Configured(CreateConfig(_settings, UsEast1)));
        try
        {
            var response = await probe.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = Bucket }).ConfigureAwait(false);
            return NormalizeRegion(response.Location?.Value) ?? UsEast1;
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden || e.ErrorCode == "NoSuchBucket")
        {
            // No such bucket (the bucket check says so next), or no permission to ask: the server's default region.
            // Anything else fails the connection, and the next call asks again.
            return UsEast1;
        }
    }

    private async Task CheckBucketAsync(IAmazonS3 client)
    {
        try
        {
            await client.HeadBucketAsync(new HeadBucketRequest { BucketName = Bucket }).ConfigureAwait(false);
            return;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            if (!_settings.CreateBucketIfMissing)
                throw MissingBucket(e);
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest || (int)e.StatusCode is >= 300 and < 400)
        {
            // Forbidden, refused or redirected: the bucket exists, or these credentials may not ask. Each operation reports
            // its own error, and a missing bucket still maps to the same exception there. Any other failure faults the
            // check, and the next call checks again.
            return;
        }
        try
        {
            await client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = Bucket,
                BucketRegionName = _settings.BucketLocation,
            }).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "BucketAlreadyOwnedByYou")
        {
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "BucketAlreadyExists")
        {
            // Bucket names are global: this one belongs to another account, so the store cannot be set up as configured.
            throw new InvalidOperationException(
                $"The bucket '{Bucket}' does not exist for these credentials, and CreateBucketIfMissing cannot create it: another account owns the name. Name another bucket.",
                e);
        }
    }

    private AmazonS3Config Configured(AmazonS3Config config)
    {
        if (HttpClientFactory is not null)
            config.HttpClientFactory = HttpClientFactory;
        return config;
    }

    private InvalidOperationException MissingBucket(Exception? inner)
    {
        lock (_gate)
            _bucketCheck = null;
        return new InvalidOperationException(_settings.CreateBucketIfMissing
            ? $"The bucket '{Bucket}' does not exist; with the option CreateBucketIfMissing set, the next call creates it again."
            : $"The bucket '{Bucket}' does not exist. Create it, or set the option CreateBucketIfMissing to let the provider create it.", inner);
    }

    // HEAD answers 404 for a missing key and for a missing bucket alike. Where a 404 would end the call as "file not
    // found", the bucket is asked first, so a bucket that went missing since the check is reported as such.
    private async Task<FileNotFoundException> NotFoundAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.HeadBucketAsync(new HeadBucketRequest { BucketName = Bucket }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw MissingBucket(e);
        }
        catch (AmazonS3Exception)
        {
            // Not allowed to ask: the file is what is missing.
        }
        return NotFound(path);
    }

    // Every call goes through here, so a bucket that disappears is reported the same way everywhere.
    private async Task<T> CallAsync<T>(Func<IAmazonS3, Task<T>> call, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(client).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchBucket")
        {
            throw MissingBucket(e);
        }
    }

    private static bool IsHttp(IClientConfig config) =>
        config.ServiceURL is { } url
            ? url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            : config.UseHttp;

    private static string Key(ResolvedPath path) => string.Join('/', path.AbsoluteSegments);

    private static string FolderPrefix(ResolvedPath path) => path.AbsoluteSegments.Length == 0 ? "" : Key(path) + "/";

    private static FileNotFoundException NotFound(ResolvedPath path, Exception? inner = null) =>
        new($"The file '{path}' does not exist.", path.ToString(), inner);

    private sealed record Connection(IAmazonS3 Client, AWSCredentials? Credentials, bool Owned, bool UseHttp);
}
