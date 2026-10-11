using System.Net;
using System.Text;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// The provider against the in-memory Graph, for what the conformance suite cannot reach: the requests it sends, the
/// session protocol, races the service settles, faults, and how each failure surfaces.
/// </summary>
[TestClass]
public sealed class SharePointProviderTests
{
    private const string Docs = "/v1.0/drives/" + GraphFake.DriveId;
    private readonly List<IDisposable> _owned = [];

    [TestCleanup]
    public void DisposeAll()
    {
        foreach (var owned in _owned)
            owned.Dispose();
    }

    private (WriteableSharePointFileProvider Provider, GraphFake Fake) Create(string? root = "r", string? workFolder = null, Action<GraphFake>? configure = null,
        string? library = "Documents")
    {
        var fake = new GraphFake { RequireGraphToken = true };
        configure?.Invoke(fake);
        var client = TestGraph.Client(fake);
        var provider = TestGraph.Provider(client, TestGraph.Options(root, workFolder, library));
        _owned.Add(provider);
        _owned.Add(client);
        return (provider, fake);
    }

    private static Task Write(IWriteableFileProvider provider, string path, string text, UpdateFileOptions? options = null) =>
        provider.UpdateFileAsync(path, TestStreams.Text(text), options);

    private static async Task<string> Read(IWriteableFileProvider provider, string path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
    }

    private static async Task<string> ReadRange(IWriteableFileProvider provider, string path, long offset, long? length)
    {
        await using var stream = await provider.ReadRangeAsync(path, offset, length);
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
    }

    private static string Text(byte[]? bytes) => bytes is null ? "<missing>" : Encoding.UTF8.GetString(bytes);

    private static RecordedRequest[] Sent(GraphFake fake, string method, string? host = GraphFake.GraphHost) =>
        [.. fake.Requests.Where(r => r.Method == method && (host is null || r.Host == host))];

    private static bool IsFragment(RecordedRequest r) => r.Host == GraphFake.UploadHost && r.Method == "PUT";

    // ----- The site and the library -----

    [TestMethod]
    public void Constructors_SendNoRequest()
    {
        var fake = new GraphFake();
        using var own = TestGraph.OwnProvider(fake, TestGraph.Options("r"));
        using var client = TestGraph.Client(fake);
        using var given = new WriteableSharePointFileProvider(TestGraph.Options("r"), client);
        Assert.IsEmpty(fake.Requests);
    }

    [TestMethod]
    public async Task Library_IsLookedUpOnce_AndAFailedLookupIsRetried()
    {
        var (provider, fake) = Create(configure: f => f.FailNext(4, HttpStatusCode.ServiceUnavailable, r => r.Path.StartsWith("/v1.0/sites/", StringComparison.Ordinal)));
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("x"));
        Assert.IsInstanceOfType<AggregateException>(failure.InnerException);

