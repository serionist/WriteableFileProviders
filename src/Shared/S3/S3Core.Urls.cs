using System.Net.Http.Headers;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace WriteableFileProviders.S3;

internal sealed partial class S3Core
{
    /// <summary>The longest SigV4 presigned URL. Past it the SDK silently signs with SigV2 where it can.</summary>
    public static readonly TimeSpan MaxPresignValidity = TimeSpan.FromDays(7);

    /// <summary>
    /// Presigns a GET (read) or PUT (create, write) URL. The caller checked that the credentials can sign
    /// (<see cref="CanPresign"/>), the validity (positive, at most <see cref="MaxPresignValidity"/>) and, for a read, that
    /// the file exists. A URL never outlives the credential that
    /// signs it: when that credential expires first, or its expiry cannot be known (a session token, a client the
    /// caller gave), the URL is shorter than asked and is issued only with
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">What the URL allows and how long it lives.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<SignedUrl> PresignAsync(ResolvedPath path, SignedUrlOptions options, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Access != SignedUrlAccess.Read && options.DownloadFileName is not null)
            throw new NotSupportedException("A download file name applies to read URLs only.");
        var connection = await ConnectedAsync(cancellationToken).ConfigureAwait(false);
        var expires = now + options.Validity;
        var (known, credentialExpiry) = await CredentialExpiryAsync(connection.Credentials).ConfigureAwait(false);
        DateTimeOffset? expiresOn = expires;
        if (!known || credentialExpiry < expires)
        {
            if (!options.AllowShorterValidity)
            {
                throw new NotSupportedException(known
                    ? $"The credential that signs the URL expires at {credentialExpiry:O}, before the {options.Validity} asked."
                    : "The credential that signs the URL may expire before the validity asked; set AllowShorterValidity to accept that.");
            }
            expiresOn = credentialExpiry;
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = Key(path),
            Verb = options.Access == SignedUrlAccess.Read ? HttpVerb.GET : HttpVerb.PUT,
            Expires = expires.UtcDateTime,
            // The SDK signs https unless told otherwise, whatever the endpoint's scheme.
            Protocol = connection.UseHttp ? Protocol.HTTP : Protocol.HTTPS,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.Access == SignedUrlAccess.Read)
        {
            if (options.DownloadFileName is not null)
                request.ResponseHeaderOverrides.ContentDisposition = AttachmentDisposition(options.DownloadFileName);
            if (options.ContentType is not null)
                request.ResponseHeaderOverrides.ContentType = options.ContentType;
        }
        else
        {
            if (options.ContentType is not null)
            {
                // Signed: the upload must send exactly this Content-Type.
                request.ContentType = options.ContentType;
                headers["Content-Type"] = options.ContentType;
            }
            if (options.Access == SignedUrlAccess.Create)
            {
                // Signed: the upload must send it, and S3 refuses it when the file exists.
                request.Headers["If-None-Match"] = "*";
                headers["If-None-Match"] = "*";
            }
        }
        string url;
        try
        {
            url = await connection.Client.GetPreSignedURLAsync(request).ConfigureAwait(false);
        }
        catch (ArgumentException e) when (e.Message.Contains("maximum expiry", StringComparison.OrdinalIgnoreCase))
        {
            throw TooLongForTheSdkClock(e);
        }
        // The SDK measures the validity on its own clock, corrected for the server's skew: when that clock runs behind,
        // a validity near 7 days exceeds SigV4's limit, and the SDK signs with SigV2 where the region allows it (or throws
        // where it does not) instead of failing.
        if (!url.Contains("X-Amz-Signature=", StringComparison.Ordinal))
            throw TooLongForTheSdkClock(null);
        return new SignedUrl
        {
            Url = new Uri(url),
            Method = options.Access == SignedUrlAccess.Read ? "GET" : "PUT",
            ExpiresOn = expiresOn,
            Headers = headers,
            Kind = SignedUrlKind.Presigned,
        };
    }

    private static NotSupportedException TooLongForTheSdkClock(Exception? inner) => new(
        "The validity is too close to 7 days for the SDK's clock, which is corrected for the server's skew: S3 cannot sign it.", inner);

    /// <summary>
    /// <c>attachment</c> with the name as RFC 6266 has it: <c>filename*=UTF-8''…</c>, and an ASCII <c>filename</c> for
    /// clients that do not read the other.
    /// </summary>
    /// <param name="fileName">The name a browser saves the download as.</param>
    public static string AttachmentDisposition(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var ascii = new string([.. fileName.Select(c => c is >= ' ' and <= '~' and not '"' and not '\\' ? c : '_')]);
        return new ContentDispositionHeaderValue("attachment") { FileName = "\"" + ascii + "\"", FileNameStar = fileName }.ToString();
    }

    // Long-term keys never expire; a refreshing credential knows its expiry once loaded; a session token without one, or a
    // client the caller built, cannot say.
    private static async Task<(bool Known, DateTimeOffset? Expiry)> CredentialExpiryAsync(AWSCredentials? credentials)
    {
        if (credentials is null)
            return (false, null);
        var current = await credentials.GetCredentialsAsync().ConfigureAwait(false);
        if (credentials.Expiration is { } expiration)
            return (true, Utc(expiration));
        return current.UseToken ? (false, null) : (true, null);
    }
}
