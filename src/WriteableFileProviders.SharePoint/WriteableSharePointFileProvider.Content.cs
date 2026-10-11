using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Graph.Drives.Item.Items.Item.Copy;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace WriteableFileProviders.SharePoint;

public sealed partial class WriteableSharePointFileProvider
{
    // ----- Reads -----

    /// <inheritdoc />
    /// <remarks>Streams the file from its download URL; nothing is buffered on disk.</remarks>
    protected override async Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var item = await GetDownloadableAsync(path, cancellationToken).ConfigureAwait(false);
        return item.Size == 0
            ? new MemoryStream([], writable: false)
            : await DownloadAsync(path, item, 0, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the range from the file's download URL. <paramref name="ifMatch"/> is checked against the file's ETag just
    /// before, which is not atomic: Graph's download URLs take no condition.
    /// </remarks>
    protected override async Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var item = await GetDownloadableAsync(path, cancellationToken).ConfigureAwait(false);
        if (ifMatch is not null && !string.Equals(item.ETag, ifMatch, StringComparison.Ordinal))
            throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{ifMatch}'.");
        // A size the service does not report is left to the download: a range past the end answers 416, read as empty.
        return length == 0 || (item.Size is { } size && offset >= size)
            ? new MemoryStream([], writable: false)
            : await DownloadAsync(path, item, offset, length, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Pinned to the file's ETag unless modifications are allowed, checked before each fetch (not atomic). A file whose
    /// size Graph does not report is measured first, from one byte of its download.
    /// </remarks>
    protected override async Task<Stream> OpenReadSeekableCoreAsync(ResolvedPath path, SeekableReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        var info = await GetFileInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.Exists || info.IsDirectory)
            throw NotFound(path);
        if (!options.AllowModifications && info.ETag is null)
            throw new NotSupportedException($"{GetType().Name} keeps no version of '{path}', so a read cannot be pinned to one; allow modifications to read it unpinned.");
        var pin = options.AllowModifications ? null : info.ETag;
        var length = info.Length >= 0 ? info.Length : await MeasureAsync(path, pin, cancellationToken).ConfigureAwait(false);
        return new RangeReadStream(length, (offset, count, token) => ReadRangeCoreAsync(path, offset, count, pin, token), options.BufferSize);
    }

    /// <summary>The length of a file whose size Graph does not report: from the answer to a request for its first byte.</summary>
    private async Task<long> MeasureAsync(ResolvedPath path, string? pin, CancellationToken cancellationToken)
    {
        var item = await GetDownloadableAsync(path, cancellationToken).ConfigureAwait(false);
        if (pin is not null && !string.Equals(item.ETag, pin, StringComparison.Ordinal))
            throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{pin}'.");
        var url = DownloadUrlOf(item) ?? throw new IOException($"Reading '{path}' failed: Microsoft Graph gave no download URL for it.");
        var request = new RequestInformation { HttpMethod = Method.GET, URI = url };
        request.Headers.Add("Range", "bytes=0-0");
        using var response = await SendRawAsync(request, $"Reading '{path}'", streamed: true, cancellationToken).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.PartialContent when response.Content.Headers.ContentRange?.Length is { } total => total,
            HttpStatusCode.RequestedRangeNotSatisfiable => response.Content.Headers.ContentRange?.Length ?? 0,
            HttpStatusCode.OK when response.Content.Headers.ContentLength is { } whole => whole,
            HttpStatusCode.NotFound => throw NotFound(path),
            _ => throw new IOException($"Reading '{path}' failed: the service did not say how long the file is ({(int)response.StatusCode})."),
        };
    }

