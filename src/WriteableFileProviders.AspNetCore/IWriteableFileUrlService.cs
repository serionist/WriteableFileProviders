namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// Issues signed URLs for the app's <see cref="IWriteableFileProvider"/>, or for one it registered under a key: the
/// provider's own URL where it can sign one, and otherwise a URL of the gateway
/// (<see cref="Microsoft.AspNetCore.Builder.WriteableFileProviderGatewayEndpointRouteBuilderExtensions.MapWriteableFileProviderGateway"/>), which
/// serves the file itself.
/// </summary>
/// <remarks>
/// <para>
/// Without a key, the URL is for the provider the container gives for <see cref="IWriteableFileProvider"/>, as registered
/// with <c>AddWriteableFileProvider</c> or <c>AddSingleton&lt;IWriteableFileProvider&gt;</c> (the last one registered,
/// when there are several). With a key, it is for the provider registered with
/// <c>AddKeyedSingleton&lt;IWriteableFileProvider&gt;(key, ...)</c>. The gateway serves each URL from the same provider.
/// </para>
/// <para>
/// The provider's native URL comes first, unless <see cref="WriteableFileProviderGatewayOptions.PreferGateway"/> is set;
/// when the provider throws <see cref="NotSupportedException"/> (no native URLs, not for this access, not for this long,
/// not with a download name or content type), the gateway issues one instead (<see cref="SignedUrlKind.Gateway"/>).
/// </para>
/// <para>
/// The gateway's own rules bind only gateway URLs (<see cref="SignedUrlKind.Gateway"/>):
/// <see cref="WriteableFileProviderGatewayOptions.OneTimeUploadTokens"/>,
/// <see cref="WriteableFileProviderGatewayOptions.MaxReadValidity"/> and
/// <see cref="WriteableFileProviderGatewayOptions.MaxUploadValidity"/>. A native URL follows its provider: a native
/// upload URL can be used again until it expires (a Create URL fails once the file exists), and lives up to the
/// provider's own maximum (7 days). An app that needs the same upload rules on every provider sets
/// <see cref="WriteableFileProviderGatewayOptions.PreferGateway"/>.
/// </para>
/// </remarks>
public interface IWriteableFileUrlService
{
    /// <summary>
    /// Issues a URL that lets its holder download or upload one file of the app's <see cref="IWriteableFileProvider"/>
    /// for a limited time: the provider's native URL, or a gateway URL.
    /// </summary>
    /// <param name="path">The file, relative to the provider's root.</param>
    /// <param name="options">
    /// What the URL allows and how long it lives. A gateway URL honours <see cref="SignedUrlOptions.DownloadFileName"/>
    /// and <see cref="SignedUrlOptions.ContentType"/> for every access. An upload URL's
    /// <see cref="SignedUrl.Headers"/> name the <c>Content-Type</c> the upload must send, when one is set.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IWriteableFileProvider"/> is registered without a key; or the path of an upload is the provider root.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The path is the root of a download or has a segment the provider refuses; or
    /// <see cref="SignedUrlOptions.ContentType"/> is not a valid content type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The validity is not positive, or the access is unknown.</exception>
    /// <exception cref="FileNotFoundException">A download URL for a file that does not exist, or for a folder.</exception>
    /// <exception cref="NotSupportedException">
    /// Neither the provider nor the gateway can issue the URL: the validity is longer than the gateway's maximum for the
    /// access (<see cref="WriteableFileProviderGatewayOptions.MaxReadValidity"/>,
    /// <see cref="WriteableFileProviderGatewayOptions.MaxUploadValidity"/>) and
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/> is not set.
    /// </exception>
    Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a URL that lets its holder download or upload one file of the provider registered under
    /// <paramref name="providerKey"/> (<c>AddKeyedSingleton&lt;IWriteableFileProvider&gt;(providerKey, ...)</c>) for a
    /// limited time: the provider's native URL, or a gateway URL.
    /// </summary>
    /// <param name="path">The file, relative to the provider's root.</param>
    /// <param name="options">
    /// What the URL allows and how long it lives, as for
    /// <see cref="GetSignedUrlAsync(ProviderPath, SignedUrlOptions, CancellationToken)"/>.
    /// </param>
    /// <param name="providerKey">The key the provider is registered under; not empty.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="providerKey"/> is null or empty, or no <see cref="IWriteableFileProvider"/> is registered under it;
    /// the path is the root of a download or has a segment the provider refuses; or
    /// <see cref="SignedUrlOptions.ContentType"/> is not a valid content type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The validity is not positive, or the access is unknown.</exception>
    /// <exception cref="InvalidOperationException">The path of an upload is the provider root.</exception>
    /// <exception cref="FileNotFoundException">A download URL for a file that does not exist, or for a folder.</exception>
    /// <exception cref="NotSupportedException">
    /// Neither the provider nor the gateway can issue the URL, as for
    /// <see cref="GetSignedUrlAsync(ProviderPath, SignedUrlOptions, CancellationToken)"/>.
    /// </exception>
    Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, string providerKey, CancellationToken cancellationToken = default);
}
