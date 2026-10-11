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
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.GetFileInfoAsync("a.txt")));
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
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.UpdateFileAsync("a.txt", TestStreams.Text("x"))));
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
            CredentialAssert.CarriesNone(await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.DeleteAsync("a.txt")));
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
    public void Redact_KeepsTheAnswer_AndDropsTheRequestsCredentials()
    {
        var original = new BoxApiException("{\"code\":\"bad_request\"}", DateTimeOffset.UtcNow,
            new RequestInfo("POST", "https://api.box.test/oauth2/token",
                new Dictionary<string, string> { ["fields"] = "id", ["access_token"] = "token-9" },
                new Dictionary<string, string> { ["Authorization"] = "Bearer token-9", ["As-User"] = "7" }, "application/x-www-form-urlencoded")
            {
                Body = "client_secret=wrong-secret&assertion=x",
            },
            new ResponseInfo(400, new Dictionary<string, string>(), JsonUtils.JsonToSerializedData("{\"code\":\"bad_request\"}"), "{\"code\":\"bad_request\"}", "bad_request", null, "r1", null));

        var redacted = RedactingNetworkClient.Redact(original);

        Assert.AreEqual(original.Message, redacted.Message);
        Assert.AreSame(original.ResponseInfo, redacted.ResponseInfo);
        Assert.AreEqual("POST", redacted.RequestInfo.Method);
        Assert.AreEqual("https://api.box.test/oauth2/token", redacted.RequestInfo.Url);
        CollectionAssert.AreEquivalent(new[] { "fields" }, redacted.RequestInfo.QueryParams.Keys.ToList());
        CollectionAssert.AreEquivalent(new[] { "As-User" }, redacted.RequestInfo.Headers.Keys.ToList());
        CredentialAssert.CarriesNone(redacted);
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
