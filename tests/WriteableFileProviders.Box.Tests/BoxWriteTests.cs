using System.Net;
using System.Text;
using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Box.Tests;

/// <summary>Uploads, direct and chunked, their conditions, and the races Box answers with 404 and 409.</summary>
[TestClass]
public sealed class BoxWriteTests
{
    private static readonly TestCredentials _credentials = TestCredentials.Default;

    [TestMethod]
    public async Task Write_NeverSeeksTheCallersStream()
    {
        var box = new FakeBox();
        using var provider = _credentials.Provider(box);

        await provider.UpdateFileAsync("f.txt", new NoSeekStream("HEADERbody", 6));
        await provider.UpdateFileAsync("f.txt", new NoSeekStream("XX-tail", 2), UpdateFileOptions.Append);

        Assert.AreEqual("body-tail", box.ContentOf("f.txt"));
    }

    [TestMethod]
    public async Task Write_StreamThatCannotSeek_KeepsEveryChunk()
    {
        var box = new FakeBox();
        using var provider = BoxFixture.Tune(_credentials.Provider(box));
        var content = TestStreams.Bytes(300_000);

        await provider.UpdateFileAsync("f.bin", new ChunkedReadStream(content, 1000));
        await provider.UpdateFileAsync("f.bin", new ChunkedReadStream(Encoding.UTF8.GetBytes("+more"), 2), UpdateFileOptions.Append);

        await using var stored = await provider.ReadFileAsync("f.bin");
        using var copy = new MemoryStream();
        await stored.CopyToAsync(copy);
        CollectionAssert.AreEqual(content.Concat(Encoding.UTF8.GetBytes("+more")).ToArray(), copy.ToArray());
    }

    [TestMethod]
    public async Task Write_SmallFile_IsOneUpload_AndTheContentTypeIsIgnored()
    {
        var box = new FakeBox();
        using var provider = _credentials.Provider(box);

        await provider.UpdateFileAsync("a/b.txt", TestStreams.Text("hello"), new UpdateFileOptions { ContentType = "text/plain" });

        Assert.AreEqual("hello", box.ContentOf("a/b.txt"));
        Assert.AreEqual(1, box.Requests.Count(r => r.Method == "POST" && r.Path == "/api/2.0/files/content"));
        Assert.IsFalse(box.Requests.Any(r => r.Path.Contains("upload_sessions", StringComparison.Ordinal)));
        Assert.IsNull((await provider.GetFileInfoAsync("a/b.txt")).ContentType);
    }

