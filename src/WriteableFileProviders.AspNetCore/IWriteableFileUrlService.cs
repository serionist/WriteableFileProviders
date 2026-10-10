namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// Issues signed URLs for the providers registered with
/// <see cref="WriteableFileProviderServiceCollectionExtensions.AddWriteableFileProvider(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, Microsoft.Extensions.Configuration.IConfiguration)"/>:
/// the provider's own URL where it can sign one, and otherwise a URL of the gateway
/// (<see cref="WriteableFileProviderGatewayEndpointRouteBuilderExtensions.MapWriteableFileProviderGateway"/>), which
/// serves the file itself.
/// </summary>
public interface IWriteableFileUrlService
{
    /// <summary>
    /// Issues a URL that lets its holder download or upload one file for a limited time. The provider's native URL comes
    /// first, unless <see cref="WriteableFileProviderGatewayOptions.PreferGateway"/> is set; when the provider throws
    /// <see cref="NotSupportedException"/> (no native URLs, not for this access, not for this long, not with a download
    /// name or content type), the gateway issues one instead (<see cref="SignedUrlKind.Gateway"/>).
    /// </summary>
    /// <param name="providerName">The name the provider is registered under (a keyed <see cref="IWriteableFileProvider"/>).</param>
    /// <param name="path">The file, relative to the provider's root.</param>
    /// <param name="options">
    /// What the URL allows and how long it lives. A gateway URL honours <see cref="SignedUrlOptions.DownloadFileName"/>
    /// and <see cref="SignedUrlOptions.ContentType"/> for every access. An upload URL's
    /// <see cref="SignedUrl.Headers"/> name the <c>Content-Type</c> the upload must send, when one is set.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException">
    /// No provider is registered under <paramref name="providerName"/>; the path is the root of a download or has a segment
    /// the provider refuses; or <see cref="SignedUrlOptions.ContentType"/> is not a valid content type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The validity is not positive, or the access is unknown.</exception>
    /// <exception cref="InvalidOperationException">The path of an upload is the provider root.</exception>
    /// <exception cref="FileNotFoundException">A download URL for a file that does not exist, or for a folder.</exception>
    /// <exception cref="NotSupportedException">
    /// Neither the provider nor the gateway can issue the URL: the validity is longer than the gateway's maximum for the
    /// access (<see cref="WriteableFileProviderGatewayOptions.MaxReadValidity"/>,
    /// <see cref="WriteableFileProviderGatewayOptions.MaxUploadValidity"/>) and
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/> is not set.
    /// </exception>
    Task<SignedUrl> GetSignedUrlAsync(string providerName, ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default);
}
