using System.Net;
using Amazon;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static WriteableFileProviders.S3.Testing.InterceptingHttpClientFactory;

namespace WriteableFileProviders.S3.Testing;

/// <summary>
/// What the S3 core does that a real server will not provoke, run through each provider over a fake S3 behind the SDK's
/// HTTP layer: the part limit and the one-request boundary, the bucket check and a bucket that goes missing, conditions
/// checked first or not, the version a move deletes, a copy in parts, keys no path can name, and a skewed SDK clock near
/// the 7-day limit.
/// </summary>
public abstract class S3OfflineTests
{
    /// <summary>
    /// Builds this package's provider, with the bucket <c>bucket</c> and the root <c>root</c>, for a server that the
    /// fake answers; it must be one where the SDK may sign with SigV2 past 7 days.
    /// </summary>
    /// <param name="createBucketIfMissing">The option of the same name.</param>
    internal abstract IWriteableFileProvider CreateOffline(bool createBucketIfMissing = false);

    /// <summary>The provider's core.</summary>
    /// <param name="provider">A provider of <see cref="CreateOffline"/>.</param>
    internal abstract S3Core CoreOf(IWriteableFileProvider provider);

    /// <summary>The location constraint a bucket the provider creates is given, or null for none.</summary>
    protected virtual string? ExpectedLocationConstraint => null;

    /// <summary>Whether the provider leaves conditions to the store (AWS), rather than also checking them first.</summary>
    protected virtual bool ChecksAtomically => false;

    private const string ETag = "\"0123456789abcdef\"";

    private static bool IsKey(HttpRequestMessage request, string relativeKey) =>
        request.RequestUri!.AbsolutePath.EndsWith("/root/" + relativeKey, StringComparison.Ordinal);

    private (IWriteableFileProvider Provider, InterceptingHttpClientFactory Factory) Offline(Func<HttpRequestMessage, Task<HttpResponseMessage?>>? answer = null,
        bool createBucketIfMissing = false)
    {
        var provider = CreateOffline(createBucketIfMissing);
        var factory = new InterceptingHttpClientFactory { Intercept = async r => (answer is null ? null : await answer(r)) ?? Existing(r) };
        CoreOf(provider).HttpClientFactory = factory;
        return (provider, factory);
    }

