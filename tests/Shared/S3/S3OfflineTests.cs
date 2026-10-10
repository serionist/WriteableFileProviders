using System.Net;
using Amazon;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static WriteableFileProviders.S3.Testing.InterceptingHttpClientFactory;

namespace WriteableFileProviders.S3.Testing;

/// <summary>
/// What the S3 core does that a real server will not provoke, run through each provider over a fake S3 behind the SDK's
/// HTTP layer: the part limit, a bucket created by someone else meanwhile, names a provider refuses, and a skewed SDK
/// clock near the 7-day limit.
/// </summary>
public abstract class S3OfflineTests
{
    /// <summary>
    /// Builds this package's provider, with the bucket <c>bucket</c> and the root <c>root</c>, for a server that the
    /// fake answers; it must be one where the SDK may sign with SigV2 past 7 days.
    /// </summary>
    /// <param name="createBucketIfMissing">The option of the same name.</param>
    /// <param name="refuseSecrets">Whether the provider refuses segments that start with "secret".</param>
    internal abstract IWriteableFileProvider CreateOffline(bool createBucketIfMissing = false, bool refuseSecrets = false);

    /// <summary>The provider's core.</summary>
    /// <param name="provider">A provider of <see cref="CreateOffline"/>.</param>
    internal abstract S3Core CoreOf(IWriteableFileProvider provider);

    /// <summary>The location constraint a bucket the provider creates is given, or null for none.</summary>
    protected virtual string? ExpectedLocationConstraint => null;

    private (IWriteableFileProvider Provider, InterceptingHttpClientFactory Factory) Offline(Func<HttpRequestMessage, Task<HttpResponseMessage?>>? answer = null,
        bool createBucketIfMissing = false, bool refuseSecrets = false)
    {
        var provider = CreateOffline(createBucketIfMissing, refuseSecrets);
        var factory = new InterceptingHttpClientFactory { Intercept = async r => (answer is null ? null : await answer(r)) ?? Existing(r) };
        CoreOf(provider).HttpClientFactory = factory;
        return (provider, factory);
    }

    private static HttpResponseMessage Existing(HttpRequestMessage request)
    {
        var response = Empty();
        if (request.Method == HttpMethod.Head)
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0123456789abcdef\"");
        return response;
    }

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
    public async Task Listing_SkipsNamesTheProviderRefuses()
    {
        var (provider, _) = Offline(r => Task.FromResult(IsList(r)
            ? ListResult(["root/a.txt", "root/secret.txt"], ["root/dir/", "root/secrets/"])
            : null), refuseSecrets: true);
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
