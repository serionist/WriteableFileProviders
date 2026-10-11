using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>The URL service: the provider's native URL first, the gateway when the provider cannot sign one.</summary>
[TestClass]
public sealed class UrlServiceTests
{
    private GatewayHost _host = null!;

    [TestInitialize]
    public async Task Start()
    {
        _host = await GatewayHost.StartAsync();
        await _host.WriteAsync("doc.txt", "content");
        await _host.WriteAsync("doc.txt", "native content", provider: "native");
    }

    [TestCleanup]
    public async Task Stop() => await _host.DisposeAsync();

    [TestMethod]
    public async Task ProviderWithNativeUrls_GetsItsOwnUrl()
    {
        var url = await _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)), "native");

        Assert.AreEqual(SignedUrlKind.Presigned, url.Kind);
        Assert.AreEqual("memory", url.Url.Scheme);
    }

    [TestMethod]
    public async Task ProviderWithoutNativeUrls_GetsAGatewayUrl()
    {
        var before = DateTimeOffset.UtcNow;
        var url = await _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)), "files");

        Assert.AreEqual(SignedUrlKind.Gateway, url.Kind);
        Assert.AreEqual("GET", url.Method);
        Assert.AreEqual("https://files.example.com/files/", url.Url.GetLeftPart(UriPartial.Path)[..32]);
        Assert.AreEqual("doc.txt", url.Url.Segments[^1]);
        Assert.AreEqual(0, url.Headers.Count);
        Assert.IsTrue(url.ExpiresOn >= before.AddMinutes(5) && url.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.AreEqual(1, ((DelegatingFileProvider)_host.Provider()).SignedUrlCalls);
    }

    [TestMethod]
    public async Task DefaultProvider_GetsAGatewayUrl_ThatTheGatewayServesFromIt()
    {
        await _host.WriteAsync("doc.txt", "default content", provider: null);

        var url = await _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)));

        Assert.AreEqual(SignedUrlKind.Gateway, url.Kind);
        Assert.AreEqual("", GatewayHost.TokenOf(url.Url).Split('.')[1]);
        Assert.AreEqual(1, ((DelegatingFileProvider)_host.Provider(null)).SignedUrlCalls);
        using var response = await _host.Client.GetAsync(url.Url);
        Assert.AreEqual("default content", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DefaultProvider_UploadUrl_WritesToIt()
    {
        var url = await _host.Urls.GetSignedUrlAsync("up/new.txt", Options(TimeSpan.FromMinutes(5), SignedUrlAccess.Create));

        using var response = await _host.PutAsync(url.Url, "uploaded");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("uploaded", await _host.ReadAsync("up/new.txt", provider: null));
        Assert.IsFalse((await _host.Provider().GetFileInfoAsync("up/new.txt")).Exists);
    }

    [TestMethod]
    public async Task DefaultProviderWithNativeUrls_GetsItsOwnUrl_AndTheLastRegisteredIsTheDefault()
    {
        await using var host = await GatewayHost.StartAsync(configureBuilder: builder =>
            builder.Services.AddWriteableFileProvider(services => services.GetRequiredKeyedService<IWriteableFileProvider>("native")));
        await host.WriteAsync("doc.txt", "native content", provider: "native");

        var url = await host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)));

        Assert.AreEqual(SignedUrlKind.Presigned, url.Kind);
        Assert.AreEqual("memory", url.Url.Scheme);
    }

    [TestMethod]
    public async Task NoDefaultProvider_Throws_AndKeyedOnesStillWork()
    {
        await using var host = await GatewayHost.StartAsync(withDefault: false);
        await host.WriteAsync("doc.txt", "keyed");

        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5))));
        StringAssert.Contains(missing.Message, "AddWriteableFileProvider");
        var keyed = await host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)), "files");
        using var response = await host.Client.GetAsync(keyed.Url);
        Assert.AreEqual("keyed", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DefaultProvider_InvalidArguments_Throw()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", null!));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.Zero)));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _host.Urls.GetSignedUrlAsync("missing.txt", Options(TimeSpan.FromMinutes(1))));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync("a/../b.txt", Options(TimeSpan.FromMinutes(1))));
    }

    [TestMethod]
    public async Task PreferGateway_IssuesAGatewayUrl_EvenForANativeProvider()
    {
        await using var host = await GatewayHost.StartAsync(options => options.PreferGateway = true);
        await host.WriteAsync("doc.txt", "native content", provider: "native");

        var url = await host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)), "native");

        Assert.AreEqual(SignedUrlKind.Gateway, url.Kind);
        using var response = await host.Client.GetAsync(url.Url);
        Assert.AreEqual("native content", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task NativeProvider_LongerThanItCanSign_FallsBackToTheGateway()
    {
        var url = await _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromDays(8)) with { AllowShorterValidity = true }, "native");

        Assert.AreEqual(SignedUrlKind.Gateway, url.Kind);
        AssertAbout(DateTimeOffset.UtcNow.AddDays(7), url.ExpiresOn);
    }

    [TestMethod]
    public async Task UploadUrl_IsAPut_NamingTheContentTypeToSend()
    {
        var typed = await _host.Urls.GetSignedUrlAsync("up.json", Options(TimeSpan.FromMinutes(5), SignedUrlAccess.Create) with { ContentType = "application/json" }, "files");
        var untyped = await _host.Urls.GetSignedUrlAsync("up.bin", Options(TimeSpan.FromMinutes(5), SignedUrlAccess.Write), "files");

        Assert.AreEqual("PUT", typed.Method);
        Assert.AreEqual("application/json", typed.Headers["Content-Type"]);
        Assert.AreEqual("PUT", untyped.Method);
        Assert.AreEqual(0, untyped.Headers.Count);
    }

    [TestMethod]
    public async Task UploadUrl_ForAMissingFile_IsIssued()
    {
        var url = await _host.Urls.GetSignedUrlAsync("new/deep/file.txt", Options(TimeSpan.FromMinutes(5), SignedUrlAccess.Create), "files");

        using var response = await _host.PutAsync(url.Url, "made");
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("made", await _host.ReadAsync("new/deep/file.txt"));
    }

    [TestMethod]
    public async Task ValidityOverTheGatewaysMaximum_IsNotSupported_UnlessShorterIsAllowed()
    {
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1)), "files"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromHours(2), SignedUrlAccess.Write), "files"));

        var capped = await _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromHours(2), SignedUrlAccess.Write) with { AllowShorterValidity = true }, "files");
        AssertAbout(DateTimeOffset.UtcNow.AddHours(1), capped.ExpiresOn);
    }

    [TestMethod]
    public async Task ConfiguredMaximums_Apply()
    {
        await using var host = await GatewayHost.StartAsync(options =>
        {
            options.MaxReadValidity = TimeSpan.FromMinutes(10);
            options.MaxUploadValidity = TimeSpan.FromDays(2);
        });
        await host.WriteAsync("doc.txt", "x");

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(11)), "files"));
        var upload = await host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromDays(2), SignedUrlAccess.Write), "files");
        Assert.AreEqual(SignedUrlKind.Gateway, upload.Kind);
    }

    [TestMethod]
    public async Task InvalidArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(1)), null!));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(1)), ""));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", null!, "files"));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.Zero), "files"));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(-1)), "files"));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(1), (SignedUrlAccess)42), "files"));
        var unknown = await Assert.ThrowsExactlyAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(1)), "nobody"));
        Assert.AreEqual("providerKey", unknown.ParamName);
        var badType = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(1)) with { ContentType = "not a type" }, "files"));
        Assert.AreEqual("options", badType.ParamName);
    }

    [TestMethod]
    public async Task ReadUrl_ForAMissingFileOrAFolder_ThrowsFileNotFound()
    {
        await _host.WriteAsync("folder/inside.txt", "x");

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _host.Urls.GetSignedUrlAsync("missing.txt", Options(TimeSpan.FromMinutes(1)), "files"));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _host.Urls.GetSignedUrlAsync("folder", Options(TimeSpan.FromMinutes(1)), "files"));
        // A native provider says so itself, and the gateway does not take over.
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _host.Urls.GetSignedUrlAsync("missing.txt", Options(TimeSpan.FromMinutes(1)), "native"));
    }

    [TestMethod]
    public async Task PathTheProviderRefuses_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync("a/../b.txt", Options(TimeSpan.FromMinutes(1)), "files"));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync("%2e%2e/b.txt", Options(TimeSpan.FromMinutes(1), SignedUrlAccess.Write), "files"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _host.Urls.GetSignedUrlAsync(ProviderPath.Root, Options(TimeSpan.FromMinutes(1)), "files"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _host.Urls.GetSignedUrlAsync(ProviderPath.Root, Options(TimeSpan.FromMinutes(1), SignedUrlAccess.Create), "files"));
    }

    [TestMethod]
    public async Task ForgedHostHeader_DoesNotChangeTheUrl()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/issue?provider=files&path=doc.txt&access=Read");
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        using var response = await _host.Client.SendAsync(request);

        var url = new Uri(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("files.example.com", url.Host);
        Assert.AreEqual("https", url.Scheme);
    }

    [TestMethod]
    public async Task PublicBaseUriWithAPath_PrefixesTheUrl_AndServesBehindAPathBase()
    {
        await using var host = await GatewayHost.StartAsync(
            options => options.PublicBaseUri = new Uri("https://files.example.com/app"),
            configureApp: app => app.UsePathBase("/app"));
        await host.WriteAsync("doc.txt", "behind a proxy");

        var url = await host.UrlAsync("doc.txt");

        Assert.IsTrue(url.AbsolutePath.StartsWith("/app/files/", StringComparison.Ordinal), url.ToString());
        using var response = await host.Client.GetAsync(url);
        Assert.AreEqual("behind a proxy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task CustomBasePath_IsWhereTheGatewayServes()
    {
        await using var host = await GatewayHost.StartAsync(options => options.BasePath = "/api/v1/downloads");
        await host.WriteAsync("doc.txt", "elsewhere");

        var url = await host.UrlAsync("doc.txt");

        Assert.IsTrue(url.AbsolutePath.StartsWith("/api/v1/downloads/", StringComparison.Ordinal), url.ToString());
        using var response = await host.Client.GetAsync(url);
        Assert.AreEqual("elsewhere", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("..", 3)]
    [DataRow(".", 3)]
    [DataRow("", 4)]
    [DataRow("my report (final).pdf", 4)]
    public async Task FileNameSegment_IsEscaped_OrLeftOutWhenOnlyDots(string fileName, int segments)
    {
        var url = await _host.UrlAsync("doc.txt", fileName: fileName);

        Assert.AreEqual(segments, url.Segments.Length, url.ToString());
        using var response = await _host.Client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(fileName.Length == 0 ? "doc.txt" : fileName, response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [TestMethod]
    public void BuildUrl_KeepsReservedCharactersOfTheNameEscaped()
    {
        var options = new WriteableFileProviderGatewayOptions { PublicBaseUri = new Uri("https://h.example/base/") };

        var url = WriteableFileUrlService.BuildUrl(options, "r.abc.def", "a/b?c#d.txt");

        Assert.AreEqual("https://h.example/base/files/r.abc.def/a%2Fb%3Fc%23d.txt", url.AbsoluteUri);
    }

    [TestMethod]
    public async Task ExpiresOn_ComesFromTheRegisteredTimeProvider()
    {
        var fixedNow = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await using var host = await GatewayHost.StartAsync(configureBuilder: builder =>
            builder.Services.AddSingleton<TimeProvider>(new ManualTimeProvider(fixedNow)));
        await host.WriteAsync("doc.txt", "x");

        var url = await host.Urls.GetSignedUrlAsync("doc.txt", Options(TimeSpan.FromMinutes(5)), "files");

        Assert.AreEqual(fixedNow.AddMinutes(5), url.ExpiresOn);
    }

    // Within the minute before the expected time: the URL was issued just before it was computed.
    private static void AssertAbout(DateTimeOffset expected, DateTimeOffset? actual) =>
        Assert.IsTrue(actual <= expected && actual >= expected.AddMinutes(-1), $"{actual:O} is not just before {expected:O}");

    private static SignedUrlOptions Options(TimeSpan validity, SignedUrlAccess access = SignedUrlAccess.Read) =>
        new() { Validity = validity, Access = access };
}
