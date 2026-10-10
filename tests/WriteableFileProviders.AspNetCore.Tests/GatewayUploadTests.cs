using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>PUT through the gateway: Create and Write, one-time tokens, the size cap and the content type.</summary>
[TestClass]
public sealed class GatewayUploadTests
{
    private GatewayHost _host = null!;

    [TestInitialize]
    public async Task Start() => _host = await GatewayHost.StartAsync(options => options.MaxUploadBytes = 1024);

    [TestCleanup]
    public async Task Stop() => await _host.DisposeAsync();

    [TestMethod]
    public async Task Create_StoresTheFile_AndAnswers201()
    {
        var url = await _host.UrlAsync("in/new.txt", SignedUrlAccess.Create);

        using var response = await _host.PutAsync(url, "fresh");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("fresh", await _host.ReadAsync("in/new.txt"));
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.AreEqual("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [TestMethod]
    public async Task Create_WhenTheFileExists_Answers412_AndLeavesIt()
    {
        await _host.WriteAsync("taken.txt", "first");
        var url = await _host.UrlAsync("taken.txt", SignedUrlAccess.Create);

        using var response = await _host.PutAsync(url, "second");

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.AreEqual("first", await _host.ReadAsync("taken.txt"));
    }

    [TestMethod]
    public async Task Write_ReplacesTheFile_AndAnswers200()
    {
        await _host.WriteAsync("doc.txt", "old");
        var url = await _host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using var response = await _host.PutAsync(url, "new");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("new", await _host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task OneTimeToken_WorksOnce()
    {
        var url = await _host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using (var first = await _host.PutAsync(url, "first"))
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using var second = await _host.PutAsync(url, "second");

        Assert.AreEqual(HttpStatusCode.NotFound, second.StatusCode);
        Assert.AreEqual("first", await _host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task OneTimeToken_UnderConcurrentPuts_IsConsumedExactlyOnce()
    {
        var url = await _host.UrlAsync("race.txt", SignedUrlAccess.Write);

        var statuses = await PutConcurrentlyAsync(_host, url, 32);

        Assert.AreEqual(1, statuses.Count(s => s == HttpStatusCode.OK), string.Join(", ", statuses));
        Assert.AreEqual(31, statuses.Count(s => s == HttpStatusCode.NotFound), string.Join(", ", statuses));
    }

    [TestMethod]
    public async Task OneTimeToken_WithTheDistributedCacheStore_UnderConcurrentPuts_IsConsumedExactlyOnce()
    {
        await using var host = await GatewayHost.StartAsync(configureBuilder: builder =>
        {
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton<IGatewayReplayStore, DistributedCacheGatewayReplayStore>();
        });
        Assert.IsInstanceOfType<DistributedCacheGatewayReplayStore>(host.Services.GetRequiredService<IGatewayReplayStore>());
        var url = await host.UrlAsync("race.txt", SignedUrlAccess.Create);

        var statuses = await PutConcurrentlyAsync(host, url, 16);

        Assert.AreEqual(1, statuses.Count(s => s == HttpStatusCode.Created), string.Join(", ", statuses));
        Assert.AreEqual(15, statuses.Count(s => s == HttpStatusCode.NotFound), string.Join(", ", statuses));
    }

    [TestMethod]
    public async Task OneTimeTokensOff_TokenWorksUntilItExpires()
    {
        await using var host = await GatewayHost.StartAsync(options => options.OneTimeUploadTokens = false);
        var url = await host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using (var first = await host.PutAsync(url, "first"))
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using var second = await host.PutAsync(url, "second");

        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("second", await host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task Upload_AtTheLimit_IsStored()
    {
        var url = await _host.UrlAsync("limit.bin", SignedUrlAccess.Create);

        using var response = await _host.Client.PutAsync(url, new ByteArrayContent(new byte[1024]));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(1024, (await _host.Provider().GetFileInfoAsync("limit.bin")).Length);
    }

    [TestMethod]
    public async Task Upload_DeclaredOverTheLimit_Answers413_BeforeTheTokenIsUsed()
    {
        var url = await _host.UrlAsync("big.bin", SignedUrlAccess.Create);

        using (var tooBig = await _host.Client.PutAsync(url, new ByteArrayContent(new byte[1025])))
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.IsFalse((await _host.Provider().GetFileInfoAsync("big.bin")).Exists);

        // The refusal did not use the one-time token up.
        using var retry = await _host.Client.PutAsync(url, new ByteArrayContent(new byte[10]));
        Assert.AreEqual(HttpStatusCode.Created, retry.StatusCode);
    }

    [TestMethod]
    public async Task Upload_StreamedOverTheLimit_Answers413_AndLeavesTheOldContent()
    {
        await _host.WriteAsync("doc.txt", "old");
        var url = await _host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        // No Content-Length: the body is cut off as it is read.
        using var content = new StreamContent(new UnknownLengthStream(new byte[4096]));
        using var response = await _host.Client.PutAsync(url, content);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.AreEqual("old", await _host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task Upload_RaisesTheServersBodyLimit_ToMaxUploadBytes()
    {
        var feature = new BodySizeFeature { MaxRequestBodySize = 30_000_000 };
        await using var host = await GatewayHost.StartAsync(
            options => options.MaxUploadBytes = 12345,
            configureApp: app => app.Use((context, next) =>
            {
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
                return next(context);
            }));
        var url = await host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using var response = await host.PutAsync(url, "x");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(12345, feature.MaxRequestBodySize);
    }

    [TestMethod]
    public async Task Upload_TheServerRefusesAsTooLarge_Answers413()
    {
        await using var host = await GatewayHost.StartAsync(configureApp: app => app.Use((context, next) =>
        {
            context.Request.Body = new ThrowingStream(new BadHttpRequestException("Too large.", StatusCodes.Status413PayloadTooLarge));
            return next(context);
        }));
        await host.WriteAsync("doc.txt", "old");
        var url = await host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using var response = await host.PutAsync(url, "x");

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.AreEqual("old", await host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task Upload_WithTheTokensContentType_StoresIt()
    {
        var url = await _host.UrlAsync("page.html", SignedUrlAccess.Create, contentType: "text/html");

        using var response = await _host.PutAsync(url, "<p>", "TEXT/HTML");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("text/html", (await _host.Provider().GetFileInfoAsync("page.html")).ContentType);
    }

    [TestMethod]
    [DataRow("text/plain")]
    [DataRow("text/html; charset=utf-8")]
    [DataRow(null)]
    public async Task Upload_WithADifferentContentType_Answers415_BeforeTheTokenIsUsed(string? sent)
    {
        var url = await _host.UrlAsync("page.html", SignedUrlAccess.Create, contentType: "text/html");

        using (var refused = await _host.PutAsync(url, "<p>", sent))
            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
        Assert.IsFalse((await _host.Provider().GetFileInfoAsync("page.html")).Exists);

        using var retry = await _host.PutAsync(url, "<p>", "text/html");
        Assert.AreEqual(HttpStatusCode.Created, retry.StatusCode);
    }

    [TestMethod]
    public async Task Upload_TokenWithoutContentType_StoresTheRequests()
    {
        var typed = await _host.UrlAsync("typed.json", SignedUrlAccess.Write);
        var untyped = await _host.UrlAsync("untyped.bin", SignedUrlAccess.Write);

        using (var response = await _host.PutAsync(typed, "{}", "application/json"))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await _host.PutAsync(untyped, "x"))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.AreEqual("application/json", (await _host.Provider().GetFileInfoAsync("typed.json")).ContentType);
        Assert.IsNull((await _host.Provider().GetFileInfoAsync("untyped.bin")).ContentType);
    }

    [TestMethod]
    public async Task Upload_TokenWithoutContentType_InvalidRequestContentType_Answers415()
    {
        var url = await _host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using var response = await _host.PutAsync(url, "x", "not a media type");

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [TestMethod]
    public void BadRequestIn_FindsTheServersRefusal_InsideWrappers()
    {
        var bad = new BadHttpRequestException("Too large.", StatusCodes.Status413PayloadTooLarge);

        Assert.AreSame(bad, GatewayRequestHandler.BadRequestIn(bad));
        Assert.AreSame(bad, GatewayRequestHandler.BadRequestIn(new IOException("wrapped", bad)));
        Assert.AreSame(bad, GatewayRequestHandler.BadRequestIn(new AggregateException(new IOException("other"), new InvalidOperationException("x", bad))));
        Assert.IsNull(GatewayRequestHandler.BadRequestIn(new AggregateException(new IOException("other"))));
        Assert.IsNull(GatewayRequestHandler.BadRequestIn(new IOException("other")));
        Assert.IsNull(GatewayRequestHandler.BadRequestIn(null));
    }

    private static async Task<HttpStatusCode[]> PutConcurrentlyAsync(GatewayHost host, Uri url, int count)
    {
        using var start = new ManualResetEventSlim();
        var puts = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            start.Wait();
            using var response = await host.PutAsync(url, "writer " + i);
            return response.StatusCode;
        })).ToArray();
        start.Set();
        return await Task.WhenAll(puts);
    }

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }

    /// <summary>A readable stream that hides its length, so the client sends it chunked.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A request body that fails as the server does when a body is too large.</summary>
    private sealed class ThrowingStream(Exception failure) : MemoryStream(Encoding.UTF8.GetBytes("ignored"))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);
    }
}