    [TestMethod]
    public async Task Write_LargeFile_GoesThroughAnUploadSession_PartByPart()
    {
        var box = new FakeBox { PartSize = 100_000 };
        using var provider = BoxFixture.Tune(_credentials.Provider(box));
        var first = TestStreams.Bytes(450_000, seed: 1);

        await provider.UpdateFileAsync("big.bin", new MemoryStream(first));

        var ranges = box.Requests.Where(r => r.Method == "PUT" && r.Path.Contains("upload_sessions", StringComparison.Ordinal)).Select(r => r.Header("Content-Range")).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "bytes 0-99999/450000", "bytes 100000-199999/450000", "bytes 200000-299999/450000", "bytes 300000-399999/450000", "bytes 400000-449999/450000",
        }, ranges);
        Assert.IsTrue(box.Requests.Single(r => r.Path.EndsWith("/commit", StringComparison.Ordinal)).Header("Digest")!.StartsWith("sha=", StringComparison.Ordinal));
        Assert.IsFalse(box.Requests.Any(r => r.Path == "/api/2.0/files/content"));

        var second = TestStreams.Bytes(300_000, seed: 2);
        await provider.UpdateFileAsync("big.bin", new MemoryStream(second));
        var id = box.IdOf("big.bin")!;
        Assert.AreEqual(1, box.Requests.Count(r => r.Path == $"/api/2.0/files/{id}/upload_sessions"));
        Assert.AreEqual(2, box.VersionCount(id));
        Assert.AreEqual(0, box.OpenSessions);
    }

    [TestMethod]
    [DataRow("""{"type":"upload_session","part_size":1024}""")]
    [DataRow("""{"id":"s1","type":"upload_session","part_size":0}""")]
    [DataRow("""{"id":"s1","type":"upload_session","part_size":4294967296}""")]
    public async Task UploadSession_WithoutAnIdOrAUsablePartSize_IsRefused(string session)
    {
        var box = new FakeBox();
        box.Intercept = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/2.0/files/upload_sessions"
            ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(session, Encoding.UTF8, "application/json") }
            : null;
        using var provider = BoxFixture.Tune(_credentials.Provider(box));

        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(300_000))));
        Assert.IsNull(box.IdOf("big.bin"));
    }

    [TestMethod]
    public async Task Chunked_CommitStillProcessing_IsAskedAgain()
    {
        var box = new FakeBox { CommitsNotReady = 3 };
        using var provider = BoxFixture.Tune(_credentials.Provider(box));

        await provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(300_000)));

        Assert.AreEqual(4, box.Requests.Count(r => r.Path.EndsWith("/commit", StringComparison.Ordinal)));
        Assert.IsNotNull(box.IdOf("big.bin"));
    }

    [TestMethod]
    public async Task Chunked_CommitNeverDone_AbortsTheSession_AndKeepsTheOldContent()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "big.bin", "old");
        using var provider = BoxFixture.Tune(_credentials.Provider(box));
        provider.NotReadyTimeout = TimeSpan.Zero;
        box.CommitsNotReady = int.MaxValue;

        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(300_000))));

        Assert.AreEqual("old", box.ContentOf("big.bin"));
        Assert.AreEqual(1, box.SessionsAborted);
        Assert.AreEqual(0, box.OpenSessions);
    }

    [TestMethod]
    public async Task Chunked_FailedPart_AbortsTheSession_AndKeepsTheOldContent()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "big.bin", "old");
        box.Intercept = request => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.Contains("upload_sessions", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) }
            : null;
        using var provider = BoxFixture.Tune(_credentials.Provider(box));

        var e = BoxErrors.Inner(await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(300_000)))));

        Assert.AreEqual(503, e.ResponseInfo.StatusCode);
        Assert.AreEqual("old", box.ContentOf("big.bin"));
        Assert.AreEqual(1, box.SessionsAborted);
    }

    [TestMethod]
    public async Task Chunked_AbortThatFails_StillReportsTheFirstFailure()
    {
        var box = new FakeBox();
        box.Intercept = request => request.RequestUri!.AbsolutePath.Contains("upload_sessions", StringComparison.Ordinal)
            && (request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"type":"error","code":"bad_request"}""") }
            : null;
        using var provider = BoxFixture.Tune(_credentials.Provider(box));

        var e = BoxErrors.Inner(await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(300_000)))));

        Assert.AreEqual("PUT", e.RequestInfo.Method);
        Assert.AreEqual(1, box.Requests.Count(r => r.Method == "DELETE"));
    }

    [TestMethod]
    public async Task IfMatch_IsSentQuoted_WhenTheHeaderIsOn()
    {
        var box = new FakeBox();
        using var provider = BoxFixture.Tune(_credentials.Provider(box));
        provider.SendIfMatch = true;
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("one"));
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag!;
        var id = box.IdOf("f.txt")!;

        // The caller's ETag is the version; on the wire goes Box's own etag of that same item, quoted.
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("two"), new UpdateFileOptions { IfMatch = etag });
        Assert.AreEqual("\"0\"", box.Requests.Single(r => r.Path == $"/api/2.0/files/{id}/content").Header("If-Match"));

        etag = (await provider.GetFileInfoAsync("f.txt")).ETag!;
        await provider.UpdateFileAsync("f.txt", new MemoryStream(TestStreams.Bytes(300_000)), new UpdateFileOptions { IfMatch = etag });
        Assert.AreEqual("\"1\"", box.Requests.Single(r => r.Path.EndsWith("/commit", StringComparison.Ordinal)).Header("If-Match"));
        Assert.AreEqual(300_000, (await provider.GetFileInfoAsync("f.txt")).Length);
    }

    [TestMethod]
    public async Task IfMatch_StaleOnTheWire_IsPreconditionFailed()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "f.txt", "one");
        using var provider = BoxFixture.Tune(_credentials.Provider(box));
        provider.SendIfMatch = true;
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag!;
        // A new version lands after the provider found the file, before its upload or its commit reaches Box.
        box.Intercept = OverwriteBefore(box, id, $"/api/2.0/files/{id}/content");

        var direct = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfMatch = etag }));
        Assert.IsInstanceOfType<BoxApiException>(direct.InnerException, "Box refused it: the check was atomic");

        etag = (await provider.GetFileInfoAsync("f.txt")).ETag!;
        box.Intercept = OverwriteBefore(box, id, "/commit");
        var chunked = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("f.txt", new MemoryStream(TestStreams.Bytes(300_000)), new UpdateFileOptions { IfMatch = etag }));
        Assert.IsInstanceOfType<BoxApiException>(chunked.InnerException);
        Assert.AreEqual("theirs", box.ContentOf("f.txt"));
        Assert.AreEqual(0, box.OpenSessions);
    }

    [TestMethod]
    public async Task IfMatch_IsCheckedBeforeTheWrite_WhileTheHeaderIsOff()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "f.txt", "one");
        using var provider = _credentials.Provider(box);
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag!;

        await provider.UpdateFileAsync("f.txt", TestStreams.Text("two"), new UpdateFileOptions { IfMatch = etag });
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("f.txt", TestStreams.Text("three"), new UpdateFileOptions { IfMatch = etag }));

        Assert.AreEqual("two", box.ContentOf("f.txt"));
        Assert.IsFalse(box.Requests.Any(r => r.Header("If-Match") is not null), "No If-Match is sent until a live run confirms Box takes the quoted form");
        Assert.AreEqual(2, box.VersionCount(id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IfMatch_AcrossADeleteAndRecreate_IsPreconditionFailed(bool sendIfMatch)
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "f.txt", "one");
        using var provider = _credentials.Provider(box);
        provider.SendIfMatch = sendIfMatch;
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;

        // Box's own etag of the new item is 0 again, as the old one's was: it would match.
        box.Delete(box.IdOf("f.txt")!);
        box.AddFile(FakeBox.RootId, "f.txt", "someone else's");

        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfMatch = etag }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.ReadRangeAsync("f.txt", 0, 1, new ReadOptions { IfMatch = etag }));
        Assert.AreEqual("someone else's", box.ContentOf("f.txt"));
    }

    [TestMethod]
    public async Task ConcurrentCreate_BecomesANewVersion()
    {
        var box = new FakeBox();
        box.Intercept = CreateFirst(box, "f.txt", "theirs");
        using var provider = _credentials.Provider(box);

        await provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"));

        Assert.AreEqual("mine", box.ContentOf("f.txt"));
        Assert.AreEqual(2, box.VersionCount(box.IdOf("f.txt")!));
    }

    [TestMethod]
    public async Task ConcurrentCreate_WithIfNotExists_IsAFileThatAlreadyExists()
    {
        var box = new FakeBox();
        box.Intercept = CreateFirst(box, "f.txt", "theirs");
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() =>
            provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfNotExists = true }));

        Assert.AreEqual("theirs", box.ContentOf("f.txt"));
    }

    [TestMethod]
    public async Task ConcurrentCreate_OfALargeFile_BecomesANewVersion()
    {
        var box = new FakeBox();
        var created = false;
        box.Intercept = request =>
        {
            if (!created && request.RequestUri!.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal))
            {
                created = true;
                box.AddFile(FakeBox.RootId, "big.bin", "theirs");
            }
            return null;
        };
        using var provider = BoxFixture.Tune(_credentials.Provider(box));

        await provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(300_000)));

        Assert.AreEqual(300_000, (await provider.GetFileInfoAsync("big.bin")).Length);
        Assert.AreEqual(1, box.SessionsAborted, "The session that lost the race is aborted");
    }

    [TestMethod]
    public async Task NameTakenTwice_SurfacesTheConflict()
    {
        var box = new FakeBox();
        box.Intercept = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/2.0/files/content"
            ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"type":"error","status":409,"code":"item_name_in_use"}""") }
            : null;
        using var provider = _credentials.Provider(box);

        var e = BoxErrors.Inner(await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"))));
        Assert.AreEqual(409, e.ResponseInfo.StatusCode);
        Assert.AreEqual(2, box.Requests.Count(r => r.Path == "/api/2.0/files/content"));
    }

    [TestMethod]
    public async Task ConcurrentDelete_CreatesTheFileAgain()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "f.txt", "old");
        box.Intercept = DeleteBeforeVersion(box, id);
        using var provider = _credentials.Provider(box);

        await provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"));

        Assert.AreEqual("mine", box.ContentOf("f.txt"));
        Assert.AreNotEqual(id, box.IdOf("f.txt"));
    }

    [TestMethod]
    public async Task ConcurrentDelete_WithIfMatch_IsPreconditionFailed()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "f.txt", "old");
        using var provider = _credentials.Provider(box);
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        box.Intercept = DeleteBeforeVersion(box, id);

        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfMatch = etag }));
        Assert.IsNull(box.IdOf("f.txt"));
    }

    [TestMethod]
    public async Task ConcurrentFolderCreate_IsUsed()
    {
        var box = new FakeBox();
        var raced = false;
        box.Intercept = request =>
        {
            if (!raced && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/2.0/folders")
            {
                raced = true;
                box.AddFolder(FakeBox.RootId, "dir");
            }
            return null;
        };
        using var provider = _credentials.Provider(box);

        await provider.UpdateFileAsync("dir/f.txt", TestStreams.Text("x"));

        Assert.AreEqual("x", box.ContentOf("dir/f.txt"));
    }

    [TestMethod]
    public async Task FolderNameTakenByAFile_IsRefused()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        var raced = false;
        box.Intercept = request =>
        {
            if (!raced && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/2.0/folders")
            {
                raced = true;
                box.AddFile(FakeBox.RootId, "dir", "a file now");
            }
            return null;
        };
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("a.txt/b.txt", TestStreams.Text("y")));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("dir/b.txt", TestStreams.Text("y")));
        Assert.AreEqual("x", box.ContentOf("a.txt"));
    }

    [TestMethod]
    public async Task FolderNameTakenByNeitherAFileNorAFolder_SaysSo()
    {
        var box = new FakeBox();
        box.Intercept = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/2.0/folders"
            ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"type":"error","status":409,"code":"item_name_in_use"}""") }
            : null;
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("dir/b.txt", TestStreams.Text("y")));

        StringAssert.Contains(e.Message, "neither a file nor a folder");
    }

    [TestMethod]
    public async Task Write_ToAFolder_IsRefused()
    {
        var box = new FakeBox();
        box.AddFolder(FakeBox.RootId, "dir");
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("DIR", TestStreams.Text("y")));
    }

    [TestMethod]
    [DataRow("trailing /f.txt")]
    [DataRow("dir/trailing ")]
    public async Task Write_NameBoxRefuses_IsArgumentException(string path)
    {
        var box = new FakeBox();
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync(path, TestStreams.Text("y")));
        Assert.AreEqual("path", e.ParamName);
        Assert.IsInstanceOfType<BoxApiException>(e.InnerException);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync(new string('n', 256), TestStreams.Text("y")));
    }

    [TestMethod]
    public async Task Write_KeepsTheStoredNameAndItsCase()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "a.txt", "old");
        using var provider = _credentials.Provider(box);

        await provider.UpdateFileAsync("A.TXT", TestStreams.Text("new"));

        Assert.AreEqual("a.txt", box.NameOf(id));
        Assert.AreEqual("new", box.ContentOf("a.txt"));
    }

    // Before the first upload of a new file, someone else creates it.
    private static Func<HttpRequestMessage, HttpResponseMessage?> CreateFirst(FakeBox box, string name, string text)
    {
        var raced = false;
        return request =>
        {
            if (!raced && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/2.0/files/content")
            {
                raced = true;
                box.AddFile(FakeBox.RootId, name, text);
            }
            return null;
        };
    }

    // Before the first request to the path, someone else uploads a new version of the file.
    private static Func<HttpRequestMessage, HttpResponseMessage?> OverwriteBefore(FakeBox box, string id, string pathEnd)
    {
        var raced = false;
        return request =>
        {
            if (!raced && request.RequestUri!.AbsolutePath.EndsWith(pathEnd, StringComparison.Ordinal))
            {
                raced = true;
                box.Overwrite(id, "theirs");
            }
            return null;
        };
    }

    // Before the first new version of the file, someone else deletes it.
    private static Func<HttpRequestMessage, HttpResponseMessage?> DeleteBeforeVersion(FakeBox box, string id)
    {
        var raced = false;
        return request =>
        {
            if (!raced && request.RequestUri!.AbsolutePath == $"/api/2.0/files/{id}/content")
            {
                raced = true;
                box.Delete(id);
            }
            return null;
        };
    }

    /// <summary>A stream positioned past a header, that throws if anyone moves its position.</summary>
    private sealed class NoSeekStream : MemoryStream
    {
        public NoSeekStream(string text, int position)
            : base(Encoding.UTF8.GetBytes(text)) => base.Position = position;

        public override long Position
        {
            get => base.Position;
            set => throw new InvalidOperationException("The caller's stream was rewound.");
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new InvalidOperationException("The caller's stream was seeked.");
    }

    /// <summary>A stream that cannot seek and returns at most a chunk per read, like a request body.</summary>
    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
