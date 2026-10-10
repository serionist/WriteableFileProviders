using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WriteableFileProviders.AspNetCore;

/// <summary>Maps the signed-URL gateway's endpoints.</summary>
public static class WriteableFileProviderGatewayEndpointRouteBuilderExtensions
{
    private static readonly string[] _readMethods = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>
    /// Maps the gateway under <see cref="WriteableFileProviderGatewayOptions.BasePath"/>:
    /// <c>GET</c> and <c>HEAD {BasePath}/{token}/{fileName?}</c> download a file, with HTTP ranges, and
    /// <c>PUT {BasePath}/{token}/{fileName?}</c> uploads one. The <c>{fileName}</c> segment is cosmetic and ignored. Call
    /// it once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is the credential, so the endpoints allow anonymous access: a fallback authorization policy does not apply
    /// to them. They are left out of output caching, since a cached response would outlive its token, and out of API
    /// descriptions. Add rate limiting, CORS or host filtering through the returned builder.
    /// </para>
    /// <para>
    /// A download sends <c>Content-Disposition: attachment</c> with the token's file name, the token's content type (else
    /// the stored one, else <c>application/octet-stream</c>), <c>X-Content-Type-Options: nosniff</c>,
    /// <c>Referrer-Policy: no-referrer</c> and <c>Cache-Control: private, no-store</c>. Ranges and the <c>ETag</c> need a
    /// provider that pins a version (<see cref="IWriteableFileProvider.OpenReadSeekableAsync"/>); a provider that keeps no
    /// version of a file serves it whole. An upload answers 201 (Create) or 200 (Write); 412 when a Create finds the file;
    /// 413 over <see cref="WriteableFileProviderGatewayOptions.MaxUploadBytes"/>; 415 when the <c>Content-Type</c> differs
    /// from the token's. Any problem with the token itself answers 404.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The app's endpoints.</param>
    /// <returns>A builder for conventions on the gateway's endpoints.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="WriteableFileProviderGatewayServiceCollectionExtensions.AddWriteableFileProviderGateway"/> was not called.
    /// </exception>
    /// <exception cref="OptionsValidationException">The options are not valid.</exception>
    public static IEndpointConventionBuilder MapWriteableFileProviderGateway(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var handler = endpoints.ServiceProvider.GetService<GatewayRequestHandler>()
            ?? throw new InvalidOperationException("Call services.AddWriteableFileProviderGateway(...) before mapping the gateway.");
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<WriteableFileProviderGatewayOptions>>().Value;

        var group = endpoints.MapGroup(options.BasePath);
        group.MapMethods("{token}/{fileName?}", _readMethods, (HttpContext context, string token) => handler.ReadAsync(context, token));
        group.MapPut("{token}/{fileName?}", (HttpContext context, string token) => handler.WriteAsync(context, token));
        group.AllowAnonymous();
        group.CacheOutput(policy => policy.NoCache());
        group.ExcludeFromDescription();
        return group;
    }
}
