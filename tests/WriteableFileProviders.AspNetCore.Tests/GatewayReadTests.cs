using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>GET and HEAD through the gateway: the body, HTTP ranges, and the response headers.</summary>
[TestClass]
public sealed class GatewayReadTests
{
    private GatewayHost _host = null!;

    [TestInitialize]
    public async Task Start() => _host = await GatewayHost.StartAsync();

    [TestCleanup]
    public async Task Stop() => await _host.DisposeAsync();

    [TestMethod]
    public async Task Get_ServesTheFile_WithTheSecurityHeaders()
    {
        await _host.WriteAsync("docs/report.txt", "hello gateway");
        var url = await _host.UrlAsync("docs/report.txt", contentType: "text/plain");

        using var response = await _host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("hello gateway", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("default-src 'none'; sandbox", Single(response, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.AreEqual("no-referrer", Single(response, "Referrer-Policy"));
        Assert.IsTrue(response.Headers.CacheControl is { Private: true, NoStore: true });
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual("report.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.AreEqual("bytes", response.Headers.AcceptRanges.Single());
        Assert.IsNotNull(response.Headers.ETag);
        Assert.IsNotNull(response.Content.Headers.LastModified);
    }

    [TestMethod]
    public async Task Get_Range_ServesPartialContent()
    {
        await _host.WriteAsync("big.txt", "0123456789abcdef");
        var url = await _host.UrlAsync("big.txt");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(4, 9);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("456789", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(4, response.Content.Headers.ContentRange?.From);
        Assert.AreEqual(9, response.Content.Headers.ContentRange?.To);
        Assert.AreEqual(16, response.Content.Headers.ContentRange?.Length);
    }

    [TestMethod]
    public async Task Get_SuffixRange_ServesTheEnd()
    {
        await _host.WriteAsync("big.txt", "0123456789abcdef");
        var url = await _host.UrlAsync("big.txt");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(null, 3);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("def", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Get_UnsatisfiableRange_Answers416()
    {
        await _host.WriteAsync("small.txt", "abc");
        var url = await _host.UrlAsync("small.txt");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(10, 20);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [TestMethod]
    public async Task Get_IfRangeWithAnOldETag_ServesTheWholeFile()
    {
        await _host.WriteAsync("doc.txt", "first version");
        var url = await _host.UrlAsync("doc.txt");
        string oldETag;
        using (var first = await _host.Client.GetAsync(url))
            oldETag = first.Headers.ETag!.Tag;
        await _host.WriteAsync("doc.txt", "second version");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 4);
        request.Headers.IfRange = new RangeConditionHeaderValue(oldETag);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("second version", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Get_IfNoneMatchTheETag_Answers304()
    {
        await _host.WriteAsync("doc.txt", "content");
        var url = await _host.UrlAsync("doc.txt");
        EntityTagHeaderValue etag;
        using (var first = await _host.Client.GetAsync(url))
            etag = first.Headers.ETag!;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(etag);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
    }

    [TestMethod]
    public async Task Head_SendsTheHeaders_WithoutABody()
    {
        await _host.WriteAsync("doc.txt", "twelve bytes");
        var url = await _host.UrlAsync("doc.txt");

        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(12, response.Content.Headers.ContentLength);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.AreEqual("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual("bytes", response.Headers.AcceptRanges.Single());
    }

    [TestMethod]
    public async Task Get_EditedFileNameSegment_LeavesTheHeaderUnchanged()
    {
        await _host.WriteAsync("doc.txt", "content");
        var url = await _host.UrlAsync("doc.txt", fileName: "invoice.pdf");
        Assert.AreEqual("invoice.pdf", url.Segments[^1]);
        var edited = new Uri(url, "evil.html");
        var bare = new Uri(url.GetLeftPart(UriPartial.Path)[..^"/invoice.pdf".Length]);

        foreach (var target in new[] { edited, bare })
        {
            using var response = await _host.Client.GetAsync(target);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, target.ToString());
            Assert.AreEqual("invoice.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.AreEqual("invoice.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }
    }

    [TestMethod]
    [DataRow("quote\"name.txt")]
    [DataRow("semi;colon.txt")]
    [DataRow("line\r\nbreak.txt")]
    [DataRow("back\\slash.txt")]
    [DataRow("naïve café.txt")]
    [DataRow("/slash/and/..")]
    public async Task Get_FileNameWithSpecialCharacters_YieldsAValidHeader(string fileName)
    {
        await _host.WriteAsync("doc.txt", "content");
        var url = await _host.UrlAsync("doc.txt", fileName: fileName);

        using var response = await _host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Content.Headers.TryGetValues("Content-Disposition", out var values));
        var header = values.Single();
        Assert.IsFalse(header.Any(char.IsControl), header);
        // The header parses back, and its UTF-8 form carries the exact name.
        var parsed = ContentDispositionHeaderValue.Parse(header);
        Assert.AreEqual("attachment", parsed.DispositionType);
        Assert.AreEqual(fileName, parsed.FileNameStar);
        Assert.IsTrue(parsed.FileName is { Length: > 0 }, header);
    }

    [TestMethod]
    public async Task Get_ContentType_IsTheReadTokensOnly_NeverTheStoredOne()
    {
        await _host.WriteAsync("typed.bin", "x", contentType: "image/png");

        using (var fromToken = await _host.Client.GetAsync(await _host.UrlAsync("typed.bin", contentType: "application/pdf")))
            Assert.AreEqual("application/pdf", fromToken.Content.Headers.ContentType?.MediaType);
        using var untyped = await _host.Client.GetAsync(await _host.UrlAsync("typed.bin"));
        Assert.AreEqual("application/octet-stream", untyped.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Get_AScriptAnUploaderTyped_IsServedInert()
    {
        // An untyped upload URL lets its holder choose the stored type; an untyped read URL never serves it back.
        var upload = await _host.UrlAsync("x.js", SignedUrlAccess.Write);
        using (var put = await _host.PutAsync(upload, "alert(1)", "text/javascript"))
            Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
        Assert.AreEqual("text/javascript", (await _host.Provider().GetFileInfoAsync("x.js")).ContentType);

        using var response = await _host.Client.GetAsync(await _host.UrlAsync("x.js"));

        Assert.AreEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("default-src 'none'; sandbox", Single(response, "Content-Security-Policy"));
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [TestMethod]
    public async Task Get_FileDeletedAfterTheUrlWasIssued_Answers404()
    {
        await _host.WriteAsync("gone.txt", "x");
        var url = await _host.UrlAsync("gone.txt");
        await _host.Provider().DeleteAsync("gone.txt");

        using var response = await _host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("nosniff", Single(response, "X-Content-Type-Options"));
    }

    [TestMethod]
    public async Task Get_PathReplacedByAFolder_Answers404()
    {
        await _host.WriteAsync("thing", "x");
        var url = await _host.UrlAsync("thing");
        await _host.Provider().DeleteAsync("thing");
        await _host.WriteAsync("thing/inside.txt", "x");

        using var response = await _host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Get_EmptyFile_ServesNoBytes()
    {
        await _host.WriteAsync("empty.txt", "");

        using var response = await _host.Client.GetAsync(await _host.UrlAsync("empty.txt"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    public async Task Get_ProviderThatCannotPin_ServesTheWholeFile_WithoutRanges()
    {
        await using var host = await GatewayHost.StartAsync(canPin: false);
        await host.WriteAsync("doc.txt", "0123456789");
        var url = await host.UrlAsync("doc.txt");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(2, 4);
        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0123456789", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, response.Headers.AcceptRanges.Count);
        Assert.IsNull(response.Headers.ETag);
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual(10, response.Content.Headers.ContentLength);
        CollectionAssert.AreEqual(new (long, long?)[] { (0, 10) }, host.Files.RangeReads.ToArray());
        Assert.IsTrue(await host.Files.AllStreamsDisposedAsync());
    }

    [TestMethod]
    public async Task Head_ProviderThatCannotPin_ReadsNothing()
    {
        await using var host = await GatewayHost.StartAsync(canPin: false);
        await host.WriteAsync("doc.txt", "0123456789");

        using var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, await host.UrlAsync("doc.txt")));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(10, response.Content.Headers.ContentLength);
        Assert.AreEqual(0, host.Files.RangeReads.Count + host.Files.FileReads);
    }

    [TestMethod]
    public async Task Get_ProviderThatCannotPin_MissingFile_Answers404()
    {
        await using var host = await GatewayHost.StartAsync(canPin: false);
        await host.WriteAsync("doc.txt", "x");
        var url = await host.UrlAsync("doc.txt");
        await host.Provider().DeleteAsync("doc.txt");

        using var response = await host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Get_WholeFile_IsOneReadOfTheStore_AndEveryStreamIsDisposed()
    {
        var data = Testing.TestStreams.Bytes(3 * 1024 * 1024 + 17);
        await _host.Provider().UpdateFileAsync("large.bin", new MemoryStream(data));

        using var response = await _host.Client.GetAsync(await _host.UrlAsync("large.bin"));

        CollectionAssert.AreEqual(data, await response.Content.ReadAsByteArrayAsync());
        // The 1 MiB seekable stream this replaced fetched one range per MiB: four here, a hundred for 100 MiB.
        CollectionAssert.AreEqual(new (long, long?)[] { (0, data.Length) }, _host.Files.RangeReads.ToArray());
        Assert.IsTrue(await _host.Files.AllStreamsDisposedAsync());
    }

    [TestMethod]
    [DataRow(1_000_000L, 1_999_999L, 1_000_000L, 1_000_000L)]
    [DataRow(2_000_000L, null, 2_000_000L, 1_145_728L)]
    [DataRow(null, 500L, 3_145_228L, 500L)]
    public async Task Get_Range_IsOneReadOfTheStore_ForJustThatSpan(long? from, long? to, long offset, long length)
    {
        await _host.Provider().UpdateFileAsync("large.bin", new MemoryStream(Testing.TestStreams.Bytes(3 * 1024 * 1024)));

        using var request = new HttpRequestMessage(HttpMethod.Get, await _host.UrlAsync("large.bin"));
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual(length, (await response.Content.ReadAsByteArrayAsync()).Length);
        CollectionAssert.AreEqual(new (long, long?)[] { (offset, length) }, _host.Files.RangeReads.ToArray());
        Assert.IsTrue(await _host.Files.AllStreamsDisposedAsync());
    }

    [TestMethod]
    public async Task HeadAnd304AndIfRangeMismatch_ReadOnlyWhatTheyServe()
    {
        await _host.WriteAsync("doc.txt", "0123456789");
        var url = await _host.UrlAsync("doc.txt");

        using (var head = await _host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url)))
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(0, _host.Files.RangeReads.Count);

        EntityTagHeaderValue etag;
        using (var first = await _host.Client.GetAsync(url))
            etag = first.Headers.ETag!;
        using (var conditional = new HttpRequestMessage(HttpMethod.Get, url))
        {
            conditional.Headers.IfNoneMatch.Add(etag);
            using var notModified = await _host.Client.SendAsync(conditional);
            Assert.AreEqual(HttpStatusCode.NotModified, notModified.StatusCode);
        }
        Assert.AreEqual(1, _host.Files.RangeReads.Count);

        // A range whose If-Range no longer matches is served whole, in one read of the whole file.
        using (var stale = new HttpRequestMessage(HttpMethod.Get, url))
        {
            stale.Headers.Range = new RangeHeaderValue(2, 4);
            stale.Headers.IfRange = new RangeConditionHeaderValue("\"stale\"");
            using var whole = await _host.Client.SendAsync(stale);
            Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
            Assert.AreEqual("0123456789", await whole.Content.ReadAsStringAsync());
        }
        Assert.AreEqual((0L, (long?)10L), _host.Files.RangeReads.Last());

        // One whose If-Range matches reads just its span.
        using (var current = new HttpRequestMessage(HttpMethod.Get, url))
        {
            current.Headers.Range = new RangeHeaderValue(2, 4);
            current.Headers.IfRange = new RangeConditionHeaderValue(etag);
            using var partial = await _host.Client.SendAsync(current);
            Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.AreEqual("234", await partial.Content.ReadAsStringAsync());
        }
        Assert.AreEqual((2L, (long?)3L), _host.Files.RangeReads.Last());
        Assert.IsTrue(await _host.Files.AllStreamsDisposedAsync());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Get_FileGoneBeforeItIsRead_Answers404(bool canPin)
    {
        await using var host = await GatewayHost.StartAsync(canPin: canPin);
        await host.WriteAsync("doc.txt", "x");
        var url = await host.UrlAsync("doc.txt");
        host.Files.AfterGetFileInfo = async () => await host.Provider().DeleteAsync("doc.txt");

        using var response = await host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.AreEqual("default-src 'none'; sandbox", Single(response, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.IsNull(response.Content.Headers.ContentDisposition);
        Assert.IsTrue(await host.Files.AllStreamsDisposedAsync());
    }

    [TestMethod]
    public async Task Get_TokenWithoutAFileName_NamesTheDownloadAfterThePath()
    {
        await _host.WriteAsync("folder/named.txt", "x");
        var codec = _host.Services.GetRequiredService<GatewayTokenCodec>();
        var token = codec.Protect("files", new GatewayTokenPayload { Path = ["folder", "named.txt"], Access = SignedUrlAccess.Read, Id = "id" }, DateTimeOffset.UtcNow.AddMinutes(1));

        using var response = await _host.Client.GetAsync(GatewayHost.GatewayUrl(token));

        Assert.AreEqual("named.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [TestMethod]
    public async Task Get_LargeFile_StreamsEveryByte()
    {
        var data = Testing.TestStreams.Bytes(3 * 1024 * 1024 + 17);
        await _host.Provider().UpdateFileAsync("large.bin", new MemoryStream(data));

        using var response = await _host.Client.GetAsync(await _host.UrlAsync("large.bin"));
        CollectionAssert.AreEqual(data, await response.Content.ReadAsByteArrayAsync());

        using var request = new HttpRequestMessage(HttpMethod.Get, await _host.UrlAsync("large.bin"));
        request.Headers.Range = new RangeHeaderValue(2 * 1024 * 1024, 2 * 1024 * 1024 + 9);
        using var ranged = await _host.Client.SendAsync(request);
        CollectionAssert.AreEqual(data[(2 * 1024 * 1024)..(2 * 1024 * 1024 + 10)], await ranged.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task Get_StoreReportsTheFileMissing_Answers404_WithoutReading()
    {
        await _host.WriteAsync("doc.txt", "x");
        var url = await _host.UrlAsync("doc.txt");
        _host.Files.ReportMissing = true;

        using var response = await _host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(0, _host.Files.RangeReads.Count + _host.Files.FileReads);
    }

    [TestMethod]
    public async Task Get_StoreFails_FailsTheRequest_WithoutReading()
    {
        await _host.WriteAsync("doc.txt", "x");
        var url = await _host.UrlAsync("doc.txt");
        _host.Files.GetFileInfoFailure = new IOException("The store is down.");

        // TestServer hands an unhandled app exception to the client; a real server answers 500.
        var failure = await Assert.ThrowsAsync<Exception>(() => _host.Client.GetAsync(url));
        Assert.IsTrue(failure is IOException || failure.InnerException is IOException, failure.ToString());
        Assert.AreEqual(0, _host.Files.RangeReads.Count + _host.Files.FileReads);
    }

    [TestMethod]
    public async Task Get_FileChangedBeforeItIsRead_FailsRatherThanServingAnotherVersion()
    {
        await _host.WriteAsync("doc.txt", "first version");
        var url = await _host.UrlAsync("doc.txt");
        _host.Files.AfterGetFileInfo = () => _host.WriteAsync("doc.txt", "second, longer version");

        // TestServer hands the failure to the client; a real server aborts the response.
        var failure = await Assert.ThrowsAsync<Exception>(() => _host.Client.GetAsync(url));

        Assert.IsTrue(failure is PreconditionFailedException || failure.InnerException is PreconditionFailedException, failure.ToString());
        Assert.IsTrue(await _host.Files.AllStreamsDisposedAsync());
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("abc", "\"abc\"")]
    [DataRow("\"0x8DCB\"", "\"0x8DCB\"")]
    [DataRow("W/\"weak\"", "W/\"weak\"")]
    [DataRow("in\"side", null)]
    public void EntityTag_QuotesAStoresETag(string? etag, string? expected) =>
        Assert.AreEqual(expected, GatewayRequestHandler.EntityTag(etag)?.ToString());

    private static string Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) ? values.Single() : "";
}
