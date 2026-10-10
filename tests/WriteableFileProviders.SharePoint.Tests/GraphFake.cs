using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>A request the fake received, as the tests check it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Host">The host.</param>
/// <param name="Path">The path, each segment unescaped.</param>
/// <param name="Query">The query, keys and values unescaped.</param>
/// <param name="Headers">The request's headers and its content's headers.</param>
/// <param name="BodyLength">The body's length.</param>
internal sealed record RecordedRequest(string Method, string Host, string Path, IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers, int BodyLength)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The Microsoft Graph API of one SharePoint site, in memory, behind an <see cref="HttpMessageHandler"/> that sits at the end
/// of the SDK's own pipeline (retries and redirects run as they do against Graph). It keeps a stateful document library
/// and answers the requests the provider sends, as Graph documents them (.local/research/graph-v6.md, section 7): site and
/// library lookup, items by path or id with <c>$select</c>, paged children, folder creation, simple uploads with
/// <c>If-Match</c> and the conflict behaviour, upload sessions, pre-authenticated downloads with ranges, delete, move by
/// PATCH, and server-side copy with a monitor URL. Every error is a JSON Graph error. Switches reproduce the variants the
/// documentation leaves open, and fault injection reproduces throttling and failures.
/// </summary>
internal sealed class GraphFake
{
    public const string GraphHost = "graph.fake";
    public const string BaseUrl = "https://graph.fake/v1.0";
    public const string UploadHost = "upload.fake";
    public const string DownloadHost = "download.fake";
    public const string MonitorHost = "monitor.fake";
    public const string SiteHost = "contoso.sharepoint.com";
    public const string SiteId = "contoso.sharepoint.com,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222";
    public const string DriveId = "b!docs";
    public const int FragmentMultiple = 320 * 1024;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CopyOperation> _copies = new(StringComparer.Ordinal);
    private readonly List<Fault> _faults = [];
    private readonly Item _root;
    private int _nextId;

    public GraphFake(string sitePath = "/sites/Team Site")
    {
        SitePath = sitePath;
        _root = new Item("01ROOT", "root", null, isFolder: true);
        _items[_root.Id] = _root;
    }

    /// <summary>The site's server-relative path; with <see cref="SiteHost"/>, the site the fake serves.</summary>
    public string SitePath { get; }

    /// <summary>The site's libraries, listed one per page so the provider follows the next link.</summary>
    public IReadOnlyList<(string Id, string Name)> Libraries { get; set; } = [("b!other", "Other Library"), (DriveId, "Documents")];

    /// <summary>How many children a page lists at most, whatever <c>$top</c> asks.</summary>
    public int ChildrenPageSize { get; set; } = 200;

    /// <summary>Whether a new upload session's answer lists <c>nextExpectedRanges</c> (the documented answer does not).</summary>
    public bool SessionCreateListsRanges { get; set; }

    /// <summary>Whether fragments that are not the last must be multiples of 320 KiB, as Graph requires.</summary>
    public bool StrictFragments { get; set; } = true;

    /// <summary>Whether the download host ignores <c>Range</c> and sends the whole file, as Graph may.</summary>
    public bool IgnoreRanges { get; set; }

    /// <summary>Whether download URLs have expired (403).</summary>
    public bool ExpireDownloadUrls { get; set; }

    /// <summary>How many polls of a copy's monitor answer "inProgress" before the copy ends.</summary>
    public int CopyPollsBeforeDone { get; set; } = 1;

    /// <summary>The status code of the monitor's final answer: 202 in Graph's example, 200 elsewhere.</summary>
    public HttpStatusCode CopyDoneStatus { get; set; } = HttpStatusCode.Accepted;

    /// <summary>Whether the monitor ends a successful copy with a 303 to the new item instead of a status.</summary>
    public bool CopyDoneByRedirect { get; set; }

    /// <summary>Whether a copy onto a taken name is refused at once (409) instead of failing in the monitor.</summary>
    public bool CopyConflictIsSynchronous { get; set; }

