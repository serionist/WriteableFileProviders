using Microsoft.AspNetCore.Http;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// A download that answers 404 when the file is gone by the time its bytes are read: the gateway reads a file's info
/// first and its bytes only as the response is written, so a delete in between surfaces as
/// <see cref="FileNotFoundException"/> from the first read, before the response has started. A path the provider has
/// come to refuse meanwhile (a link put on it, with Physical's <c>RefuseLinks</c>) surfaces as
/// <see cref="ArgumentException"/>, and answers 404 the same way, as a refused path does when the token is read.
/// </summary>
/// <param name="download">The download.</param>
internal sealed class NotFoundIfGoneResult(IResult download) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        try
        {
            await download.ExecuteAsync(httpContext).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException && !httpContext.Response.HasStarted)
        {
            httpContext.Response.Clear();
            GatewayRequestHandler.SetSecurityHeaders(httpContext.Response);
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }
}
