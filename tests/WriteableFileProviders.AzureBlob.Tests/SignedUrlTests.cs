using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Web;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// Signed URLs offline, against a handler that plays the blob service: the SAS a shared key signs, the user delegation
/// SAS a token credential signs, and the rules on their lifetimes (F11, F40, D18, D24).
/// </summary>
[TestClass]
public sealed class SignedUrlTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static WriteableAzureBlobFileProvider SharedKeyProvider(StubHandler handler, string scheme = "https", bool createContainer = false)
    {
        var client = new BlobContainerClient(new Uri($"{scheme}://account.blob.core.windows.net/files"), new StorageSharedKeyCredential("account", Azurite.AccountKey), AzureBlobOptionsTests.Offline(handler));
        return new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { CreateContainerIfMissing = createContainer }, client) { Clock = new FixedClock(_now) };
    }

    private static WriteableAzureBlobFileProvider TokenProvider(StubHandler handler, FixedClock? clock = null)
    {
        var client = new BlobContainerClient(new Uri("https://account.blob.core.windows.net/files"), new FakeCredential(), AzureBlobOptionsTests.Offline(handler));
        return new WriteableAzureBlobFileProvider(null, client) { Clock = clock ?? new FixedClock(_now) };
    }

    // The blob service: a file exists, the container exists, and keys are issued for what was asked, if it ends within 7
    // days of the service's clock (the start asked plus the provider's 15 minutes back), as Azure requires.
    private static StubHandler Service(Func<DateTimeOffset, DateTimeOffset>? keyExpiry = null) => new((request, body) =>
    {
        if (StubHandler.IsDelegationKeyRequest(request))
        {
            var (start, expiry) = StubHandler.RequestedKeyTimes(body);
            if (expiry > start + TimeSpan.FromMinutes(15) + TimeSpan.FromDays(7))
                return AzureHandler.Error(HttpStatusCode.BadRequest, "InvalidQueryParameterValue");
            return StubHandler.DelegationKey(start, keyExpiry?.Invoke(expiry) ?? expiry);
        }
        return StubHandler.BlobProperties();
    });

    private static NameValueCollection Query(SignedUrl url) => HttpUtility.ParseQueryString(url.Url.Query);

    private static DateTimeOffset Time(string? value) => DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task ServiceSas_StartsInThePastAndEndsAtTheValidity()
    {
        using var provider = SharedKeyProvider(Service());
        var url = await provider.GetSignedUrlAsync("a/b.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(2) });
        var query = Query(url);
        Assert.AreEqual("r", query["sp"]);
        Assert.AreEqual("b", query["sr"]);
        Assert.AreEqual("https", query["spr"]);
        Assert.AreEqual(_now.AddMinutes(-15), Time(query["st"]), "F40: 15 minutes in the past");
        Assert.AreEqual(_now.AddHours(2), Time(query["se"]), "Never longer than asked");
        Assert.IsNotNull(query["sig"]);
        Assert.IsNull(query["skoid"], "A service SAS, not a user delegation SAS");
        Assert.AreEqual(_now.AddHours(2), url.ExpiresOn);
        Assert.AreEqual("GET", url.Method);
        Assert.AreEqual(0, url.Headers.Count);
        Assert.AreEqual(SignedUrlKind.Presigned, url.Kind);
        Assert.AreEqual("https://account.blob.core.windows.net/files/a/b.txt", url.Url.GetLeftPart(UriPartial.Path));
    }

    [TestMethod]
    public async Task ServiceSas_OverHttpAllowsHttp()
    {
        using var provider = SharedKeyProvider(Service(), "http");
        var url = await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) });
        Assert.IsNull(Query(url)["spr"]);
    }

    [TestMethod]
    [DataRow(SignedUrlAccess.Create, "c")]
    [DataRow(SignedUrlAccess.Write, "w")]
    public async Task UploadSas_IsAPutOfABlockBlob(SignedUrlAccess access, string permission)
    {
        using var provider = SharedKeyProvider(Service());
        var url = await provider.GetSignedUrlAsync("new.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = access });
        Assert.AreEqual(permission, Query(url)["sp"]);
        Assert.AreEqual("PUT", url.Method);
        Assert.AreEqual("BlockBlob", url.Headers["x-ms-blob-type"]);
    }

    [TestMethod]
    public async Task ReadSas_CarriesTheFileNameAndTheContentType()
    {
        using var provider = SharedKeyProvider(Service());
        var url = await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), DownloadFileName = "résumé \"1\".pdf", ContentType = "application/pdf" });
        var query = Query(url);
        Assert.AreEqual("attachment; filename=\"r_sum_ _1_.pdf\"; filename*=utf-8''r%C3%A9sum%C3%A9%20%221%22.pdf", query["rscd"]);
        Assert.AreEqual("application/pdf", query["rsct"]);
    }

    [TestMethod]
    public async Task UploadSas_RefusesContentHeaders()
    {
        using var provider = SharedKeyProvider(Service());
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write, ContentType = "text/plain" }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create, DownloadFileName = "a.txt" }));
    }

    [TestMethod]
    public async Task Validity_IsAtMostSevenDays()
    {
        using var provider = SharedKeyProvider(Service());
        var url = await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(7) });
        Assert.AreEqual(_now.AddDays(7), url.ExpiresOn);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1) }));
    }

    [TestMethod]
    public async Task UploadSas_ChecksTheContainerOnce()
    {
        var handler = Service();
        using var provider = SharedKeyProvider(handler);
        await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write });
        await provider.GetSignedUrlAsync("b.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create });
        Assert.AreEqual(1, handler.Requests.Count(r => r.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UploadSas_IntoAMissingContainer()
    {
        static HttpResponseMessage Missing(HttpRequestMessage request) =>
            request.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.Created) : AzureHandler.Error(HttpStatusCode.NotFound, "ContainerNotFound");
        var options = new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write };

        using var refusing = SharedKeyProvider(new StubHandler(Missing));
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => refusing.GetSignedUrlAsync("a.txt", options));
        StringAssert.Contains(e.Message, "files");
        StringAssert.Contains(e.Message, nameof(AzureFileProviderOptions.CreateContainerIfMissing));

        var handler = new StubHandler(Missing);
        using var creating = SharedKeyProvider(handler, createContainer: true);
        await creating.GetSignedUrlAsync("a.txt", options);
        Assert.IsTrue(handler.Requests.Any(r => r.Method == HttpMethod.Put && r.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal)), "The container is created");
    }

    [TestMethod]
    public async Task UserDelegationSas_IsSignedWithAKeyThatOutlivesIt()
    {
        var handler = Service();
        using var provider = TokenProvider(handler);
        var url = await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(3) });
        var query = Query(url);
        Assert.AreEqual("00000000-0000-0000-0000-000000000001", query["skoid"]);
        Assert.AreEqual("00000000-0000-0000-0000-000000000002", query["sktid"]);
        Assert.AreEqual(_now.AddMinutes(-15), Time(query["st"]));
        Assert.AreEqual(_now.AddHours(3), Time(query["se"]));
        Assert.IsTrue(Time(query["ske"]) >= Time(query["se"]), "The SAS never outlives its key");
        Assert.AreEqual(_now.AddHours(3), url.ExpiresOn);

        var request = handler.Requests.Single(StubHandler.IsDelegationKeyRequest);
        var (start, expiry) = StubHandler.RequestedKeyTimes(handler.Body(request));
        Assert.AreEqual(_now.AddMinutes(-15), start);
        Assert.AreEqual(_now.AddHours(3), expiry);
    }

    [TestMethod]
    public async Task UserDelegationKey_IsReusedWhileItLasts()
    {
        var handler = Service();
        var clock = new FixedClock(_now);
        using var provider = TokenProvider(handler, clock);
        await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) });
        Assert.AreEqual(_now.AddHours(1), StubHandler.RequestedKeyTimes(handler.Body(handler.Requests.Single(StubHandler.IsDelegationKeyRequest))).Expiry, "A key serves the next hour");
        await provider.GetSignedUrlAsync("b.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(30), Permission = SignedUrlAccess.Write });
        Assert.AreEqual(1, handler.Requests.Count(StubHandler.IsDelegationKeyRequest));

        // Azure's keys end within 7 days: the provider asks for 7 days less 5 minutes, and a 7-day URL would outlive it (D18).
        var week = new SignedUrlOptions { Validity = TimeSpan.FromDays(7) };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("c.txt", week));
        Assert.AreEqual(2, handler.Requests.Count(StubHandler.IsDelegationKeyRequest), "A longer URL needs a longer key");
        Assert.AreEqual(_now.AddDays(7).AddMinutes(-5), StubHandler.RequestedKeyTimes(handler.Body(handler.Requests.Last(StubHandler.IsDelegationKeyRequest))).Expiry);
        var url = await provider.GetSignedUrlAsync("c.txt", week with { AllowShorterValidity = true });
        Assert.AreEqual(_now.AddDays(7).AddMinutes(-5), url.ExpiresOn);
        Assert.AreEqual(2, handler.Requests.Count(StubHandler.IsDelegationKeyRequest), "The longest key is reused");
    }

    [TestMethod]
    public async Task UserDelegationKey_BadRequestIsNotRemembered()
    {
        var handler = new StubHandler(request => StubHandler.IsDelegationKeyRequest(request)
            ? AzureHandler.Error(HttpStatusCode.BadRequest, "InvalidQueryParameterValue")
            : StubHandler.BlobProperties());
        using var provider = TokenProvider(handler);
        var options = new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", options));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", options));
        Assert.AreEqual(2, handler.Requests.Count(StubHandler.IsDelegationKeyRequest), "Only a refused credential is remembered");
    }

    [TestMethod]
    public async Task UserDelegationKey_ThatEndsEarlyNeedsAllowShorterValidity()
    {
        var keyEnds = _now.AddMinutes(40);
        using var provider = TokenProvider(Service(_ => keyEnds));
        var options = new SignedUrlOptions { Validity = TimeSpan.FromHours(2) };
        var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", options));
        StringAssert.Contains(e.Message, "user delegation key");

        var url = await provider.GetSignedUrlAsync("a.txt", options with { AllowShorterValidity = true });
        Assert.AreEqual(keyEnds, url.ExpiresOn);
        Assert.AreEqual(keyEnds, Time(Query(url)["se"]));
    }

    [TestMethod]
    public async Task UserDelegationKey_RefusedMeansNotSupported()
    {
        var clock = new FixedClock(_now);
        var handler = new StubHandler(request => StubHandler.IsDelegationKeyRequest(request)
            ? AzureHandler.Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")
            : StubHandler.BlobProperties());
        using var provider = TokenProvider(handler, clock);
        var options = new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) };
        var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", options));
        Assert.IsInstanceOfType<Azure.RequestFailedException>(e.InnerException);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", options));
        Assert.AreEqual(1, handler.Requests.Count(StubHandler.IsDelegationKeyRequest), "The refusal is remembered");
        clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", options));
        Assert.AreEqual(2, handler.Requests.Count(StubHandler.IsDelegationKeyRequest), "Then asked for again");
    }

    [TestMethod]
    public async Task ClientWithoutKeyOrToken_SignsNothing()
    {
        var handler = new StubHandler(request => StubHandler.IsDelegationKeyRequest(request)
            ? AzureHandler.Error(HttpStatusCode.Forbidden, "AuthenticationFailed")
            : StubHandler.BlobProperties());
        var client = new BlobContainerClient(new Uri("https://account.blob.core.windows.net/files?sv=2026-10-06&sp=rwdl&sig=x"), AzureBlobOptionsTests.Offline(handler));
        using var provider = new WriteableAzureBlobFileProvider(null, client);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) }));
    }

    [TestMethod]
    public void AttachmentDisposition_IsAValidHeaderForAnyName()
    {
        foreach (var name in new[] { "a.txt", "quote\".txt", "semi;colon.txt", "crlf\r\nname.txt", "日本.txt" })
        {
            var header = WriteableAzureBlobFileProvider.AttachmentDisposition(name);
            Assert.IsTrue(System.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(header, out var parsed), header);
            Assert.AreEqual(name, parsed!.FileNameStar, header);
            Assert.IsFalse(header.Contains('\r', StringComparison.Ordinal) || header.Contains('\n', StringComparison.Ordinal), header);
        }
    }
}

/// <summary>A clock the test sets.</summary>
/// <param name="now">The time it starts at.</param>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
