using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;
using static WriteableFileProviders.S3.Testing.InterceptingHttpClientFactory;

namespace WriteableFileProviders.S3.Testing;

/// <summary>
/// What the conformance suite cannot reach on the S3 core, run against a real S3-compatible server for each provider
/// that shares the core: keys the paths cannot name, batches, multipart and copy in parts, faults injected through the
/// SDK's HTTP layer, missing buckets, and presigned URLs used for real.
/// </summary>
public abstract class S3BehaviourTests
{
    private const int MiB = 1 << 20;
    private static readonly HttpClient _http = new();
    private string _root = "";

    /// <summary>The server.</summary>
    internal abstract S3Endpoint Endpoint { get; }

    /// <summary>Whether the server checks presigned signatures and expiry (MinIO does, S3Mock does not).</summary>
    protected virtual bool ValidatesSignatures => false;

    /// <summary>Whether an aborted multipart upload leaves the existing object (S3Mock 5.2.3 deletes it).</summary>
    protected virtual bool KeepsContentOnAbort => true;

    /// <summary>The test's root folder in the endpoint's bucket.</summary>
    protected string Root => _root;

    /// <summary>Builds this package's provider over the server.</summary>
    /// <param name="root">The root folder.</param>
    /// <param name="bucket">The bucket; the endpoint's when null.</param>
    /// <param name="createBucketIfMissing">The option of the same name.</param>
    internal abstract IWriteableFileProvider CreateProvider(string? root, string? bucket = null, bool createBucketIfMissing = false);

    /// <summary>Builds this package's provider over a client the test owns.</summary>
    /// <param name="client">The client.</param>
    /// <param name="root">The root folder.</param>
    internal abstract IWriteableFileProvider CreateProvider(IAmazonS3 client, string? root);

    /// <summary>The provider's core, to tune its sizes and its HTTP layer.</summary>
    /// <param name="provider">A provider built by <see cref="CreateProvider(string?, string?, bool)"/>.</param>
    internal abstract S3Core CoreOf(IWriteableFileProvider provider);

