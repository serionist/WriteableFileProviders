using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// Serves the gateway's endpoints: GET and HEAD download a file, PUT uploads one. Whatever is wrong with a token (malformed,
/// tampered with, expired, for another provider or access, already used, or naming a path the provider refuses) answers
/// 404, so a caller learns nothing about why. The path comes only from the token, and the provider validates it again.
/// </summary>
/// <param name="codec">Reads tokens.</param>
/// <param name="replayStore">Remembers used one-time tokens.</param>
/// <param name="options">The gateway's options.</param>
internal sealed class GatewayRequestHandler(GatewayTokenCodec codec, IGatewayReplayStore replayStore, IOptions<WriteableFileProviderGatewayOptions> options)
{
    private const string DefaultContentType = "application/octet-stream";

    /// <summary>GET and HEAD: the file, with HTTP ranges where the provider keeps a version to pin.</summary>
    /// <param name="context">The request.</param>
    /// <param name="token">The token from the URL.</param>
    public async Task<IResult> ReadAsync(HttpContext context, string token)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetSecurityHeaders(context.Response);
        var cancellationToken = context.RequestAborted;
        if (codec.Read(token) is not { Payload.Access: SignedUrlAccess.Read } read || ProviderFor(context, read) is not { } provider)
            return Results.NotFound();
        var path = new ProviderPath(read.Payload.Path);
        if (path.IsRoot)
            return Results.NotFound();

        IWriteableFileInfo info;
        try
        {
            info = await provider.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }
        if (!info.Exists || info.IsDirectory)
            return Results.NotFound();

        // Only the type the read token names, never one the store holds: an untyped upload URL let its holder choose that
        // one, and serving it back would let them pick text/javascript or text/html on this origin.
        var contentType = ValidContentType(read.Payload.ContentType) ?? DefaultContentType;
        // Content-Disposition: attachment, with the token's file name only, encoded per RFC 6266 by
        // ContentDispositionHeaderValue (an ASCII filename and a UTF-8 filename*).
        var fileName = string.IsNullOrEmpty(read.Payload.FileName) ? path.Name : read.Payload.FileName;
        DateTimeOffset? lastModified = info.LastModified == default ? null : info.LastModified;

        if (info.ETag is not { } version)
        {
            // The store keeps no version of the file, so nothing can pin a read: serve it whole, without ranges or an ETag,
            // in one read from the start, opened only when the body is written (a HEAD reads nothing).
            var whole = new PinnedRangeStream(provider, path, info.Length, etag: null, window: null);
            return new NotFoundIfGoneResult(Results.Stream(whole, contentType, fileName, lastModified, entityTag: null, enableRangeProcessing: false));
        }