    /// <summary>The file's metadata with its download URL; a missing path or a folder is not found.</summary>
    private async Task<DriveItem> GetDownloadableAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
        var item = await TryGetItemAsync(driveId, LibrarySegments(path), _downloadFields, $"Reading '{path}'", cancellationToken).ConfigureAwait(false);
        return item is null || item.Folder is not null ? throw NotFound(path) : item;
    }

    private async Task<Stream> DownloadAsync(ResolvedPath path, DriveItem item, long offset, long? length, CancellationToken cancellationToken)
    {
        var url = DownloadUrlOf(item) ?? throw new IOException($"Reading '{path}' failed: Microsoft Graph gave no download URL for it.");
        // The URL is pre-authenticated: requested through the Graph client's adapter, it carries no Graph token, because
        // the SDK sends tokens to Graph's hosts only. URI, not a template, so the URL is sent as it is.
        var request = new RequestInformation { HttpMethod = Method.GET, URI = url };
        var ranged = offset > 0 || length is not null;
        if (ranged)
            request.Headers.Add("Range", length is { } count ? $"bytes={offset}-{offset + count - 1}" : $"bytes={offset}-");
        var response = await SendRawAsync(request, $"Reading '{path}'", streamed: true, cancellationToken).ConfigureAwait(false);
        try
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    // The service may ignore a range it cannot serve and send the whole file.
                    var whole = new DownloadStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), length);
                    await whole.SkipAsync(offset, cancellationToken).ConfigureAwait(false);
                    return whole;
                case HttpStatusCode.PartialContent when !ranged || response.Content.Headers.ContentRange?.From == offset:
                    return new DownloadStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), length);
                case HttpStatusCode.PartialContent:
                    throw new IOException($"Reading '{path}' failed: the service sent another range than the one asked.");
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    // The file shrank since its size was read.
                    response.Dispose();
                    return new MemoryStream([], writable: false);
                case HttpStatusCode.NotFound:
                    throw NotFound(path);
                default:
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw GraphErrors.FromResponse((int)response.StatusCode, body, $"Reading '{path}'");
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static Uri? DownloadUrlOf(DriveItem item) =>
        item.AdditionalData.TryGetValue(DownloadUrlAnnotation, out var value) && value is string { Length: > 0 } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : null;

    // ----- Writes -----

    /// <inheritdoc />
    /// <remarks>
    /// The content, from the stream's position, is copied to a temp file first, so a source that fails sends nothing and
    /// the old file stays. Up to <see cref="SimpleUploadLimit"/> it goes in one request, which replaces the file at once;
    /// above, through an upload session that the service commits with its last fragment. An append rewrites the file
    /// with <c>If-Match</c> on the version it read (<see cref="WriteableFileProvider{TOptions}.AppendByRewriteAsync"/>).
    /// </remarks>
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == UpdateFileMode.Append)
        {
            await AppendByRewriteAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Kiota disposes a request's body, and an upload session reads it at absolute offsets: neither may touch the
        // caller's stream.
        var spooled = await SpoolToTempFileAsync(contents, cancellationToken).ConfigureAwait(false);
        await using (spooled.ConfigureAwait(false))
        {
            var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
            var what = $"Writing '{path}'";
            // The library's top level always exists; any other parent is created if missing.
            if (LibrarySegments(path.Parent).Length > 0)
                await EnsureFolderAsync(driveId, path.Parent.RelativeSegments, what, cancellationToken).ConfigureAwait(false);
            if (spooled.Length <= SimpleUploadLimit)
                await PutAsync(driveId, path, spooled, options, what, cancellationToken).ConfigureAwait(false);
            else
                await UploadInSessionAsync(driveId, path, spooled, options, what, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PutAsync(string driveId, ResolvedPath path, Stream content, UpdateFileOptions options, string what, CancellationToken cancellationToken)
    {
        var request = ContentRequest(driveId, LibrarySegments(path)).ToPutRequestInformation(content, c =>
        {
            if (options.IfMatch is { } etag)
                c.Headers.Add("If-Match", etag);
        });
        request.UrlTemplate += ConflictBehaviorTemplate;
        request.QueryParameters[ConflictBehaviorParameter] = options.IfNotExists ? "fail" : "replace";
        try
        {
            await _graph.RequestAdapter.SendAsync(request, DriveItem.CreateFromDiscriminatorValue, GraphErrors.Mapping, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (options.IfNotExists && GraphErrors.Status(e) == 409)
        {
            throw new PreconditionFailedException($"The file '{path}' already exists.", e);
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, what);
        }
    }

    /// <summary>
    /// Uploads through an upload session, in fragments that are multiples of 320 KiB. Each fragment may be resent once:
    /// after a transient failure (500, 502, 503, 504 or the network) or a 416 the session is asked where it stands, and
    /// after an answer that does not move the upload on it says so itself; the upload goes on from there. A failure or
    /// such an answer at or before the offset of the last resend ends the upload, so a session that keeps sending it back
    /// never loops; a resync that moves forward leaves the next fragment its own resend. Any other failure cancels the
    /// session and throws. The service commits the file with the last fragment, so a failure leaves the old content.
    /// </summary>
    private async Task UploadInSessionAsync(string driveId, ResolvedPath path, FileStream content, UpdateFileOptions options, string what, CancellationToken cancellationToken)
    {
        var body = new CreateUploadSessionPostRequestBody
        {
            Item = new DriveItemUploadableProperties
            {
                AdditionalData = new Dictionary<string, object> { [ConflictBehaviorAnnotation] = options.IfNotExists ? "fail" : "replace" },
            },
        };
        UploadSession? session;
        try
        {
            session = await UploadSessionRequest(driveId, LibrarySegments(path)).PostAsync(body, c =>
            {
                if (options.IfMatch is { } etag)
                    c.Headers.Add("If-Match", etag);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (options.IfNotExists && GraphErrors.Status(e) == 409)
        {
            throw new PreconditionFailedException($"The file '{path}' already exists.", e);
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, what);
        }
        if (session?.UploadUrl is not { } uploadUrlText || !Uri.TryCreate(uploadUrlText, UriKind.Absolute, out var uploadUrl))
            throw new IOException($"{what} failed: Microsoft Graph returned no upload session.");

        var fragmentSize = UploadFragmentSize;
        var buffer = ArrayPool<byte>.Shared.Rent(fragmentSize);
        try
        {
            var total = content.Length;
            var offset = 0L;
            var resentAt = -1L;
            while (true)
            {
                if (offset < 0 || offset >= total)
                    throw new IOException($"{what} failed: the upload session expects byte {offset} of {total}.");
                var count = (int)Math.Min(fragmentSize, total - offset);
                content.Position = offset;
                await content.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                var (status, answer) = await PutFragmentAsync(uploadUrl, buffer, offset, count, total, what, cancellationToken).ConfigureAwait(false);
                switch (status)
                {
                    case null or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestedRangeNotSatisfiable:
                        // Ask the session where it stands and go on from there, once per fragment.
                        if (offset <= resentAt)
                            throw status is { } failed ? GraphErrors.FromResponse((int)failed, answer, what) : new IOException($"{what} failed: the upload was interrupted again at byte {offset}.");
                        resentAt = offset;
                        offset = await NextExpectedAsync(uploadUrl, what, cancellationToken).ConfigureAwait(false) ?? offset;
                        continue;
                    case HttpStatusCode.OK or HttpStatusCode.Created:
                        return;
                    case HttpStatusCode.Accepted:
                        var next = NextExpectedOf(answer) ?? offset + count;
                        if (next >= total)
                            throw new IOException($"{what} failed: the service did not commit the upload after its last fragment.");
                        if (next <= offset)
                        {
                            if (offset <= resentAt)
                                throw new IOException($"{what} failed: the upload session did not move past byte {offset}.");
                            resentAt = offset;
                        }
                        offset = next;
                        continue;
                    case HttpStatusCode.Conflict when options.IfNotExists:
                        throw new PreconditionFailedException($"The file '{path}' already exists.");
                    default:
                        throw GraphErrors.FromResponse((int)status, answer, what);
                }
            }
        }
        catch
        {
            await CancelSessionAsync(uploadUrl).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Sends one fragment; null status means the request did not get an answer.</summary>
    private async Task<(HttpStatusCode? Status, string Answer)> PutFragmentAsync(Uri uploadUrl, byte[] buffer, long offset, int count, long total,
        string what, CancellationToken cancellationToken)
    {
        // Upload URLs are pre-authenticated, and a Graph token there can fail the request: the adapter sends none to a
        // host other than Graph's.
        var request = new RequestInformation { HttpMethod = Method.PUT, URI = uploadUrl };
        request.SetStreamContent(new MemoryStream(buffer, 0, count, writable: false), "application/octet-stream");
        request.Headers.Add("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {offset}-{offset + count - 1}/{total}"));
        HttpResponseMessage response;
        try
        {
            response = await SendRawAsync(request, what, streamed: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e) when (e.InnerException is HttpRequestException)
        {
            return (null, "");
        }
        using (response)
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<long?> NextExpectedAsync(Uri uploadUrl, string what, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(new RequestInformation { HttpMethod = Method.GET, URI = uploadUrl }, what, streamed: false, cancellationToken).ConfigureAwait(false);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? NextExpectedOf(answer) : throw GraphErrors.FromResponse((int)response.StatusCode, answer, what);
    }

    /// <summary>The start of the first range in an upload session's <c>nextExpectedRanges</c>, or null.</summary>
    private static long? NextExpectedOf(string answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array
                && ranges.GetArrayLength() > 0 && ranges[0].ValueKind == JsonValueKind.String && ranges[0].GetString() is { } first)
            {
                var dash = first.IndexOf('-', StringComparison.Ordinal);
                return long.TryParse(dash < 0 ? first : first[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : null;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private async Task CancelSessionAsync(Uri uploadUrl)
    {
        // Not the caller's token: a cancelled write still cancels its session, but never waits long for it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await SendRawAsync(new RequestInformation { HttpMethod = Method.DELETE, URI = uploadUrl }, "Cancelling an upload", streamed: false, timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the session expires on its own, and the caller is already getting the failure that matters.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Sends a request whose answer the provider reads itself, through the Graph client's adapter (its retry and redirect
    /// handlers), and returns the response, which the caller disposes. The status is not checked.
    /// </summary>
    private async Task<HttpResponseMessage> SendRawAsync(RequestInformation request, string what, bool streamed, CancellationToken cancellationToken)
    {
        var native = new NativeResponseHandler();
        request.SetResponseHandler(native);
        try
        {
            // Only the Stream path reads the headers first and leaves the body to stream.
            if (streamed)
                await _graph.RequestAdapter.SendPrimitiveAsync<Stream>(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
                await _graph.RequestAdapter.SendNoContentAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, what);
        }
        return native.Value as HttpResponseMessage ?? throw new IOException($"{what} failed: no answer was received.");
    }

    // ----- Move and copy -----

    /// <inheritdoc />
    /// <remarks>
    /// One request: Graph moves and renames the item in place, refusing a destination that exists (conflict behaviour
    /// <c>fail</c>). A name that differs only in case is a rename.
    /// </remarks>
    protected override async Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
        var what = $"Moving '{source}' to '{destination}'";
        var rename = string.Equals(source.Path.ToString(), destination.Path.ToString(), StringComparison.OrdinalIgnoreCase);
        var change = new DriveItem { Name = destination.Name };
        if (!rename)
        {
            // Graph wants the parent's real id: the literal "root" is refused here.
            var parentId = await EnsureFolderAsync(driveId, destination.Parent.RelativeSegments, what, cancellationToken).ConfigureAwait(false);
            change.ParentReference = new ItemReference { Id = parentId };
        }
        var request = ItemRequest(driveId, LibrarySegments(source)).ToPatchRequestInformation(change);
        if (!rename)
        {
            request.UrlTemplate += ConflictBehaviorTemplate;
            request.QueryParameters[ConflictBehaviorParameter] = "fail";
        }
        try
        {
            await _graph.RequestAdapter.SendAsync(request, DriveItem.CreateFromDiscriminatorValue, GraphErrors.Mapping, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GraphErrors.Status(e) == 409)
        {
            throw new FileExistsException($"The destination '{destination}' already exists.", e);
        }
        catch (Exception e) when (GraphErrors.IsNotFound(e))
        {
            throw NotFound(source, e);
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, what);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Copies on the server, with conflict behaviour <c>fail</c>, and polls the copy's monitor until it completes, for at
    /// most 10 minutes. A destination taken while the copy runs throws <see cref="FileExistsException"/>. When the wait
    /// runs out the call throws <see cref="IOException"/>, but the service may still finish the copy and create the
    /// destination afterwards.
    /// </remarks>
    protected override async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
        var what = $"Copying '{source}' to '{destination}'";
        var parentId = await EnsureFolderAsync(driveId, destination.Parent.RelativeSegments, what, cancellationToken).ConfigureAwait(false);
        var request = CopyRequest(driveId, LibrarySegments(source)).ToPostRequestInformation(new CopyPostRequestBody
        {
            Name = destination.Name,
            ParentReference = new ItemReference { DriveId = driveId, Id = parentId },
        });
        request.UrlTemplate += ConflictBehaviorTemplate;
        request.QueryParameters[ConflictBehaviorParameter] = "fail";
        Uri monitor;
        using (var accepted = await SendRawAsync(request, what, streamed: false, cancellationToken).ConfigureAwait(false))
        {
            switch (accepted.StatusCode)
            {
                case HttpStatusCode.Accepted when accepted.Headers.Location is { IsAbsoluteUri: true } location:
                    monitor = location;
                    break;
                case HttpStatusCode.OK or HttpStatusCode.Created:
                    return;
                case HttpStatusCode.Conflict:
                    throw new FileExistsException($"The destination '{destination}' already exists.");
                case HttpStatusCode.NotFound:
                    throw NotFound(source);
                default:
                    throw GraphErrors.FromResponse((int)accepted.StatusCode, await accepted.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), what);
            }
        }
        await WaitForCopyAsync(monitor, destination, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls a copy's monitor until it reports the copy completed or failed, for at most <see cref="CopyTimeout"/>. It
    /// decides on the <c>status</c> field, never on the status code, which differs between services; a redirect to the
    /// new item, or the item itself, also means done.
    /// </summary>
    private async Task WaitForCopyAsync(Uri monitor, ResolvedPath destination, string what, CancellationToken cancellationToken)
    {
        const int MaxUnreadable = 10;
        var delay = CopyPollDelay;
        var unreadable = 0;
        var started = TimeProvider.GetTimestamp();
        string? last;
        while (true)
        {
            // A redirect from the monitor points at the new item: it means done, and is not followed (the monitor's
            // host is not Graph's, so the redirected request would carry no token).
            var poll = new RequestInformation { HttpMethod = Method.GET, URI = monitor };
            poll.AddRequestOptions([new RedirectHandlerOption { ShouldRedirect = _ => false }]);
            using (var response = await SendRawAsync(poll, what, streamed: false, cancellationToken).ConfigureAwait(false))
            {
                var code = (int)response.StatusCode;
                if (code is >= 300 and < 400)
                    return;
                var answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw GraphErrors.FromResponse(code, answer, $"{what} (its progress)");
                var (status, isItem) = CopyStatusOf(answer);
                last = status;
                switch (status)
                {
                    case "completed":
                        return;
                    case "failed":
                        throw GraphErrors.CodeOf(answer) == "nameAlreadyExists"
                            ? new FileExistsException($"The destination '{destination}' already exists.")
                            : GraphErrors.FromResponse(code, answer, what);
                    case "notStarted" or "inProgress" or "waiting" or "running":
                        break;
                    case null when isItem:
                        return;
                    case null when ++unreadable < MaxUnreadable:
                        break;
                    default:
                        throw new IOException($"{what} failed: the copy ended as '{status ?? "unknown"}'.");
                }
            }
            if (TimeProvider.GetElapsedTime(started) >= CopyTimeout)
                throw new IOException($"{what} failed: the copy did not complete within {CopyTimeout}; it last reported '{last ?? "nothing readable"}'.");
            await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxCopyPollDelay.Ticks));
        }
    }

    private static (string? Status, bool IsItem) CopyStatusOf(string answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, false);
            var status = root.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (status, status is null && root.TryGetProperty("id", out _) && root.TryGetProperty("name", out _));
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    // ----- Signed URLs -----

    /// <inheritdoc />
    /// <remarks>
    /// The file's pre-authenticated download URL. Graph says it lives up to an hour and may stop working within
    /// minutes, and does not say when: a validity under an hour throws <see cref="NotSupportedException"/> (the URL could
    /// outlive it), a longer one needs <see cref="SignedUrlOptions.AllowShorterValidity"/>, and
    /// <see cref="SignedUrl.ExpiresOn"/> is null. Removing someone's access may not end a URL already issued.
    /// </remarks>
    protected override async Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validity < DownloadUrlLifetime)
        {
            throw new NotSupportedException(
                $"SharePoint's download URLs live up to {DownloadUrlLifetime}, longer than the {options.Validity} asked; serve the file through the gateway instead.");
        }
        if (!options.AllowShorterValidity)
        {
            throw new NotSupportedException(
                $"SharePoint's download URLs may stop working before {options.Validity} ends; set AllowShorterValidity to accept one.");
        }
        var item = await GetDownloadableAsync(path, cancellationToken).ConfigureAwait(false);
        var url = DownloadUrlOf(item) ?? throw new IOException($"Microsoft Graph gave no download URL for '{path}'.");
        return new SignedUrl { Url = url, Method = "GET", ExpiresOn = null, Kind = SignedUrlKind.PreAuthenticated };
    }
}
