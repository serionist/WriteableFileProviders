using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

// Box's digests are SHA-1: the protocol asks for them, as integrity checks.
#pragma warning disable CA5350

namespace WriteableFileProviders.Box.Tests.Fake;

/// <summary>
/// A Box account in memory, behind the Box API's URLs: the token endpoint (with the JWT assertion checked against the
/// test key), folders and files with ETags and versions, marker paging, direct and chunked uploads, server-side moves and
/// copies, the trash-free delete, and pre-authenticated download URLs that serve byte ranges of a pinned version. It
/// answers the way the Box API reference and the SDK's behaviour describe, so the provider's whole contract runs offline.
/// </summary>
/// <remarks>Tests change its behaviour through the properties, and inject faults through <see cref="Intercept"/>.</remarks>
internal sealed class FakeBox : HttpMessageHandler
{
    public const string ApiUrl = "https://api.box.test";
    public const string UploadUrl = "https://upload.box.test/api";
    public const string AccountUrl = "https://account.box.test/api/oauth2";
    public const string DownloadUrl = "https://dl.box.test";
    public const string RootId = "0";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string FileId, string VersionId)> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UploadSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];
    private int _nextId = 1000;

    public FakeBox() => _items[RootId] = new Item { Id = RootId, IsFolder = true, Name = "All Files", ParentId = null, Modified = DateTimeOffset.UtcNow };

    /// <summary>The app credentials the token endpoint accepts.</summary>
    public TestCredentials Credentials { get; init; } = TestCredentials.Default;

    /// <summary>The most entries a listing page holds, whatever limit is asked; Box's own cap is 1000.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>How the last page of a marker listing says it is the last.</summary>
    public LastPageMarker LastPageMarker { get; set; } = LastPageMarker.Null;

    /// <summary>The part size of new upload sessions.</summary>
    public long PartSize { get; set; } = 128 * 1024;

    /// <summary>The smallest file an upload session takes (Box: 20 MB).</summary>
    public long ChunkedMinimum { get; set; }

    /// <summary>Whether the download host ignores Range and sends the whole file with 200.</summary>
    public bool IgnoreRange { get; set; }

    /// <summary>How many of the next commits answer 202: the parts are still being processed.</summary>
    public int CommitsNotReady { get; set; }

    /// <summary>How many of the next download URL requests answer 202: the file is not ready yet.</summary>
    public int DownloadsNotReady { get; set; }

    /// <summary>Whether a rename to a name that differs only in case is refused as taken.</summary>
    public bool RefuseCaseOnlyRename { get; set; }

    /// <summary>
    /// Runs before every request: returns a response to answer with instead, or null to go on. Tests inject faults and
    /// races with it.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>The access tokens issued so far.</summary>
    public int TokensIssued { get; private set; }

    /// <summary>The upload sessions aborted so far.</summary>
    public int SessionsAborted { get; private set; }

    /// <summary>The upload sessions neither committed nor aborted.</summary>
    public int OpenSessions
    {
        get
        {
            lock (_gate)
                return _sessions.Count;
        }
    }

    /// <summary>Every request served, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    /// <summary>Forgets the requests served so far.</summary>
    public void ClearRequests()
    {
        lock (_gate)
            _requests.Clear();
    }

    /// <summary>Makes every access token issued so far invalid, as when they expire.</summary>
    public void ExpireTokens()
    {
        lock (_gate)
            _tokens.Clear();
    }

    public string AddFolder(string parentId, string name)
    {
        lock (_gate)
            return Create(parentId, name, isFolder: true, null).Id;
    }

    public string AddFile(string parentId, string name, string text)
    {
        lock (_gate)
            return Create(parentId, name, isFolder: false, Encoding.UTF8.GetBytes(text)).Id;
    }

    /// <summary>The id of the item at a path under the account root, matched without regard to case, or null.</summary>
    public string? IdOf(string path)
    {
        lock (_gate)
            return Walk(path)?.Id;
    }

    public string NameOf(string id)
    {
        lock (_gate)
            return _items[id].Name;
    }

    public string ETagOf(string id)
    {
        lock (_gate)
            return _items[id].ETag.ToString(CultureInfo.InvariantCulture);
    }

    public int VersionCount(string id)
    {
        lock (_gate)
            return _items[id].Versions.Count;
    }

    public string ContentOf(string path)
    {
        lock (_gate)
            return Encoding.UTF8.GetString((Walk(path) ?? throw new FileNotFoundException(path)).Current.Bytes);
    }

    public void Delete(string id)
    {
        lock (_gate)
            Remove(id);
    }

    /// <summary>Uploads a new version of a file behind the provider's back.</summary>
    public void Overwrite(string id, string text)
    {
        lock (_gate)
            AddVersion(_items[id], Encoding.UTF8.GetBytes(text));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null or MultipartFormDataContent ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Record(request);
        if (Intercept?.Invoke(request) is { } intercepted)
            return intercepted;
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == ApiUrl + "/oauth2/token" && request.Method == HttpMethod.Post)
            return Token(body);
        if (url.StartsWith(DownloadUrl + "/d/", StringComparison.Ordinal))
            return Download(url[(DownloadUrl.Length + 3)..], request);
        if (!Authorized(request))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new ByteArrayContent([]) };
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        if (url.StartsWith(ApiUrl + "/2.0/", StringComparison.Ordinal))
            return Api(request.Method, url[(ApiUrl.Length + 5)..].Split('/'), query, request, body);
        if (url.StartsWith(UploadUrl + "/2.0/", StringComparison.Ordinal))
            return await UploadAsync(request.Method, url[(UploadUrl.Length + 5)..].Split('/'), request, body, cancellationToken).ConfigureAwait(false);
        return Error(HttpStatusCode.NotFound, "not_found", "No such endpoint.");
    }

    // ----- Authentication -----

    private HttpResponseMessage Token(byte[]? body)
    {
        var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body ?? []));
        if (form["grant_type"] != "urn:ietf:params:oauth:grant-type:jwt-bearer")
            return OAuthError("unsupported_grant_type");
        if (form["client_id"] != Credentials.ClientId || form["client_secret"] != Credentials.ClientSecret)
            return OAuthError("invalid_client");
        if (!ValidAssertion(form["assertion"]))
            return OAuthError("invalid_grant");
        lock (_gate)
        {
            var token = "token-" + ++TokensIssued;
            _tokens.Add(token);
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["access_token"] = token,
                ["expires_in"] = 3600,
                ["token_type"] = "bearer",
                ["restricted_to"] = new JsonArray(),
            });
        }
    }

    // The JWT Box checks: RS256 signed by the app's key, issued by the client for the enterprise, for Box's token URL.
    private bool ValidAssertion(string? assertion)
    {
        var parts = assertion?.Split('.');
        if (parts is not { Length: 3 })
            return false;
        using var key = RSA.Create();
        key.ImportFromPem(Credentials.PublicKeyPem);
        if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return false;
        var header = JsonNode.Parse(FromBase64Url(parts[0]))!;
        var payload = JsonNode.Parse(FromBase64Url(parts[1]))!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = payload["exp"]!.GetValue<long>();
        var jti = payload["jti"]!.GetValue<string>();
        return (string?)header["alg"] == "RS256"
            && (string?)payload["iss"] == Credentials.ClientId
            && (string?)payload["sub"] == Credentials.EnterpriseId
            && (string?)payload["box_sub_type"] == "enterprise"
            && (string?)payload["aud"] == "https://api.box.com/oauth2/token"
            && expires > now - 5 && expires <= now + 65
            && jti.Length is >= 16 and <= 128;
    }

    private bool Authorized(HttpRequestMessage request)
    {
        var authorization = request.Headers.Authorization;
        lock (_gate)
            return authorization is { Scheme: "Bearer", Parameter: { } token } && _tokens.Contains(token);
    }

    // ----- The API host -----

    private HttpResponseMessage Api(HttpMethod method, string[] route, System.Collections.Specialized.NameValueCollection query, HttpRequestMessage request, byte[]? body)
    {
        lock (_gate)
        {
            return (method.Method, route) switch
            {
                ("GET", ["folders", var id, "items"]) => ListFolder(id, query),
                ("GET", ["folders", var id]) => Get(id, folder: true, query),
                ("POST", ["folders"]) => CreateFolder(Parse(body)),
                ("DELETE", ["folders", var id]) => DeleteFolder(id, IsTrue(query["recursive"]), request),
                ("GET", ["files", var id]) => Get(id, folder: false, query),
                ("PUT", ["files", var id]) => UpdateFile(id, Parse(body), request, query),
                ("POST", ["files", var id, "copy"]) => CopyFile(id, Parse(body), query),
                ("DELETE", ["files", var id]) => DeleteFile(id, request),
                ("GET", ["files", var id, "content"]) => DownloadLink(id, query["version"]),
                _ => Error(HttpStatusCode.NotFound, "not_found", "No such endpoint."),
            };
        }
    }

    private HttpResponseMessage ListFolder(string id, System.Collections.Specialized.NameValueCollection query)
    {
        if (!_items.TryGetValue(id, out var folder) || !folder.IsFolder)
            return NotFound();
        var children = Children(id).OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        var limit = (int)Math.Min(Math.Min(long.Parse(query["limit"] ?? "100", CultureInfo.InvariantCulture), 1000), MaxPageSize);
        var fields = Fields(query);
        var marker = IsTrue(query["usemarker"]);
        var offset = marker
            ? query["marker"] is { Length: > 0 } m ? int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(m)), CultureInfo.InvariantCulture) : 0
            : int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
        var page = children.Skip(offset).Take(limit).ToList();
        var result = new JsonObject
        {
            ["limit"] = limit,
            ["entries"] = new JsonArray([.. page.Select(i => (JsonNode)Render(i, fields, full: false))]),
        };
        if (!marker)
        {
            result["total_count"] = children.Count;
            result["offset"] = offset;
        }
        else if (offset + page.Count < children.Count)
        {
            result["next_marker"] = Convert.ToBase64String(Encoding.UTF8.GetBytes((offset + page.Count).ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            switch (LastPageMarker)
            {
                case LastPageMarker.Null:
                    result["next_marker"] = null;
                    break;
                case LastPageMarker.Empty:
                    result["next_marker"] = "";
                    break;
            }
        }
        return Json(HttpStatusCode.OK, result);
    }

    private HttpResponseMessage Get(string id, bool folder, System.Collections.Specialized.NameValueCollection query) =>
        _items.TryGetValue(id, out var item) && item.IsFolder == folder ? Json(HttpStatusCode.OK, Render(item, Fields(query), full: true)) : NotFound();

    private HttpResponseMessage CreateFolder(JsonNode body)
    {
        var name = (string?)body["name"] ?? "";
        var parentId = (string?)body["parent"]?["id"] ?? "";
        if (!_items.TryGetValue(parentId, out var parent) || !parent.IsFolder)
            return NotFound();
        if (NameError(name) is { } invalid)
            return invalid;
        if (Conflict(parentId, name, null) is { } taken)
            return NameInUse(taken, asArray: true);
        return Json(HttpStatusCode.Created, Render(Create(parentId, name, isFolder: true, null), null, full: true));
    }

    private HttpResponseMessage DeleteFolder(string id, bool recursive, HttpRequestMessage request)
    {
        if (!_items.TryGetValue(id, out var folder) || !folder.IsFolder)
            return NotFound();
        if (id == RootId)
            return Error(HttpStatusCode.Forbidden, "access_denied_insufficient_permissions", "The root folder cannot be deleted.");
        if (IfMatchFails(request, folder))
            return PreconditionFailed();
        if (!recursive && Children(id).Any())
            return Error(HttpStatusCode.BadRequest, "folder_not_empty", "The folder is not empty.");
        Remove(id);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage UpdateFile(string id, JsonNode body, HttpRequestMessage request, System.Collections.Specialized.NameValueCollection query)
    {
        if (!_items.TryGetValue(id, out var file) || file.IsFolder)
            return NotFound();
        if (IfMatchFails(request, file))
            return PreconditionFailed();
        var name = (string?)body["name"] ?? file.Name;
        var parentId = (string?)body["parent"]?["id"] ?? file.ParentId!;
        if (!_items.TryGetValue(parentId, out var parent) || !parent.IsFolder)
            return NotFound();
        if (NameError(name) is { } invalid)
            return invalid;
        if (Conflict(parentId, name, id) is { } taken)
            return NameInUse(taken, asArray: false);
        if (RefuseCaseOnlyRename && parentId == file.ParentId && name != file.Name && string.Equals(name, file.Name, StringComparison.OrdinalIgnoreCase))
            return NameInUse(file, asArray: false);
        file.Name = name;
        file.ParentId = parentId;
        Bump(file);
        return Json(HttpStatusCode.OK, Render(file, Fields(query), full: true));
    }

    private HttpResponseMessage CopyFile(string id, JsonNode body, System.Collections.Specialized.NameValueCollection query)
    {
        if (!_items.TryGetValue(id, out var source) || source.IsFolder)
            return NotFound();
        var parentId = (string?)body["parent"]?["id"] ?? "";
        if (!_items.TryGetValue(parentId, out var parent) || !parent.IsFolder)
            return NotFound();
        var name = (string?)body["name"] ?? source.Name;
        if (NameError(name) is { } invalid)
            return invalid;
        if (Conflict(parentId, name, null) is { } taken)
            return NameInUse(taken, asArray: false);
        var versionId = (string?)body["version"];
        var version = versionId is null ? source.Current : source.Versions.FirstOrDefault(v => v.Id == versionId);
        if (version is null)
            return NotFound();
        return Json(HttpStatusCode.Created, Render(Create(parentId, name, isFolder: false, version.Bytes), Fields(query), full: true));
    }

    private HttpResponseMessage DeleteFile(string id, HttpRequestMessage request)
    {
        if (!_items.TryGetValue(id, out var file) || file.IsFolder)
            return NotFound();
        if (IfMatchFails(request, file))
            return PreconditionFailed();
        Remove(id);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    // GET /files/{id}/content: a redirect to a fresh pre-authenticated URL for the version, or 202 while not ready.
    private HttpResponseMessage DownloadLink(string id, string? versionId)
    {
        if (!_items.TryGetValue(id, out var file) || file.IsFolder)
            return NotFound();
        if (DownloadsNotReady > 0)
        {
            DownloadsNotReady--;
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            accepted.Headers.Add("Retry-After", "0");
            return accepted;
        }
        var version = versionId is null ? file.Current : file.Versions.FirstOrDefault(v => v.Id == versionId);
        if (version is null)
            return NotFound();
        var token = Guid.NewGuid().ToString("N");
        _downloads[token] = (id, version.Id);
        var found = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
        found.Headers.Location = new Uri($"{DownloadUrl}/d/{token}");
        return found;
    }

    // ----- The download host: no authentication, the URL is the credential -----

    private HttpResponseMessage Download(string token, HttpRequestMessage request)
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!_downloads.TryGetValue(token, out var target) || !_items.TryGetValue(target.FileId, out var file))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            bytes = file.Versions.First(v => v.Id == target.VersionId).Bytes;
        }
        if (request.Headers.Range is { Unit: "bytes", Ranges.Count: 1 } header && header.Ranges.First() is { From: { } from } range && !IgnoreRange)
        {
            if (from >= bytes.Length)
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            var to = Math.Min(range.To ?? bytes.Length - 1, bytes.Length - 1);
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)from..(int)(to + 1)]) };
            partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, bytes.Length);
            return partial;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    // ----- The upload host -----

    private async Task<HttpResponseMessage> UploadAsync(HttpMethod method, string[] route, HttpRequestMessage request, byte[]? body, CancellationToken cancellationToken)
    {
        switch (method.Method, route)
        {
            case ("POST", ["files", "content"]):
            case ("POST", ["files", _, "content"]):
                var (attributes, content) = await MultipartAsync(request, cancellationToken).ConfigureAwait(false);
                if (attributes is null || content is null)
                    return Error(HttpStatusCode.BadRequest, "bad_request", "The attributes part must come first, then the file part.");
                lock (_gate)
                    return route.Length == 2 ? UploadNew(attributes, content) : UploadVersion(route[1], attributes, content, request);
        }
        lock (_gate)
        {
            return (method.Method, route) switch
            {
                ("POST", ["files", "upload_sessions"]) => CreateSession(Parse(body), null),
                ("POST", ["files", var id, "upload_sessions"]) => CreateSession(Parse(body), id),
                ("PUT", ["files", "upload_sessions", var id]) => UploadPart(id, request, body ?? []),
                ("POST", ["files", "upload_sessions", var id, "commit"]) => Commit(id, Parse(body), request),
                ("DELETE", ["files", "upload_sessions", var id]) => Abort(id),
                _ => Error(HttpStatusCode.NotFound, "not_found", "No such endpoint."),
            };
        }
    }

    private HttpResponseMessage UploadNew(JsonNode attributes, byte[] content)
    {
        var name = (string?)attributes["name"] ?? "";
        var parentId = (string?)attributes["parent"]?["id"] ?? "";
        if (!_items.TryGetValue(parentId, out var parent) || !parent.IsFolder)
            return NotFound();
        if (NameError(name) is { } invalid)
            return invalid;
        if (Conflict(parentId, name, null) is { } taken)
            return NameInUse(taken, asArray: false);
        return Files(HttpStatusCode.Created, Create(parentId, name, isFolder: false, content));
    }

    private HttpResponseMessage UploadVersion(string id, JsonNode attributes, byte[] content, HttpRequestMessage request)
    {
        if (!_items.TryGetValue(id, out var file) || file.IsFolder)
            return NotFound();
        if (IfMatchFails(request, file))
            return PreconditionFailed();
        if ((string?)attributes["name"] is { } name && name != file.Name)
        {
            if (Conflict(file.ParentId!, name, id) is { } taken)
                return NameInUse(taken, asArray: false);
            file.Name = name;
        }
        AddVersion(file, content);
        return Files(HttpStatusCode.OK, file);
    }

    private HttpResponseMessage CreateSession(JsonNode body, string? fileId)
    {
        var size = body["file_size"]!.GetValue<long>();
        string folderId;
        string name;
        if (fileId is null)
        {
            folderId = (string?)body["folder_id"] ?? "";
            name = (string?)body["file_name"] ?? "";
            if (!_items.TryGetValue(folderId, out var folder) || !folder.IsFolder)
                return NotFound();
            if (NameError(name) is { } invalid)
                return invalid;
            if (Conflict(folderId, name, null) is { } taken)
                return NameInUse(taken, asArray: false);
        }
        else
        {
            if (!_items.TryGetValue(fileId, out var file) || file.IsFolder)
                return NotFound();
            folderId = file.ParentId!;
            name = (string?)body["file_name"] ?? file.Name;
        }
        if (size < ChunkedMinimum)
            return Error(HttpStatusCode.BadRequest, "file_size_too_small", "The file is too small for an upload session.");
        var session = new UploadSessionState(NewId("s"), folderId, fileId, name, size, PartSize);
        _sessions[session.Id] = session;
        var endpoint = $"{UploadUrl}/2.0/files/upload_sessions/{session.Id}";
        return Json(HttpStatusCode.Created, new JsonObject
        {
            ["id"] = session.Id,
            ["type"] = "upload_session",
            ["session_expires_at"] = Date(DateTimeOffset.UtcNow.AddDays(7)),
            ["part_size"] = session.PartSize,
            ["total_parts"] = (int)Math.Max(1, (size + session.PartSize - 1) / session.PartSize),
            ["num_parts_processed"] = 0,
            ["session_endpoints"] = new JsonObject
            {
                ["upload_part"] = endpoint,
                ["commit"] = endpoint + "/commit",
                ["abort"] = endpoint,
                ["list_parts"] = endpoint + "/parts",
                ["status"] = endpoint,
                ["log_event"] = endpoint + "/log",
            },
        });
    }

    private HttpResponseMessage UploadPart(string id, HttpRequestMessage request, byte[] body)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return NotFound();
        var range = request.Content?.Headers.ContentRange;
        if (range is not { From: { } from, To: { } to, Length: { } total } || total != session.Size)
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "range_not_satisfiable", "The part's range does not fit the session.");
        var expected = Math.Min(session.PartSize, session.Size - from);
        if (from % session.PartSize != 0 || to - from + 1 != expected || body.Length != expected)
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "range_not_satisfiable", "Every part but the last must be exactly the part size, aligned to it.");
        var digest = request.Headers.TryGetValues("digest", out var values) ? values.First() : "";
        if (digest != "sha=" + Convert.ToBase64String(SHA1.HashData(body)))
            return Error(HttpStatusCode.PreconditionFailed, "precondition_failed", "The digest does not match the part.");
        var part = new JsonObject
        {
            ["part_id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)),
            ["offset"] = from,
            ["size"] = body.Length,
            ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(body)),
        };
        session.Parts[from] = (body, part);
        return Json(HttpStatusCode.OK, new JsonObject { ["part"] = part });
    }

    private HttpResponseMessage Commit(string id, JsonNode body, HttpRequestMessage request)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return NotFound();
        var listed = body["parts"]!.AsArray().Select(p => PartKey(p!)).ToList();
        var stored = session.Parts.Values.Select(p => PartKey(p.Part)).ToList();
        if (!listed.SequenceEqual(stored))
            return Error(HttpStatusCode.BadRequest, "bad_request", "The parts listed are not the parts uploaded.");
        var content = session.Parts.Values.SelectMany(p => p.Bytes).ToArray();
        if (content.Length != session.Size)
            return Error(HttpStatusCode.BadRequest, "bad_request", "Parts are missing.");
        var digest = request.Headers.TryGetValues("digest", out var values) ? values.First() : "";
        if (digest != "sha=" + Convert.ToBase64String(SHA1.HashData(content)))
            return Error(HttpStatusCode.UnprocessableEntity, "bad_digest", "The digest does not match the file.");
        Item file;
        if (session.FileId is null)
        {
            if (Conflict(session.FolderId, session.Name, null) is { } taken)
                return NameInUse(taken, asArray: false);
            if (NotReady())
                return Accepted();
            file = Create(session.FolderId, session.Name, isFolder: false, content);
        }
        else
        {
            if (!_items.TryGetValue(session.FileId, out var existing))
                return NotFound();
            if (IfMatchFails(request, existing))
                return PreconditionFailed();
            if (NotReady())
                return Accepted();
            file = existing;
            AddVersion(file, content);
        }
        _sessions.Remove(id);
        return Files(HttpStatusCode.Created, file);

        bool NotReady()
        {
            if (CommitsNotReady <= 0)
                return false;
            CommitsNotReady--;
            return true;
        }

        static HttpResponseMessage Accepted()
        {
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            accepted.Headers.Add("Retry-After", "0");
            return accepted;
        }
    }

    private static string PartKey(JsonNode part) =>
        $"{(string?)part["part_id"]}/{part["offset"]?.ToJsonString()}/{part["size"]?.ToJsonString()}/{(string?)part["sha1"]}";

    private HttpResponseMessage Abort(string id)
    {
        if (!_sessions.Remove(id))
            return NotFound();
        SessionsAborted++;
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    // ----- State -----

    private Item Create(string parentId, string name, bool isFolder, byte[]? content)
    {
        var item = new Item { Id = NewId(""), IsFolder = isFolder, Name = name, ParentId = parentId, Modified = DateTimeOffset.UtcNow };
        _items[item.Id] = item;
        if (!isFolder)
            item.Versions.Add(new FileVersion(NewId("v"), content ?? []));
        return item;
    }

    private void AddVersion(Item file, byte[] content)
    {
        file.Versions.Add(new FileVersion(NewId("v"), content));
        Bump(file);
    }

    private static void Bump(Item item)
    {
        item.ETag++;
        item.Sequence++;
        item.Modified = DateTimeOffset.UtcNow;
    }

    private void Remove(string id)
    {
        foreach (var child in Children(id).ToList())
            Remove(child.Id);
        _items.Remove(id);
    }

    private IEnumerable<Item> Children(string folderId) => _items.Values.Where(i => i.ParentId == folderId);

    private Item? Conflict(string parentId, string name, string? exceptId) =>
        Children(parentId).FirstOrDefault(i => i.Id != exceptId && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    private Item? Walk(string path)
    {
        var current = _items[RootId];
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Children(current.Id).FirstOrDefault(i => string.Equals(i.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }
        return current;
    }

    private string NewId(string prefix) => prefix + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);

    // Box compares If-Match with the ETag; the SDK can only send it quoted, so both forms match here.
    private static bool IfMatchFails(HttpRequestMessage request, Item item) =>
        request.Headers.TryGetValues("If-Match", out var values) && values.First().Trim('"') != item.ETag.ToString(CultureInfo.InvariantCulture);

    private static HttpResponseMessage? NameError(string name)
    {
        if (name.Length > 255)
            return Error(HttpStatusCode.BadRequest, "item_name_too_long", "The name is too long.");
        if (name.Length == 0 || name is "." or ".." || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)
            || name.EndsWith(' ') || name.Any(char.IsControl))
            return Error(HttpStatusCode.BadRequest, "item_name_invalid", "The name is not allowed.");
        return null;
    }

    // ----- JSON -----

    private static readonly string[] _fileMini = ["type", "id", "etag", "name", "sequence_id", "sha1", "file_version"];
    private static readonly string[] _folderMini = ["type", "id", "etag", "name", "sequence_id"];

    // Box returns the mini representation plus the fields asked for; a GET without fields returns the standard one.
    private static JsonObject Render(Item item, string[]? fields, bool full)
    {
        var node = new JsonObject
        {
            ["type"] = item.IsFolder ? "folder" : "file",
            ["id"] = item.Id,
            ["etag"] = item.ETag.ToString(CultureInfo.InvariantCulture),
            ["sequence_id"] = item.Sequence.ToString(CultureInfo.InvariantCulture),
            ["name"] = item.Name,
            ["modified_at"] = Date(item.Modified),
            ["parent"] = item.ParentId is null ? null : new JsonObject { ["type"] = "folder", ["id"] = item.ParentId },
        };
        if (!item.IsFolder)
        {
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(item.Current.Bytes));
            node["sha1"] = sha1;
            node["file_version"] = new JsonObject { ["type"] = "file_version", ["id"] = item.Current.Id, ["sha1"] = sha1 };
            node["size"] = item.Current.Bytes.Length;
            node["content_modified_at"] = Date(item.Modified);
        }
        if (fields is null && full)
            return node;
        var keep = (item.IsFolder ? _folderMini : _fileMini).Concat(fields ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var key in node.Select(p => p.Key).ToList())
        {
            if (!keep.Contains(key))
                node.Remove(key);
        }
        return node;
    }

    private static HttpResponseMessage Files(HttpStatusCode status, Item file) =>
        Json(status, new JsonObject { ["total_count"] = 1, ["entries"] = new JsonArray(Render(file, null, full: true)) });

    private static string[]? Fields(System.Collections.Specialized.NameValueCollection query) =>
        query["fields"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Date(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static JsonNode Parse(byte[]? body) => JsonNode.Parse(body is { Length: > 0 } ? body : "{}"u8.ToArray())!;

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    private static async Task<(JsonNode? Attributes, byte[]? File)> MultipartAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not MultipartFormDataContent multipart)
            return (null, null);
        JsonNode? attributes = null;
        foreach (var part in multipart)
        {
            switch (part.Headers.ContentDisposition?.Name?.Trim('"'))
            {
                case "attributes":
                    attributes = JsonNode.Parse(await part.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    break;
                case "file" when attributes is not null:
                    return (attributes, await part.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                default:
                    return (null, null);
            }
        }
        return (null, null);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message, JsonNode? contextInfo = null) =>
        Json(status, new JsonObject
        {
            ["type"] = "error",
            ["status"] = (int)status,
            ["code"] = code,
            ["message"] = message,
            ["request_id"] = "fake-" + Guid.NewGuid().ToString("N")[..8],
            ["context_info"] = contextInfo,
        });

    private static HttpResponseMessage OAuthError(string error) =>
        Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = error, ["error_description"] = "The fake refused the token request." });

    private static HttpResponseMessage NotFound() => Error(HttpStatusCode.NotFound, "not_found", "Not found.");

    private static HttpResponseMessage PreconditionFailed() => Error(HttpStatusCode.PreconditionFailed, "precondition_failed", "The resource has been modified.");

    // Box sends the conflict as an object or as an array, depending on the endpoint.
    private static HttpResponseMessage NameInUse(Item taken, bool asArray)
    {
        var mini = Render(taken, null, full: false);
        return Error(HttpStatusCode.Conflict, "item_name_in_use", "Item with the same name already exists",
            new JsonObject { ["conflicts"] = asArray ? new JsonArray(mini) : mini });
    }

    private void Record(HttpRequestMessage request)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            _requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, headers));
    }

    private sealed class Item
    {
        public required string Id { get; init; }
        public required bool IsFolder { get; init; }
        public required string Name { get; set; }
        public string? ParentId { get; set; }
        public int ETag { get; set; }
        public int Sequence { get; set; }
        public DateTimeOffset Modified { get; set; }
        public List<FileVersion> Versions { get; } = [];
        public FileVersion Current => Versions[^1];
    }

    private sealed record FileVersion(string Id, byte[] Bytes);

    private sealed class UploadSessionState(string id, string folderId, string? fileId, string name, long size, long partSize)
    {
        public string Id { get; } = id;
        public string FolderId { get; } = folderId;
        public string? FileId { get; } = fileId;
        public string Name { get; } = name;
        public long Size { get; } = size;
        public long PartSize { get; } = partSize;
        public SortedDictionary<long, (byte[] Bytes, JsonObject Part)> Parts { get; } = [];
    }
}

/// <summary>How the last page of a marker listing ends: <c>next_marker</c> null, absent, or empty.</summary>
public enum LastPageMarker
{
    Null,
    Absent,
    Empty,
}

/// <summary>A request the fake served.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The URL.</param>
/// <param name="Headers">The request's and its content's headers.</param>
internal sealed record RecordedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public string Path => Uri.AbsolutePath;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public override string ToString() => $"{Method} {Uri}";
}
