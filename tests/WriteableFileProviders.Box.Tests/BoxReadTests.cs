using System.Net;
using System.Text;
using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;

namespace WriteableFileProviders.Box.Tests;

/// <summary>Paths, listings, reads and signed URLs, where the conformance suite cannot see the wire.</summary>
[TestClass]
public sealed class BoxReadTests
{
    private static readonly TestCredentials _credentials = TestCredentials.Default;

    [TestMethod]
    [DataRow(LastPageMarker.Null)]
    [DataRow(LastPageMarker.Absent)]
    [DataRow(LastPageMarker.Empty)]
    public async Task Listing_PagesWithMarkers(LastPageMarker lastPage)
    {
        var box = new FakeBox { MaxPageSize = 3, LastPageMarker = lastPage };
        var big = box.AddFolder(FakeBox.RootId, "big");
        for (var i = 0; i < 7; i++)
            box.AddFile(big, $"f{i}.txt", new string('x', i));
        box.AddFolder(big, "sub");
        using var provider = _credentials.Provider(box);

        var entries = (await provider.GetDirectoryContentsAsync("big")).Cast<IWriteableFileInfo>().ToList();

        Assert.AreEqual(8, entries.Count);
        Assert.AreEqual(-1, entries.Single(e => e.Name == "sub").Length);
        Assert.AreEqual(6, entries.Single(e => e.Name == "f6.txt").Length);
        Assert.AreEqual("big/f6.txt", entries.Single(e => e.Name == "f6.txt").Path.ToString());
        Assert.IsTrue(entries.All(e => e.Exists && e.PhysicalPath is null));
        var listings = box.Requests.Where(r => r.Path.EndsWith("/items", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1 + 3, listings.Count, "The root once, then three pages of 'big'");
        Assert.IsTrue(listings.All(r => r.Uri.Query.Contains("usemarker=True", StringComparison.Ordinal) && !r.Uri.Query.Contains("offset", StringComparison.Ordinal)));
        Assert.IsTrue(listings.All(r => r.Uri.Query.Contains("fields=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Paths_AreFoundPastTheFirstPage_WithoutRegardToCase()
    {
        var box = new FakeBox { MaxPageSize = 2 };
        var docs = box.AddFolder(FakeBox.RootId, "Docs");
        for (var i = 0; i < 5; i++)
            box.AddFile(docs, $"f{i}.txt", "hello");
        using var provider = _credentials.Provider(box);

        var info = await provider.GetFileInfoAsync("DOCS/F4.TXT");

        Assert.IsTrue(info.Exists);
        Assert.AreEqual(5, info.Length);
        Assert.AreEqual(box.ETagOf(box.IdOf("Docs/f4.txt")!), info.ETag);
        Assert.IsNull(info.ContentType, "Box keeps no content type");
        Assert.IsTrue((await provider.GetFileInfoAsync("docs")).IsDirectory);
        Assert.IsFalse((await provider.GetFileInfoAsync("docs/f4.txt/below")).Exists, "A file is not a folder");
        Assert.IsFalse((await provider.GetFileInfoAsync("docs/missing/below")).Exists);
    }

    [TestMethod]
    public async Task FolderDeletedWhileThePathIsWalked_IsMissing()
    {
        var box = new FakeBox();
        var dir = box.AddFolder(FakeBox.RootId, "dir");
        box.AddFile(dir, "a.txt", "x");
        box.Intercept = request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/2.0/folders/{dir}/items")
                box.Delete(dir);
            return null;
        };
        using var provider = _credentials.Provider(box);

        Assert.IsFalse((await provider.GetFileInfoAsync("dir/a.txt")).Exists);
    }

    [TestMethod]
    public async Task WebLinks_AreNeitherFilesNorFolders()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        box.Intercept = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal))
                return null;
            var listing = """{"limit":1000,"next_marker":null,"entries":[{"type":"web_link","id":"9","etag":"0","name":"link","url":"https://example.test"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing, Encoding.UTF8, "application/json") };
        };
        using var provider = _credentials.Provider(box);

        Assert.AreEqual(0, (await provider.GetDirectoryContentsAsync("")).Count());
        Assert.IsFalse((await provider.GetFileInfoAsync("link")).Exists);
    }

    [TestMethod]
    public async Task RecursiveEnumeration_ListsEachFolderOnce()
    {
        var box = new FakeBox();
        var a = box.AddFolder(FakeBox.RootId, "a");
        var b = box.AddFolder(a, "b");
        box.AddFile(b, "c.txt", "c");
        box.AddFile(a, "d.txt", "d");
        using var provider = _credentials.Provider(box);

        var paths = new List<string>();
        await foreach (var entry in provider.EnumerateAsync("", recursive: true))
            paths.Add(entry.Path.ToString());

        CollectionAssert.AreEquivalent(new[] { "a", "a/b", "a/d.txt", "a/b/c.txt" }, paths);
        Assert.AreEqual(3, box.Requests.Count(r => r.Path.EndsWith("/items", StringComparison.Ordinal)), "The root, a and b: no path is resolved again");
        Assert.AreEqual(0, await CountAsync(provider.EnumerateAsync("missing", recursive: true)));
        Assert.AreEqual(0, await CountAsync(provider.EnumerateAsync("a/d.txt", recursive: true)));
    }

    [TestMethod]
    public async Task ReadFile_StreamsTheDownload()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "hello");
        using var provider = _credentials.Provider(box);

        await using var stream = await provider.ReadFileAsync("a.txt");

        Assert.IsNotInstanceOfType<FileStream>(stream, "No temp copy of the whole file");
        Assert.AreEqual("hello", await new StreamReader(stream).ReadToEndAsync());
        var download = box.Requests.Single(r => r.Uri.Host == "dl.box.test");
        Assert.IsNull(download.Header("Range"));
        Assert.IsNull(download.Header("Authorization"), "The download URL is the credential");
    }

    [TestMethod]
    public async Task Range_GoesToTheDownloadUrlOfTheVersionFound()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "f.txt", "0123456789");
        using var provider = _credentials.Provider(box);

        Assert.AreEqual("2345", await ReadAllAsync(await provider.ReadRangeAsync("f.txt", 2, 4)));

        var link = box.Requests.Single(r => r.Path == $"/2.0/files/{id}/content");
        StringAssert.Contains(link.Uri.Query, "version=v");
        var download = box.Requests.Single(r => r.Uri.Host == "dl.box.test");
        Assert.AreEqual("bytes=2-5", download.Header("Range"));
        Assert.IsNull(download.Header("Authorization"));
    }

    [TestMethod]
    public async Task Range_ReadsThePinnedVersion_WhenTheFileChangesUnderIt()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "f.txt", "0123456789");
        var changed = false;
        box.Intercept = request =>
        {
            // A new version lands between finding the file and asking for its download URL.
            if (!changed && request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
            {
                changed = true;
                box.Overwrite(id, "CHANGED!!!");
            }
            return null;
        };
        using var provider = _credentials.Provider(box);

        Assert.AreEqual("01234", await ReadAllAsync(await provider.ReadRangeAsync("f.txt", 0, 5)));
        Assert.AreEqual("CHANGED!!!", box.ContentOf("f.txt"));
    }

    [TestMethod]
    public async Task Range_IgnoredByTheServer_IsCutFromTheWholeFile()
    {
        var box = new FakeBox { IgnoreRange = true };
        box.AddFile(FakeBox.RootId, "f.txt", "0123456789");
        using var provider = _credentials.Provider(box);

        Assert.AreEqual("2345", await ReadAllAsync(await provider.ReadRangeAsync("f.txt", 2, 4)));
        Assert.AreEqual("789", await ReadAllAsync(await provider.ReadRangeAsync("f.txt", 7, null)));
        Assert.AreEqual("0123", await ReadAllAsync(await provider.ReadRangeAsync("f.txt", 0, 4)));

        // The synchronous read path too.
        using var stream = await provider.ReadRangeAsync("f.txt", 6, 3);
        var buffer = new byte[10];
        var read = stream.Read(buffer, 0, 10);
        Assert.AreEqual("678", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.AreEqual(0, stream.Read(buffer, 0, 10));
    }

    [TestMethod]
    public async Task Download_NotReadyYet_IsAskedForAgain()
    {
        var box = new FakeBox { DownloadsNotReady = 2 };
        var id = box.AddFile(FakeBox.RootId, "a.txt", "fresh");
        using var provider = _credentials.Provider(box);

        Assert.AreEqual("fresh", await ReadAllAsync(await provider.ReadFileAsync("a.txt")));
        Assert.AreEqual(3, box.Requests.Count(r => r.Path == $"/2.0/files/{id}/content"));
    }

    [TestMethod]
    public async Task Download_NeverReady_ThrowsIOException()
    {
        var box = new FakeBox { DownloadsNotReady = int.MaxValue };
        box.AddFile(FakeBox.RootId, "a.txt", "fresh");
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("a.txt"));
    }

    [TestMethod]
    public async Task Download_WithoutALocation_ThrowsIOException()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        box.Intercept = request => request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }
            : null;
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("a.txt"));
        StringAssert.Contains(e.Message, "200");
    }

    [TestMethod]
    public async Task Read_FileDeletedAfterItWasFound_IsFileNotFound()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "a.txt", "x");
        box.Intercept = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                box.Delete(id);
            return null;
        };
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("a.txt"));
        Assert.IsInstanceOfType<BoxApiException>(e.InnerException);
    }

    [TestMethod]
    public async Task ServerErrors_AreRetried_ThenSurfaceAsTheBoxError()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        var failures = 1;
        box.Intercept = request => request.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal) && failures-- > 0
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new ByteArrayContent([]) }
            : null;
        using var provider = _credentials.Provider(box);
        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists, "One 500 is retried by the SDK");

        failures = int.MaxValue;
        var e = await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.AreEqual(500, e.ResponseInfo.StatusCode);
    }

    [TestMethod]
    public async Task SignedUrl_IsBoxsDownloadUrl_WithItsLifetime()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "shared");
        using var provider = _credentials.Provider(box);
        var options = new SignedUrlOptions { Validity = TimeSpan.FromHours(1), AllowShorterValidity = true };

        var before = DateTimeOffset.UtcNow;
        var url = await provider.GetSignedUrlAsync("a.txt", options);
        var after = DateTimeOffset.UtcNow;

        Assert.AreEqual("dl.box.test", url.Url.Host);
        Assert.AreEqual("GET", url.Method);
        Assert.AreEqual(SignedUrlKind.PreAuthenticated, url.Kind);
        Assert.IsTrue(url.ExpiresOn >= before.AddMinutes(15) && url.ExpiresOn <= after.AddMinutes(15), $"ExpiresOn {url.ExpiresOn}");
        using var anonymous = new HttpClient(box, disposeHandler: false);
        Assert.AreEqual("shared", await anonymous.GetStringAsync(url.Url));
    }

    [TestMethod]
    public async Task SignedUrl_ThatWouldLiveLongerOrShorterThanAsked_IsNotSupported()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = _credentials.Provider(box);

        var tooShort = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(10), AllowShorterValidity = true }));
        StringAssert.Contains(tooShort.Message, "longer");
        var notAllowed = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) }));
        StringAssert.Contains(notAllowed.Message, "AllowShorterValidity");
        // The 2026 call shape asks for exactly the validity given, which Box cannot promise.
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetDownloadUrlAsync("a.txt", TimeSpan.FromHours(1)));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(1), AllowShorterValidity = true, DownloadFileName = "b.txt" }));
        Assert.IsFalse(box.Requests.Any(r => r.Path.EndsWith("/content", StringComparison.Ordinal)), "No URL was issued");
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        await using (stream)
            return await new StreamReader(stream).ReadToEndAsync();
    }

    private static async Task<int> CountAsync(IAsyncEnumerable<IWriteableFileInfo> entries)
    {
        var count = 0;
        await foreach (var _ in entries)
            count++;
        return count;
    }
}
