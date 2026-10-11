using System.Security.Cryptography;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using Box.Sdk.Gen.Schemas;
using Task = System.Threading.Tasks.Task;

namespace WriteableFileProviders.Box;

public sealed partial class WriteableBoxFileProvider
{
    private const long GiB = 1L << 30;

    /// <inheritdoc />
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == UpdateFileMode.Append)
        {
            await AppendByRewriteAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
            return;
        }
        // The SDK rewinds and measures what it uploads: copy the caller's stream from its position first. A stream that
        // fails here leaves Box untouched.
        var spooled = await SpoolToTempFileAsync(contents, cancellationToken).ConfigureAwait(false);
        await using (spooled.ConfigureAwait(false))
        {
            var parentId = await EnsureFolderAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                await UploadAsync(path, parentId, spooled, options, cancellationToken).ConfigureAwait(false);
            }
            catch (BoxApiException e) when (IsNameRefused(e))
            {
                throw NameRefused(path, e);
            }
        }
    }

    private async Task UploadAsync(ResolvedPath path, string parentId, FileStream content, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var existing = await FindChildAsync(parentId, path.Name, cancellationToken).ConfigureAwait(false);
            if (existing is { IsFolder: true })
                throw new IOException($"The path '{path}' is a folder.");
            // The version found must be the one asked for, whether or not Box checks it again on the wire.
            if (options.IfMatch is not null && !string.Equals(existing?.ETag, options.IfMatch, StringComparison.Ordinal))
                throw Changed(path, options.IfMatch);
            if (existing is not null && options.IfNotExists)
                throw new PreconditionFailedException($"The file '{path}' already exists.");
            try
            {
                if (existing is null)
                    await UploadNewAsync(parentId, path.Name, content, cancellationToken).ConfigureAwait(false);
                else
                    await UploadVersionAsync(path, existing, content, options.IfMatch, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (BoxApiException e) when (attempt == 1 && (existing is null ? IsNameInUse(e) : e.ResponseInfo.StatusCode == 404))
            {
                // Someone else created or deleted the file meanwhile: find it again, and check the conditions again.
            }
        }
    }

    private async Task UploadNewAsync(string parentId, string name, FileStream content, CancellationToken cancellationToken)
    {
        content.Position = 0;
        if (content.Length >= ChunkedUploadThreshold)
        {
            var session = await _client.ChunkedUploads.CreateFileUploadSessionAsync(
                new CreateFileUploadSessionRequestBody(parentId, content.Length, name), cancellationToken: cancellationToken).ConfigureAwait(false);
            await UploadChunkedAsync(session, content, null, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Never set the file part's content type: the SDK would then rename the part (its avatar upload), and Box keeps none.
        await _client.Uploads.UploadFileAsync(
            new UploadFileRequestBody(new UploadFileRequestBodyAttributesField(name, new UploadFileRequestBodyAttributesParentField(parentId)), content),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ifMatch is the caller's ETag, which the file found already has: on the wire goes Box's own etag of that same item.
    private async Task UploadVersionAsync(ResolvedPath path, Entry file, FileStream content, string? ifMatch, CancellationToken cancellationToken)
    {
        content.Position = 0;
        var boxIfMatch = SendIfMatch && ifMatch is not null ? file.BoxETag : null;
        try
        {
            if (content.Length >= ChunkedUploadThreshold)
            {
                var session = await _client.ChunkedUploads.CreateFileUploadSessionForExistingFileAsync(file.Id,
                    new CreateFileUploadSessionForExistingFileRequestBody(content.Length), cancellationToken: cancellationToken).ConfigureAwait(false);
                await UploadChunkedAsync(session, content, boxIfMatch, cancellationToken).ConfigureAwait(false);
                return;
            }
            // The file keeps its name, also when the path differs from it in case.
            await _client.Uploads.UploadFileVersionAsync(file.Id,
                new UploadFileVersionRequestBody(new UploadFileVersionRequestBodyAttributesField(file.Name), content),
                headers: new UploadFileVersionHeaders { IfMatch = Quote(boxIfMatch) },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (BoxApiException e) when (e.ResponseInfo.StatusCode == 412 && boxIfMatch is not null)
        {
            throw Changed(path, ifMatch!, e);
        }
    }

    // The provider's own chunked loop: every part is exactly the session's part size but the last, each with its digest;
    // the commit carries the whole file's digest and the condition. Box makes the file or version only at the commit, so
    // a failure leaves the old content; the session is then aborted.
    private async Task UploadChunkedAsync(UploadSession session, FileStream content, string? ifMatch, CancellationToken cancellationToken)
    {
        var sessionId = session.Id ?? throw new IOException("Box opened an upload session without an id.");
        try
        {
            var partSize = session.PartSize is > 0 and <= int.MaxValue and var size
                ? (int)size
                : throw new IOException($"Box opened upload session '{sessionId}' with no usable part size.");
            var length = content.Length;
            var parts = new List<UploadPart>();
            var buffer = new byte[(int)Math.Min(partSize, length)];
            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            for (long offset = 0; offset < length;)
            {
                var count = (int)Math.Min(partSize, length - offset);
                await content.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                whole.AppendData(buffer, 0, count);
                var part = new MemoryStream(buffer, 0, count, writable: false);
                await using (part.ConfigureAwait(false))
                {
                    var uploaded = await _client.ChunkedUploads.UploadFilePartAsync(sessionId, part,
                        new UploadFilePartHeaders(Digest(buffer.AsSpan(0, count)), $"bytes {offset}-{offset + count - 1}/{length}"),
                        cancellationToken).ConfigureAwait(false);
                    parts.Add(uploaded.Part ?? throw new IOException($"Box did not describe part {offset} of upload session '{sessionId}'."));
                }
                offset += count;
            }
            var headers = new CreateFileUploadSessionCommitHeaders("sha=" + Convert.ToBase64String(whole.GetHashAndReset())) { IfMatch = Quote(ifMatch) };
            var body = new CreateFileUploadSessionCommitRequestBody(parts);
            var limit = NotReadyTimeout * (1 + (length / GiB));
            var started = TimeProvider.GetTimestamp();
            while (true)
            {
                // Null is Box's 202: the parts are still being processed.
                if (await _client.ChunkedUploads.CreateFileUploadSessionCommitAsync(sessionId, body, headers, cancellationToken).ConfigureAwait(false) is not null)
                    return;
                if (TimeProvider.GetElapsedTime(started) + PollDelay > limit)
                    throw new IOException($"Box was still processing upload session '{sessionId}' after {limit}.");
                await Task.Delay(PollDelay, TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await AbortAsync(sessionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AbortAsync(string sessionId)
    {
        try
        {
            await _client.ChunkedUploads.DeleteFileUploadSessionByIdAsync(sessionId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (BoxSdkException)
        {
            // Best effort: the caller is already reporting the failure that matters, and Box drops sessions after 7 days.
        }
    }

#pragma warning disable CA5350 // Box's part digest is SHA-1: the protocol asks for it; it is an integrity check, not security.
    private static string Digest(ReadOnlySpan<byte> part) => "sha=" + Convert.ToBase64String(SHA1.HashData(part));
#pragma warning restore CA5350

    // The SDK adds headers with validation, so an ETag must be an RFC entity tag: quoted.
    private static string? Quote(string? etag) =>
        etag is null || etag.StartsWith('"') || etag.StartsWith("W/", StringComparison.Ordinal) ? etag : $"\"{etag}\"";

    // Streams the file version that was found, or a byte range of it, from Box's pre-authenticated download URL. The SDK's
    // own download drops the Range header when it follows Box's redirect, so the range goes to the URL itself.
    private async Task<Stream> DownloadAsync(ResolvedPath path, Entry file, long offset, long? length, CancellationToken cancellationToken)
    {
        var url = await GetDownloadUrlAsync(path, file, cancellationToken).ConfigureAwait(false);
        var count = Math.Min(length ?? long.MaxValue, file.Length - offset);
        var ranged = offset > 0 || count < file.Length;
        FetchResponse response;
        try
        {
            response = await Session.NetworkClient.FetchAsync(new FetchOptions(url, "GET", responseFormat: ResponseFormat.Binary)
            {
                Headers = ranged ? new Dictionary<string, string> { ["range"] = $"bytes={offset}-{offset + count - 1}" } : null,
                NetworkSession = Session,
                CancellationToken = cancellationToken,
            }).ConfigureAwait(false);
        }
        catch (BoxSdkException e)
        {
            // The SDK's error names the URL, which is a credential: keep the status only.
            var status = e is BoxApiException api ? api.ResponseInfo.StatusCode : 0;
            throw status == 404
                ? new FileNotFoundException($"Box's download of '{path}' answered HTTP 404.", path.ToString())
                : new IOException(status == 0 ? $"Box's download of '{path}' failed before an answer." : $"Box's download of '{path}' answered HTTP {status}.");
        }
        var body = response.Content ?? throw new IOException($"Box sent no content for '{path}'.");
        if (!ranged)
            return body;
        // 206 is the range; any other success is the whole file, which the stream then skips into.
        return new SliceStream(body, response.Status == 206 ? 0 : offset, count);
    }

    private async Task<string> GetDownloadUrlAsync(ResolvedPath path, Entry file, CancellationToken cancellationToken)
    {
        var started = TimeProvider.GetTimestamp();
        while (true)
        {
            var response = await Session.NetworkClient.FetchAsync(new FetchOptions(
                $"{Session.BaseUrls.BaseUrl}/2.0/files/{Uri.EscapeDataString(file.Id)}/content", "GET", responseFormat: ResponseFormat.NoContent)
            {
                Parameters = file.VersionId is null ? null : new Dictionary<string, string> { ["version"] = file.VersionId },
                Auth = _client.Auth,
                NetworkSession = Session,
                CancellationToken = cancellationToken,
                FollowRedirects = false,
            }).ConfigureAwait(false);
            if (Header(response, "Location") is { } location)
                return location;
            // 202: Box is not ready to serve a file that was just uploaded.
            if (response.Status != 202)
                throw new IOException($"Box gave no download URL for '{path}' (HTTP {response.Status}).");
            var delay = RetryAfter(response);
            if (TimeProvider.GetElapsedTime(started) + delay > NotReadyTimeout)
                throw new IOException($"Box did not make '{path}' ready to download within {NotReadyTimeout}.");
            await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    // As long as Box asks, at least the poll delay, at most 30 seconds.
    private TimeSpan RetryAfter(FetchResponse response) =>
        Header(response, "Retry-After") is { } value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, PollDelay.TotalSeconds, _maxRetryAfter.TotalSeconds))
            : PollDelay;

    private static string? Header(FetchResponse response, string name) =>
        response.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