    /// <summary>Whether requests to Graph's host must carry a bearer token.</summary>
    public bool RequireGraphToken { get; set; }

    /// <summary>Whether items come without their download URL.</summary>
    public bool OmitDownloadUrls { get; set; }

    /// <summary>Called with each request before it is answered; a test changes the library here to race the provider.</summary>
    public Action<RecordedRequest>? OnRequest { get; set; }

    /// <summary>Answers a request in place of the fake when it returns a response.</summary>
    public Func<RecordedRequest, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Every request received, in order.</summary>
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>What the fake saw that Graph would refuse or that leaks a credential, such as a token sent to a host other than Graph's.</summary>
    public ConcurrentQueue<string> Violations { get; } = new();

    /// <summary>A handler for an HTTP client: the end of the SDK's pipeline.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    /// <summary>Answers the next <paramref name="count"/> matching requests with <paramref name="status"/> instead.</summary>
    public void FailNext(int count, HttpStatusCode status, Func<RecordedRequest, bool>? when = null, string? body = null, string contentType = "application/json")
    {
        lock (_lock)
            _faults.Add(new Fault(count, status, when ?? (_ => true), body, contentType));
    }

    /// <summary>Fails the next <paramref name="count"/> matching requests without an answer, as a network failure does.</summary>
    public void BreakNext(int count, Func<RecordedRequest, bool> when)
    {
        lock (_lock)
            _faults.Add(new Fault(count, null, when, null, ""));
    }

    /// <summary>Clears the request log.</summary>
    public void ClearRequests() => Requests.Clear();

    // ----- Direct access for the tests -----

    /// <summary>Writes a file at a library path, creating its folders.</summary>
    public void SetFile(string libraryPath, byte[] content)
    {
        lock (_lock)
        {
            var names = Split(libraryPath);
            var parent = _root;
            foreach (var name in names[..^1])
                parent = parent.Child(name) ?? Add(parent, name, isFolder: true);
            var file = parent.Child(names[^1]) ?? Add(parent, names[^1], isFolder: false);
            file.SetContent(content);
        }
    }

    /// <summary>Creates a folder at a library path, with its parents.</summary>
    public void CreateFolder(string libraryPath)
    {
        lock (_lock)
        {
            var parent = _root;
            foreach (var name in Split(libraryPath))
                parent = parent.Child(name) ?? Add(parent, name, isFolder: true);
        }
    }

    /// <summary>Deletes what is at a library path.</summary>
    public void Delete(string libraryPath)
    {
        lock (_lock)
        {
            if (Resolve(_root, Split(libraryPath)) is { } item && item != _root)
                Remove(item);
        }
    }

    /// <summary>A file's content, or null when the path is not a file.</summary>
    public byte[]? GetFile(string libraryPath)
    {
        lock (_lock)
            return Resolve(_root, Split(libraryPath)) is { IsFolder: false } file ? file.Content : null;
    }

    /// <summary>Whether a folder exists at the library path.</summary>
    public bool IsFolder(string libraryPath)
    {
        lock (_lock)
            return Resolve(_root, Split(libraryPath)) is { IsFolder: true };
    }

    /// <summary>The names in a folder.</summary>
    public IReadOnlyList<string> Names(string libraryPath)
    {
        lock (_lock)
            return Resolve(_root, Split(libraryPath)) is { IsFolder: true } folder ? [.. folder.Children.Select(c => c.Name)] : [];
    }

    /// <summary>How many upload sessions are open.</summary>
    public int OpenSessions
    {
        get
        {
            lock (_lock)
                return _sessions.Count;
        }
    }

    // ----- The handler -----

