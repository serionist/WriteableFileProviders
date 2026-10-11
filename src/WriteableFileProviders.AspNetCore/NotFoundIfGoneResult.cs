using Microsoft.AspNetCore.Http;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// A download that answers 404 when the file is gone by the time its bytes are read: the gateway reads a file's info
/// first and its bytes only as the response is written, so a delete in between surfaces as
/// <see cref="FileNotFoundException"/> from the first read, before the response has started.
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
        catch (FileNotFoundException) when (!httpContext.Response.HasStarted)
        {
            httpContext.Response.Clear();
            GatewayRequestHandler.SetSecurityHeaders(httpContext.Response);
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }
}
