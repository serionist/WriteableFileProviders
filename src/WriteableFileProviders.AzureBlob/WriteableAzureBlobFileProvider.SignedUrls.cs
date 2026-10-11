using System.Net.Http.Headers;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;

namespace WriteableFileProviders.AzureBlob;

public sealed partial class WriteableAzureBlobFileProvider
{
    // A SAS starts 15 minutes in the past, Microsoft's advice for clock skew; the start time also satisfies a SAS
    // expiration policy, which refuses a SAS without one.
    private static readonly TimeSpan _clockSkew = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _refusalMemory = TimeSpan.FromMinutes(10);

    // Azure takes a user delegation key that ends within 7 days of its own clock; a margin keeps a clock that runs ahead
    // from asking past that.
    private static readonly TimeSpan _maxKeyLifetime = TimeSpan.FromDays(7) - TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyDictionary<string, string> _noHeaders = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string> _uploadHeaders = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" };

    private readonly Lock _keyLock = new();
    private UserDelegationKey? _delegationKey;
    private DateTimeOffset _delegationRefusedUntil;
    private RequestFailedException? _delegationRefusal;
    private volatile bool _containerExists;

    /// <summary>Read, create and write URLs are signed natively, as a service SAS or a user delegation SAS.</summary>
    /// <param name="access">The access.</param>
    protected override bool SupportsSignedUrl(SignedUrlAccess access) => true;

