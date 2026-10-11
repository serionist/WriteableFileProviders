using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WriteableFileProviders;
using WriteableFileProviders.AspNetCore;

// The ASP.NET Core convention: endpoint mappings live in Microsoft.AspNetCore.Builder, beside MapGet, so they need no
// using of their own.
#pragma warning disable IDE0130 // the namespace is the convention's, not this folder's
namespace Microsoft.AspNetCore.Builder;
#pragma warning restore IDE0130

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
    /// Every response sends <c>Content-Security-Policy: default-src 'none'; sandbox</c>,
    /// <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c> and
    /// <c>Cache-Control: private, no-store</c>. A download sends <c>Content-Disposition: attachment</c> with the token's
    /// file name, and the content type the read token names, else <c>application/octet-stream</c>: never a type the store
    /// keeps that the token did not name. It reads the file with one pinned
    /// <see cref="IWriteableFileProvider.ReadRangeAsync"/> per contiguous span (the whole file, or the HTTP range), with
    /// <see cref="ReadOptions.IfMatch"/> on the <c>ETag</c> it read first; a file the store keeps no <c>ETag</c> for is
    /// served whole, without ranges. An upload answers 201 (Create) or 200 (Write); 412 when a Create finds the file; 413
    /// over <see cref="WriteableFileProviderGatewayOptions.MaxUploadBytes"/>; 415 when the <c>Content-Type</c> differs
    /// from the token's. Any problem with the token itself answers 404.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The app's endpoints.</param>
    /// <returns>A builder for conventions on the gateway's endpoints.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddWriteableFileProviderGateway</c> was not called.
    /// </exception>
    /// <exception cref="OptionsValidationException">The options are not valid.</exception>
    public static IEndpointConventionBuilder MapWriteableFileProviderGateway(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var handler = endpoints.ServiceProvider.GetService<GatewayRequestHandler>()
            ?? throw new InvalidOperationException("Call services.AddWriteableFileProviderGateway(...) before mapping the gateway.");
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<WriteableFileProviderGatewayOptions>>().Value;

        // Plain request delegates, not route handlers: those bind parameters through reflection or a source generator,
        // and this mapping stays safe in a trimmed or native AOT app without either.
        var group = endpoints.MapGroup(options.BasePath);
        group.MapMethods("{token}/{fileName?}", _readMethods, context => ReadAsync(handler, context));
        group.MapPut("{token}/{fileName?}", context => WriteAsync(handler, context));
        group.AllowAnonymous();
        group.CacheOutput(policy => policy.NoCache());
        group.ExcludeFromDescription();
        return group;
    }

    private static async Task ReadAsync(GatewayRequestHandler handler, HttpContext context)
    {
        var result = await handler.ReadAsync(context, Token(context)).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task WriteAsync(GatewayRequestHandler handler, HttpContext context)
    {
        var result = await handler.WriteAsync(context, Token(context)).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private static string Token(HttpContext context) => (string)context.Request.RouteValues["token"]!;
}