    private static HttpResponseMessage Existing(HttpRequestMessage request)
    {
        var response = Empty();
        if (request.Method == HttpMethod.Head || request.Method == HttpMethod.Put)
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag);
        return response;
    }

    // A key that does not exist: HEAD answers 404, and the folder listing is empty.
    private static HttpResponseMessage? Missing(HttpRequestMessage request, string relativeKey) =>
        request.Method == HttpMethod.Head && IsKey(request, relativeKey) ? Empty(HttpStatusCode.NotFound)
        : IsList(request) ? ListResult([])
        : null;

    [TestMethod]
    public async Task Write_OfMorePartsThanS3TakesFailsAndAborts()
    {
        var (provider, factory) = Offline(r => Task.FromResult(IsInitiate(r) ? InitiateResult() : null));
        using (provider)
        {
            var core = CoreOf(provider);
            core.PartSize = 1024;
            core.MaxPartCount = 2;
            var e = await Assert.ThrowsExactlyAsync<IOException>(() =>
                provider.UpdateFileAsync("f.bin", new NonSeekableStream(new MemoryStream(new byte[5000]))));
            StringAssert.Contains(e.Message, "too large");
            Assert.AreEqual(2, factory.Requests.Count(r => r.Contains("partNumber=", StringComparison.Ordinal)));
            Assert.IsTrue(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("uploadId=upload-1", StringComparison.Ordinal)));
            Assert.IsFalse(factory.Requests.Any(r => r.StartsWith("POST", StringComparison.Ordinal) && r.Contains("uploadId=", StringComparison.Ordinal)), "Never completed");
        }
    }

    [TestMethod]
    public async Task Write_OfExactlyTheLastPartS3TakesCompletes()
    {
        var (provider, factory) = Offline(r => Task.FromResult(IsInitiate(r) ? InitiateResult() : IsComplete(r) ? CompleteResult() : null));
        using (provider)
        {
            var core = CoreOf(provider);
            core.PartSize = 1024;
            core.MaxPartCount = 2;
            await provider.UpdateFileAsync("f.bin", new NonSeekableStream(new MemoryStream(new byte[2048])));
        }
        Assert.AreEqual(2, factory.Requests.Count(r => r.Contains("partNumber=", StringComparison.Ordinal)));
        Assert.IsTrue(factory.Requests.Any(r => r.StartsWith("POST", StringComparison.Ordinal) && r.Contains("uploadId=upload-1", StringComparison.Ordinal)), "Completed");
        Assert.IsFalse(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal)), "Not aborted");
    }

    [TestMethod]
    public async Task Write_OfExactlyOnePartIsOneRequest()
    {
        var (provider, factory) = Offline();
        using (provider)
        {
            CoreOf(provider).PartSize = 1024;
            await provider.UpdateFileAsync("f.bin", new NonSeekableStream(new MemoryStream(new byte[1024])));
            await provider.UpdateFileAsync("g.bin", new MemoryStream(new byte[1024]));
        }
        Assert.IsFalse(factory.Requests.Any(r => r.Contains("uploads", StringComparison.Ordinal)), string.Join("\n", factory.Requests));
        Assert.AreEqual(2, factory.Requests.Count(r => r.StartsWith("PUT", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Write_SendsItsConditionAndChecksFirstOnlyOffAws()
    {
        string? ifNoneMatch = null;
        var (provider, factory) = Offline(r =>
        {
            if (r.Method == HttpMethod.Put && IsKey(r, "f.txt"))
                ifNoneMatch = Header(r, "If-None-Match");
            return Task.FromResult(Missing(r, "f.txt"));
        });
        using (provider)
            await provider.UpdateFileAsync("f.txt", new MemoryStream([1]), new UpdateFileOptions { IfNotExists = true });
        Assert.AreEqual("*", ifNoneMatch, "The condition goes to the store either way");
        Assert.AreEqual(!ChecksAtomically, factory.Requests.Any(r => r.StartsWith("HEAD", StringComparison.Ordinal) && r.Contains("/root/f.txt", StringComparison.Ordinal)),
            "Off AWS the provider checks first");
    }

    [TestMethod]
    public async Task Move_DeletesTheSourceOnlyAtTheVersionCopied()
    {
        string? ifMatch = null;
        var (provider, factory) = Offline(r =>
        {
            if (r.Method == HttpMethod.Delete && IsKey(r, "a.txt"))
                ifMatch = Header(r, "If-Match");
            return Task.FromResult(IsCopy(r) ? CopyResult() : Missing(r, "b.txt"));
        });
        using (provider)
            await provider.MoveAsync("a.txt", "b.txt");
        Assert.AreEqual(ETag, ifMatch);
        // The base checks the source and the copy reads its version; off AWS the move checks it again before deleting.
        Assert.AreEqual(ChecksAtomically ? 2 : 3,
            factory.Requests.Count(r => r.StartsWith("HEAD", StringComparison.Ordinal) && r.Contains("/root/a.txt", StringComparison.Ordinal)));
        Assert.IsFalse(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("/root/b.txt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Move_WhoseSourceChangedRemovesTheCopyAtItsVersion()
    {
        string? ifMatch = null;
        var (provider, factory) = MoveFake(r =>
        {
            if (r.Method == HttpMethod.Delete && IsKey(r, "a.txt"))
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            if (r.Method == HttpMethod.Delete && IsKey(r, "b.txt"))
                ifMatch = Header(r, "If-Match");
            return null;
        });
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
            StringAssert.Contains(e.Message, "undone");
        }
        Assert.AreEqual(CopyETag, ifMatch, "The copy is removed only while it is the copy");
        Assert.IsTrue(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("/root/b.txt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Move_WhoseSourceWentAfterTheCopyCompletes()
    {
        // Off AWS the check before the delete finds the source gone; on AWS the delete answers 404: either way the copy
        // holds the content, and the move is done, as on the other providers.
        var (provider, factory) = MoveFake(r =>
            (r.Method == HttpMethod.Head || r.Method == HttpMethod.Delete) && IsKey(r, "a.txt") ? Error(HttpStatusCode.NotFound, "NoSuchKey") : null);
        using (provider)
            await provider.MoveAsync("a.txt", "b.txt");
        Assert.IsFalse(factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("/root/b.txt", StringComparison.Ordinal)), "The copy is kept");
    }

    [TestMethod]
    public async Task Move_WhoseCopyWasOverwrittenMeanwhileLeavesIt()
    {
        var (provider, factory) = MoveFake(r =>
            r.Method == HttpMethod.Delete && IsKey(r, "a.txt") ? Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed")
            // Another writer has put its own file where the copy was: off AWS the check sees it, on AWS the delete refuses.
            : r.Method == HttpMethod.Head && IsKey(r, "b.txt") ? WithETag(Empty(), "\"theirs\"")
            : r.Method == HttpMethod.Delete && IsKey(r, "b.txt") ? Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed")
            : null);
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("a.txt", "b.txt"));
            StringAssert.Contains(e.Message, "now holds another write");
        }
        Assert.AreEqual(ChecksAtomically, factory.Requests.Any(r => r.StartsWith("DELETE", StringComparison.Ordinal) && r.Contains("/root/b.txt", StringComparison.Ordinal)),
            "Off AWS the other write is seen before any delete");
    }

    [TestMethod]
    public async Task Move_ThatCannotRemoveItsCopyNamesIt()
    {
        var (provider, _) = MoveFake(r =>
            r.Method == HttpMethod.Delete && IsKey(r, "a.txt") ? Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed")
            : r.Method == HttpMethod.Delete && IsKey(r, "b.txt") ? throw new HttpRequestException("Injected network failure")
            : null);
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.MoveAsync("a.txt", "b.txt"));
            StringAssert.Contains(e.Message, "b.txt");
            Assert.IsInstanceOfType<AggregateException>(e.InnerException);
        }
    }

    private const string CopyETag = "\"copy\"";

    // A move from a.txt to b.txt: before the copy, a.txt exists and b.txt does not; after it, b.txt holds the copy.
    // `afterCopy` answers what it wants once the copy is made.
    private (IWriteableFileProvider Provider, InterceptingHttpClientFactory Factory) MoveFake(Func<HttpRequestMessage, HttpResponseMessage?> afterCopy)
    {
        var copied = false;
        return Offline(r =>
        {
            if (IsCopy(r))
            {
                copied = true;
                return Task.FromResult<HttpResponseMessage?>(CopyResult());
            }
            if (!copied)
                return Task.FromResult(Missing(r, "b.txt"));
            return Task.FromResult(afterCopy(r) ?? (r.Method == HttpMethod.Head && IsKey(r, "b.txt") ? WithETag(Empty(), CopyETag) : null));
        });
    }

    private static HttpResponseMessage WithETag(HttpResponseMessage response, string etag)
    {
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    [TestMethod]
    public async Task Copy_InPartsKeepsTheSourcesHeadersAndMetadata()
    {
        var initiate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (provider, factory) = Offline(r =>
        {
            if (r.Method == HttpMethod.Head && IsKey(r, "big.bin"))
            {
                var head = Existing(r);
                head.Content.Headers.ContentLength = 2048;
                head.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-test");
                head.Content.Headers.ContentEncoding.Add("gzip");
                head.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "big.bin" };
                head.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                head.Headers.Add("x-amz-meta-owner", "tests");
                head.Content.Headers.ContentLanguage.Add("hu");
                head.Content.Headers.Expires = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
                return Task.FromResult<HttpResponseMessage?>(head);
            }
            if (IsInitiate(r))
            {
                foreach (var (name, values) in r.Headers.Concat(r.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
                    initiate[name] = string.Join(",", values);
                return Task.FromResult<HttpResponseMessage?>(InitiateResult());
            }
            return Task.FromResult(IsCopyPart(r) ? CopyResult(part: true) : IsComplete(r) ? CompleteResult() : Missing(r, "copy.bin"));
        });
        using (provider)
        {
            var core = CoreOf(provider);
            core.MaxSingleCopySize = 1024;
            core.CopyPartSize = 1024;
            await provider.CopyAsync("big.bin", "copy.bin");
        }
        Assert.AreEqual(2, factory.Requests.Count(r => r.Contains("partNumber=", StringComparison.Ordinal)));
        Assert.AreEqual("application/x-test", initiate.GetValueOrDefault("Content-Type"));
        Assert.AreEqual("gzip", initiate.GetValueOrDefault("Content-Encoding"));
        StringAssert.Contains(initiate.GetValueOrDefault("Content-Disposition"), "big.bin");
        Assert.AreEqual("max-age=3600", initiate.GetValueOrDefault("Cache-Control"));
        Assert.AreEqual("tests", initiate.GetValueOrDefault("x-amz-meta-owner"));
        Assert.AreEqual("hu", initiate.GetValueOrDefault("Content-Language"));
        StringAssert.Contains(initiate.GetValueOrDefault("Expires"), "02 Jan 2030");
    }

    [TestMethod]
    public async Task BucketCheck_ThatFailsOtherwiseIsRetriedAndARefusalMeansItExists()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var checks = 0;
        var (provider, _) = Offline(r =>
        {
            if (!IsBucket(r) || r.Method != HttpMethod.Head)
                return Task.FromResult<HttpResponseMessage?>(null);
            Interlocked.Increment(ref checks);
            return Task.FromResult<HttpResponseMessage?>(Empty(status));
        });
        using (provider)
        {
            // 503 after the SDK's retries says nothing about the bucket: the next call checks again. The SDK's error travels
            // inside the contract's IOException.
            Assert.IsInstanceOfType<AmazonS3Exception>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"))).InnerException);
            var failed = checks;
            Assert.IsTrue(failed > 0);
            status = HttpStatusCode.BadRequest;
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists, "400: the bucket exists, or cannot be asked");
            var recovered = checks;
            Assert.IsTrue(recovered > failed, "Checked again");
            Assert.IsTrue((await provider.GetFileInfoAsync("g.txt")).Exists);
            Assert.AreEqual(recovered, checks, "Then never again");
        }
    }

    [TestMethod]
    public async Task MissingBucket_SinceTheCheckIsReportedEvenWhereOnlyHeadIsAsked()
    {
        var gone = false;
        var (provider, _) = Offline(r => Task.FromResult(gone && r.Method == HttpMethod.Head ? Empty(HttpStatusCode.NotFound) : null));
        using (provider)
        {
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
            gone = true;
            // HEAD answers 404 for a missing key and a missing bucket alike: the provider asks which.
            var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReadRangeAsync("f.txt", 0, 0));
            StringAssert.Contains(e.Message, "set the option CreateBucketIfMissing to let the provider create it");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.CopyAsync("f.txt", "g.txt"));
        }
    }

    [TestMethod]
    public async Task MissingBucket_WithTheOptionSaysTheNextCallCreatesIt()
    {
        var gone = false;
        var (provider, _) = Offline(r => Task.FromResult(gone && r.Method == HttpMethod.Get ? Error(HttpStatusCode.NotFound, "NoSuchBucket") : null),
            createBucketIfMissing: true);
        using (provider)
        {
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
            gone = true;
            var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReadFileAsync("f.txt"));
            StringAssert.Contains(e.Message, "the next call creates it again");
        }
    }

    [TestMethod]
    public async Task Listing_FollowsEveryPage()
    {
        // Keys come in order across pages, as a store lists them; the folder d spans the page boundary.
        var (provider, _) = Offline(r =>
        {
            if (!IsList(r))
                return Task.FromResult<HttpResponseMessage?>(null);
            var second = r.RequestUri!.Query.Contains("continuation-token=page2", StringComparison.Ordinal);
            var oneLevel = r.RequestUri.Query.Contains("delimiter", StringComparison.Ordinal);
            return Task.FromResult<HttpResponseMessage?>((oneLevel, second) switch
            {
                (true, false) => ListResult(["root/a.txt"], ["root/d/"], next: "page2"),
                (true, true) => ListResult(["root/e.txt"], ["root/f/"]),
                (false, false) => ListResult(["root/a.txt", "root/d/x.txt"], next: "page2"),
                (false, true) => ListResult(["root/d/y.txt", "root/e.txt", "root/f/z.txt"]),
            });
        });
        using (provider)
        {
            var level = (await provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { "a.txt", "d", "e.txt", "f" }, level, "One level: both pages");
            var all = new List<string>();
            await foreach (var entry in provider.EnumerateAsync("", recursive: true))
                all.Add(entry.Path.ToString());
            CollectionAssert.AreEqual(new[] { "a.txt", "d", "d/x.txt", "d/y.txt", "e.txt", "f", "f/z.txt" }, all, "Recursive: d once, across the pages");
        }
    }

    [TestMethod]
    public async Task Write_RefusedByTheStoreIsAPreconditionFailure()
    {
        // The check before the write passes; the store refuses it anyway, as when another writer got there in between.
        var (provider, _) = Offline(r => Task.FromResult(
            r.Method == HttpMethod.Put && IsKey(r, "new.txt") ? Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed")
            : r.Method == HttpMethod.Put && IsKey(r, "f.txt") ? Error(HttpStatusCode.NotFound, "NoSuchKey")
            : Missing(r, "new.txt")));
        using (provider)
        {
            var exists = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
                provider.UpdateFileAsync("new.txt", new MemoryStream([1]), new UpdateFileOptions { IfNotExists = true }));
            StringAssert.Contains(exists.Message, "already exists");
            var gone = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() =>
                provider.UpdateFileAsync("f.txt", new MemoryStream([1]), new UpdateFileOptions { IfMatch = ETag }));
            StringAssert.Contains(gone.Message, "no longer");
        }
    }

    [TestMethod]
    public async Task Range_OfLengthZeroOfAMissingFileIsNotFoundWhenTheBucketCannotBeAsked()
    {
        var checkedOnce = false;
        var (provider, _) = Offline(r =>
        {
            if (IsBucket(r) && r.Method == HttpMethod.Head)
            {
                // The first check passes; asked again, the credentials may not ask.
                var answer = checkedOnce ? Empty(HttpStatusCode.Forbidden) : null;
                checkedOnce = true;
                return Task.FromResult(answer);
            }
            return Task.FromResult(Missing(r, "f.txt"));
        });
        using (provider)
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("f.txt", 0, 0));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [TestMethod]
    public async Task MissingBucket_CreatedByAnotherCallerMeanwhileIsUsed()
    {
        string? body = null;
        var creations = 0;
        var (provider, _) = Offline(async r =>
        {
            if (!IsBucket(r))
                return null;
            if (r.Method == HttpMethod.Head)
                return Empty(HttpStatusCode.NotFound);
            Interlocked.Increment(ref creations);
            body = r.Content is null ? "" : await r.Content.ReadAsStringAsync();
            return Error(HttpStatusCode.Conflict, "BucketAlreadyOwnedByYou");
        }, createBucketIfMissing: true);
        using (provider)
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
        Assert.IsNotNull(body, "The bucket was created");
        Assert.AreEqual(ExpectedLocationConstraint is null, !body.Contains("LocationConstraint", StringComparison.Ordinal), body);
        if (ExpectedLocationConstraint is not null)
            StringAssert.Contains(body, $"<LocationConstraint>{ExpectedLocationConstraint}</LocationConstraint>");
        Assert.AreEqual(1, creations);
    }

    [TestMethod]
    public async Task Errors_ARefusalIsUnauthorizedAccess_AnyOtherFailureIOException()
    {
        var (provider, _) = Offline(r => Task.FromResult(
            (r.Method == HttpMethod.Delete || r.Method == HttpMethod.Get) && IsKey(r, "denied.txt") ? Error(HttpStatusCode.Forbidden, "AccessDenied")
            : r.Method == HttpMethod.Get && IsKey(r, "broken.txt") ? Error(HttpStatusCode.BadRequest, "InvalidArgument")
            : null));
        using (provider)
        {
            // A file delete refused is refused as a folder delete is: the store refused the credential or the permission.
            var delete = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("denied.txt"));
            Assert.IsInstanceOfType<AmazonS3Exception>(delete.InnerException);
            Assert.IsInstanceOfType<AmazonS3Exception>((await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.ReadFileAsync("denied.txt"))).InnerException);
            Assert.IsInstanceOfType<AmazonS3Exception>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("broken.txt"))).InnerException);
        }
    }

    [TestMethod]
    public async Task Delete_OfAFolderWhoseKeysAreRefusedIsUnauthorizedAccess()
    {
        const string Refused = """<?xml version="1.0" encoding="UTF-8"?><DeleteResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Error><Key>root/dir/a.txt</Key><Code>AccessDenied</Code><Message>Access Denied</Message></Error></DeleteResult>""";
        const string Failed = """<?xml version="1.0" encoding="UTF-8"?><DeleteResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Error><Key>root/dir/a.txt</Key><Code>InternalError</Code><Message>Try again</Message></Error></DeleteResult>""";
        var answer = Refused;
        var (provider, _) = Offline(r => Task.FromResult(
            IsDeleteObjects(r) ? Xml(HttpStatusCode.OK, answer)
            : IsList(r) ? ListResult(["root/dir/a.txt"])
            : Missing(r, "dir")));
        using (provider)
        {
            var refused = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
            StringAssert.Contains(refused.Message, "AccessDenied");
            answer = Failed;
            StringAssert.Contains((await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("dir"))).Message, "InternalError");
        }
    }

    [TestMethod]
    public async Task CreateBucket_WhoseNameAnotherAccountOwns_IsInvalidOperation()
    {
        var (provider, _) = Offline(r => Task.FromResult(!IsBucket(r) ? null
            : r.Method == HttpMethod.Head ? Empty(HttpStatusCode.NotFound)
            : Error(HttpStatusCode.Conflict, "BucketAlreadyExists")), createBucketIfMissing: true);
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("f.txt"));
            StringAssert.Contains(e.Message, "another account owns the name");
            Assert.IsInstanceOfType<AmazonS3Exception>(e.InnerException);
        }
    }

    [TestMethod]
    public async Task Listing_SkipsKeysNoPathCanName()
    {
        // A marker, names that decode to "..", hold a backslash or a control character (a tab), and an empty folder name.
        var (provider, _) = Offline(r => Task.FromResult(IsList(r)
            ? ListResult(["root/", "root/a.txt", "root/%2e%2e", @"root/a\b.txt", "root/x&#x9;y"], ["root/dir/", "root//", "root/%252e%252e/"])
            : null));
        using (provider)
        {
            var names = (await provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { "a.txt", "dir" }, names);
        }
    }

    [TestMethod]
    public async Task Presign_ASkewedSdkClockNeverFallsBackToSigV2()
    {
        // The SDK measures expiry on its clock, corrected for the server's skew: behind ours, 7 days exceed SigV4's
        // limit, and for a custom endpoint the SDK would sign with SigV2 instead of failing.
        var saved = AWSConfigs.ManualClockCorrection;
        try
        {
            AWSConfigs.ManualClockCorrection = TimeSpan.FromMinutes(-10);
            var (provider, _) = Offline();
            using (provider)
            {
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                    provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(7), Permission = SignedUrlAccess.Write }));
                var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(6), Permission = SignedUrlAccess.Write });
                StringAssert.Contains(url.Url.Query, "X-Amz-Signature=");
            }
        }
        finally
        {
            AWSConfigs.ManualClockCorrection = saved;
        }
    }
}
