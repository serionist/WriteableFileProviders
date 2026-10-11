using System.Net;
using System.Text;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// No error the provider throws carries a credential: the SDK keeps the request in its <see cref="BoxApiException"/>, with
/// the access token in the Authorization header and, for a token request, the client secret in the body.
/// </summary>
[TestClass]
public sealed class BoxRedactionTests
{
    private static readonly TestCredentials _credentials = TestCredentials.Default;

    public static IEnumerable<object[]> Clients => [["options"], ["ready client"]];

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task ListingFailure(string built)
    {
        var (box, provider) = Create(built);
        using (provider)
        {
            box.Intercept = Fail(r => r.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal), HttpStatusCode.BadRequest, "bad_request");
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("a.txt")));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task UploadFailure(string built)
    {
        var (box, provider) = Create(built);
        using (provider)
        {
            box.Intercept = Fail(r => r.RequestUri!.AbsolutePath == "/api/2.0/files/content", HttpStatusCode.BadRequest, "bad_request");
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.txt", TestStreams.Text("x"))));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task DeleteFailure(string built)
    {
        var (box, provider) = Create(built);
        using (provider)
        {
            box.AddFile(FakeBox.RootId, "a.txt", "x");
            box.Intercept = Fail(r => r.Method == HttpMethod.Delete, HttpStatusCode.BadRequest, "bad_request");
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("a.txt")));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task MoveAndCopyConflicts(string built)
    {
        var (box, provider) = Create(built);
        using (provider)
        {
            box.AddFile(FakeBox.RootId, "a.txt", "x");
            box.Intercept = Fail(r => r.Method == HttpMethod.Put || r.RequestUri!.AbsolutePath.EndsWith("/copy", StringComparison.Ordinal), HttpStatusCode.Conflict, "item_name_in_use");
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.MoveAsync("a.txt", "b.txt")));
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("a.txt", "b.txt")));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task PreconditionAndNameFailures(string built)
    {
        var (box, provider) = Create(built);
        using (provider)
        {
            provider.SendIfMatch = true;
            var id = box.AddFile(FakeBox.RootId, "a.txt", "x");
            var etag = (await provider.GetFileInfoAsync("a.txt")).ETag;
            box.Intercept = request =>
            {
                if (request.RequestUri!.AbsolutePath == $"/api/2.0/files/{id}/content")
                    box.Overwrite(id, "theirs");
                return null;
            };
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
                provider.UpdateFileAsync("a.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfMatch = etag })));

            box.Intercept = null;
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync("b.txt ", TestStreams.Text("x"))));
        }
    }

    [TestMethod]
    [DynamicData(nameof(Clients))]
    public async Task NotFoundAfterTheFileWasFound(string built)
    {
        var (box, provider) = Create(built);
        using (provider)
        {
            var id = box.AddFile(FakeBox.RootId, "a.txt", "x");
            box.Intercept = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                    box.Delete(id);
                return null;
            };
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("a.txt")));
        }
    }

    [TestMethod]
    public void Redact_KeepsTheAnswer_AndOnlyTheAllowListedParts()
    {
        // As the SDK builds it: the query parameters are in the URL too.
        var original = new BoxApiException("{\"code\":\"bad_request\"}", DateTimeOffset.UtcNow,
            new RequestInfo("GET", "https://user:pw@api.box.test/2.0/folders/0/items?fields=id&access_token=token-9&shared_link_password=hunter2#x",
                new Dictionary<string, string> { ["fields"] = "id", ["access_token"] = "token-9", ["shared_link_password"] = "hunter2" },
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer token-9",
                    ["As-User"] = "7",
                    ["If-Match"] = "\"3\"",
                    ["BoxApi"] = "shared_link=s&shared_link_password=hunter2",
                    ["Proxy-Authorization"] = "Basic hunter2",
                    ["Cookie"] = "session=hunter2",
                    ["X-Custom-Auth"] = "hunter2",
                },
                "application/json")
            {
                Body = "client_secret=wrong-secret&assertion=x",
            },
            new ResponseInfo(400, new Dictionary<string, string> { ["Retry-After"] = "1", ["Set-Cookie"] = "session=hunter2" },
                JsonUtils.JsonToSerializedData("{\"code\":\"bad_request\"}"), "{\"code\":\"bad_request\"}", "bad_request", null, "r1", null));

        var redacted = RedactingNetworkClient.Redact(original);

        Assert.AreEqual(original.Message, redacted.Message);
        Assert.AreEqual(400, redacted.ResponseInfo.StatusCode);
        Assert.AreEqual("bad_request", redacted.ResponseInfo.Code);
        Assert.AreEqual("r1", redacted.ResponseInfo.RequestId);
        Assert.AreEqual("GET", redacted.RequestInfo.Method);
        Assert.AreEqual("https://api.box.test/2.0/folders/0/items", redacted.RequestInfo.Url);
        CollectionAssert.AreEquivalent(new[] { "fields" }, redacted.RequestInfo.QueryParams.Keys.ToList());
        CollectionAssert.AreEquivalent(new[] { "As-User", "If-Match" }, redacted.RequestInfo.Headers.Keys.ToList());
        CollectionAssert.AreEquivalent(new[] { "Retry-After" }, redacted.ResponseInfo.Headers.Keys.ToList());
        CredentialAssert.CarriesNone(redacted, "hunter2", "shared_link", "user:pw");
    }

    [TestMethod]
    public void Redact_KeepsAUrlThatIsNotAbsolute_WithoutItsQuery()
    {
        var original = new BoxApiException("", DateTimeOffset.UtcNow,
            new RequestInfo("GET", "/2.0/files/1?access_token=token-9", null, new Dictionary<string, string>()),
            new ResponseInfo(500, new Dictionary<string, string>(), JsonUtils.JsonToSerializedData("{}"), "", null, null, null, null));

        Assert.AreEqual("/2.0/files/1", RedactingNetworkClient.Redact(original).RequestInfo.Url);
    }

    [TestMethod]
    public void Redact_CopesWithAnErrorThatHoldsLittle()
    {
        var original = new BoxApiException("", DateTimeOffset.UtcNow,
            new RequestInfo("GET", null, null, null!),
            new ResponseInfo(502, null!, null!, null!, null, null, null, null));

        var redacted = RedactingNetworkClient.Redact(original);

        Assert.IsTrue(string.IsNullOrEmpty(redacted.RequestInfo.Url));
        Assert.AreEqual(0, redacted.RequestInfo.Headers.Count);
        Assert.AreEqual(0, redacted.ResponseInfo.Headers.Count);
        Assert.AreEqual("", redacted.ResponseInfo.RawBody);
        Assert.AreEqual(502, redacted.ResponseInfo.StatusCode);
    }

    [TestMethod]
    public async Task ReadyClientsExtraHeaders_StayOutOfTheErrors()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        var client = _credentials.Client(box).WithExtraHeaders(new Dictionary<string, string>
        {
            ["BoxApi"] = "shared_link=https://app.box.test/s/abc&shared_link_password=hunter2-secret",
        });
        using var provider = new WriteableBoxFileProvider(new BoxFileProviderOptions(), client) { PollDelay = TimeSpan.Zero };
        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        Assert.IsTrue(box.Requests.Any(r => r.Header("BoxApi") is { } value && value.Contains("hunter2-secret", StringComparison.Ordinal)), "The header reaches Box");

        box.Intercept = Fail(r => r.Method == HttpMethod.Put || r.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal), HttpStatusCode.Conflict, "item_name_in_use");
        CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("a.txt")), "hunter2-secret", "shared_link");
        box.Intercept = Fail(r => r.Method == HttpMethod.Delete, HttpStatusCode.BadRequest, "bad_request");
        CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("a.txt")), "hunter2-secret", "shared_link");
    }

    [TestMethod]
    public async Task ReadyClientsAsUserHeader_ReachesBox()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = new WriteableBoxFileProvider(new BoxFileProviderOptions(), _credentials.Client(box).WithAsUserHeader("7"));

        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        await using (var stream = await provider.ReadFileAsync("a.txt"))
            Assert.AreEqual("x", await new StreamReader(stream).ReadToEndAsync());

        var api = box.Requests.Where(r => r.Uri.Host == "api.box.test" && r.Path != "/oauth2/token").ToList();
        Assert.IsTrue(api.Count > 0);
        Assert.IsTrue(api.All(r => r.Header("As-User") == "7"), "The copy keeps the session's headers, on the provider's own calls too");
    }

    [TestMethod]
    public void CredentialAssert_WalksExceptionDataAndContextValues()
    {
        static BoxApiException Error(Dictionary<string, object>? context) =>
            new("", DateTimeOffset.UtcNow, new RequestInfo("GET", "https://api.box.test/2.0/files/1", null, new Dictionary<string, string>()),
                new ResponseInfo(409, new Dictionary<string, string>(), JsonUtils.JsonToSerializedData("{}"), "", "x", context, null, null));

        CredentialAssert.CarriesNone(Error(null));
        Assert.ThrowsExactly<AssertFailedException>(() =>
            CredentialAssert.CarriesNone(Error(new Dictionary<string, object> { ["conflicts"] = System.Text.Json.JsonDocument.Parse("{\"x\":\"Bearer token-1\"}").RootElement })));
        var wrapped = new IOException("wrapped", Error(null));
        wrapped.Data["header"] = "Bearer token-1";
        Assert.ThrowsExactly<AssertFailedException>(() => CredentialAssert.CarriesNone(wrapped));
    }

    [TestMethod]
    public async Task AnotherKindOfClient_IsUsedAsItIs()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = new WriteableBoxFileProvider(new BoxFileProviderOptions(), new ForwardingClient(_credentials.Client(box)));

        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
    }

    private static (FakeBox Box, WriteableBoxFileProvider Provider) Create(string built)
    {
        var box = new FakeBox();
        var provider = built == "options"
            ? _credentials.Provider(box)
            : new WriteableBoxFileProvider(new BoxFileProviderOptions(), _credentials.Client(box)) { PollDelay = TimeSpan.Zero };
        return (box, provider);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage?> Fail(Func<HttpRequestMessage, bool> when, HttpStatusCode status, string code) =>
        request => when(request)
            ? new HttpResponseMessage(status)
            {
                Content = new StringContent($$"""{"type":"error","status":{{(int)status}},"code":"{{code}}"}""", Encoding.UTF8, "application/json"),
            }
            : null;

    /// <summary>An <see cref="IBoxClient"/> that is not a <see cref="BoxClient"/>: the provider cannot copy it.</summary>
    private sealed class ForwardingClient(BoxClient inner) : IBoxClient
    {
        public IAuthentication Auth => inner.Auth;

        public NetworkSession NetworkSession => inner.NetworkSession;

        public IFoldersManager Folders => inner.Folders;

        public IFilesManager Files => inner.Files;

        public IUploadsManager Uploads => inner.Uploads;

        public IChunkedUploadsManager ChunkedUploads => inner.ChunkedUploads;

        public IDownloadsManager Downloads => inner.Downloads;
    }
}