    /// <summary>Starts the server and picks the test's root folder.</summary>
    [TestInitialize]
    public void StartTest()
    {
        _ = Endpoint;
        _root = "beh-" + Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>Deletes what the test wrote.</summary>
    [TestCleanup]
    public async Task EndTest()
    {
        if (_root.Length == 0)
            return;
        using var client = Endpoint.CreateClient();
        while (true)
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Endpoint.Bucket, Prefix = _root + "/" });
            if (page.S3Objects is not { Count: > 0 } objects)
                break;
            await client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = Endpoint.Bucket, Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })] });
        }
    }

    // ----- Keys, listings and deletes -----

    [TestMethod]
    public async Task Listing_SkipsMarkersAndKeysThePathsCannotName()
    {
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("d/ok.txt", TestStreams.Text("ok"));
        // A folder marker, a key that decodes to "..", one with a backslash, one with an empty segment: some servers refuse
        // some of them, which is fine; the listing must never offer one back.
        foreach (var odd in new[] { "d/", "d/%2e%2e", "d/a\\b.txt", "d/e//x.txt" })
            await TryPutRawAsync(odd);

        var level = (await provider.GetDirectoryContentsAsync("d")).Cast<IWriteableFileInfo>().ToList();
        Assert.IsTrue(level.Any(e => e.Name == "ok.txt"));
        Assert.IsFalse(level.Any(e => e.Name is "" or "%2e%2e" or "a\\b.txt"), string.Join(", ", level.Select(e => e.Name)));
        var all = new List<IWriteableFileInfo>();
        await foreach (var entry in provider.EnumerateAsync("", recursive: true))
            all.Add(entry);
        Assert.IsFalse(all.Any(e => e.Path.Segments.Any(s => s is "" or "%2e%2e" or "a\\b.txt")), string.Join(", ", all.Select(e => e.Path)));
        foreach (var entry in level.Concat(all))
            Assert.IsTrue((await provider.GetFileInfoAsync(entry.Path)).Exists, $"'{entry.Path}' does not round-trip");
        Assert.AreEqual(1, all.Count(e => e.Path == new ProviderPath("d")), "The folder is yielded once");
    }

    [TestMethod]
    public async Task Enumerate_RecursiveYieldsEachFolderOnceBeforeItsContents()
    {
        using var provider = CreateProvider(Root);
        // '-' and '!' sort before '/', so "a-b" and "a/b!" come between keys of "a/" and "a/b/".
        foreach (var path in new[] { "a.txt", "a/b.txt", "a-b/c.txt", "a/b!/c.txt", "a/b/d.txt", "a/b/e/f.txt", "z/y/x.txt" })
            await provider.UpdateFileAsync(path, TestStreams.Text(path));
        await TryPutRawAsync("m/");
        await TryPutRawAsync("a/b/");

        var paths = new List<string>();
        await foreach (var entry in provider.EnumerateAsync("", recursive: true))
            paths.Add(entry.Path.ToString());
        var expected = new[] { "a.txt", "a", "a/b.txt", "a-b", "a-b/c.txt", "a/b!", "a/b!/c.txt", "a/b", "a/b/d.txt", "a/b/e", "a/b/e/f.txt", "z", "z/y", "z/y/x.txt" };
        CollectionAssert.AreEquivalent(expected.Concat(paths.Contains("m") ? ["m"] : Array.Empty<string>()).ToList(), paths, string.Join(", ", paths));
        foreach (var path in paths.Where(p => p.Contains('/', StringComparison.Ordinal)))
            Assert.IsTrue(paths.IndexOf(path[..path.LastIndexOf('/')]) < paths.IndexOf(path), $"'{path}' came before its folder");

        var below = new List<string>();
        await foreach (var entry in provider.EnumerateAsync("a/b", recursive: true))
            below.Add(entry.Path.ToString());
        CollectionAssert.AreEquivalent(new[] { "a/b/d.txt", "a/b/e", "a/b/e/f.txt" }, below);
    }

    [TestMethod]
    public async Task FileInfo_ExactKeyWinsAndAFolderNeedsAKeyBelowIt()
    {
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("k/a.txt.bak", TestStreams.Text("backup"));
        Assert.IsFalse((await provider.GetFileInfoAsync("k/a.txt")).Exists, "a.txt.bak is not a.txt");
        Assert.IsFalse((await provider.GetFileInfoAsync("k/a")).IsDirectory, "k/a.txt.bak is not under a folder k/a");

        await provider.UpdateFileAsync("x", TestStreams.Text("file"));
        await provider.UpdateFileAsync("x/y.txt", TestStreams.Text("below"));
        var info = await provider.GetFileInfoAsync("x");
        Assert.IsTrue(info.Exists && !info.IsDirectory, "A file wins over a folder of the same name");
        Assert.IsTrue(await provider.DeleteAsync("x"));
        Assert.AreEqual("below", await ReadAsync(provider, "x/y.txt"), "Deleting the file leaves the folder");
        Assert.IsTrue((await provider.GetFileInfoAsync("x")).IsDirectory);
    }

    [TestMethod]
    public async Task Delete_FolderOfMoreThanOneBatch()
    {
        using var provider = CreateProvider(Root);
        using var client = Endpoint.CreateClient();
        using (var gate = new SemaphoreSlim(32))
        {
            await Task.WhenAll(Enumerable.Range(0, 1100).Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    await client.PutObjectAsync(new PutObjectRequest { BucketName = Endpoint.Bucket, Key = $"{Root}/big/s{i % 3}/f{i:D4}.txt", ContentBody = "x" });
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        await provider.UpdateFileAsync("big.txt", TestStreams.Text("sibling"));
        Assert.IsTrue(await provider.DeleteAsync("big"));
        Assert.IsFalse((await provider.GetDirectoryContentsAsync("big")).Exists);
        Assert.AreEqual("sibling", await ReadAsync(provider, "big.txt"));
        Assert.IsFalse(await provider.DeleteAsync("big"));
    }

    [TestMethod]
    public async Task Delete_ReportsObjectsTheStoreCouldNotDelete()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("d/a.txt", TestStreams.Text("a"));
            factory.Intercept = r => Task.FromResult(IsDeleteObjects(r)
                ? Xml(HttpStatusCode.OK, "<DeleteResult><Error><Key>k</Key><Code>AccessDenied</Code><Message>Injected</Message></Error></DeleteResult>")
                : null);
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("d"));
            StringAssert.Contains(e.Message, "AccessDenied");
        }
    }

    [TestMethod]
    public async Task Delete_StopsWhenTheStoreKeepsListingTheSameKeys()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("d/a.txt", TestStreams.Text("a"));
            factory.Intercept = r => Task.FromResult(IsDeleteObjects(r) ? Xml(HttpStatusCode.OK, "<DeleteResult></DeleteResult>") : null);
            await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("d"));
        }
    }

    // ----- Reads -----

    [TestMethod]
    public async Task Range_OfLengthZeroStillChecksTheFile()
    {
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123"));
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        await using (var empty = await provider.ReadRangeAsync("f.txt", 1, 0, new ReadOptions { IfMatch = etag }))
            Assert.AreEqual(0, await empty.ReadAsync(new byte[4]));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("missing.txt", 0, 0));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 0, 0, new ReadOptions { IfMatch = "\"stale\"" }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 0, 2, new ReadOptions { IfMatch = "\"stale\"" }));
    }

    [TestMethod]
    public async Task Range_LongerThanARangeCanNameReadsToTheEnd()
    {
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123"));
        // offset + length - 1 overflows for the last two: the read must still go to the end, not fail.
        foreach (var (offset, length) in new[] { (1L, long.MaxValue), (2L, long.MaxValue - 1), (2L, long.MaxValue), (3L, long.MaxValue - 1) })
        {
            await using var stream = await provider.ReadRangeAsync("f.txt", offset, length);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.AreEqual("0123"[(int)offset..], await reader.ReadToEndAsync(), $"{offset}, {length}");
        }
    }

    [TestMethod]
    public async Task Read_MissingFileKeepsTheStoresError()
    {
        using var provider = CreateProvider(Root);
        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("missing.txt"));
        Assert.IsInstanceOfType<AmazonS3Exception>(e.InnerException);
    }

    // ----- Writes -----

    [TestMethod]
    public async Task Write_SmallContentGoesInOneRequestAndLargeInParts()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("small.bin", new NonSeekableStream(new MemoryStream(TestStreams.Bytes(10))));
            Assert.IsFalse(factory.Requests.Any(r => r.Contains("?uploads", StringComparison.Ordinal)), "A small write is one PUT");

            var large = TestStreams.Bytes(12 * MiB, seed: 7);
            await provider.UpdateFileAsync("unknown-length.bin", new NonSeekableStream(new MemoryStream(large)));
            Assert.AreEqual(3, factory.Requests.Count(r => r.StartsWith("PUT", StringComparison.Ordinal) && r.Contains("partNumber=", StringComparison.Ordinal)));
            await AssertContentAsync(provider, "unknown-length.bin", large);

            var uploads = factory.Requests.Count(r => r.Contains("?uploads", StringComparison.Ordinal));
            var exact = TestStreams.Bytes(5 * MiB, seed: 8);
            await provider.UpdateFileAsync("one-part.bin", new NonSeekableStream(new MemoryStream(exact)));
            await AssertContentAsync(provider, "one-part.bin", exact);
            Assert.AreEqual(uploads, factory.Requests.Count(r => r.Contains("?uploads", StringComparison.Ordinal)), "Exactly one part is one PUT");

            var positioned = new MemoryStream(large) { Position = MiB };
            await provider.UpdateFileAsync("from-position.bin", positioned);
            await AssertContentAsync(provider, "from-position.bin", large[MiB..]);
            Assert.IsTrue(positioned.CanRead, "The caller's stream is left open");
        }
    }

    [TestMethod]
    public async Task Write_StoresTheContentTypeAndAnAppendKeepsIt()
    {
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("plain", TestStreams.Text("x"));
        Assert.AreEqual(S3Core.DefaultContentType, (await provider.GetFileInfoAsync("plain")).ContentType);
        await provider.UpdateFileAsync("page.html", TestStreams.Text("<p>"), new UpdateFileOptions { ContentType = "text/html" });
        await provider.UpdateFileAsync("page.html", TestStreams.Text("</p>"), UpdateFileOptions.Append);
        Assert.AreEqual("text/html", (await provider.GetFileInfoAsync("page.html")).ContentType);
        Assert.AreEqual("<p></p>", await ReadAsync(provider, "page.html"));
        await provider.UpdateFileAsync("page.html", TestStreams.Text("!"), new UpdateFileOptions { Mode = UpdateFileMode.Append, ContentType = "text/plain" });
        Assert.AreEqual("text/plain", (await provider.GetFileInfoAsync("page.html")).ContentType);
        await provider.UpdateFileAsync("new.txt", TestStreams.Text("n"), UpdateFileOptions.Append);
        Assert.AreEqual(S3Core.DefaultContentType, (await provider.GetFileInfoAsync("new.txt")).ContentType);
    }

    [TestMethod]
    public async Task Write_IfMatchOnAMissingFileFails()
    {
        using var provider = CreateProvider(Root);
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
            provider.UpdateFileAsync("missing.txt", TestStreams.Text("x"), new UpdateFileOptions { IfMatch = "\"0123456789abcdef0123456789abcdef\"" }));
        Assert.IsFalse((await provider.GetFileInfoAsync("missing.txt")).Exists);
    }

    [TestMethod]
    public async Task Write_LargeIfNotExistsOnAnExistingFileFails()
    {
        var (provider, _) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("f.bin", TestStreams.Text("old"));
            await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
                provider.UpdateFileAsync("f.bin", new MemoryStream(TestStreams.Bytes(6 * MiB)), new UpdateFileOptions { IfNotExists = true }));
            if (KeepsContentOnAbort)
                Assert.AreEqual("old", await ReadAsync(provider, "f.bin"));
        }
    }

    [TestMethod]
    public async Task Write_AFailedPartAbortsTheUpload()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("f.bin", TestStreams.Text("old"));
            var parts = 0;
            factory.Intercept = r => Task.FromResult(IsUploadPart(r) && Interlocked.Increment(ref parts) == 2
                ? Error(HttpStatusCode.BadRequest, "InvalidPart")
                : null);
            var e = await Assert.ThrowsExactlyAsync<AmazonS3Exception>(() => provider.UpdateFileAsync("f.bin", new MemoryStream(TestStreams.Bytes(12 * MiB))));
            Assert.AreEqual("InvalidPart", e.ErrorCode);
            Assert.IsTrue(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("uploadId=", StringComparison.Ordinal)), "The upload was aborted");
            factory.Intercept = null;
            if (KeepsContentOnAbort)
                Assert.AreEqual("old", await ReadAsync(provider, "f.bin"));
        }
    }

    [TestMethod]
    public async Task Write_AConflictOnCompletionIsAPreconditionFailure()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("f.bin", TestStreams.Text("old"));
            var etag = (await provider.GetFileInfoAsync("f.bin")).ETag;
            factory.Intercept = r => Task.FromResult(IsComplete(r) ? Error(HttpStatusCode.Conflict, "ConditionalRequestConflict") : null);
            await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
                provider.UpdateFileAsync("f.bin", new MemoryStream(TestStreams.Bytes(6 * MiB)), new UpdateFileOptions { IfMatch = etag }));
            Assert.IsTrue(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("uploadId=", StringComparison.Ordinal)), "The upload was aborted");
            factory.Intercept = null;
            if (KeepsContentOnAbort)
                Assert.AreEqual("old", await ReadAsync(provider, "f.bin"));
        }
    }

    // ----- Copies -----

    [TestMethod]
    public async Task Copy_InPartsAboveTheSingleCopyLimit()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            var core = CoreOf(provider);
            core.MaxSingleCopySize = 5 * MiB;
            core.CopyPartSize = 5 * MiB;
            var content = TestStreams.Bytes(11 * MiB, seed: 9);
            await provider.UpdateFileAsync("big.bin", new MemoryStream(content), new UpdateFileOptions { ContentType = "application/x-test" });
            await provider.CopyAsync("big.bin", "copies/big.bin");
            Assert.AreEqual(3, factory.Requests.Count(r => r.StartsWith("PUT", StringComparison.Ordinal) && r.Contains("partNumber=", StringComparison.Ordinal)
                && r.Contains("copies", StringComparison.Ordinal)));
            await AssertContentAsync(provider, "copies/big.bin", content);
            Assert.AreEqual("application/x-test", (await provider.GetFileInfoAsync("copies/big.bin")).ContentType);
            await provider.MoveAsync("copies/big.bin", "moved.bin");
            await AssertContentAsync(provider, "moved.bin", content);
            Assert.IsFalse((await provider.GetFileInfoAsync("copies/big.bin")).Exists);
        }
    }

    [TestMethod]
    public async Task Copy_InPartsAbortsWhenAPartFails()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            var core = CoreOf(provider);
            core.MaxSingleCopySize = 5 * MiB;
            core.CopyPartSize = 5 * MiB;
            await provider.UpdateFileAsync("big.bin", new MemoryStream(TestStreams.Bytes(11 * MiB)));
            var parts = 0;
            factory.Intercept = r => Task.FromResult(
                IsCopyPart(r) && Interlocked.Increment(ref parts) == 2 ? Error(HttpStatusCode.BadRequest, "InvalidRequest")
                // The abort fails too: the copy's own failure is what the caller sees.
                : IsAbort(r) ? Error(HttpStatusCode.BadRequest, "InvalidRequest")
                : null);
            var e = await Assert.ThrowsExactlyAsync<AmazonS3Exception>(() => provider.CopyAsync("big.bin", "copy.bin"));
            Assert.AreEqual("InvalidRequest", e.ErrorCode);
            Assert.IsTrue(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("uploadId=", StringComparison.Ordinal)), "The copy was aborted");
            factory.Intercept = null;
            Assert.IsFalse((await provider.GetFileInfoAsync("copy.bin")).Exists);
        }
    }

    [TestMethod]
    public async Task Copy_ASourceDeletedAfterTheCheckIsNotFound()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            var heads = 0;
            factory.Intercept = r => Task.FromResult(
                r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.EndsWith("/a.txt", StringComparison.Ordinal) && Interlocked.Increment(ref heads) == 2
                    ? Empty(HttpStatusCode.NotFound)
                    : null);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.CopyAsync("a.txt", "b.txt"));
        }
    }

    [TestMethod]
    public async Task Copy_ADestinationCreatedMeanwhileIsRefused()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            factory.Intercept = async r =>
            {
                if (!IsCopy(r))
                    return null;
                // Another writer creates the destination between the check and the copy; AWS answers 412.
                await PutRawAsync("b.txt", "theirs");
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            };
            await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("a.txt", "b.txt"));
            factory.Intercept = null;
            Assert.AreEqual("theirs", await ReadAsync(provider, "b.txt"));
        }
    }

    [TestMethod]
    public async Task Copy_ASourceThatChangesOrGoesIsReported()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            factory.Intercept = r => Task.FromResult(IsCopy(r) ? Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed") : null);
            await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.CopyAsync("a.txt", "b.txt"));
            factory.Intercept = r => Task.FromResult(IsCopy(r) ? Error(HttpStatusCode.NotFound, "NoSuchKey") : null);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.CopyAsync("a.txt", "b.txt"));
            factory.Intercept = null;
            Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists);
        }
    }

    [TestMethod]
    public async Task Move_WhoseSourceChangesAfterTheCopyIsUndone()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("old"));
            var heads = 0;
            factory.Intercept = async r =>
            {
                // The third HEAD of the source is the check before the delete off AWS: another writer gets there first.
                if (r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.EndsWith("/a.txt", StringComparison.Ordinal) && Interlocked.Increment(ref heads) == 3)
                    await PutRawAsync("a.txt", "theirs");
                return null;
            };
            await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
            factory.Intercept = null;
            Assert.AreEqual("theirs", await ReadAsync(provider, "a.txt"), "The concurrent change is kept");
            Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists, "The copy was removed");
        }
    }

    [TestMethod]
    public async Task Move_WhoseSourceGoesAfterTheCopyCompletes()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            var heads = 0;
            factory.Intercept = async r =>
            {
                // The third HEAD of the source is the check before the delete off AWS: another writer deletes it first.
                if (r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.EndsWith("/a.txt", StringComparison.Ordinal) && Interlocked.Increment(ref heads) == 3)
                {
                    using var client = Endpoint.CreateClient();
                    await client.DeleteObjectAsync(Endpoint.Bucket, $"{Root}/a.txt");
                }
                return null;
            };
            await provider.MoveAsync("a.txt", "b.txt");
            factory.Intercept = null;
            Assert.AreEqual("a", await ReadAsync(provider, "b.txt"), "The copy keeps the content");
            Assert.IsFalse((await provider.GetFileInfoAsync("a.txt")).Exists);
        }
    }

    [TestMethod]
    public async Task Move_WhoseCopyIsOverwrittenBeforeTheUndoLeavesIt()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            factory.Intercept = async r =>
            {
                if (r.Method != HttpMethod.Delete || !r.RequestUri!.AbsolutePath.EndsWith("/a.txt", StringComparison.Ordinal))
                    return null;
                // The source's delete is refused, and meanwhile another writer puts its own file where the copy is.
                await PutRawAsync("b.txt", "theirs");
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            };
            var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
            StringAssert.Contains(e.Message, "now holds another write");
            factory.Intercept = null;
            Assert.AreEqual("theirs", await ReadAsync(provider, "b.txt"), "The other write is kept");
            Assert.AreEqual("a", await ReadAsync(provider, "a.txt"));
        }
    }

    [TestMethod]
    public async Task Move_WhoseSourceDeleteIsRefusedIsUndone()
    {
        var (provider, factory) = Intercepted();
        using (provider)
        {
            await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            // What AWS answers when the source changed between the copy and the delete.
            factory.Intercept = r => Task.FromResult(r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath.EndsWith("/a.txt", StringComparison.Ordinal)
                ? Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed")
                : null);
            await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
            factory.Intercept = null;
            Assert.AreEqual("a", await ReadAsync(provider, "a.txt"));
            Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists, "The copy was removed");
        }
    }

    // ----- Buckets -----

    [TestMethod]
    public async Task MissingBucket_IsCreatedOnlyWhenAsked()
    {
        var bucket = "wfp-" + Guid.NewGuid().ToString("N")[..16];
        try
        {
            using (var refusing = CreateProvider(Root, bucket))
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => refusing.UpdateFileAsync("f.txt", TestStreams.Text("x")));
            using var creating = CreateProvider(Root, bucket, createBucketIfMissing: true);
            await creating.UpdateFileAsync("f.txt", TestStreams.Text("x"));
            Assert.AreEqual("x", await ReadAsync(creating, "f.txt"));
            using var again = CreateProvider(Root, bucket, createBucketIfMissing: true);
            Assert.IsTrue((await again.GetFileInfoAsync("f.txt")).Exists);
        }
        finally
        {
            await Endpoint.DeleteBucketAsync(bucket);
        }
    }

    [TestMethod]
    public async Task MissingBucket_DeletedAfterFirstUseIsReportedThenFoundAgain()
    {
        var bucket = await Endpoint.CreateBucketAsync();
        try
        {
            using var provider = CreateProvider(Root, bucket);
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
            await Endpoint.DeleteBucketAsync(bucket);
            // A HEAD answers 404 for a missing bucket as for a missing key: the provider still names the bucket.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReadRangeAsync("f.txt", 0, 0));
            var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReadFileAsync("f.txt"));
            StringAssert.Contains(e.Message, bucket);
            StringAssert.Contains(e.Message, "CreateBucketIfMissing");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("f.txt"));
            using (var client = Endpoint.CreateClient())
                await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("y"));
            Assert.AreEqual("y", await ReadAsync(provider, "f.txt"));
        }
        finally
        {
            await Endpoint.DeleteBucketAsync(bucket);
        }
    }

    // ----- Signed URLs -----

    [TestMethod]
    public async Task SignedUrls_ReadCreateAndWriteForReal()
    {
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("hello"));

        var read = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions
        {
            Validity = TimeSpan.FromMinutes(5),
            DownloadFileName = "report é.csv",
            ContentType = "text/csv",
        });
        Assert.AreEqual(Uri.UriSchemeHttp, read.Url.Scheme, "The URL keeps the endpoint's scheme");
        Assert.AreEqual(SignedUrlKind.Presigned, read.Kind);
        using (var response = await _http.GetAsync(read.Url))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("hello", await response.Content.ReadAsStringAsync());
            Assert.AreEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("report é.csv", response.Content.Headers.ContentDisposition?.FileNameStar);
        }

        var write = await provider.GetSignedUrlAsync("w.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write, ContentType = "text/plain" });
        Assert.AreEqual("PUT", write.Method);
        Assert.AreEqual("text/plain", write.Headers["Content-Type"]);
        Assert.AreEqual(HttpStatusCode.OK, await PutAsync(write, "written"));
        Assert.AreEqual(HttpStatusCode.OK, await PutAsync(write, "again"));
        Assert.AreEqual("again", await ReadAsync(provider, "w.txt"));
        Assert.AreEqual("text/plain", (await provider.GetFileInfoAsync("w.txt")).ContentType);

        var create = await provider.GetSignedUrlAsync("c.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create });
        Assert.AreEqual("*", create.Headers["If-None-Match"]);
        Assert.AreEqual(HttpStatusCode.OK, await PutAsync(create, "first"));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, await PutAsync(create, "second"));
        Assert.AreEqual("first", await ReadAsync(provider, "c.txt"));

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("w.txt",
            new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write, DownloadFileName = "x.txt" }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1) }));
        // SigV4, as far as the SDK's skew-corrected clock allows (the limit itself is pinned offline).
        var days = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(6) });
        StringAssert.Contains(days.Url.Query, "X-Amz-Algorithm=AWS4-HMAC-SHA256");
    }

    [TestMethod]
    public async Task SignedUrls_AreCheckedByTheServer()
    {
        if (!ValidatesSignatures)
            return;
        using var provider = CreateProvider(Root);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("hello"));
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromSeconds(1) });
        var tampered = new Uri(url.Url.ToString()[..^1] + (url.Url.ToString()[^1] == '0' ? "1" : "0"));
        using (var response = await _http.GetAsync(tampered))
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        using (var response = await _http.GetAsync(url.Url))
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "The URL outlived its validity");
        var create = await provider.GetSignedUrlAsync("c.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create });
        using var withoutHeader = new HttpRequestMessage(HttpMethod.Put, create.Url) { Content = new StringContent("x") };
        using (var response = await _http.SendAsync(withoutHeader))
            Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode, "A create URL needs its signed If-None-Match");
    }

    [TestMethod]
    public async Task GivenClient_IsLeftOpenAndItsUrlsMayBeShorter()
    {
        using var client = Endpoint.CreateClient();
        using (var provider = CreateProvider(client, Root))
        {
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) }));
            var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), AllowShorterValidity = true });
            Assert.IsNull(url.ExpiresOn, "The credentials of a given client have no known expiry");
            using var response = await _http.GetAsync(url.Url);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Endpoint.Bucket, Prefix = Root + "/" });
        Assert.AreEqual(1, listed.S3Objects?.Count, "The client still works after the provider is disposed");
    }

    // ----- Helpers -----

    private (IWriteableFileProvider Provider, InterceptingHttpClientFactory Factory) Intercepted()
    {
        var provider = CreateProvider(Root);
        var factory = new InterceptingHttpClientFactory();
        var core = CoreOf(provider);
        core.HttpClientFactory = factory;
        core.PartSize = S3Core.MinimumPartSize;
        return (provider, factory);
    }

    private async Task PutRawAsync(string relativeKey, string text)
    {
        using var client = Endpoint.CreateClient();
        await client.PutObjectAsync(new PutObjectRequest { BucketName = Endpoint.Bucket, Key = $"{Root}/{relativeKey}", ContentBody = text });
    }

    private async Task TryPutRawAsync(string relativeKey)
    {
        try
        {
            await PutRawAsync(relativeKey, "");
        }
        catch (AmazonS3Exception)
        {
            // The server refuses such a key; there is nothing to list then.
        }
    }

    private static async Task<string> ReadAsync(IWriteableFileProvider provider, ProviderPath path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task AssertContentAsync(IWriteableFileProvider provider, ProviderPath path, byte[] expected)
    {
        await using var stream = await provider.ReadFileAsync(path);
        CollectionAssert.AreEqual(SHA256.HashData(expected), await SHA256.HashDataAsync(stream), $"'{path}' differs");
    }

    private static async Task<HttpStatusCode> PutAsync(SignedUrl url, string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url.Url) { Content = new StringContent(text) };
        request.Content.Headers.ContentType = null;
        foreach (var (name, value) in url.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            else
                request.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }
}

/// <summary>A stream that hides its length and cannot seek: content of unknown length.</summary>
/// <param name="inner">The content.</param>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