    /// <summary>
    /// A read URL carries the download file name and the content type (<c>rscd</c> and <c>rsct</c>). An upload URL takes
    /// neither: a SAS cannot make the uploader send a content type, so such a request throws
    /// <see cref="NotSupportedException"/> and the gateway, which can, takes over.
    /// </summary>
    /// <param name="access">The access.</param>
    protected override bool SupportsSignedUrlContentHeaders(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    /// <summary>
    /// Signs a SAS for the blob: a service SAS when the client has the shared key, else a user delegation SAS, which never
    /// outlives its key (Azure's keys last at most 7 days, so a 7-day user delegation URL needs
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/>). It starts 15 minutes in the past and ends at the requested
    /// validity. An upload URL is for a PUT
    /// that sends <c>x-ms-blob-type: BlockBlob</c>; a create URL refuses a blob that exists.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="NotSupportedException">
    /// The client has neither a shared key nor a credential that can get a user delegation key, or the key would expire
    /// before the validity asked and <see cref="SignedUrlOptions.AllowShorterValidity"/> is not set.
    /// </exception>
    protected override async Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken) =>
        await GuardAsync(() => SignAsync(path, options, cancellationToken)).ConfigureAwait(false);

    private async Task<SignedUrl> SignAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var blob = Blob(path);
        var now = TimeProvider.GetUtcNow();
        var expiresOn = now + options.Validity;
        var builder = SasBuilder(blob, Permissions(options.Access), now, expiresOn);
        builder.ContentDisposition = options.DownloadFileName is { } fileName ? AttachmentDisposition(fileName) : null;
        builder.ContentType = options.ContentType;
        Uri url;
        if (blob.CanGenerateSasUri)
        {
            url = blob.GenerateSasUri(builder);
        }
        else
        {
            var key = await UserDelegationKeyAsync(now, expiresOn, cancellationToken).ConfigureAwait(false)
                ?? throw new NotSupportedException($"{nameof(WriteableAzureBlobFileProvider)} cannot sign a URL: its client has no shared key, and its credential cannot get a user delegation key.", _delegationRefusal);
            if (key.SignedExpiresOn < expiresOn)
            {
                if (!options.AllowShorterValidity)
                    throw new NotSupportedException($"The user delegation key expires at {key.SignedExpiresOn:O}, before the {options.Validity} asked; allow a shorter validity to accept a URL that ends with it.");
                expiresOn = key.SignedExpiresOn;
                builder.ExpiresOn = expiresOn;
            }
            url = new BlobUriBuilder(blob.Uri) { Sas = builder.ToSasQueryParameters(key, _container.AccountName) }.ToUri();
        }
        // An upload URL into a missing container would only fail when used: the container is checked now, and created
        // when the options allow it.
        if (options.Access != SignedUrlAccess.Read)
            await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        return new SignedUrl
        {
            Url = url,
            Method = options.Access == SignedUrlAccess.Read ? "GET" : "PUT",
            ExpiresOn = expiresOn,
            Headers = options.Access == SignedUrlAccess.Read ? _noHeaders : _uploadHeaders,
            Kind = SignedUrlKind.Presigned,
        };
    }

    // A read SAS for a blob the service copies from (Put Block From URL), or null when the provider cannot sign one.
    private async Task<Uri?> ReadSasUriAsync(BlobBaseClient blob, CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();
        var builder = SasBuilder(blob, BlobSasPermissions.Read, now, now + TimeSpan.FromHours(1));
        if (blob.CanGenerateSasUri)
            return blob.GenerateSasUri(builder);
        var key = await UserDelegationKeyAsync(now, builder.ExpiresOn, cancellationToken).ConfigureAwait(false);
        return key is null ? null : new BlobUriBuilder(blob.Uri) { Sas = builder.ToSasQueryParameters(key, _container.AccountName) }.ToUri();
    }

    private BlobSasBuilder SasBuilder(BlobBaseClient blob, BlobSasPermissions permissions, DateTimeOffset now, DateTimeOffset expiresOn) =>
        new(permissions, expiresOn)
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = now - _clockSkew,
            Protocol = blob.Uri.Scheme == Uri.UriSchemeHttps ? SasProtocol.Https : SasProtocol.None,
        };

    private static BlobSasPermissions Permissions(SignedUrlAccess access) => access switch
    {
        SignedUrlAccess.Read => BlobSasPermissions.Read,
        SignedUrlAccess.Create => BlobSasPermissions.Create,
        SignedUrlAccess.Write => BlobSasPermissions.Write,
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, null),
    };

    // RFC 6266, as the gateway writes it: the UTF-8 name in filename*, and an ASCII fallback in filename.
    internal static string AttachmentDisposition(string fileName)
    {
        var fallback = new string([.. fileName.Select(c => c is >= ' ' and < '\u007f' and not '"' and not '\\' ? c : '_')]);
        var header = new ContentDispositionHeaderValue("attachment") { FileName = $"\"{fallback}\"", FileNameStar = fileName };
        return header.ToString();
    }

    /// <summary>
    /// A user delegation key that lasts until <paramref name="expiresOn"/>, or as long as Azure allows (7 days, less a
    /// margin) when that is sooner, kept and reused while it does; the caller checks a URL against the key's expiry.
    /// Null when the client cannot get one: a refusal of its credential (401, 403: no token credential, or no
    /// <c>generateUserDelegationKey</c> right) is remembered for a few minutes; any other answer only fails this call.
    /// </summary>
    private async Task<UserDelegationKey?> UserDelegationKeyAsync(DateTimeOffset now, DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        var latest = now + _maxKeyLifetime;
        var wanted = expiresOn < latest ? expiresOn : latest;
        lock (_keyLock)
        {
            if (_delegationKey is { } cached && cached.SignedExpiresOn >= wanted && cached.SignedStartsOn <= now)
                return cached;
            if (now < _delegationRefusedUntil)
                return null;
        }
        // One key serves the URLs of the next hour or more.
        var keyExpiresOn = wanted > now + TimeSpan.FromHours(1) ? wanted : now + TimeSpan.FromHours(1);
        try
        {
            var key = (await _container.GetParentBlobServiceClient().GetUserDelegationKeyAsync(
                new BlobGetUserDelegationKeyOptions(keyExpiresOn) { StartsOn = now - _clockSkew }, cancellationToken).ConfigureAwait(false)).Value;
            lock (_keyLock)
                _delegationKey = key;
            return key;
        }
        catch (RequestFailedException e) when (e.Status is 400 or 401 or 403 or 404)
        {
            lock (_keyLock)
            {
                _delegationRefusal = e;
                if (e.Status is 401 or 403)
                    _delegationRefusedUntil = now + _refusalMemory;
            }
            return null;
        }
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerExists)
            return;
        if (!(await _container.ExistsAsync(cancellationToken).ConfigureAwait(false)).Value)
        {
            if (!Options.CreateContainerIfMissing)
                throw ContainerMissing(null);
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        _containerExists = true;
    }
}