    private sealed class Handler(GraphFake fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The length a real handler would send, before the body is read; and the body itself, because the SDK disposes
            // a request's body after sending.
            var length = request.Content?.Headers.ContentLength;
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // As a real handler does: the retry and redirect handlers resend from the response's request.
            var response = fake.Answer(request, body, length);
            response.RequestMessage = request;
            return response;
        }
    }

    private HttpResponseMessage Answer(HttpRequestMessage request, byte[] body, long? contentLength)
    {
        var uri = request.RequestUri!;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in request.Headers)
            headers[key] = string.Join(",", values);
        if (request.Content is not null)
        {
            foreach (var (key, values) in request.Content.Headers)
                headers[key] = string.Join(",", values);
            if (contentLength is { } length)
                headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
        }
        var recorded = new RecordedRequest(request.Method.Method, uri.Host, "/" + string.Join('/', segments), ParseQuery(uri.Query), headers, body.Length);
        Requests.Enqueue(recorded);
        if (uri.Host != GraphHost && headers.ContainsKey("Authorization"))
            Violations.Enqueue($"A token was sent to {uri.Host}: {recorded.Method} {recorded.Path}");
        if (uri.Host == GraphHost && RequireGraphToken && !(recorded.Header("Authorization")?.StartsWith("Bearer ", StringComparison.Ordinal) ?? false))
            return Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token is empty.");
        OnRequest?.Invoke(recorded);
        if (Intercept?.Invoke(recorded) is { } intercepted)
            return intercepted;

        lock (_lock)
        {
            var fault = _faults.FirstOrDefault(f => f.Remaining > 0 && f.When(recorded));
            if (fault is not null)
            {
                fault.Remaining--;
                if (fault.Status is not { } faultStatus)
                    throw new HttpRequestException("The connection was reset (injected).");
                var faulted = new HttpResponseMessage(faultStatus)
                {
                    Content = new StringContent(fault.Body ?? ErrorJson("activityLimitReached", "Injected failure."), Encoding.UTF8, fault.ContentType),
                };
                faulted.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return faulted;
            }
            return uri.Host switch
            {
                GraphHost when segments is ["v1.0", "sites", ..] => Sites(recorded, segments[2..]),
                GraphHost when segments is ["v1.0", "drives", var drive, .. var rest] => drive == DriveId
                    ? Drive(recorded, rest, body)
                    : Error(HttpStatusCode.NotFound, "itemNotFound", "The drive does not exist."),
                UploadHost when segments is ["up", var id] => UploadSession(recorded, id, body),
                DownloadHost when segments is ["d", var id] => Download(recorded, id),
                MonitorHost when segments is ["_api", "v2.0", "monitor", var id] => Monitor(id),
                _ => Error(HttpStatusCode.BadRequest, "invalidRequest", $"The fake does not serve {recorded.Method} {uri.Host}{recorded.Path}."),
            };
        }
    }

    // ----- Sites and libraries -----

    private HttpResponseMessage Sites(RecordedRequest request, string[] rest)
    {
        if (request.Method != "GET")
            return Error(HttpStatusCode.MethodNotAllowed, "invalidRequest", "Only GET.");
        if (rest is [var key])
        {
            return string.Equals(key, $"{SiteHost}:{SitePath}", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, new JsonObject { ["id"] = SiteId, ["displayName"] = "Team Site", ["webUrl"] = $"https://{SiteHost}{SitePath}" })
                : Error(HttpStatusCode.NotFound, "itemNotFound", "Requested site could not be found");
        }
        if (rest is [var siteId, "drives"] && siteId == SiteId)
        {
            var index = request.Query.TryGetValue("$skiptoken", out var token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
            var page = new JsonObject
            {
                ["value"] = new JsonArray([.. Libraries.Skip(index).Take(1).Select(l => (JsonNode)new JsonObject
                {
                    ["id"] = l.Id,
                    ["name"] = l.Name,
                    ["driveType"] = "documentLibrary",
                    ["webUrl"] = $"https://{SiteHost}{SitePath}/{Uri.EscapeDataString(l.Name)}",
                })]),
            };
            if (index + 1 < Libraries.Count)
                page["@odata.nextLink"] = $"{BaseUrl}/sites/{Uri.EscapeDataString(SiteId)}/drives?$select=id,name&$skiptoken={index + 1}";
            return Json(HttpStatusCode.OK, page);
        }
        return Error(HttpStatusCode.NotFound, "itemNotFound", "The site does not exist.");
    }

    // ----- Items -----

    private HttpResponseMessage Drive(RecordedRequest request, string[] tokens, byte[] body)
    {
        // Addressing: root | root:/a/b: | items/{id} | items/{id}:/a/b: — then an optional action.
        Item? start;
        var i = 0;
        List<string>? names = null;
        switch (tokens)
        {
            case ["root", ..]:
                start = _root;
                i = 1;
                break;
            case ["root:", ..]:
                start = _root;
                i = 1;
                names = [];
                break;
            case ["items", var id, ..]:
                var pathFollows = id.EndsWith(':');
                id = pathFollows ? id[..^1] : id;
                start = id == "root" ? _root : _items.GetValueOrDefault(id);
                i = 2;
                names = pathFollows ? [] : null;
                break;
            default:
                return Error(HttpStatusCode.BadRequest, "invalidRequest", "Unknown drive request.");
        }
        if (names is not null)
        {
            // Path segments run up to the one that ends with ':'.
            while (i < tokens.Length)
            {
                var token = tokens[i++];
                if (token.EndsWith(':'))
                {
                    names.Add(token[..^1]);
                    break;
                }
                names.Add(token);
            }
            if (names.Count == 0 || names.Any(n => n.Length == 0 || n.Contains(':', StringComparison.Ordinal)))
                return Error(HttpStatusCode.BadRequest, "invalidRequest", "Invalid path.");
        }
        if (start is null)
            return Error(HttpStatusCode.NotFound, "itemNotFound", "Item not found");
        var action = tokens[i..];
        var target = names is null ? start : Resolve(start, names);
        var parent = names is null ? start.Parent : Resolve(start, names[..^1]);
        var name = names is null ? start.Name : names[^1];

        return (request.Method, action) switch
        {
            ("GET", []) => target is null ? NotFound() : Json(HttpStatusCode.OK, Select(ItemJson(target), request)),
            ("DELETE", []) => Delete(request, target),
            ("PATCH", []) => Patch(request, target, body),
            ("GET", ["children"]) => target is { IsFolder: true } ? Children(request, target) : target is null ? NotFound() : Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray() }),
            ("POST", ["children"]) => target is { IsFolder: true } ? CreateFolder(request, target, body) : NotFound(),
            ("PUT", ["content"]) => PutContent(request, parent, name, target, body),
            ("POST", ["createUploadSession"]) => CreateSession(request, parent, name, target, body),
            ("POST", ["copy"]) => Copy(target, body),
            _ => Error(HttpStatusCode.BadRequest, "invalidRequest", $"The fake does not serve {request.Method} {string.Join('/', action)}."),
        };
    }

    private HttpResponseMessage Children(RecordedRequest request, Item folder)
    {
        var top = request.Query.TryGetValue("$top", out var t) ? int.Parse(t, CultureInfo.InvariantCulture) : 200;
        var size = Math.Min(top, ChildrenPageSize);
        var skip = request.Query.TryGetValue("$skiptoken", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
        var page = new JsonObject
        {
            ["value"] = new JsonArray([.. folder.Children.Skip(skip).Take(size).Select(c => (JsonNode)Select(ItemJson(c), request))]),
        };
        if (skip + size < folder.Children.Count)
        {
            // Like Graph's, the next link is absolute and keeps the query options.
            var select = request.Query.TryGetValue("$select", out var fields) ? $"$select={Uri.EscapeDataString(fields)}&" : "";
            page["@odata.nextLink"] = $"{BaseUrl}/drives/{DriveId}/items/{folder.Id}/children?{select}$top={top}&$skiptoken={skip + size}";
        }
        return Json(HttpStatusCode.OK, page);
    }

    private HttpResponseMessage CreateFolder(RecordedRequest request, Item parent, byte[] body)
    {
        var json = JsonNode.Parse(body)!.AsObject();
        var name = json["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name) || json["folder"] is null)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "A folder needs a name and the folder facet.");
        var behavior = json["@microsoft.graph.conflictBehavior"]?.GetValue<string>() ?? request.Query.GetValueOrDefault("@microsoft.graph.conflictBehavior");
        if (parent.Child(name) is not null && behavior is null or "fail")
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");
        if (behavior is not "fail")
            Violations.Enqueue($"A folder was created without the conflict behaviour 'fail': {request.Path}");
        return Json(HttpStatusCode.Created, ItemJson(Add(parent, name, isFolder: true)));
    }

    private HttpResponseMessage PutContent(RecordedRequest request, Item? parent, string name, Item? target, byte[] body)
    {
        if (parent is not { IsFolder: true })
            return NotFound();
        if (Precondition(request, target) is { } failed)
            return failed;
        var behavior = request.Query.GetValueOrDefault("@microsoft.graph.conflictBehavior") ?? "replace";
        if (target is not null && (behavior == "fail" || target.IsFolder))
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");
        if (request.Header("Content-Length") is { } length && long.Parse(length, CultureInfo.InvariantCulture) != body.Length)
            Violations.Enqueue($"Content-Length {length} does not match the body of {body.Length} bytes.");
        var created = target is null;
        var file = target ?? Add(parent, name, isFolder: false);
        file.SetContent(body);
        return Json(created ? HttpStatusCode.Created : HttpStatusCode.OK, ItemJson(file));
    }

    private HttpResponseMessage CreateSession(RecordedRequest request, Item? parent, string name, Item? target, byte[] body)
    {
        if (parent is not { IsFolder: true })
            return NotFound();
        if (Precondition(request, target) is { } failed)
            return failed;
        JsonObject json = body.Length == 0 ? [] : JsonNode.Parse(body)!.AsObject();
        // The documented default for a session is "fail", unlike a simple upload's "replace".
        var behavior = json["item"]?["@microsoft.graph.conflictBehavior"]?.GetValue<string>() ?? "fail";
        var id = NewId();
        _sessions[id] = new Session(parent, name, behavior);
        var answer = new JsonObject
        {
            ["uploadUrl"] = $"https://{UploadHost}/up/{id}",
            ["expirationDateTime"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
        };
        if (SessionCreateListsRanges)
            answer["nextExpectedRanges"] = new JsonArray("0-");
        return Json(HttpStatusCode.OK, answer);
    }

    private HttpResponseMessage UploadSession(RecordedRequest request, string id, byte[] body)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return Error(HttpStatusCode.NotFound, "itemNotFound", "The upload session does not exist or has expired.");
        switch (request.Method)
        {
            case "GET":
                return Json(HttpStatusCode.OK, SessionStatus(session));
            case "DELETE":
                _sessions.Remove(id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "PUT":
                break;
            default:
                return Error(HttpStatusCode.MethodNotAllowed, "invalidRequest", "Not allowed.");
        }
        if (request.Header("Content-Range") is not { } range || !TryParseContentRange(range, out var from, out var to, out var total))
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "A fragment needs a Content-Range.");
        if (to - from + 1 != body.Length)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "The fragment's length does not match its range.");
        if (session.Total is { } known && known != total)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "The total size changed.");
        if (from != session.Received.Length)
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange", "The fragment is not the one expected.");
        var last = to + 1 == total;
        if (StrictFragments && !last && body.Length % FragmentMultiple != 0)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "A fragment must be a multiple of 320 KiB.");
        if (body.Length >= 60 * 1024 * 1024)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "A fragment must be under 60 MiB.");
        session.Total = total;
        session.Received.Write(body);
        if (!last)
            return Json(HttpStatusCode.Accepted, SessionStatus(session));

        // The last fragment commits, atomically, under the session's conflict behaviour.
        var existing = session.Parent.Child(session.Name);
        if (existing is not null && (session.Behavior == "fail" || existing.IsFolder))
        {
            // The session stays open: the caller decides whether to cancel it.
            session.Received.SetLength(session.Received.Length - body.Length);
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");
        }
        _sessions.Remove(id);
        var file = existing ?? Add(session.Parent, session.Name, isFolder: false);
        file.SetContent(session.Received.ToArray());
        return Json(existing is null ? HttpStatusCode.Created : HttpStatusCode.OK, ItemJson(file));
    }

    private static JsonObject SessionStatus(Session session) => new()
    {
        ["expirationDateTime"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
        ["nextExpectedRanges"] = new JsonArray($"{session.Received.Length}-"),
    };

    private HttpResponseMessage Delete(RecordedRequest request, Item? target)
    {
        if (target is null)
            return NotFound();
        if (target == _root)
            return Error(HttpStatusCode.Forbidden, "accessDenied", "The root cannot be deleted.");
        if (Precondition(request, target) is { } failed)
            return failed;
        Remove(target);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Patch(RecordedRequest request, Item? target, byte[] body)
    {
        if (target is null)
            return NotFound();
        if (target == _root)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "The root cannot be moved.");
        if (Precondition(request, target) is { } failed)
            return failed;
        var json = JsonNode.Parse(body)!.AsObject();
        var parent = target.Parent!;
        if (json["parentReference"]?["id"]?.GetValue<string>() is { } parentId)
        {
            if (parentId == "root")
                return Error(HttpStatusCode.BadRequest, "invalidRequest", "The parent must be given by its id.");
            if (_items.GetValueOrDefault(parentId) is not { IsFolder: true } newParent)
                return NotFound();
            for (var p = newParent; p is not null; p = p.Parent)
            {
                if (p == target)
                    return Error(HttpStatusCode.BadRequest, "invalidRequest", "A folder cannot move into itself.");
            }
            parent = newParent;
        }
        var name = json["name"]?.GetValue<string>() ?? target.Name;
        // Another item with the name (in any case) is a conflict; the item itself, renamed in another case, is not.
        if (parent.Child(name) is { } other && other != target)
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");
        target.Parent!.Children.Remove(target);
        target.Parent = parent;
        target.Name = name;
        parent.Children.Add(target);
        target.Touch(content: false);
        return Json(HttpStatusCode.OK, ItemJson(target));
    }

    private HttpResponseMessage Copy(Item? source, byte[] body)
    {
        if (source is null)
            return NotFound();
        var json = JsonNode.Parse(body)!.AsObject();
        if (json["parentReference"]?["id"]?.GetValue<string>() is not { } parentId || _items.GetValueOrDefault(parentId) is not { IsFolder: true } parent)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "The copy needs its destination folder's id.");
        if (json["parentReference"]?["driveId"]?.GetValue<string>() is { } drive && drive != DriveId)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "Unknown drive.");
        var name = json["name"]?.GetValue<string>() ?? source.Name;
        if (CopyConflictIsSynchronous && parent.Child(name) is not null)
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");
        var id = NewId();
        _copies[id] = new CopyOperation(source, parent, name, CopyPollsBeforeDone);
        var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
        accepted.Headers.Location = new Uri($"https://{MonitorHost}/_api/v2.0/monitor/{id}");
        return accepted;
    }

    private HttpResponseMessage Monitor(string id)
    {
        if (!_copies.TryGetValue(id, out var operation))
            return Error(HttpStatusCode.NotFound, "itemNotFound", "Unknown operation.");
        if (operation.Result is null && operation.PollsLeft-- > 0)
            return Json(HttpStatusCode.Accepted, new JsonObject { ["operation"] = "ItemCopy", ["percentageComplete"] = 50.0, ["status"] = "inProgress" });
        if (operation.Result is null)
        {
            if (operation.Parent.Child(operation.Name) is not null)
            {
                operation.Result = "failed";
            }
            else
            {
                var copy = Add(operation.Parent, operation.Name, isFolder: operation.Source.IsFolder);
                copy.SetContent(operation.Source.Content);
                operation.Result = copy.Id;
            }
        }
        if (operation.Result == "failed")
        {
            return Json(CopyDoneStatus, new JsonObject
            {
                ["status"] = "failed",
                ["error"] = new JsonObject { ["code"] = "nameAlreadyExists", ["message"] = "Name already exists" },
            });
        }
        if (CopyDoneByRedirect)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.SeeOther);
            redirect.Headers.Location = new Uri($"{BaseUrl}/drives/{DriveId}/items/{operation.Result}");
            return redirect;
        }
        return Json(CopyDoneStatus, new JsonObject { ["percentageComplete"] = 100.0, ["resourceId"] = operation.Result, ["status"] = "completed" });
    }

    // ----- Downloads -----

    private HttpResponseMessage Download(RecordedRequest request, string id)
    {
        if (request.Method != "GET")
            return Error(HttpStatusCode.MethodNotAllowed, "invalidRequest", "Only GET.");
        if (!request.Query.ContainsKey("tempauth") || request.Query.GetValueOrDefault("UniqueId") != "{" + id + "}")
            return Error(HttpStatusCode.Unauthorized, "unauthenticated", "The download URL was altered.");
        if (ExpireDownloadUrls)
            return Error(HttpStatusCode.Forbidden, "accessDenied", "The download URL has expired.");
        // The worst case: the URL serves the file's current content, not the version it was issued for.
        if (_items.GetValueOrDefault(id) is not { IsFolder: false } file)
            return NotFound();
        var content = file.Content;
        if (request.Header("Range") is { } range && !IgnoreRanges)
        {
            var spec = range["bytes=".Length..].Split('-');
            var from = long.Parse(spec[0], CultureInfo.InvariantCulture);
            var to = spec[1].Length == 0 ? content.Length - 1 : Math.Min(long.Parse(spec[1], CultureInfo.InvariantCulture), content.Length - 1);
            if (from >= content.Length)
            {
                var unsatisfiable = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                unsatisfiable.Content.Headers.ContentRange = new ContentRangeHeaderValue(content.Length);
                return unsatisfiable;
            }
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content[(int)from..(int)(to + 1)]) };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
            partial.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return partial;
        }
        var whole = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        whole.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return whole;
    }

    // ----- State -----

    private static HttpResponseMessage? Precondition(RecordedRequest request, Item? target)
    {
        if (request.Header("If-Match") is { } ifMatch && (target is null || (ifMatch != target.ETag && ifMatch != target.CTag)))
            return Error(HttpStatusCode.PreconditionFailed, "resourceModified", "ETag does not match current item's value");
        if (request.Header("If-None-Match") is { } ifNoneMatch && target is not null && (ifNoneMatch == "*" || ifNoneMatch == target.ETag))
            return Error(HttpStatusCode.PreconditionFailed, "resourceModified", "The item exists.");
        return null;
    }

    private Item Add(Item parent, string name, bool isFolder)
    {
        var item = new Item(NewId(), name, parent, isFolder);
        parent.Children.Add(item);
        _items[item.Id] = item;
        parent.Touch(content: false);
        return item;
    }

    private void Remove(Item item)
    {
        foreach (var child in item.Children.ToList())
            Remove(child);
        item.Parent?.Children.Remove(item);
        _items.Remove(item.Id);
    }

    private string NewId() => $"01ITEM{Interlocked.Increment(ref _nextId):D6}";

    private static Item? Resolve(Item start, IEnumerable<string> names)
    {
        var current = start;
        foreach (var name in names)
        {
            if (!current.IsFolder || current.Child(name) is not { } next)
                return null;
            current = next;
        }
        return current;
    }

    private static string[] Split(string libraryPath) => libraryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private JsonObject ItemJson(Item item)
    {
        var json = new JsonObject
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["eTag"] = item.ETag,
            ["size"] = item.IsFolder ? item.TotalSize : item.Content.Length,
            ["createdDateTime"] = item.Created.ToString("O", CultureInfo.InvariantCulture),
            ["lastModifiedDateTime"] = item.Modified.ToString("O", CultureInfo.InvariantCulture),
            ["webUrl"] = $"https://{SiteHost}{SitePath}/Shared%20Documents/{Uri.EscapeDataString(item.Name)}",
        };
        if (item.Parent is { } parent)
            json["parentReference"] = new JsonObject { ["driveId"] = DriveId, ["driveType"] = "documentLibrary", ["id"] = parent.Id };
        if (item.IsFolder)
        {
            json["folder"] = new JsonObject { ["childCount"] = item.Children.Count };
            if (item == _root)
                json["root"] = new JsonObject();
        }
        else
        {
            json["cTag"] = item.CTag;
            json["file"] = new JsonObject { ["mimeType"] = MimeType(item.Name), ["hashes"] = new JsonObject { ["quickXorHash"] = "AAAA" } };
            if (!OmitDownloadUrls)
                json["@microsoft.graph.downloadUrl"] = $"https://{DownloadHost}/d/{item.Id}?UniqueId=%7B{item.Id}%7D&tempauth=v1.{Guid.NewGuid():N}+x";
        }
        return json;
    }

    private static JsonObject Select(JsonObject item, RecordedRequest request)
    {
        if (!request.Query.TryGetValue("$select", out var select))
            return item;
        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new JsonObject();
        foreach (var (key, value) in item)
        {
            if (fields.Contains(key))
                selected[key] = value?.DeepClone();
        }
        return selected;
    }

    private static string MimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".html" => "text/html",
        ".csv" => "text/csv",
        _ => "application/octet-stream",
    };

    private static bool TryParseContentRange(string value, out long from, out long to, out long total)
    {
        from = to = total = 0;
        if (!value.StartsWith("bytes ", StringComparison.Ordinal))
            return false;
        var parts = value["bytes ".Length..].Split('/');
        var range = parts[0].Split('-');
        return parts.Length == 2 && range.Length == 2
            && long.TryParse(range[0], CultureInfo.InvariantCulture, out from)
            && long.TryParse(range[1], CultureInfo.InvariantCulture, out to)
            && long.TryParse(parts[1], CultureInfo.InvariantCulture, out total);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            result[key] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return result;
    }

    private static HttpResponseMessage NotFound() => Error(HttpStatusCode.NotFound, "itemNotFound", "Item not found");

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode json) =>
        new(status) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    public static string ErrorJson(string code, string message) =>
        new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["innerError"] = new JsonObject { ["request-id"] = Guid.NewGuid().ToString() } } }.ToJsonString();

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = new StringContent(ErrorJson(code, message), Encoding.UTF8, "application/json") };

    private sealed class Item(string id, string name, Item? parent, bool isFolder)
    {
        private readonly Guid _guid = Guid.NewGuid();
        private int _version = 1;
        private int _contentVersion = 1;

        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public Item? Parent { get; set; } = parent;
        public bool IsFolder { get; } = isFolder;
        public byte[] Content { get; private set; } = [];
        public List<Item> Children { get; } = [];
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset Modified { get; private set; } = DateTimeOffset.UtcNow;
        public string ETag => $"\"{{{_guid.ToString().ToUpperInvariant()}}},{_version}\"";
        public string CTag => $"\"c:{{{_guid.ToString().ToUpperInvariant()}}},{_contentVersion}\"";
        public long TotalSize => IsFolder ? Children.Sum(c => c.TotalSize) : Content.Length;

        public Item? Child(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        public void SetContent(byte[] content)
        {
            Content = content;
            Touch(content: true);
        }

        public void Touch(bool content)
        {
            _version++;
            if (content)
                _contentVersion++;
            Modified = DateTimeOffset.UtcNow;
        }
    }

    private sealed class Session(Item parent, string name, string behavior)
    {
        public Item Parent { get; } = parent;
        public string Name { get; } = name;
        public string Behavior { get; } = behavior;
        public long? Total { get; set; }
        public MemoryStream Received { get; } = new();
    }

    private sealed class CopyOperation(Item source, Item parent, string name, int polls)
    {
        public Item Source { get; } = source;
        public Item Parent { get; } = parent;
        public string Name { get; } = name;
        public int PollsLeft { get; set; } = polls;
        public string? Result { get; set; }
    }

    private sealed class Fault(int count, HttpStatusCode? status, Func<RecordedRequest, bool> when, string? body, string contentType)
    {
        public int Remaining { get; set; } = count;
        public HttpStatusCode? Status { get; } = status;
        public Func<RecordedRequest, bool> When { get; } = when;
        public string? Body { get; } = body;
        public string ContentType { get; } = contentType;
    }
}