        // One ranged read of the store per contiguous span, pinned to the version read above: the whole file or one HTTP
        // range costs one request, and a file that changes meanwhile fails the response rather than mixing versions.
        var entityTag = EntityTag(version);
        var stream = new PinnedRangeStream(provider, path, info.Length, version, RangeWindow(context.Request, info.Length, entityTag, lastModified));
        return new NotFoundIfGoneResult(Results.Stream(stream, contentType, fileName, lastModified, entityTag, enableRangeProcessing: true));
    }

    /// <summary>PUT: stores the body, creating the file only if absent (Create) or replacing it (Write).</summary>
    /// <param name="context">The request.</param>
    /// <param name="token">The token from the URL.</param>
    public async Task<IResult> WriteAsync(HttpContext context, string token)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetSecurityHeaders(context.Response);
        var cancellationToken = context.RequestAborted;
        if (codec.Read(token) is not { } read || read.Payload.Access == SignedUrlAccess.Read || ProviderFor(context, read) is not { } provider)
            return Results.NotFound();
        var path = new ProviderPath(read.Payload.Path);
        if (path.IsRoot)
            return Results.NotFound();

        // Everything the request can get wrong is refused before a one-time token is used up.
        var gateway = options.Value;
        if (ContentTypeFor(read.Payload, context.Request.ContentType, out var contentType) is { } refused)
            return refused;
        if (context.Request.ContentLength > gateway.MaxUploadBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } serverLimit)
            serverLimit.MaxRequestBodySize = gateway.MaxUploadBytes;
        if (gateway.OneTimeUploadTokens
            && !await replayStore.TryConsumeAsync(read.Payload.Id, read.ExpiresOn, cancellationToken).ConfigureAwait(false))
            return Results.NotFound();

        var create = read.Payload.Access == SignedUrlAccess.Create;
        var body = new UploadLimitStream(context.Request.Body, gateway.MaxUploadBytes);
        await using (body.ConfigureAwait(false))
        {
            try
            {
                var write = new UpdateFileOptions { IfNotExists = create, ContentType = contentType };
                await provider.UpdateFileAsync(path, body, write, cancellationToken).ConfigureAwait(false);
            }
            catch (PreconditionFailedException)
            {
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }
            catch (ArgumentException)
            {
                return Results.NotFound();
            }
            catch (Exception e) when (BadRequestIn(e) is { } bad)
            {
                // The body grew past MaxUploadBytes (ours or the server's limit), or the server found it malformed.
                return Results.StatusCode(bad.StatusCode);
            }
        }
        return create ? Results.StatusCode(StatusCodes.Status201Created) : Results.Ok();
    }

    private static IWriteableFileProvider? ProviderFor(HttpContext context, GatewayToken token) =>
        context.RequestServices.GetKeyedService<IWriteableFileProvider>(token.ProviderName);

    internal static void SetSecurityHeaders(HttpResponse response)
    {
        // A response is never a document: no script, style or plugin runs from it, even when a browser renders it.
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "private, no-store";
    }

    // The content type to store, or the 415 that refuses the request: a token's content type must be sent exactly
    // (compared without case); without one, the request's own is stored if it is valid.
    private static IResult? ContentTypeFor(GatewayTokenPayload payload, string? requestContentType, out string? contentType)
    {
        contentType = payload.ContentType ?? requestContentType;
        if (payload.ContentType is { } expected)
        {
            return string.Equals(requestContentType?.Trim(), expected, StringComparison.OrdinalIgnoreCase)
                ? null
                : Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
        return requestContentType is null || MediaTypeHeaderValue.TryParse(requestContentType, out _)
            ? null
            : Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    /// <summary>
    /// The span of the file a request's single <c>Range</c> asks for, as a hint for how far the first read reaches; null
    /// when there is none, or several, or when an <c>If-Range</c> does not match, so the whole file is served. ASP.NET
    /// Core decides what is served: a read outside the hint opens another range.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="length">The file's length.</param>
    /// <param name="entityTag">The file's entity tag, as sent.</param>
    /// <param name="lastModified">The file's last-modified time, as sent.</param>
    internal static (long Start, long End)? RangeWindow(HttpRequest request, long length, EntityTagHeaderValue? entityTag, DateTimeOffset? lastModified)
    {
        var headers = request.GetTypedHeaders();
        var range = headers.Range;
        if (range is null || !string.Equals(range.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) || range.Ranges.Count != 1)
            return null;
        if (headers.IfRange is { } ifRange && !IfRangeMatches(ifRange, entityTag, lastModified))
            return null;
        var item = range.Ranges.First();
        long start, end;
        if (item.From is { } from)
        {
            start = from;
            end = Math.Min(item.To ?? length - 1, length - 1);
        }
        else
        {
            start = Math.Max(0, length - (item.To ?? 0));
            end = length - 1;
        }
        return start <= end ? (start, end) : null;
    }

    // As ASP.NET Core judges it: a date no earlier than the file's last change (HTTP dates carry whole seconds), or an
    // entity tag compared strongly; with nothing sent to compare against, the range stands.
    private static bool IfRangeMatches(RangeConditionHeaderValue ifRange, EntityTagHeaderValue? entityTag, DateTimeOffset? lastModified)
    {
        if (ifRange.LastModified is { } date)
            return lastModified is not { } modified || new DateTimeOffset(modified.UtcTicks - modified.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero) <= date;
        return entityTag is null || ifRange.EntityTag is not { } tag || tag.Compare(entityTag, useStrongComparison: true);
    }

    private static string? ValidContentType(string? contentType) =>
        contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out _) ? contentType : null;

    // A store's ETag as an HTTP entity tag: quoted as it is when it already is one, otherwise quoted here.
    internal static EntityTagHeaderValue? EntityTag(string? etag)
    {
        if (string.IsNullOrEmpty(etag))
            return null;
        if (EntityTagHeaderValue.TryParse(etag, out var parsed))
            return parsed;
        return EntityTagHeaderValue.TryParse($"\"{etag}\"", out parsed) ? parsed : null;
    }

    internal static BadHttpRequestException? BadRequestIn(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is BadHttpRequestException bad)
                return bad;
            if (exception is AggregateException aggregate)
                return aggregate.InnerExceptions.Select(BadRequestIn).FirstOrDefault(found => found is not null);
        }
        return null;
    }
}