        Assert.IsFalse((await provider.GetFileInfoAsync("x")).Exists);
        var lookups = fake.Requests.Count(r => r.Path.StartsWith("/v1.0/sites/", StringComparison.Ordinal));
        await provider.GetFileInfoAsync("y");
        await Write(provider, "z.txt", "z");
        Assert.AreEqual(lookups, fake.Requests.Count(r => r.Path.StartsWith("/v1.0/sites/", StringComparison.Ordinal)), "The library id is kept after a success");
    }

    [TestMethod]
    public async Task Site_IsAddressedByItsUnescapedPath()
    {
        var (provider, fake) = Create();
        await provider.GetFileInfoAsync("x");
        // Kiota escapes the key once; the 2026 provider passed AbsolutePath and so escaped the space twice.
        Assert.AreEqual("/v1.0/sites/contoso.sharepoint.com:/sites/Team Site", fake.Requests.First().Path);
        Assert.IsTrue(fake.Requests.Any(r => r.Path.EndsWith("/drives", StringComparison.Ordinal) && r.Query.ContainsKey("$skiptoken")), "The libraries' next page was followed");
    }

    [TestMethod]
    public async Task MissingSite_ThrowsInvalidOperationNamingTheOption()
    {
        var fake = new GraphFake();
        using var client = TestGraph.Client(fake);
        var options = TestGraph.Options("r");
        options.SiteUrl = "https://contoso.sharepoint.com/sites/Elsewhere";
        using var provider = new WriteableSharePointFileProvider(options, client);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("x"));
        StringAssert.Contains(error.Message, "SiteUrl");
        var missingLibrary = Create(library: "Nope").Provider;
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => missingLibrary.ReadFileAsync("x"))).Message, "DocumentLibraryName");
    }

    [TestMethod]
    public async Task Library_ListingFailure_IsReported()
    {
        var (provider, fake) = Create();
        fake.FailNext(1, HttpStatusCode.Forbidden, r => r.Path.EndsWith("/drives", StringComparison.Ordinal), GraphFake.ErrorJson("accessDenied", "No"));
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("x"))).Message, "document libraries");
        Assert.IsFalse((await provider.GetFileInfoAsync("x")).Exists);
    }

    [TestMethod]
    public async Task Library_WithoutAName_IsTheFirst_AndNamesIgnoreCase()
    {
        var (unnamed, _) = Create(library: null, configure: f => f.Libraries = [(GraphFake.DriveId, "Documents"), ("b!other", "Other")]);
        await Write(unnamed, "a.txt", "a");
        var (lowerCase, _) = Create(library: "documents");
        await Write(lowerCase, "a.txt", "a");
        var (none, _) = Create(library: null, configure: f => f.Libraries = []);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => none.GetFileInfoAsync("a.txt"));
    }

    // ----- Addressing -----

    [TestMethod]
    public async Task Requests_AddressTheIntendedItems()
    {
        var (atTop, topFake) = Create(root: null);
        await atTop.GetDirectoryContentsAsync("");
        Assert.AreEqual($"{Docs}/items/root/children", topFake.Requests.Last().Path, "The library's top level is items/root: ItemWithPath(\"\") throws");

        var (provider, fake) = Create(root: "r");
        await Write(provider, "dir/f.txt", "x");
        await provider.UpdateFileAsync("dir/big.bin", new MemoryStream(TestStreams.Bytes(400_000)));
        await provider.GetDirectoryContentsAsync("dir");
        await provider.CopyAsync("dir/f.txt", "dir/g.txt");
        await provider.MoveAsync("dir/g.txt", "h.txt");
        await provider.DeleteAsync("h.txt");
        var paths = fake.Requests.Where(r => r.Host == GraphFake.GraphHost).Select(r => $"{r.Method} {r.Path}").ToList();
        CollectionAssert.Contains(paths, $"POST {Docs}/items/root/children");
        CollectionAssert.Contains(paths, $"POST {Docs}/root:/r:/children");
        CollectionAssert.Contains(paths, $"PUT {Docs}/root:/r/dir/f.txt:/content");
        CollectionAssert.Contains(paths, $"POST {Docs}/root:/r/dir/big.bin:/createUploadSession");
        CollectionAssert.Contains(paths, $"GET {Docs}/root:/r/dir:/children");
        CollectionAssert.Contains(paths, $"POST {Docs}/root:/r/dir/f.txt:/copy");
        CollectionAssert.Contains(paths, $"PATCH {Docs}/root:/r/dir/g.txt:");
        CollectionAssert.Contains(paths, $"DELETE {Docs}/root:/r/h.txt:");
        // The builder trap: a sub-request built on the parent would address the folder instead of the file.
        Assert.IsFalse(paths.Any(p => p.EndsWith("/r/dir:/content", StringComparison.Ordinal) || p.EndsWith("/r/dir:/createUploadSession", StringComparison.Ordinal) || p.EndsWith("/r/dir:/copy", StringComparison.Ordinal)), string.Join('\n', paths));
        Assert.AreEqual("replace", Sent(fake, "PUT").Single().Query["@microsoft.graph.conflictBehavior"]);
    }

    [TestMethod]
    public async Task Names_WithReservedCharacters_RoundTrip()
    {
        var (provider, fake) = Create();
        const string name = "50% off a b#c&d+e,f;g=h@i$j!k'l(m)n~o[1]{x}é.txt";
        await Write(provider, $"dir/{name}", "x");
        Assert.AreEqual("x", Text(fake.GetFile($"r/dir/{name}")));
        Assert.AreEqual(name, (await provider.GetDirectoryContentsAsync("dir")).Single().Name);
        Assert.AreEqual("x", await Read(provider, $"dir/{name}"));
    }

    [TestMethod]
    [DataRow("a:b")]
    [DataRow("a%3Ab")]
    [DataRow("a%253Ab")]
    public async Task Segments_WithAColon_EvenEscaped_AreRefusedBeforeAnyRequest(string path) => await AssertRefusedBeforeAnyRequest(path);

    [TestMethod]
    [DataRow("dir/%2E%2E")]
    [DataRow("%252E%252E/f.txt")]
    public async Task Segments_ThatDecodeToADotDot_AreRefusedBeforeAnyRequest(string path) => await AssertRefusedBeforeAnyRequest(path);

    private async Task AssertRefusedBeforeAnyRequest(string path)
    {
        var (provider, fake) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetFileInfoAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.UpdateFileAsync(path, TestStreams.Text("x")));
        Assert.IsEmpty(fake.Requests);
    }

    // ----- Writes and preconditions -----

    [TestMethod]
    public async Task IfMatch_IsSentWithASimpleUpload_AndAChangeAfterTheCheckIsRefused()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "one");
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        fake.OnRequest = r =>
        {
            if (r.Method == "PUT" && r.Path.EndsWith("/f.txt:/content", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.SetFile("r/f.txt", Encoding.UTF8.GetBytes("theirs"));
            }
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "f.txt", "mine", new UpdateFileOptions { IfMatch = etag }));
        Assert.AreEqual("theirs", Text(fake.GetFile("r/f.txt")));
        Assert.AreEqual(etag, Sent(fake, "PUT").Last().Header("If-Match"));
    }

    [TestMethod]
    public async Task IfNotExists_IsSentWithASimpleUpload_AndAFileCreatedAfterTheCheckIsKept()
    {
        var (provider, fake) = Create();
        fake.OnRequest = r =>
        {
            if (r.Method == "PUT" && r.Path.EndsWith("/f.txt:/content", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.SetFile("r/f.txt", Encoding.UTF8.GetBytes("theirs"));
            }
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "f.txt", "mine", new UpdateFileOptions { IfNotExists = true }));
        Assert.AreEqual("theirs", Text(fake.GetFile("r/f.txt")));
        Assert.AreEqual("fail", Sent(fake, "PUT").Last().Query["@microsoft.graph.conflictBehavior"]);
    }

    [TestMethod]
    public async Task Append_RacedAfterItsCheck_IsRefusedByTheIfMatchItSends()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "start");
        fake.OnRequest = r =>
        {
            if (r.Method == "PUT" && r.Path.EndsWith("/f.txt:/content", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.SetFile("r/f.txt", Encoding.UTF8.GetBytes("theirs"));
            }
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "f.txt", "+mine", UpdateFileOptions.Append));
        Assert.AreEqual("theirs", Text(fake.GetFile("r/f.txt")), "A service that honours If-Match keeps the concurrent write");
    }

    [TestMethod]
    public async Task IfMatch_IsSentWhenASessionIsCreated()
    {
        var (provider, fake) = Create();
        await Write(provider, "big.bin", "small first");
        var etag = (await provider.GetFileInfoAsync("big.bin")).ETag;
        fake.OnRequest = r =>
        {
            if (r.Path.EndsWith("/createUploadSession", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.SetFile("r/big.bin", Encoding.UTF8.GetBytes("theirs"));
            }
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(400_000)), new UpdateFileOptions { IfMatch = etag }));
        Assert.AreEqual("theirs", Text(fake.GetFile("r/big.bin")));
        Assert.AreEqual(etag, fake.Requests.Single(r => r.Path.EndsWith("/createUploadSession", StringComparison.Ordinal)).Header("If-Match"));
    }

    [TestMethod]
    public async Task IfNotExists_InASession_IsCheckedAtTheLastFragment_AndTheSessionIsCancelled()
    {
        var (provider, fake) = Create();
        fake.OnRequest = r =>
        {
            if (IsFragment(r))
            {
                fake.OnRequest = null;
                fake.SetFile("r/big.bin", Encoding.UTF8.GetBytes("theirs"));
            }
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(400_000)), new UpdateFileOptions { IfNotExists = true }));
        Assert.AreEqual("theirs", Text(fake.GetFile("r/big.bin")));
        Assert.AreEqual(0, fake.OpenSessions, "The session is cancelled");
        Assert.IsTrue(fake.Requests.Any(r => r.Host == GraphFake.UploadHost && r.Method == "DELETE"));
    }

    [TestMethod]
    public async Task IfNotExists_RefusedWhenTheSessionIsCreated()
    {
        var (provider, fake) = Create(configure: f => f.Intercept = r => r.Path.EndsWith("/createUploadSession", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(GraphFake.ErrorJson("nameAlreadyExists", "Name already exists"), Encoding.UTF8, "application/json") }
            : null);
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(400_000)), new UpdateFileOptions { IfNotExists = true }));
    }

    [TestMethod]
    public async Task UploadSession_SendsFragmentsOf320KiB_FromTheStreamPosition()
    {
        var (provider, fake) = Create();
        var data = TestStreams.Bytes(1_000_000, seed: 7);
        var source = new MemoryStream(data) { Position = 1000 };
        await provider.UpdateFileAsync("big.bin", source);
        CollectionAssert.AreEqual(data[1000..], fake.GetFile("r/big.bin"));
        Assert.IsTrue(source.CanRead, "The caller's stream is left open");
        var ranges = fake.Requests.Where(IsFragment).Select(r => r.Header("Content-Range")).ToArray();
        CollectionAssert.AreEqual(new[] { "bytes 0-327679/999000", "bytes 327680-655359/999000", "bytes 655360-983039/999000", "bytes 983040-998999/999000" }, ranges);
        Assert.IsTrue(fake.Requests.Where(IsFragment).All(r => r.Header("Content-Type") == "application/octet-stream"));
        Assert.AreEqual(0, fake.OpenSessions);
        Assert.IsEmpty(Sent(fake, "PUT"), "A file over the limit never goes in one request");
    }

    [TestMethod]
    public async Task EmptyFile_GoesInOneRequest_AndReadsWithoutADownload()
    {
        var (provider, fake) = Create();
        await provider.UpdateFileAsync("empty.txt", new MemoryStream());
        Assert.AreEqual("0", Sent(fake, "PUT").Single().Header("Content-Length"));
        Assert.IsFalse(fake.Requests.Any(r => r.Path.EndsWith("/createUploadSession", StringComparison.Ordinal)));
        Assert.AreEqual("", await Read(provider, "empty.txt"));
        Assert.AreEqual(0, (await provider.GetFileInfoAsync("empty.txt")).Length);
        Assert.IsFalse(fake.Requests.Any(r => r.Host == GraphFake.DownloadHost));
    }

    [TestMethod]
    public async Task UploadSession_TransientFailure_IsSentOnceMoreFromWhereTheSessionStands()
    {
        var (provider, fake) = Create();
        var data = TestStreams.Bytes(700_000, seed: 2);
        var fragments = 0;
        fake.FailNext(1, HttpStatusCode.InternalServerError, r => IsFragment(r) && ++fragments == 2);
        await provider.UpdateFileAsync("a.bin", new MemoryStream(data));
        CollectionAssert.AreEqual(data, fake.GetFile("r/a.bin"));
        Assert.AreEqual(1, fake.Requests.Count(r => r.Host == GraphFake.UploadHost && r.Method == "GET"), "The session's status was asked once");

        fake.BreakNext(1, IsFragment);
        await provider.UpdateFileAsync("b.bin", new MemoryStream(data));
        CollectionAssert.AreEqual(data, fake.GetFile("r/b.bin"));
    }

    [TestMethod]
    public async Task UploadSession_SecondFailure_CancelsTheSession_AndKeepsTheOldFile()
    {
        var (provider, fake) = Create();
        await Write(provider, "a.bin", "old");
        fake.FailNext(2, HttpStatusCode.BadGateway, IsFragment);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))));
        StringAssert.Contains(error.Message, "502");
        Assert.AreEqual("old", Text(fake.GetFile("r/a.bin")));
        Assert.AreEqual(0, fake.OpenSessions);

        fake.BreakNext(2, IsFragment);
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))));
        Assert.AreEqual(0, fake.OpenSessions);
        Assert.AreEqual("old", Text(fake.GetFile("r/a.bin")));
    }

    [TestMethod]
    public async Task UploadSession_ExpiredSession_FailsWithoutALoop()
    {
        var (provider, fake) = Create();
        fake.FailNext(1, HttpStatusCode.NotFound, IsFragment, GraphFake.ErrorJson("itemNotFound", "The upload session expired."));
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))));
        StringAssert.Contains(error.Message, "itemNotFound");
        Assert.AreEqual(1, fake.Requests.Count(IsFragment));
    }

    [TestMethod]
    public async Task UploadSession_StatusCheckFailure_EndsTheUpload()
    {
        var (provider, fake) = Create();
        fake.FailNext(1, HttpStatusCode.InternalServerError, IsFragment);
        fake.FailNext(1, HttpStatusCode.Forbidden, r => r.Host == GraphFake.UploadHost && r.Method == "GET", GraphFake.ErrorJson("accessDenied", "No."));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))));
        Assert.AreEqual(0, fake.OpenSessions);
    }

    [TestMethod]
    public async Task UploadSession_StatusPastTheEnd_EndsTheUpload()
    {
        var (provider, fake) = Create();
        fake.FailNext(1, HttpStatusCode.InternalServerError, IsFragment);
        fake.Intercept = r => r.Host == GraphFake.UploadHost && r.Method == "GET"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"nextExpectedRanges\":[\"700000-\"]}", Encoding.UTF8, "application/json") }
            : null;
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))))).Message, "expects byte 700000");
        Assert.AreEqual(0, fake.OpenSessions);
    }

    [TestMethod]
    public async Task UploadSession_UnexpectedAnswers_Fail()
    {
        var (noSession, _) = Create(configure: f => f.Intercept = r => r.Path.EndsWith("/createUploadSession", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
            : null);
        await Assert.ThrowsExactlyAsync<IOException>(() => noSession.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(400_000))));

        // The service answers the last fragment as if more were expected: nothing was committed.
        var (uncommitted, fake) = Create();
        fake.Intercept = r => IsFragment(r) && r.Header("Content-Range")!.EndsWith("-399999/400000", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"nextExpectedRanges\":[\"400000-\"]}", Encoding.UTF8, "application/json") }
            : null;
        await Assert.ThrowsExactlyAsync<IOException>(() => uncommitted.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(400_000))));
        Assert.AreEqual(0, fake.OpenSessions);

        // A session-create failure other than a precondition.
        var (refused, refusedFake) = Create();
        refusedFake.FailNext(1, HttpStatusCode.InsufficientStorage, r => r.Path.EndsWith("/createUploadSession", StringComparison.Ordinal), GraphFake.ErrorJson("quotaLimitReached", "Full."));
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => refused.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(400_000))))).Message, "quotaLimitReached");
    }

    [TestMethod]
    public async Task UploadSession_AnswersWithoutRanges_ContinueAfterTheFragment_AndAFailedCancelIsIgnored()
    {
        var (provider, fake) = Create();
        var data = TestStreams.Bytes(700_000, seed: 5);
        var answers = new Queue<string>(["not json", "{\"nextExpectedRanges\":[7]}"]);
        fake.OnRequest = r =>
        {
            if (IsFragment(r) && answers.Count > 0)
                fake.Intercept = q => q == r ? NewAccepted(answers.Dequeue()) : null;
        };
        // The provider moves on after each answer. The fake stored neither intercepted fragment, so the third answers
        // 416; the provider asks the session where it stands, goes back to byte 0 once, and completes.
        await provider.UpdateFileAsync("a.bin", new MemoryStream(data));
        CollectionAssert.AreEqual(data, fake.GetFile("r/a.bin"));
        CollectionAssert.AreEqual(new[]
        {
            "bytes 0-327679/700000", "bytes 327680-655359/700000", "bytes 655360-699999/700000",
            "bytes 0-327679/700000", "bytes 327680-655359/700000", "bytes 655360-699999/700000",
        }, fake.Requests.Where(IsFragment).Select(r => r.Header("Content-Range")).ToArray());

        fake.OnRequest = null;
        fake.Intercept = null;
        fake.FailNext(1, HttpStatusCode.BadRequest, IsFragment, GraphFake.ErrorJson("invalidRequest", "No"));
        fake.BreakNext(1, r => r.Host == GraphFake.UploadHost && r.Method == "DELETE");
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("b.bin", new MemoryStream(data)))).Message, "invalidRequest");
        Assert.AreEqual(1, fake.OpenSessions, "The session could not be cancelled; it expires on its own");

        static HttpResponseMessage NewAccepted(string json) => new(HttpStatusCode.Accepted) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [TestMethod]
    public async Task Write_OntoAFolder_IsRefused()
    {
        var (provider, fake) = Create();
        fake.CreateFolder("r/dir");
        await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "dir", "x"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("dir", new MemoryStream(TestStreams.Bytes(400_000))));
        Assert.IsTrue(fake.IsFolder("r/dir"));
    }

    // ----- Folders -----

    [TestMethod]
    public async Task WorkFolderAndRoot_AreCreatedOnTheFirstWrite_AndReadsUnderThemFindNothing()
    {
        var (provider, fake) = Create(root: "tenant", workFolder: "Main/Work");
        Assert.IsFalse((await provider.GetFileInfoAsync("a.txt")).Exists);
        Assert.IsFalse((await provider.GetDirectoryContentsAsync("")).Any());
        await Write(provider, "sub/a.txt", "a");
        Assert.AreEqual("a", Text(fake.GetFile("Main/Work/tenant/sub/a.txt")));
    }

    [TestMethod]
    public async Task MissingParent_ThatCannotBeCreated_FailsWithoutALoop()
    {
        var (provider, fake) = Create();
        fake.FailNext(100, HttpStatusCode.NotFound, r => r.Method == "POST" && r.Path.EndsWith("/children", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "a/b/c.txt", "x"));
        Assert.AreEqual(1, fake.Requests.Count(r => r.Method == "POST"), "No request is retried");
    }

    [TestMethod]
    public async Task ParentThatIsAFile_IsRefused()
    {
        var (provider, fake) = Create();
        fake.SetFile("r/a", [1]);
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "a/b.txt", "x"))).Message, "is a file");
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "a/deeper/b.txt", "x"))).Message, "is a file");
    }

    [TestMethod]
    public async Task FolderCreatedByAnotherWriter_IsUsed_AndAFileInItsPlaceIsRefused()
    {
        var (provider, fake) = Create();
        fake.OnRequest = r =>
        {
            if (r.Method == "POST" && r.Path.EndsWith("/r:/children", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.CreateFolder("r/dir");
            }
        };
        await Write(provider, "dir/a.txt", "a");
        Assert.AreEqual("a", Text(fake.GetFile("r/dir/a.txt")));

        fake.OnRequest = r =>
        {
            if (r.Method == "POST" && r.Path.EndsWith("/r:/children", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.SetFile("r/other", [1]);
            }
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "other/a.txt", "a"));
    }

    [TestMethod]
    public async Task FolderCreation_OtherFailures_AreReported()
    {
        var (provider, fake) = Create();
        fake.FailNext(1, HttpStatusCode.Conflict, r => r.Method == "POST" && r.Path.EndsWith("/children", StringComparison.Ordinal), GraphFake.ErrorJson("nameAlreadyExists", "Exists"));
        // A 409 for a folder that then does not exist cannot be settled.
        await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "dir/a.txt", "a"));
        fake.FailNext(1, HttpStatusCode.Forbidden, r => r.Method == "POST" && r.Path.EndsWith("/children", StringComparison.Ordinal), GraphFake.ErrorJson("accessDenied", "No"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write(provider, "dir/a.txt", "a"));
        fake.Intercept = r => r.Method == "POST" && r.Path.EndsWith("/children", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"name\":\"dir\"}", Encoding.UTF8, "application/json") }
            : null;
        await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "dir/a.txt", "a"));
    }

    // ----- Reads -----

    [TestMethod]
    public async Task Reads_GoToTheDownloadUrlAsIssued_WithoutAToken()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "0123456789");
        Assert.AreEqual("0123456789", await Read(provider, "f.txt"));
        Assert.AreEqual("345", await ReadRange(provider, "f.txt", 3, 3));
        Assert.AreEqual("789", await ReadRange(provider, "f.txt", 7, null));
        var downloads = Sent(fake, "GET", GraphFake.DownloadHost);
        Assert.HasCount(3, downloads);
        Assert.IsNull(downloads[0].Header("Range"));
        Assert.AreEqual("bytes=3-5", downloads[1].Header("Range"));
        Assert.AreEqual("bytes=7-", downloads[2].Header("Range"));
        Assert.IsTrue(downloads.All(d => d.Header("Authorization") is null));
    }

    [TestMethod]
    public async Task Range_IgnoredByTheService_IsCutFromTheWholeFile()
    {
        var (provider, _) = Create(configure: f => f.IgnoreRanges = true);
        await Write(provider, "f.txt", "0123456789");
        Assert.AreEqual("345", await ReadRange(provider, "f.txt", 3, 3));
        Assert.AreEqual("9", await ReadRange(provider, "f.txt", 9, null));
    }

    [TestMethod]
    public async Task Range_WhenTheFileShrankBeforeTheDownload_ReadsEmpty()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "0123456789");
        fake.OnRequest = r =>
        {
            if (r.Host == GraphFake.DownloadHost)
            {
                fake.OnRequest = null;
                fake.SetFile("r/f.txt", Encoding.UTF8.GetBytes("01"));
            }
        };
        Assert.AreEqual("", await ReadRange(provider, "f.txt", 5, 2));
    }

    [TestMethod]
    public async Task Range_AnotherRangeThanAsked_IsRefused()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "0123456789");
        fake.Intercept = r =>
        {
            if (r.Host != GraphFake.DownloadHost)
                return null;
            var wrong = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([0x30, 0x31]) };
            wrong.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 1, 10);
            return wrong;
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadRangeAsync("f.txt", 3, 2));
    }

    [TestMethod]
    public async Task Read_FileDeletedBeforeTheDownload_IsNotFound()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "x");
        fake.OnRequest = r =>
        {
            if (r.Host == GraphFake.DownloadHost)
            {
                fake.OnRequest = null;
                fake.Delete("r/f.txt");
            }
        };
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("f.txt"));
    }

    [TestMethod]
    public async Task Read_DownloadFailures_AreReported()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "x");
        fake.ExpireDownloadUrls = true;
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.ReadFileAsync("f.txt"));
        fake.ExpireDownloadUrls = false;
        fake.FailNext(1, HttpStatusCode.InternalServerError, r => r.Host == GraphFake.DownloadHost);
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("f.txt"))).Message, "500");
        fake.OmitDownloadUrls = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("f.txt"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(1), AllowShorterValidity = true }));
    }

    [TestMethod]
    public async Task Range_PinnedToAnOldVersion_IsRefusedBeforeTheDownload()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "0123456789");
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        await Write(provider, "f.txt", "changed!!!");
        fake.ClearRequests();
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 0, 2, new ReadOptions { IfMatch = etag }));
        Assert.IsFalse(fake.Requests.Any(r => r.Host == GraphFake.DownloadHost));
    }

    // ----- Errors (F66) -----

    [TestMethod]
    public async Task Errors_OnlyNotFoundMeansMissing()
    {
        var (provider, fake) = Create();
        await provider.GetFileInfoAsync("warm-up");
        static bool ItemGet(RecordedRequest r) => r.Method == "GET" && r.Path.Contains("/root:/", StringComparison.Ordinal);

        fake.FailNext(1, HttpStatusCode.Forbidden, ItemGet, GraphFake.ErrorJson("accessDenied", "Access denied"));
        var denied = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("f.txt"));
        StringAssert.Contains(denied.Message, "accessDenied");

        fake.FailNext(1, HttpStatusCode.InternalServerError, ItemGet, GraphFake.ErrorJson("generalException", "Boom"));
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"))).Message, "500 generalException");

        fake.FailNext(1, HttpStatusCode.BadGateway, ItemGet, "<html><body>Bad gateway</body></html>", "text/html");
        Assert.IsNotNull((await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"))).InnerException);

        fake.FailNext(1, (HttpStatusCode)423, ItemGet, GraphFake.ErrorJson("notAllowed", "Locked"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("f.txt"));

        fake.BreakNext(1, ItemGet);
        Assert.IsInstanceOfType<HttpRequestException>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"))).InnerException);

        fake.Intercept = r => ItemGet(r) ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        Assert.IsFalse((await provider.GetFileInfoAsync("f.txt")).Exists, "A 404 without a body is still missing");
        fake.Intercept = r => ItemGet(r) ? new HttpResponseMessage(HttpStatusCode.NoContent) : null;
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"));
    }

    [TestMethod]
    public async Task Throttling_IsAbsorbedByTheSdk_ThenReported()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "x");
        static bool ItemGet(RecordedRequest r) => r.Method == "GET" && r.Path.EndsWith("/f.txt:", StringComparison.Ordinal);
        fake.FailNext(3, HttpStatusCode.TooManyRequests, ItemGet);
        Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
        fake.FailNext(4, HttpStatusCode.TooManyRequests, ItemGet);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"));
        Assert.IsInstanceOfType<AggregateException>(error.InnerException);
        StringAssert.Contains(error.Message, "429");
    }

    [TestMethod]
    public async Task ListingAndDelete_Failures_AreReported()
    {
        var (provider, fake) = Create(configure: f => f.ChildrenPageSize = 1);
        await Write(provider, "dir/a.txt", "a");
        await Write(provider, "dir/b.txt", "b");
        fake.FailNext(1, HttpStatusCode.InternalServerError, r => r.Query.ContainsKey("$skiptoken"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetDirectoryContentsAsync("dir"));
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, (await provider.GetDirectoryContentsAsync("dir")).Select(e => e.Name).ToList());
        fake.FailNext(1, HttpStatusCode.Forbidden, r => r.Method == "GET" && r.Path.EndsWith("/children", StringComparison.Ordinal), GraphFake.ErrorJson("accessDenied", "No"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetDirectoryContentsAsync("dir"));
        fake.FailNext(1, (HttpStatusCode)423, r => r.Method == "DELETE", GraphFake.ErrorJson("resourceLocked", "Locked"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("dir/a.txt"));
        Assert.IsTrue((await provider.GetFileInfoAsync("dir/a.txt")).Exists);
    }

    // ----- Move and copy -----

    [TestMethod]
    public async Task Move_IsOnePatch_WithTheParentIdAndConflictBehaviour()
    {
        var (provider, fake) = Create();
        await Write(provider, "a.txt", "a");
        fake.ClearRequests();
        await provider.MoveAsync("a.txt", "new/b.txt");
        var patch = Sent(fake, "PATCH").Single();
        Assert.AreEqual("fail", patch.Query["@microsoft.graph.conflictBehavior"]);
        Assert.IsFalse(fake.Requests.Any(r => r.Method == "DELETE" || r.Path.EndsWith("/copy", StringComparison.Ordinal)), "A move is not a copy and a delete");
        Assert.AreEqual("a", Text(fake.GetFile("r/new/b.txt")));

        await provider.MoveAsync("new/b.txt", "B.txt");
        await provider.MoveAsync("B.txt", "b.TXT");
        Assert.IsFalse(Sent(fake, "PATCH").Last().Query.ContainsKey("@microsoft.graph.conflictBehavior"), "A case-only rename changes only the name");
        CollectionAssert.AreEqual(new[] { "new", "b.TXT" }, fake.Names("r").ToArray());
    }

    [TestMethod]
    public async Task Move_RacedAfterItsChecks_IsSettledByTheService()
    {
        var (provider, fake) = Create();
        await Write(provider, "a.txt", "a");
        fake.OnRequest = r =>
        {
            if (r.Method == "PATCH")
            {
                fake.OnRequest = null;
                fake.SetFile("r/b.txt", Encoding.UTF8.GetBytes("theirs"));
            }
        };
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.MoveAsync("a.txt", "b.txt"));
        Assert.AreEqual("theirs", Text(fake.GetFile("r/b.txt")));

        fake.OnRequest = r =>
        {
            if (r.Method == "PATCH")
            {
                fake.OnRequest = null;
                fake.Delete("r/a.txt");
            }
        };
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.MoveAsync("a.txt", "c.txt"));
        await Write(provider, "a.txt", "a");
        fake.FailNext(1, HttpStatusCode.InternalServerError, r => r.Method == "PATCH");
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.MoveAsync("a.txt", "c.txt"));
    }

    [TestMethod]
    public async Task Copy_TakenDestination_IsReportedByTheMonitorOrAtOnce()
    {
        foreach (var synchronous in new[] { false, true })
        {
            var (provider, fake) = Create(configure: f => f.CopyConflictIsSynchronous = synchronous);
            await Write(provider, "a.txt", "a");
            fake.OnRequest = r =>
            {
                if (r.Path.EndsWith("/copy", StringComparison.Ordinal))
                {
                    fake.OnRequest = null;
                    fake.SetFile("r/b.txt", Encoding.UTF8.GetBytes("theirs"));
                }
            };
            await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("a.txt", "b.txt"));
            Assert.AreEqual("theirs", Text(fake.GetFile("r/b.txt")));
            Assert.AreEqual(synchronous ? 0 : 2, fake.Requests.Count(r => r.Host == GraphFake.MonitorHost));
        }
    }

    [TestMethod]
    public async Task Copy_MonitorRedirectToTheNewItem_MeansDone()
    {
        var (provider, fake) = Create(configure: f => f.CopyDoneByRedirect = true);
        await Write(provider, "a.txt", "a");
        await provider.CopyAsync("a.txt", "b.txt");
        Assert.AreEqual("a", Text(fake.GetFile("r/b.txt")));
        Assert.IsFalse(fake.Requests.Any(r => r.Path.Contains("/items/01ITEM", StringComparison.Ordinal)), "The redirect is not followed");
    }

    [TestMethod]
    public async Task Copy_MonitorAnswers_AreReadByTheirStatus()
    {
        static HttpResponseMessage Answer(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var cases = new (Func<HttpResponseMessage> Monitor, Type? Expected)[]
        {
            (() => Answer(HttpStatusCode.OK, "{\"status\":\"failed\",\"error\":{\"code\":\"generalException\",\"message\":\"No\"}}"), typeof(IOException)),
            (() => Answer(HttpStatusCode.Accepted, "{\"status\":\"cancelled\"}"), typeof(IOException)),
            (() => Answer(HttpStatusCode.NotFound, GraphFake.ErrorJson("itemNotFound", "Unknown")), typeof(IOException)),
            (() => Answer(HttpStatusCode.OK, "{}"), typeof(IOException)),
            (() => Answer(HttpStatusCode.OK, "not json"), typeof(IOException)),
            (() => Answer(HttpStatusCode.OK, "[]"), typeof(IOException)),
            (() => Answer(HttpStatusCode.OK, "{\"id\":\"01NEW\",\"name\":\"b.txt\",\"file\":{}}"), null),
            (() => Answer(HttpStatusCode.OK, "{\"status\":\"completed\",\"resourceId\":\"01NEW\"}"), null),
        };
        foreach (var (monitor, expected) in cases)
        {
            var (provider, fake) = Create();
            await Write(provider, "a.txt", "a");
            fake.Intercept = r => r.Host == GraphFake.MonitorHost ? monitor() : null;
            if (expected is null)
                await provider.CopyAsync("a.txt", "b.txt");
            else
                Assert.IsInstanceOfType(await Assert.ThrowsAsync<Exception>(() => provider.CopyAsync("a.txt", "b.txt")), expected);
        }
    }

    [TestMethod]
    public async Task Copy_RequestAnswers_AreReadByTheirStatus()
    {
        static HttpResponseMessage Answer(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var cases = new (Func<HttpResponseMessage> Copy, Type? Expected)[]
        {
            (() => Answer(HttpStatusCode.NotFound, GraphFake.ErrorJson("itemNotFound", "Gone")), typeof(FileNotFoundException)),
            (() => Answer(HttpStatusCode.InternalServerError, GraphFake.ErrorJson("generalException", "Boom")), typeof(IOException)),
            (() => Answer(HttpStatusCode.Accepted, ""), typeof(IOException)),
            (() => Answer(HttpStatusCode.Created, "{\"id\":\"01NEW\",\"name\":\"b.txt\"}"), null),
        };
        foreach (var (copy, expected) in cases)
        {
            var (provider, fake) = Create();
            await Write(provider, "a.txt", "a");
            fake.Intercept = r => r.Path.EndsWith("/copy", StringComparison.Ordinal) ? copy() : null;
            if (expected is null)
                await provider.CopyAsync("a.txt", "b.txt");
            else
                Assert.IsInstanceOfType(await Assert.ThrowsAsync<Exception>(() => provider.CopyAsync("a.txt", "b.txt")), expected);
        }
    }

    [TestMethod]
    public async Task Copy_IsCancellable_WhileItsMonitorIsPolled()
    {
        var (provider, fake) = Create(configure: f => f.CopyPollsBeforeDone = int.MaxValue);
        await Write(provider, "a.txt", "a");
        using var cancel = new CancellationTokenSource();
        fake.OnRequest = r =>
        {
            if (r.Host == GraphFake.MonitorHost && fake.Requests.Count(q => q.Host == GraphFake.MonitorHost) == 3)
                cancel.Cancel();
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.CopyAsync("a.txt", "b.txt", cancel.Token));
    }

    // ----- Review round 1 -----

    [TestMethod]
    public async Task PercentInNames_IsSentEscaped_AndUnaddressableNamesAreLeftOut()
    {
        var (provider, fake) = Create();
        fake.SetFile("r/a%41/f.txt", Encoding.UTF8.GetBytes("percent"));
        fake.SetFile("r/aA/g.txt", Encoding.UTF8.GetBytes("plain"));
        fake.SetFile("r/50%.txt", Encoding.UTF8.GetBytes("fifty"));
        fake.SetFile("r/%2E%2E/x.txt", Encoding.UTF8.GetBytes("hidden"));
        fake.SetFile("r/%252E%252E/y.txt", Encoding.UTF8.GetBytes("hidden"));

        CollectionAssert.AreEquivalent(new[] { "a%41", "aA", "50%.txt" }, (await provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToList());
        var all = new List<string>();
        await foreach (var entry in provider.EnumerateAsync("", recursive: true))
            all.Add(entry.Path.ToString());
        CollectionAssert.AreEquivalent(new[] { "a%41", "a%41/f.txt", "aA", "aA/g.txt", "50%.txt" }, all, "Nothing under an unaddressable name is walked into");

        Assert.AreEqual("percent", await Read(provider, "a%41/f.txt"));
        Assert.AreEqual("plain", await Read(provider, "aA/g.txt"));
        Assert.AreEqual("fifty", await Read(provider, "50%.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("a%41/g.txt")).Exists, "a%41 is not aA");
        await Write(provider, "a%41/new.txt", "n");
        await Write(provider, "new%20dir/z.txt", "z");
        Assert.AreEqual("n", Text(fake.GetFile("r/a%41/new.txt")));
        Assert.IsNull(fake.GetFile("r/aA/new.txt"));
        Assert.AreEqual("z", Text(fake.GetFile("r/new%20dir/z.txt")));
        Assert.IsTrue(fake.Requests.Any(r => r.Path == $"{Docs}/root:/r/a%41/f.txt:"), "The path reached Graph with '%' escaped once");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetFileInfoAsync("%2E%2E"));
    }

    [TestMethod]
    public async Task UploadSession_416_IsResyncedOnce()
    {
        var (provider, fake) = Create();
        var data = TestStreams.Bytes(700_000, seed: 9);
        var fragments = 0;
        fake.Intercept = r => IsFragment(r) && ++fragments == 2 ? Answer(HttpStatusCode.RequestedRangeNotSatisfiable, GraphFake.ErrorJson("invalidRange", "No")) : null;
        await provider.UpdateFileAsync("a.bin", new MemoryStream(data));
        CollectionAssert.AreEqual(data, fake.GetFile("r/a.bin"));
        Assert.AreEqual(1, fake.Requests.Count(r => r.Host == GraphFake.UploadHost && r.Method == "GET"));

        fake.Intercept = r => IsFragment(r) && r.Header("Content-Range")!.StartsWith("bytes 327680-", StringComparison.Ordinal)
            ? Answer(HttpStatusCode.RequestedRangeNotSatisfiable, GraphFake.ErrorJson("invalidRange", "No"))
            : null;
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("b.bin", new MemoryStream(data)))).Message, "416");
        Assert.AreEqual(0, fake.OpenSessions);
    }

    [TestMethod]
    public async Task UploadSession_AnswerThatDoesNotMoveOn_CountsAgainstTheResend()
    {
        var (provider, fake) = Create();
        var data = TestStreams.Bytes(700_000, seed: 10);
        var first = true;
        fake.Intercept = r =>
        {
            if (!IsFragment(r) || !first)
                return null;
            first = false;
            return Answer(HttpStatusCode.Accepted, "{\"nextExpectedRanges\":[\"0-\"]}");
        };
        await provider.UpdateFileAsync("a.bin", new MemoryStream(data));
        CollectionAssert.AreEqual(data, fake.GetFile("r/a.bin"));

        fake.ClearRequests();
        fake.Intercept = r => IsFragment(r) ? Answer(HttpStatusCode.Accepted, "{\"nextExpectedRanges\":[\"0-\"]}") : null;
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("b.bin", new MemoryStream(data)))).Message, "did not move past byte 0");
        Assert.AreEqual(2, fake.Requests.Count(IsFragment), "The first fragment is sent once more, then the upload ends");
    }

    [TestMethod]
    public async Task UploadSession_OnlyServerErrorsAndTheNetworkAreTransient()
    {
        var (provider, fake) = Create();
        fake.FailNext(1, HttpStatusCode.NotImplemented, IsFragment, GraphFake.ErrorJson("notSupported", "No"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))));
        Assert.AreEqual(1, fake.Requests.Count(IsFragment), "A 501 is not resent");
        Assert.IsFalse(fake.Requests.Any(r => r.Host == GraphFake.UploadHost && r.Method == "GET"));

        // 503 and 504 reach the provider only from a client without the SDK's retry handler: resent once.
        var bareFake = new GraphFake();
        using var bare = TestGraph.BareClient(bareFake);
        var data = TestStreams.Bytes(700_000, seed: 11);
        using (var overBare = TestGraph.Provider(bare, TestGraph.Options("r")))
        {
            bareFake.FailNext(1, HttpStatusCode.ServiceUnavailable, IsFragment);
            await overBare.UpdateFileAsync("a.bin", new MemoryStream(data));
            bareFake.FailNext(1, HttpStatusCode.GatewayTimeout, IsFragment);
            await overBare.UpdateFileAsync("b.bin", new MemoryStream(data));
            // Without a redirect handler too, reads work: they go to the download URL, not through /content's redirect.
            await using var read = await overBare.ReadFileAsync("b.bin");
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            CollectionAssert.AreEqual(data, copy.ToArray());
        }
        CollectionAssert.AreEqual(data, bareFake.GetFile("r/a.bin"));
    }

    [TestMethod]
    public async Task MalformedJson_IsAnIOException()
    {
        var (provider, fake) = Create();
        await provider.GetFileInfoAsync("warm-up");
        fake.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/f.txt:", StringComparison.Ordinal) ? Answer(HttpStatusCode.OK, "{\"id\": ") : null;
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"));
    }

    [TestMethod]
    public async Task Copy_ThatNeverEnds_TimesOutNamingItsLastStatus()
    {
        var (provider, _) = Create(configure: f => f.CopyPollsBeforeDone = int.MaxValue);
        provider.CopyTimeout = TimeSpan.FromMilliseconds(30);
        await Write(provider, "a.txt", "a");
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.CopyAsync("a.txt", "b.txt"))).Message, "'inProgress'");
    }

    [TestMethod]
    public async Task UnknownSize_IsDownloadedRatherThanReadAsEmpty()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "0123456789");
        fake.OmitSizes = true;
        Assert.AreEqual("0123456789", await Read(provider, "f.txt"));
        Assert.AreEqual("345", await ReadRange(provider, "f.txt", 3, 3));
        Assert.AreEqual("", await ReadRange(provider, "f.txt", 50, null), "Past the end: the service answers 416");
        Assert.AreEqual("", await ReadRange(provider, "f.txt", 2, 0));
    }

    [TestMethod]
    public async Task Dispose_ReleasesTheOwnHttpPipeline_AndNotTheCallers()
    {
        var fake = new GraphFake();
        var own = TestGraph.OwnProvider(fake, TestGraph.Options("r"));
        await own.GetFileInfoAsync("x");
        Assert.AreEqual(0, fake.DisposedHandlers);
        own.Dispose();
        Assert.AreEqual(1, fake.DisposedHandlers, "The provider's own HttpClient, with its handler, is disposed");

        var callerFake = new GraphFake();
        var client = TestGraph.Client(callerFake);
        await using (var given = new WriteableSharePointFileProvider(TestGraph.Options("r"), client))
            await given.GetFileInfoAsync("x");
        Assert.AreEqual(0, callerFake.DisposedHandlers, "The caller's client is left alone");
        client.Dispose();
    }

    // ----- Review round 2 -----

    [TestMethod]
    public async Task UploadSession_ThatKeepsSendingTheUploadBack_EndsInsteadOfLooping()
    {
        // The service accepts the first fragment, fails the second (or answers it without progress), and its status
        // sends the upload back to byte 0: one resend of the second fragment, then the upload ends.
        foreach (var secondFails in new[] { true, false })
        {
            var (provider, fake) = Create();
            fake.Intercept = r =>
            {
                if (r.Host == GraphFake.UploadHost && r.Method == "GET")
                    return Answer(HttpStatusCode.OK, "{\"nextExpectedRanges\":[\"0-\"]}");
                if (!IsFragment(r))
                    return null;
                if (r.Header("Content-Range")!.StartsWith("bytes 0-", StringComparison.Ordinal))
                    return Answer(HttpStatusCode.Accepted, "{\"nextExpectedRanges\":[\"327680-\"]}");
                return secondFails
                    ? Answer(HttpStatusCode.InternalServerError, GraphFake.ErrorJson("generalException", "No"))
                    : Answer(HttpStatusCode.Accepted, "{\"nextExpectedRanges\":[\"0-\"]}");
            };
            await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.bin", new MemoryStream(TestStreams.Bytes(700_000))));
            Assert.AreEqual(4, fake.Requests.Count(IsFragment), "0, 327680, then 0 and 327680 once more: no loop");
        }
    }

    [TestMethod]
    public async Task UploadSession_ForwardResync_LeavesTheNextFragmentItsOwnResend()
    {
        var (provider, fake) = Create();
        var data = TestStreams.Bytes(1_000_000, seed: 12);
        // The service stores the first fragment but answers 500, so its status moves the upload forward; the next
        // fragment then fails once and is still resent.
        var firstAnswered = false;
        fake.Rewrite = (r, answer) =>
        {
            if (!IsFragment(r) || firstAnswered)
                return null;
            firstAnswered = true;
            answer.Dispose();
            return Answer(HttpStatusCode.InternalServerError, GraphFake.ErrorJson("generalException", "Stored, then failed"));
        };
        fake.FailNext(1, HttpStatusCode.BadGateway, r => IsFragment(r) && r.Header("Content-Range")!.StartsWith("bytes 327680-", StringComparison.Ordinal));
        await provider.UpdateFileAsync("a.bin", new MemoryStream(data));
        CollectionAssert.AreEqual(data, fake.GetFile("r/a.bin"));
        Assert.AreEqual(2, fake.Requests.Count(r => r.Host == GraphFake.UploadHost && r.Method == "GET"), "Each failed fragment asked once");
    }

    [TestMethod]
    public async Task UnknownSize_IsMeasured_InInfoAndListings()
    {
        // The measuring request is answered 206 (with Content-Range), 416 for an empty file, or 200 where ranges are
        // ignored.
        foreach (var ignoreRanges in new[] { false, true })
        {
            var (provider, fake) = Create(configure: f => f.IgnoreRanges = ignoreRanges);
            var bytes = TestStreams.Bytes(1000, seed: 13);
            await provider.UpdateFileAsync("f.bin", new MemoryStream(bytes));
            await provider.UpdateFileAsync("empty.bin", new MemoryStream());
            fake.OmitSizes = true;
            fake.ClearRequests();
            Assert.AreEqual(1000, (await provider.GetFileInfoAsync("f.bin")).Length);
            Assert.AreEqual(0, (await provider.GetFileInfoAsync("empty.bin")).Length);
            var listed = (await provider.GetDirectoryContentsAsync("")).ToDictionary(e => e.Name, e => e.Length);
            Assert.AreEqual(1000, listed["f.bin"]);
            Assert.AreEqual(0, listed["empty.bin"]);
            Assert.IsTrue(fake.Requests.Where(r => r.Host == GraphFake.DownloadHost).All(r => r.Header("Range") == "bytes=0-0"));
            if (!ignoreRanges)
                Assert.IsTrue(fake.Requests.Any(r => r.Host == GraphFake.DownloadHost), "The sizes were measured");

            // The base's seekable read takes the measured length.
            await using (var stream = await provider.OpenReadSeekableAsync("f.bin", new SeekableReadOptions { BufferSize = 64 }))
            {
                Assert.AreEqual(1000, stream.Length);
                stream.Position = 900;
                var buffer = new byte[50];
                await stream.ReadExactlyAsync(buffer);
                CollectionAssert.AreEqual(bytes[900..950], buffer);
            }

            // A file Graph gives the size of costs nothing more.
            fake.OmitSizes = false;
            fake.ClearRequests();
            await provider.GetFileInfoAsync("f.bin");
            await provider.GetDirectoryContentsAsync("");
            Assert.IsFalse(fake.Requests.Any(r => r.Host == GraphFake.DownloadHost || (r.Query.TryGetValue("$select", out var select) && select.Contains("downloadUrl", StringComparison.Ordinal))));
        }
    }

    [TestMethod]
    public async Task UnknownSize_Measuring_FindsGoneFiles_AndNeedsALength()
    {
        var (provider, fake) = Create();
        await Write(provider, "f.txt", "0123456789");
        await Write(provider, "g.txt", "keep");
        fake.OmitSizes = true;

        // A 404 while measuring means not found: the info is missing, and a listing leaves the entry out.
        fake.Intercept = r => r.Host == GraphFake.DownloadHost && r.Path.EndsWith(FileId(fake, "r/f.txt"), StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        Assert.IsFalse((await provider.GetFileInfoAsync("f.txt")).Exists);
        CollectionAssert.AreEqual(new[] { "g.txt" }, (await provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToArray());
        fake.Intercept = null;

        // So does a file deleted between its listing and its measuring.
        fake.OnRequest = r =>
        {
            if (r.Method == "GET" && r.Query.TryGetValue("$select", out var select) && select.Contains("downloadUrl", StringComparison.Ordinal))
            {
                fake.OnRequest = null;
                fake.Delete("r/f.txt");
            }
        };
        Assert.IsFalse((await provider.GetFileInfoAsync("f.txt")).Exists);
        await Write(provider, "f.txt", "0123456789");

        // An answer that does not give the length, or no download URL, is an error.
        fake.Intercept = r => r.Host == GraphFake.DownloadHost ? new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([0x30]) } : null;
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"));
        fake.Intercept = null;
        fake.OmitDownloadUrls = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"));
        fake.OmitDownloadUrls = false;

        // A file Graph gives no ETag cannot be pinned.
        fake.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/f.txt:", StringComparison.Ordinal)
            ? Answer(HttpStatusCode.OK, "{\"id\":\"01X\",\"name\":\"f.txt\",\"size\":10,\"file\":{}}")
            : null;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.OpenReadSeekableAsync("f.txt"));
    }

    private static string FileId(GraphFake fake, string libraryPath) => fake.IdOf(libraryPath);

    private static HttpResponseMessage Answer(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ----- Signed URLs, content types, disposal -----

    [TestMethod]
    public async Task SignedUrl_IsThePreAuthenticatedDownloadUrl()
    {
        var (provider, _) = Create();
        await Write(provider, "f.txt", "x");
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(2), AllowShorterValidity = true });
        Assert.AreEqual(SignedUrlKind.PreAuthenticated, url.Kind);
        Assert.IsNull(url.ExpiresOn, "Graph does not say when the URL ends");
        Assert.AreEqual("GET", url.Method);
        Assert.AreEqual(GraphFake.DownloadHost, url.Url.Host);
        StringAssert.Contains(url.Url.Query, "tempauth=");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(59), AllowShorterValidity = true }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) }));
    }

    [TestMethod]
    public async Task ContentType_IsTheServices_AndTheHintIsIgnored()
    {
        var (provider, _) = Create();
        await Write(provider, "page.html", "<p/>", new UpdateFileOptions { ContentType = "text/plain" });
        Assert.AreEqual("text/html", (await provider.GetFileInfoAsync("page.html")).ContentType);
        var listed = (await provider.GetDirectoryContentsAsync("")).Cast<IWriteableFileInfo>().Single();
        Assert.AreEqual("text/html", listed.ContentType);
        Assert.IsFalse(string.IsNullOrEmpty(listed.ETag));
        Assert.IsNull(listed.PhysicalPath);
    }

    [TestMethod]
    public async Task Dispose_ReleasesWhatTheProviderOwns_AndNeverTheCallersClient()
    {
        var fake = new GraphFake();
        var own = TestGraph.OwnProvider(fake, TestGraph.Options("r"));
        await own.GetFileInfoAsync("x");
        await own.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => own.GetFileInfoAsync("x"));
        own.Dispose();

        using var client = TestGraph.Client(new GraphFake());
        var given = new WriteableSharePointFileProvider(TestGraph.Options("r"), client);
        await given.GetFileInfoAsync("x");
        given.Dispose();
        await given.DisposeAsync();
        Assert.IsNotNull(await client.Drives[GraphFake.DriveId].Items["root"].GetAsync(), "The caller's client still works");
    }
}
