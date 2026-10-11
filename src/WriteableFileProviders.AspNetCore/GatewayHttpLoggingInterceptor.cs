using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Options;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// Keeps gateway tokens out of ASP.NET Core's HTTP logging (<c>UseHttpLogging</c>): for a request whose path holds the
/// gateway's base path, the request path is replaced by the route template. Other logs that carry request paths are the
/// app's to redact (the package README lists them).
/// </summary>
/// <param name="options">The gateway's options.</param>
internal sealed class GatewayHttpLoggingInterceptor(IOptions<WriteableFileProviderGatewayOptions> options) : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
    {
        ArgumentNullException.ThrowIfNull(logContext);
        var basePath = options.Value.BasePath;
        var request = logContext.HttpContext.Request;
        // PathBase and Path together, so the check holds whether the logging runs before or after UsePathBase. It may
        // redact another route that shares the base path's segments, which costs nothing.
        var fullPath = request.PathBase.Add(request.Path).Value ?? "";
        if (fullPath.Contains(basePath + "/", StringComparison.OrdinalIgnoreCase) && logContext.TryDisable(HttpLoggingFields.RequestPath))
            logContext.AddParameter("Path", basePath + "/{token}/{fileName}");
        return default;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext) => default;
}
