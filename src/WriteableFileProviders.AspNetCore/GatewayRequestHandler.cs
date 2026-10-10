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

    /// <summary>GET and HEAD: the file, with HTTP ranges where the provider can pin a version.</summary>
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

        Stream stream;
        bool pinned;
        try
        {
            try
            {
                // A pinned seekable stream: ranges, and an IOException rather than mixed content if the file changes.
                stream = await provider.OpenReadSeekableAsync(path, null, cancellationToken).ConfigureAwait(false);
                pinned = true;
            }
            catch (NotSupportedException)
            {
                // The provider keeps no version of the file to pin: serve it whole, without ranges or an ETag.
                stream = await provider.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
                pinned = false;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            return Results.NotFound();
        }

        IWriteableFileInfo info;
        try
        {
            // Read after the stream is open: a pinned stream serves the bytes of this version or fails, so the ETag sent
            // matches whatever body is sent.
            info = await provider.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (!info.Exists || info.IsDirectory)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return Results.NotFound();
        }

        // Content-Disposition: attachment, with the token's file name only, encoded per RFC 6266 by
        // ContentDispositionHeaderValue (an ASCII filename and a UTF-8 filename*).
        return Results.Stream(
            stream,
            ValidContentType(read.Payload.ContentType) ?? ValidContentType(info.ContentType) ?? DefaultContentType,
            fileDownloadName: string.IsNullOrEmpty(read.Payload.FileName) ? path.Name : read.Payload.FileName,
            lastModified: info.LastModified == default ? null : info.LastModified,
            entityTag: pinned ? EntityTag(info.ETag) : null,
            enableRangeProcessing: pinned);
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

    private static void SetSecurityHeaders(HttpResponse response)
    {
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
