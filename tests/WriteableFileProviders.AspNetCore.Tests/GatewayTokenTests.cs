using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>
/// Every way a token can be wrong answers 404, on GET and on PUT: tampered, expired, for another purpose, provider or
/// access, malformed, or naming a path the provider refuses.
/// </summary>
[TestClass]
public sealed class GatewayTokenTests
{
    private GatewayHost _host = null!;

    [TestInitialize]
    public async Task Start()
    {
        _host = await GatewayHost.StartAsync();
        await _host.WriteAsync("doc.txt", "original");
    }

    [TestCleanup]
    public async Task Stop() => await _host.DisposeAsync();

    private GatewayTokenCodec Codec => _host.Services.GetRequiredService<GatewayTokenCodec>();

    private IDataProtectionProvider DataProtection => _host.Services.GetRequiredService<IDataProtectionProvider>();

    [TestMethod]
    public async Task ACraftedValidToken_Works()
    {
        var token = Codec.Protect("files", Payload(SignedUrlAccess.Read, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1));

        using var response = await _host.Client.GetAsync(GatewayHost.GatewayUrl(token));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("original", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task TamperedToken_Answers404()
    {
        var token = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt"));
        var payloadStart = token.LastIndexOf('.') + 1;
        // Not the last character: its low bits can be padding that decoding ignores.
        foreach (var index in new[] { payloadStart, payloadStart + 20, token.Length - 2 })
        {
            var flipped = token[index] == 'A' ? 'B' : 'A';
            var tampered = token[..index] + flipped + token[(index + 1)..];

            await AssertNotFoundAsync(tampered);
        }
    }

    [TestMethod]
    public async Task ExpiredToken_Answers404()
    {
        var read = Codec.Protect("files", Payload(SignedUrlAccess.Read, "doc.txt"), DateTimeOffset.UtcNow.AddSeconds(-1));
        var write = Codec.Protect("files", Payload(SignedUrlAccess.Write, "doc.txt"), DateTimeOffset.UtcNow.AddSeconds(-1));

        await AssertNotFoundAsync(read);
        await AssertNotFoundAsync(write, put: true);
        Assert.AreEqual("original", await _host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task WrongPurposeToken_Answers404()
    {
        var json = JsonPayload(Payload(SignedUrlAccess.Read, "doc.txt"));
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        string[][] purposes =
        [
            ["SomethingElse"],
            [GatewayTokenCodec.Purpose],
            [GatewayTokenCodec.Purpose, "files"],
            [GatewayTokenCodec.Purpose, "files", "Write"],
            ["WriteableFileProviders.Gateway.v2", "files", "Read"],
        ];
        foreach (var purpose in purposes)
        {
            var protector = DataProtection.CreateProtector(purpose[0], purpose[1..]).ToTimeLimitedDataProtector();
            await AssertNotFoundAsync(Token("r", "files", protector.Protect(json, expires)));
        }

        // Not time-limited: the right purpose chain without the time-limited protector's own purpose.
        var untimed = DataProtection.CreateProtector(GatewayTokenCodec.Purpose, "files", "Read").Protect(json);
        await AssertNotFoundAsync(Token("r", "files", untimed));
    }

    [TestMethod]
    public async Task ReadTokenUsedForPut_Answers404_AndLeavesTheFile()
    {
        var token = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt"));

        await AssertNotFoundAsync(token, put: true);
        // Relabelled as an upload token, it no longer unprotects.
        await AssertNotFoundAsync("w" + token[1..], put: true);
        await AssertNotFoundAsync("c" + token[1..], put: true);
        Assert.AreEqual("original", await _host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task UploadTokenUsedForGet_Answers404()
    {
        var write = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt", SignedUrlAccess.Write));
        var create = GatewayHost.TokenOf(await _host.UrlAsync("new.txt", SignedUrlAccess.Create));

        await AssertNotFoundAsync(write);
        await AssertNotFoundAsync(create);
        await AssertNotFoundAsync("r" + write[1..]);
    }

    [TestMethod]
    public async Task TokenForProviderA_UsedOnProviderB_Answers404()
    {
        await _host.WriteAsync("doc.txt", "other's", provider: "other");
        var read = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt"));
        var write = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt", SignedUrlAccess.Write));

        await AssertNotFoundAsync(Reprovider(read, "other"));
        await AssertNotFoundAsync(Reprovider(write, "other"), put: true);
        Assert.AreEqual("other's", await _host.ReadAsync("doc.txt", "other"));
    }

    [TestMethod]
    public async Task DefaultProviderToken_AndKeyedToken_DoNotRedeemAsEachOther()
    {
        await _host.WriteAsync("doc.txt", "default's", provider: null);
        var defaultRead = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt", provider: null));
        var defaultWrite = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt", SignedUrlAccess.Write, provider: null));
        var keyedRead = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt"));
        var keyedWrite = GatewayHost.TokenOf(await _host.UrlAsync("doc.txt", SignedUrlAccess.Write));

        await AssertNotFoundAsync(Reprovider(defaultRead, "files"));
        await AssertNotFoundAsync(Reprovider(defaultWrite, "files"), put: true);
        // A key that spells the access: the default's chain is one purpose shorter, never the same chain.
        await AssertNotFoundAsync(Reprovider(defaultRead, "Read"));
        await AssertNotFoundAsync(Unkeyed(keyedRead));
        await AssertNotFoundAsync(Unkeyed(keyedWrite), put: true);
        Assert.AreEqual("default's", await _host.ReadAsync("doc.txt", provider: null));
        Assert.AreEqual("original", await _host.ReadAsync("doc.txt"));
    }

    [TestMethod]
    public async Task DefaultProviderToken_WhenNoDefaultIsRegistered_Answers404()
    {
        await using var host = await GatewayHost.StartAsync(withDefault: false);
        var codec = host.Services.GetRequiredService<GatewayTokenCodec>();
        var read = codec.Protect(null, Payload(SignedUrlAccess.Read, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1));
        var write = codec.Protect(null, Payload(SignedUrlAccess.Write, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1));

        using var get = await host.Client.GetAsync(GatewayHost.GatewayUrl(read));
        using var put = await host.PutAsync(GatewayHost.GatewayUrl(write), "x");

        Assert.AreEqual(HttpStatusCode.NotFound, get.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, put.StatusCode);
    }

    [TestMethod]
    public void Protect_AnEmptyKey_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => Codec.Protect("", Payload(SignedUrlAccess.Read, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1)));

    [TestMethod]
    public async Task TokenForAProviderThatIsNotRegistered_Answers404()
    {
        var read = Codec.Protect("ghost", Payload(SignedUrlAccess.Read, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1));
        var write = Codec.Protect("ghost", Payload(SignedUrlAccess.Write, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1));

        await AssertNotFoundAsync(read);
        await AssertNotFoundAsync(write, put: true);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("r.x")]
    [DataRow("r.ZmlsZXM.abc.def")]
    [DataRow("x.ZmlsZXM.abcdef")]
    [DataRow("R.ZmlsZXM.abcdef")]
    [DataRow("r.!!!.abcdef")]
    [DataRow("r..abcdef")]
    [DataRow("r.ZmlsZXM.")]
    [DataRow("r.ZmlsZXM.!!!!")]
    [DataRow("r.ZmlsZXM.abcdef")]
    [DataRow("r._w.abcdef")]
    [DataRow("r.  .AAAA")]          // a key segment that decodes to an empty key
    [DataRow("r..AAAA")]            // the default provider's form, with a payload that does not unprotect
    [DataRow("r..")]                // the default provider's form, with no payload
    public async Task MalformedToken_Answers404(string token)
    {
        await AssertNotFoundAsync(token);
        await AssertNotFoundAsync(token, put: true);
        Assert.IsNull(Codec.Read(token));
    }

    [TestMethod]
    public void Read_Null_IsNull() => Assert.IsNull(Codec.Read(null));

    [TestMethod]
    public void AccessNames_AreTheLiteralsOfTheFormat()
    {
        Assert.AreEqual("Read", GatewayTokenCodec.AccessName(SignedUrlAccess.Read));
        Assert.AreEqual("Create", GatewayTokenCodec.AccessName(SignedUrlAccess.Create));
        Assert.AreEqual("Write", GatewayTokenCodec.AccessName(SignedUrlAccess.Write));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GatewayTokenCodec.AccessName((SignedUrlAccess)42));
        Assert.IsTrue(GatewayTokenCodec.TryParseAccessName("Create", out var create));
        Assert.AreEqual(SignedUrlAccess.Create, create);
        Assert.IsFalse(GatewayTokenCodec.TryParseAccessName("read", out _));
        Assert.IsFalse(GatewayTokenCodec.TryParseAccessName(null, out _));
    }

    [TestMethod]
    public void Protect_UnknownAccess_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Codec.Protect("files", Payload((SignedUrlAccess)42, "doc.txt"), DateTimeOffset.UtcNow.AddMinutes(1)));

    [TestMethod]
    [DataRow("..")]
    [DataRow("../secret.txt")]
    [DataRow("a/../../x.txt")]
    [DataRow("%2e%2e/x.txt")]
    [DataRow("%252e%252e/x.txt")]
    [DataRow("a%2F..%2F..%2Fx.txt")]
    [DataRow("/etc/passwd")]
    [DataRow("a//b.txt")]
    [DataRow("a\\..\\..\\x.txt")]
    [DataRow("bad\u0001name.txt")]
    public async Task PathTheProviderRefuses_InsideAValidToken_Answers404(string segment)
    {
        await _host.WriteAsync("secret.txt", "secret", provider: "other");
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        string[] path = [segment];

        await AssertNotFoundAsync(Codec.Protect("files", Payload(SignedUrlAccess.Read, path), expires));
        await AssertNotFoundAsync(Codec.Protect("files", Payload(SignedUrlAccess.Write, path), expires), put: true);
        await AssertNotFoundAsync(Codec.Protect("files", Payload(SignedUrlAccess.Create, path), expires), put: true);
        Assert.AreEqual("secret", await _host.ReadAsync("secret.txt", "other"));
    }

    [TestMethod]
    public async Task RootPathInsideAValidToken_Answers404()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);

        await AssertNotFoundAsync(Codec.Protect("files", Payload(SignedUrlAccess.Read, []), expires));
        await AssertNotFoundAsync(Codec.Protect("files", Payload(SignedUrlAccess.Write, []), expires), put: true);
    }

    [TestMethod]
    public async Task PayloadThatDoesNotMatchItsToken_Answers404()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var readProtector = DataProtection.CreateProtector(GatewayTokenCodec.Purpose, "files", "Read").ToTimeLimitedDataProtector();
        byte[][] payloads =
        [
            JsonPayload(Payload(SignedUrlAccess.Write, "doc.txt")),  // another access than the token's
            Encoding.UTF8.GetBytes("""{"p":null,"a":"Read","j":"x"}"""),
            Encoding.UTF8.GetBytes("""{"p":["doc.txt",null],"a":"Read","j":"x"}"""),
            Encoding.UTF8.GetBytes("""{"p":["doc.txt"],"a":"Read","j":""}"""),
            Encoding.UTF8.GetBytes("""{"p":["doc.txt"],"a":"Read"}"""),
            Encoding.UTF8.GetBytes("""{"p":["doc.txt"],"a":"Sideways","j":"x"}"""),
            Encoding.UTF8.GetBytes("null"),
            Encoding.UTF8.GetBytes("not json"),
        ];
        foreach (var json in payloads)
        {
            var token = Token("r", "files", readProtector.Protect(json, expires));
            Assert.IsNull(Codec.Read(token), Encoding.UTF8.GetString(json));
            await AssertNotFoundAsync(token);
        }
    }

    private async Task AssertNotFoundAsync(string token, bool put = false)
    {
        var url = token.Length == 0 ? new Uri(GatewayHost.PublicBaseUri, "files/") : GatewayHost.GatewayUrl(token);
        using var response = put ? await _host.PutAsync(url, "replaced") : await _host.Client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"{(put ? "PUT" : "GET")} {token}");
        if (token.Length > 0)
            Assert.AreEqual("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static GatewayTokenPayload Payload(SignedUrlAccess access, params string[] path) =>
        new() { Path = path, Access = access, Id = Guid.NewGuid().ToString("N"), FileName = "x" };

    private static byte[] JsonPayload(GatewayTokenPayload payload) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);

    private static string Token(string access, string provider, byte[] protectedPayload) =>
        $"{access}.{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(provider))}.{WebEncoders.Base64UrlEncode(protectedPayload)}";

    private static string Unkeyed(string token)
    {
        var parts = token.Split('.');
        return $"{parts[0]}..{parts[2]}";
    }

    private static string Reprovider(string token, string provider)
    {
        var parts = token.Split('.');
        return $"{parts[0]}.{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(provider))}.{parts[2]}";
    }
}
