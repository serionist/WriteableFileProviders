using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace WriteableFileProviders.AspNetCore;

/// <summary>The <see cref="IWriteableFileUrlService"/>: the provider's native URL, or a gateway URL.</summary>
/// <param name="services">Where the providers are registered.</param>
/// <param name="codec">Writes gateway tokens.</param>
/// <param name="gatewayOptions">The gateway's options.</param>
/// <param name="timeProvider">The clock for expiry times.</param>
internal sealed class WriteableFileUrlService(
    IServiceProvider services, GatewayTokenCodec codec, IOptions<WriteableFileProviderGatewayOptions> gatewayOptions, TimeProvider timeProvider)
    : IWriteableFileUrlService
{
    public async Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options);
        var provider = services.GetService<IWriteableFileProvider>()
            ?? throw new InvalidOperationException(
                "No IWriteableFileProvider is registered without a key: register one with AddWriteableFileProvider, or pass the key of a keyed one.");
        return await IssueAsync(providerName: null, provider, path, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, string providerKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerKey);
        Validate(options);
        var provider = services.GetKeyedService<IWriteableFileProvider>(providerKey)
            ?? throw new ArgumentException($"No IWriteableFileProvider is registered under the key '{providerKey}'.", nameof(providerKey));
        return await IssueAsync(providerKey, provider, path, options, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(SignedUrlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.Validity, "The validity must be positive.");
        if (!Enum.IsDefined(options.Permission))
            throw new ArgumentOutOfRangeException(nameof(options), options.Permission, "Unknown access.");
    }

    // The provider's native URL, else a gateway URL. The provider's key goes into a gateway token, so the gateway serves
    // the URL from the same provider; null is the app's default provider.
    private async Task<SignedUrl> IssueAsync(
        string? providerName, IWriteableFileProvider provider, ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        if (!GatewayOptions.PreferGateway)
        {
            try
            {
                return await provider.GetSignedUrlAsync(path, options, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                // The provider cannot sign this URL natively: the gateway issues it.
            }
        }
        return await IssueGatewayUrlAsync(providerName, provider, path, options, cancellationToken).ConfigureAwait(false);
    }

    private WriteableFileProviderGatewayOptions GatewayOptions => gatewayOptions.Value;

    private async Task<SignedUrl> IssueGatewayUrlAsync(
        string? providerName, IWriteableFileProvider provider, ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        var gateway = GatewayOptions;
        var access = options.Permission;
        var maximum = access == SignedUrlAccess.Read ? gateway.MaxReadValidity : gateway.MaxUploadValidity;
        var lifetime = options.Validity;
        if (lifetime > maximum)
        {
            if (!options.AllowShorterValidity)
                throw new NotSupportedException($"The gateway issues {access} URLs that live at most {maximum}; {lifetime} was asked.");
            lifetime = maximum;
        }
        if (options.ContentType is { } contentType && !MediaTypeHeaderValue.TryParse(contentType, out _))
            throw new ArgumentException($"'{contentType}' is not a valid content type.", nameof(options));

        // The provider validates the path: a segment it refuses throws ArgumentException.
        if (path.IsRoot)
        {
            throw access == SignedUrlAccess.Read
                ? new ArgumentException("The path must contain at least one segment.", nameof(path))
                : new InvalidOperationException("The provider root cannot be deleted or modified.");
        }
        var info = await provider.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (access == SignedUrlAccess.Read && (!info.Exists || info.IsDirectory))
            throw new FileNotFoundException($"The file '{path}' does not exist.", path.ToString());

        var fileName = string.IsNullOrEmpty(options.DownloadFileName) ? path.Name : options.DownloadFileName;
        var expiresOn = timeProvider.GetUtcNow() + lifetime;
        var token = codec.Protect(providerName, new GatewayTokenPayload
        {
            Path = [.. path.Segments],
            Access = access,
            ContentType = options.ContentType,
            FileName = fileName,
            Id = Guid.NewGuid().ToString("N"),
        }, expiresOn);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (access != SignedUrlAccess.Read && options.ContentType is { } uploadType)
            headers[HeaderNames.ContentType] = uploadType;
        return new SignedUrl
        {
            Url = BuildUrl(gateway, token, fileName),
            Method = access == SignedUrlAccess.Read ? "GET" : "PUT",
            ExpiresOn = expiresOn,
            Headers = headers,
            Kind = SignedUrlKind.Gateway,
        };
    }

    /// <summary>
    /// <c>{PublicBaseUri}{BasePath}/{token}/{fileName}</c>; the file name is escaped, and left out when it is only dots,
    /// which a client would resolve as a relative path.
    /// </summary>
    /// <param name="gateway">The options.</param>
    /// <param name="token">The token.</param>
    /// <param name="fileName">The cosmetic file name.</param>
    internal static Uri BuildUrl(WriteableFileProviderGatewayOptions gateway, string token, string fileName)
    {
        var root = gateway.PublicBaseUri!.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var url = $"{root}{gateway.BasePath}/{token}";
        if (fileName.Trim('.').Length > 0)
            url += "/" + Uri.EscapeDataString(fileName);
        return new Uri(url);
    }
}
