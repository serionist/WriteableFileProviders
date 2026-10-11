using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>
/// What the conformance suite cannot reach, on fake-gcs-server: the missing bucket on every member, creating the bucket,
/// signed URLs used for real, and races and server answers injected through the client's HTTP seam.
/// </summary>
[TestClass]
public sealed class GcsProviderTests
{
    private static readonly Lazy<string> _bucket = new(() => FakeGcs.CreateBucket("wfp-provider"));
    private readonly List<IDisposable> _disposables = [];
    private string _root = "";

    private static string Bucket => _bucket.Value;

    [TestInitialize]
    public void StartTest() => _root = "t-" + Guid.NewGuid().ToString("N")[..12];

    [TestCleanup]
    public void EndTest()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private WriteableGoogleCloudStorageFileProvider Provider(GoogleCloudStorageFileProviderOptions? options = null) =>
        Own(new WriteableGoogleCloudStorageFileProvider(options ?? FakeGcs.Options(Bucket, _root)));

    // A provider whose every request passes through the seam first.
    private WriteableGoogleCloudStorageFileProvider Provider(FaultInjection http) =>
        Own(new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = Bucket, RootDirectoryPath = _root }, Own(FakeGcs.CreateClient(httpClientFactory: http))));

    private StorageClient Side => Own(FakeGcs.CreateClient());

    private string Key(string path) => _root + "/" + path;

    private static async Task<string> ReadAll(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }

    private static string ReadAllSync(Stream stream)
    {
        using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    // ----- The missing bucket -----

    [TestMethod]
    public async Task MissingBucket_EveryMemberThrowsInvalidOperationNamingIt()
    {
        var name = FakeGcs.MissingBucketName();
        var provider = Provider(FakeGcs.Options(name));
        var calls = new Func<Task>[]
        {
            () => provider.ReadRangeAsync("f.txt", 2, 3),
            () => provider.ReadRangeAsync("f.txt", 2, 0),
            () => provider.OpenReadSeekableAsync("f.txt"),
            () => provider.CopyAsync("f.txt", "g.txt"),
            () => provider.MoveAsync("f.txt", "g.txt"),
            () => provider.CreateLocalFileStreamAsync("f.txt", readOnly: true),
            () => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) }),
            () => provider.UpdateFileAsync("f.txt", TestStreams.Text("x"), UpdateFileOptions.Append),
            async () => { await foreach (var _ in provider.EnumerateAsync("dir", recursive: true)) { } },
        };
        foreach (var call in calls)
        {
            var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(call);
            StringAssert.Contains(e.Message, name);
            StringAssert.Contains(e.Message, "CreateBucketIfMissing");
            Assert.IsInstanceOfType<GoogleApiException>(e.InnerException);
        }
    }

    [TestMethod]
    public async Task CreateBucketIfMissing_CreatesItOnTheFirstCall()
    {
        var name = FakeGcs.MissingBucketName();
        var options = FakeGcs.Options(name);
        options.CreateBucketIfMissing = true;
        var provider = Provider(options);
        Assert.IsFalse((await provider.GetFileInfoAsync("f.txt")).Exists);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        Assert.AreEqual("x", await ReadAll(await provider.ReadFileAsync("f.txt")));
        Assert.IsNotNull(await Side.GetBucketAsync(name));

        // An existing bucket is used as it is.
        var again = Provider(options);
        Assert.AreEqual("x", await ReadAll(await again.ReadFileAsync("f.txt")));
    }

    [TestMethod]
    public async Task CreateBucketIfMissing_TakesTheProjectOfTheKeyFile()
    {
        var name = FakeGcs.MissingBucketName();
        var provider = Provider(new GoogleCloudStorageFileProviderOptions
        {
            BucketName = name,
            CredentialsJson = FakeGcs.CredentialsJson(),
            Endpoint = FakeGcs.ConnectionString,
            AllowInsecureHttp = FakeGcs.IsRemote,
            CreateBucketIfMissing = true,
        });
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        Assert.IsNotNull(await Side.GetBucketAsync(name));
    }

    [TestMethod]
    public async Task CreateBucketIfMissing_WithoutAProjectThrowsAndTriesAgainOnTheNextCall()
    {
        var name = FakeGcs.MissingBucketName();
        var client = Own(FakeGcs.CreateClient());
        var options = new GoogleCloudStorageFileProviderOptions { BucketName = name, CreateBucketIfMissing = true };
        var provider = Provider(client, options);
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("f.txt"));
        StringAssert.Contains(e.Message, "ProjectId");

        // The failure is not kept: the next call tries again.
        options.ProjectId = FakeGcs.ProjectId;
        Assert.IsFalse((await provider.GetFileInfoAsync("f.txt")).Exists);
        Assert.IsNotNull(await Side.GetBucketAsync(name));
    }

    [TestMethod]
    public async Task CreateBucketIfMissing_ABucketCreatedMeanwhileIsUsed()
    {
        var name = FakeGcs.MissingBucketName();
        var http = new FaultInjection();
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsBucketCreate(request))
                await Side.CreateBucketAsync(FakeGcs.ProjectId, name);
            return null;
        };
        var provider = Provider(Own(FakeGcs.CreateClient(httpClientFactory: http)), new GoogleCloudStorageFileProviderOptions { BucketName = name, ProjectId = FakeGcs.ProjectId, CreateBucketIfMissing = true });
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        Assert.AreEqual("x", await ReadAll(await provider.ReadFileAsync("f.txt")));
    }

    [TestMethod]
    public async Task CreateBucketIfMissing_AFailedFirstCallBuildsNothingThatOutlivesTheProvider()
    {
        // The options' own key names no project: the client the provider built for the call is disposed with the failure,
        // and disposing the provider afterwards has nothing left to dispose.
        var options = FakeGcs.Options(FakeGcs.MissingBucketName());
        options.ProjectId = null;
        options.CreateBucketIfMissing = true;
        var provider = new WriteableGoogleCloudStorageFileProvider(options);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x")));
        await provider.DisposeAsync();
    }

    private WriteableGoogleCloudStorageFileProvider Provider(StorageClient client, GoogleCloudStorageFileProviderOptions options) =>
        Own(new WriteableGoogleCloudStorageFileProvider(options, client));

    // ----- Credentials and the client -----

    [TestMethod]
    public async Task Endpoint_TakesTheFullBaseUriToo()
    {
        var options = FakeGcs.Options(Bucket, _root);
        options.Endpoint = FakeGcs.ConnectionString;
        var provider = Provider(options);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        Assert.AreEqual("x", await ReadAll(await provider.ReadFileAsync("f.txt")));
    }

    [TestMethod]
    public async Task ApplicationDefaultCredentials_AreLookedUpOnTheFirstCall()
    {
        // The only test that resolves Application Default Credentials, which the SDK caches for the process: a key file
        // named by GOOGLE_APPLICATION_CREDENTIALS, whose project also names the bucket's project.
        var keyFile = Path.Combine(Path.GetTempPath(), $"wfp-adc-{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        await File.WriteAllTextAsync(keyFile, FakeGcs.CredentialsJson());
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", keyFile);
            var name = FakeGcs.MissingBucketName();
            var provider = Provider(new GoogleCloudStorageFileProviderOptions { BucketName = name, Endpoint = FakeGcs.Endpoint, AllowInsecureHttp = FakeGcs.IsRemote, CreateBucketIfMissing = true });
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("adc"));
            Assert.AreEqual("adc", await ReadAll(await provider.ReadFileAsync("f.txt")));
            var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) });
            StringAssert.Contains(url.Url.Query, Uri.EscapeDataString(FakeGcs.ClientEmail));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", previous);
            File.Delete(keyFile);
        }
    }

    [TestMethod]
    public async Task Provider_SendsNothingUntilItsFirstCallAndOneRequestPerRange()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        Assert.AreEqual(0, http.Requests.Count, "The constructor sends nothing");
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        http.Requests.Clear();
        Assert.AreEqual("345", await ReadAll(await provider.ReadRangeAsync("f.txt", 3, 3)));
        Assert.AreEqual(1, http.Requests.Count, string.Join(", ", http.Requests));
        StringAssert.Contains(http.Requests[0], "alt=media");
    }

    [TestMethod]
    public async Task GivenClient_WhoseCredentialCannotSignRefusesSignedUrls()
    {
        // An access token reads and writes but cannot sign: the provider says so up front.
        var client = Own(new StorageClientBuilder { BaseUri = FakeGcs.ConnectionString, GoogleCredential = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken("token") }.Build());
        var provider = Provider(client, new GoogleCloudStorageFileProviderOptions { BucketName = Bucket, RootDirectoryPath = _root });
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) }));
    }

    [TestMethod]
    public async Task GivenClient_IsNeverDisposedByTheProvider()
    {
        var client = Own(FakeGcs.CreateClient());
        var provider = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = Bucket, RootDirectoryPath = _root }, client);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        provider.Dispose();
        await provider.DisposeAsync();
        Assert.IsNotNull(await client.GetObjectAsync(Bucket, Key("f.txt")), "The caller's client still works");
    }

    [TestMethod]
    public async Task OwnClient_IsDisposedWithTheProvider()
    {
        var provider = new WriteableGoogleCloudStorageFileProvider(FakeGcs.Options(Bucket, _root));
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        await provider.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.ReadFileAsync("f.txt"));
    }

    // ----- What the requests carry -----

    [TestMethod]
    public async Task Requests_CarryTheGenerationPreconditions()
    {
        // The emulator ignores most of these conditions, so the tests that pass on it would pass without them: the
        // requests themselves show they are sent.
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        var generation = (await provider.GetFileInfoAsync("a.txt")).ETag!;

        http.Requests.Clear();
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("b"), new UpdateFileOptions { IfMatch = generation });
        StringAssert.Contains(Single(http, FaultInjection.UploadStartPattern), $"ifGenerationMatch={generation}");
        http.Requests.Clear();
        await provider.UpdateFileAsync("new.txt", TestStreams.Text("n"), new UpdateFileOptions { IfNotExists = true });
        StringAssert.Contains(Single(http, FaultInjection.UploadStartPattern), "ifGenerationMatch=0");

        generation = (await provider.GetFileInfoAsync("a.txt")).ETag!;
        http.Requests.Clear();
        await ReadAll(await provider.ReadRangeAsync("a.txt", 0, 1, new ReadOptions { IfMatch = generation }));
        var read = Single(http, "alt=media");
        StringAssert.Contains(read, $"generation={generation}");
        StringAssert.Contains(read, $"ifGenerationMatch={generation}");

        http.Requests.Clear();
        await provider.CopyAsync("a.txt", "copy.txt");
        var copy = Single(http, "/rewriteTo/");
        StringAssert.Contains(copy, "ifGenerationMatch=0");
        Assert.IsFalse(copy.Contains("sourceGeneration", StringComparison.Ordinal), copy);

        http.Requests.Clear();
        await provider.MoveAsync("a.txt", "moved.txt");
        var moveCopy = Single(http, "/rewriteTo/");
        StringAssert.Contains(moveCopy, "ifGenerationMatch=0");
        StringAssert.Contains(moveCopy, $"sourceGeneration={generation}");
        StringAssert.Contains(moveCopy, $"ifSourceGenerationMatch={generation}");
        var delete = Single(http, "DELETE ");
        StringAssert.Contains(delete, $"generation={generation}");
        StringAssert.Contains(delete, $"ifGenerationMatch={generation}");
    }

    private static string Single(FaultInjection http, string pattern)
    {
        var matches = http.Requests.Where(r => r.Contains(pattern, StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, matches.Count, $"'{pattern}' in: {string.Join(", ", http.Requests)}");
        return matches[0];
    }

    [TestMethod]
    public async Task Read_SendsTheClientsEncryptionKey()
    {
        // A client with a customer-supplied encryption key (CSEK) must send it with the provider's own downloads too.
        var key = EncryptionKey.Generate();
        var hashes = new List<string?>();
        var http = new FaultInjection
        {
            OnRequest = request =>
            {
                if (FaultInjection.IsDownload(request))
                    hashes.Add(request.Headers.TryGetValues("x-goog-encryption-key-sha256", out var values) ? values.Single() : null);
                return Task.FromResult<HttpResponseMessage?>(null);
            },
        };
        var client = Own(new StorageClientBuilder { BaseUri = FakeGcs.ConnectionString, GoogleCredential = FakeGcs.Credential(), EncryptionKey = key, HttpClientFactory = http }.Build());
        var provider = Provider(client, new GoogleCloudStorageFileProviderOptions { BucketName = Bucket, RootDirectoryPath = _root });
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        Assert.AreEqual("0123456789", await ReadAll(await provider.ReadFileAsync("f.txt")));
        Assert.AreEqual("345", await ReadAll(await provider.ReadRangeAsync("f.txt", 3, 3)));
        CollectionAssert.AreEqual(new[] { key.Base64Hash, key.Base64Hash }, hashes);
    }

    [TestMethod]
    public async Task Read_AMissingFileUnderACredentialThatCannotListIsNotFound()
    {
        // A credential that may read objects but not list them cannot tell a missing bucket: the file is missing.
        var http = new FaultInjection { OnRequest = request => Task.FromResult(FaultInjection.IsList(request) ? FaultInjection.Error(HttpStatusCode.Forbidden) : null) };
        var provider = Provider(http);
        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("missing.txt"));
        Assert.IsInstanceOfType<GoogleApiException>(e.InnerException);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("missing.txt", 0, 0));
    }

    [TestMethod]
    public async Task Copy_IsNotRetriedSoItsOwnCopyIsNeverTakenForTheDestination()
    {
        // The copy lands, but its answer is lost (503). A retry would find that copy and answer 412, which would read as
        // "the destination exists": the provider does not retry, and reports the failure it saw.
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        var copies = 0;
        http.OnRequest = async request =>
        {
            if (!FaultInjection.IsCopy(request))
                return null;
            if (++copies > 1)
                return FaultInjection.Error(HttpStatusCode.PreconditionFailed);
            await Side.CopyObjectAsync(Bucket, Key("a.txt"), Bucket, Key("b.txt"));
            return FaultInjection.Error(HttpStatusCode.ServiceUnavailable);
        };
        var e = Assert.IsInstanceOfType<GoogleApiException>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.CopyAsync("a.txt", "b.txt"))).InnerException);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, e.HttpStatusCode);
        Assert.AreEqual(1, copies, "The conditional copy is sent once");
    }

    [TestMethod]
    public async Task Write_IfMatchZeroIsNotAVersion()
    {
        // Generation 0 means "no object": IfMatch "0" must not turn into "create only".
        var provider = Provider();
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x"), new UpdateFileOptions { IfMatch = "0" }));
        Assert.IsFalse((await provider.GetFileInfoAsync("f.txt")).Exists);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 0, 1, new ReadOptions { IfMatch = "0" }));
    }

    // ----- Listing -----

    [TestMethod]
    public async Task List_SkipsObjectsThePathsCannotName()
    {
        foreach (var key in new[] { "ok.txt", "a//b.txt", "dots/../x.txt", "pct/%2e%2e/y.txt", "back\\slash.txt", "dir/" })
            await Side.UploadObjectAsync(Bucket, Key(key), "text/plain", TestStreams.Text("x"));
        var provider = Provider();
        var root = (await provider.GetDirectoryContentsAsync("")).Select(e => e.Name).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(new[] { "a", "dir", "dots", "ok.txt", "pct" }, root);
        Assert.IsTrue((await provider.GetDirectoryContentsAsync("a")).Exists);
        Assert.AreEqual(0, (await provider.GetDirectoryContentsAsync("a")).Count());
        CollectionAssert.AreEqual(Array.Empty<string>(), (await provider.GetDirectoryContentsAsync("dots")).Select(e => e.Name).ToArray());

        // A folder placeholder (an object named 'dir/', as the console makes) is an existing, empty folder.
        var dir = await provider.GetFileInfoAsync("dir");
        Assert.IsTrue(dir.Exists && dir.IsDirectory);
        var contents = await provider.GetDirectoryContentsAsync("dir");
        Assert.IsTrue(contents.Exists);
        Assert.AreEqual(0, contents.Count());
    }

    [TestMethod]
    public async Task FileInfo_KeepsTheContentTypeAndTheGeneration()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("plain.bin", TestStreams.Text("x"));
        var plain = await provider.GetFileInfoAsync("plain.bin");
        Assert.AreEqual("application/octet-stream", plain.ContentType);
        var stored = await Side.GetObjectAsync(Bucket, Key("plain.bin"));
        var generation = stored.Generation!.Value.ToString(CultureInfo.InvariantCulture);
        Assert.AreEqual(generation, plain.ETag);
        Assert.AreEqual(generation, (await provider.GetDirectoryContentsAsync("")).Cast<IWriteableFileInfo>().Single().ETag);
    }

    // ----- Reads -----

    [TestMethod]
    public async Task Range_OfLengthZeroChecksTheFileAndItsVersion()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        Assert.AreEqual("", await ReadAll(await provider.ReadRangeAsync("f.txt", 3, 0, new ReadOptions { IfMatch = etag })));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("missing.txt", 0, 0));
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("changed"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 3, 0, new ReadOptions { IfMatch = etag }));
    }

    [TestMethod]
    public async Task Range_AVersionThatIsNotAGenerationNeverMatches()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 0, 2, new ReadOptions { IfMatch = "\"abc\"" }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x"), new UpdateFileOptions { IfMatch = "-1" }));
        Assert.AreEqual("0123456789", await ReadAll(await provider.ReadFileAsync("f.txt")));
    }

    [TestMethod]
    public async Task Range_PinnedToAFileDeletedMeanwhileIsNotFound()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        await Side.DeleteObjectAsync(Bucket, Key("f.txt"));
        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("f.txt", 2, 2, new ReadOptions { IfMatch = etag }));
        Assert.IsInstanceOfType<GoogleApiException>(e.InnerException);
    }

    [TestMethod]
    public async Task Range_WhenTheServerIgnoresTheRangeTheSliceIsCutFromTheWhole()
    {
        // A transcoded object is served whole whatever the range asked: the provider skips to the offset itself.
        var http = new FaultInjection();
        http.OnRequest = request =>
        {
            if (FaultInjection.IsDownload(request))
                request.Headers.Range = null;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var provider = Provider(http);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        Assert.AreEqual("345", await ReadAll(await provider.ReadRangeAsync("f.txt", 3, 3)));
        Assert.AreEqual("789", await ReadAll(await provider.ReadRangeAsync("f.txt", 7, null)));
        Assert.AreEqual("", await ReadAll(await provider.ReadRangeAsync("f.txt", 50, 2)));
        Assert.AreEqual("345", ReadAllSync(await provider.ReadRangeAsync("f.txt", 3, 3)));
        Assert.AreEqual("", ReadAllSync(await provider.ReadRangeAsync("f.txt", 50, null)));
        await using var stream = await provider.ReadRangeAsync("f.txt", 4, 3);
        Assert.AreEqual(3, stream.Length);
        Assert.IsFalse(stream.CanSeek);
    }

    [TestMethod]
    public async Task Read_StreamsTheObjectWithItsLength()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        await using var stream = await provider.ReadFileAsync("f.txt");
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.AreEqual(10, stream.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(1, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
        stream.Flush();
        var buffer = new byte[4];
        Assert.AreEqual(4, stream.Read(buffer, 0, 4));
        Assert.AreEqual(4, await stream.ReadAsync(buffer.AsMemory(0, 4)));
#pragma warning disable CA1835 // The array overload is the one under test here.
        Assert.AreEqual(2, await stream.ReadAsync(buffer, 0, 4));
#pragma warning restore CA1835
        Assert.AreEqual("0123", ReadAllSync(await provider.ReadRangeAsync("f.txt", 0, 4)));
        // A length past what a range can name reads to the end.
        Assert.AreEqual("6789", await ReadAll(await provider.ReadRangeAsync("f.txt", 6, long.MaxValue)));
    }

    [TestMethod]
    public async Task Read_AnErrorStatusIsTheContractsType_WithGoogleApiExceptionInside()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));

        http.OnRequest = request => Task.FromResult(FaultInjection.IsDownload(request) ? FaultInjection.Error(HttpStatusCode.Forbidden, "no access") : null);
        // A 403 is Cloud Storage refusing the credential or the permission.
        var json = Assert.IsInstanceOfType<GoogleApiException>((await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.ReadFileAsync("f.txt"))).InnerException);
        Assert.AreEqual(HttpStatusCode.Forbidden, json.HttpStatusCode);
        StringAssert.Contains(json.Message, "no access");

        http.OnRequest = request => Task.FromResult(FaultInjection.IsDownload(request)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>busy</html>", Encoding.UTF8, "text/html") }
            : null);
        var html = Assert.IsInstanceOfType<GoogleApiException>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadRangeAsync("f.txt", 0, 1))).InnerException);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, html.HttpStatusCode);

        // A 412 on a pinned read: the file changed.
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        http.OnRequest = request => Task.FromResult(FaultInjection.IsDownload(request) ? FaultInjection.Error(HttpStatusCode.PreconditionFailed) : null);
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 0, 1, new ReadOptions { IfMatch = etag }));
    }

    // ----- Writes -----

    [TestMethod]
    public async Task Write_StoresTheContentTypeGivenOrOctetStream()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("page.html", TestStreams.Text("<p/>"), new UpdateFileOptions { ContentType = "text/html" });
        Assert.AreEqual("text/html", (await Side.GetObjectAsync(Bucket, Key("page.html"))).ContentType);
        await provider.UpdateFileAsync("page.html", TestStreams.Text("<p/>"));
        Assert.AreEqual("application/octet-stream", (await Side.GetObjectAsync(Bucket, Key("page.html"))).ContentType);
    }

    [TestMethod]
    public async Task Write_AFailureAfterAWholeChunkKeepsTheOldContent()
    {
        // The resumable upload sends 10 MiB chunks; the object is replaced only when the last one arrives.
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("f.bin", TestStreams.Text("original"));
        http.Requests.Clear();
        const int Chunk = 10 * 1024 * 1024;
        await Assert.ThrowsAsync<IOException>(() => provider.UpdateFileAsync("f.bin", new FailingStream(TestStreams.Bytes(Chunk + (Chunk / 2)))));
        Assert.IsTrue(http.Requests.Any(r => r.StartsWith("PUT /upload/", StringComparison.Ordinal)), "A whole chunk was sent: " + string.Join(", ", http.Requests));
        Assert.AreEqual("original", await ReadAll(await provider.ReadFileAsync("f.bin")));
    }

    [TestMethod]
    public async Task Write_A404WithTheBucketThereIsTheServersError()
    {
        var http = new FaultInjection { OnRequest = request => Task.FromResult(FaultInjection.IsUploadStart(request) ? FaultInjection.Error(HttpStatusCode.NotFound) : null) };
        var provider = Provider(http);
        var e = Assert.IsInstanceOfType<GoogleApiException>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x")))).InnerException);
        Assert.AreEqual(HttpStatusCode.NotFound, e.HttpStatusCode);
    }

    // ----- Copy and move -----

    [TestMethod]
    public async Task Copy_ADestinationWrittenAfterTheCheckIsRefused()
    {
        // Cloud Storage refuses the copy (ifGenerationMatch=0) with 412; the emulator ignores that condition, so the seam
        // answers as Cloud Storage does. GcsLiveAtomicityTests runs the real race.
        var http = new FaultInjection { OnRequest = request => Task.FromResult(FaultInjection.IsCopy(request) ? FaultInjection.Error(HttpStatusCode.PreconditionFailed) : null) };
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("a.txt", "b.txt"));

        // The same answer to a move whose destination is still free: the source changed.
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
        Assert.AreEqual("a", await ReadAll(await provider.ReadFileAsync("a.txt")));

        // And to a move whose destination was written meanwhile.
        http.OnRequest = async request =>
        {
            if (!FaultInjection.IsCopy(request))
                return null;
            await Side.UploadObjectAsync(Bucket, Key("c.txt"), "text/plain", TestStreams.Text("theirs"));
            return FaultInjection.Error(HttpStatusCode.PreconditionFailed);
        };
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.MoveAsync("a.txt", "c.txt"));
        Assert.AreEqual("a", await ReadAll(await provider.ReadFileAsync("a.txt")));
    }

    [TestMethod]
    public async Task Copy_ASourceDeletedAfterTheCheckIsNotFound()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsCopy(request))
                await Side.DeleteObjectAsync(Bucket, Key("a.txt"));
            return null;
        };
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.CopyAsync("a.txt", "b.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists);
    }

    [TestMethod]
    public async Task Move_ASourceWrittenBeforeTheCopyMovesNothing()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsCopy(request))
                await Side.UploadObjectAsync(Bucket, Key("a.txt"), "text/plain", TestStreams.Text("newer"));
            return null;
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
        http.OnRequest = null;
        Assert.AreEqual("newer", await ReadAll(await provider.ReadFileAsync("a.txt")));
        Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists);
    }

    [TestMethod]
    public async Task Move_ASourceWrittenAfterTheCopyKeepsTheWriteAndUndoesTheCopy()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsDelete(request))
            {
                http.OnRequest = null;
                await Side.UploadObjectAsync(Bucket, Key("a.txt"), "text/plain", TestStreams.Text("newer"));
            }
            return null;
        };
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
        Assert.IsInstanceOfType<GoogleApiException>(e.InnerException);
        Assert.AreEqual("newer", await ReadAll(await provider.ReadFileAsync("a.txt")));
        Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists, "The copy is undone");
    }

    [TestMethod]
    public async Task Move_ASourceDeletedAfterTheCopyCompletes()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsDelete(request))
            {
                http.OnRequest = null;
                await Side.DeleteObjectAsync(Bucket, Key("a.txt"));
            }
            return null;
        };
        await provider.MoveAsync("a.txt", "b.txt");
        Assert.AreEqual("a", await ReadAll(await provider.ReadFileAsync("b.txt")));
        Assert.IsFalse((await provider.GetFileInfoAsync("a.txt")).Exists);
    }

    [TestMethod]
    public async Task Move_AnUndoThatFindsTheCopyChangedLeavesIt()
    {
        // The source is written after the copy, and the copy too, before the move undoes it: both writes stay.
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        var deletes = 0;
        http.OnRequest = async request =>
        {
            if (!FaultInjection.IsDelete(request))
                return null;
            if (++deletes == 1)
                await Side.UploadObjectAsync(Bucket, Key("a.txt"), "text/plain", TestStreams.Text("newer"));
            else
                await Side.UploadObjectAsync(Bucket, Key("b.txt"), "text/plain", TestStreams.Text("theirs"));
            return null;
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
        http.OnRequest = null;
        Assert.AreEqual("newer", await ReadAll(await provider.ReadFileAsync("a.txt")));
        Assert.AreEqual("theirs", await ReadAll(await provider.ReadFileAsync("b.txt")));
    }

    [TestMethod]
    public async Task Delete_AFolderWhoseFilesGoMeanwhileStillSucceeds()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("dir/a.txt", TestStreams.Text("a"));
        await provider.UpdateFileAsync("dir/b.txt", TestStreams.Text("b"));
        http.OnRequest = request => Task.FromResult(FaultInjection.IsDelete(request) && request.RequestUri!.AbsolutePath.EndsWith("a.txt", StringComparison.Ordinal)
            ? FaultInjection.Error(HttpStatusCode.NotFound)
            : null);
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        http.OnRequest = null;
        Assert.IsFalse((await provider.GetFileInfoAsync("dir/b.txt")).Exists);
    }

    [TestMethod]
    public async Task Move_KeepsTheContentType()
    {
        var provider = Provider();
        await provider.UpdateFileAsync("a.html", TestStreams.Text("<p/>"), new UpdateFileOptions { ContentType = "text/html" });
        await provider.MoveAsync("a.html", "b.html");
        Assert.AreEqual("text/html", (await provider.GetFileInfoAsync("b.html")).ContentType);
        await provider.CopyAsync("b.html", "c.html");
        Assert.AreEqual("text/html", (await provider.GetFileInfoAsync("c.html")).ContentType);
    }

    // ----- Signed URLs, used -----

    [TestMethod]
    public async Task SignedUrls_UploadAndDownloadThroughTheEmulator()
    {
        var provider = Provider();
        using var http = new HttpClient();

        var write = await provider.GetSignedUrlAsync("up.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Write, ContentType = "text/plain" });
        using (var put = new HttpRequestMessage(HttpMethod.Put, write.Url) { Content = new StringContent("uploaded") })
        {
            put.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(write.Headers["Content-Type"]);
            using var response = await http.SendAsync(put);
            response.EnsureSuccessStatusCode();
        }
        Assert.AreEqual("uploaded", await ReadAll(await provider.ReadFileAsync("up.txt")));
        Assert.AreEqual("text/plain", (await provider.GetFileInfoAsync("up.txt")).ContentType);

        var create = await provider.GetSignedUrlAsync("new.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Create });
        using (var put = new HttpRequestMessage(HttpMethod.Put, create.Url) { Content = new StringContent("created") })
        {
            foreach (var (name, value) in create.Headers)
                put.Headers.TryAddWithoutValidation(name, value);
            using var response = await http.SendAsync(put);
            response.EnsureSuccessStatusCode();
        }
        Assert.AreEqual("created", await ReadAll(await provider.ReadFileAsync("new.txt")));

        var read = await provider.GetSignedUrlAsync("up.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), DownloadFileName = "report.txt", ContentType = "text/csv" });
        using (var response = await http.GetAsync(read.Url))
        {
            response.EnsureSuccessStatusCode();
            Assert.AreEqual("uploaded", await response.Content.ReadAsStringAsync());
            Assert.AreEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("report.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        }
    }

    // ----- Writes through append and local streams keep their guarantees on big files -----

    [TestMethod]
    public async Task Append_ToAFileWrittenMeanwhileThrowsAndKeepsTheWrite()
    {
        var http = new FaultInjection();
        var provider = Provider(http);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("start"));
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsUploadStart(request))
            {
                http.OnRequest = null;
                await Side.UploadObjectAsync(Bucket, Key("f.txt"), "text/plain", TestStreams.Text("theirs"));
            }
            return null;
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("+mine"), UpdateFileOptions.Append));
        Assert.AreEqual("theirs", await ReadAll(await provider.ReadFileAsync("f.txt")));
    }
}
