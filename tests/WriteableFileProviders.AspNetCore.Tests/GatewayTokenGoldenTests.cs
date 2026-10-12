using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>
/// Pins the gateway's one-way door (AGENTS.md, "The gateway's URLs"): the token format, the payload's JSON and the
/// purpose chain. Tokens issued by the first version with a fixed key ring must keep redeeming, and tokens built here from
/// literals, never through the code under test or the enum's names, must redeem too.
/// </summary>
[TestClass]
public sealed class GatewayTokenGoldenTests
{
    // A Data Protection key made for this test only, kept unencrypted on purpose: it protects nothing but the tokens below.
    // Its expiry is set far ahead, so the ring always has a default key.
    private const string KeyRing = @"<key id=""7fbef36c-df8a-49df-83eb-daa646fde64a"" version=""1""><creationDate>2026-10-10T23:49:17.1998197Z</creationDate><activationDate>2026-10-10T23:49:17.1998197Z</activationDate><expirationDate>2126-01-01T00:00:00Z</expirationDate><descriptor deserializerType=""Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer, Microsoft.AspNetCore.DataProtection, Version=10.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60""><descriptor><encryption algorithm=""AES_256_CBC"" /><validation algorithm=""HMACSHA256"" /><masterKey p4:requiresEncryption=""true"" xmlns:p4=""http://schemas.asp.net/2015/03/dataProtection""><!-- Warning: the key below is in an unencrypted form. --><value>x7hM1U1fFZ///aGU18wlX8fhUpvxx1y1Rw39EKpy1NsxMIs/ujW3t3mOhayKCXsA8PUUE6YLSr66K9kfhXGv5w==</value></masterKey></descriptor></descriptor></key>";

    // Issued with the key above by GatewayTokenCodec on 2026-10-10, for provider "files", path golden/doc.txt, type
    // text/plain, file name Golden.txt, ids 00...01, 00...02 and 00...03, expiring 2100-01-01. Never regenerate them.
    private const string GoldenRead = "r.ZmlsZXM.CfDJ8Gzzvn-K399Jg-vapkb95krZ4YgFZz3GAptw3F-Yen1-24oj9xLFPEpnt0OGiRgtW1I737_377Xm0bmXSVgjZy58t1MRJTe0e5zB7FR2CCec9GweIw8rQHKXTdph6I7oHihsdq63q4YkX4kPWHqVgajT9vCGJ8yhS3XEUG_to5_UgGcEg-okvWAvn5hz3psGR289wglEbxP7NMNUm1fPXKDLgSB7SbWwM4GCJZjstf5dgt07q3xaC5uBf2J3p-u_ox1TlbZRFIKhaYTdyPC4uB8";
    private const string GoldenCreate = "c.ZmlsZXM.CfDJ8Gzzvn-K399Jg-vapkb95kp_Cj5GZTLwpQGX9zEyjVW6uh5tt40YUW2P3VRPcZxmI7Xrd1lGljnTgHVEBs32qvmBZy5OMNMEuIPZdSjAz9KTrJou-wwx2IfPBLX91f4yBJujSZQx22Zd2K64Elrl_U60s00kzxD0nFTvJBu09mFYklifMI7p_rigr2qRqpZx68_1plaNOz-bIl1eeqsH3GiOUTJPcGIEkCa7vvormQxam-gj949ZzuHykRERqGb-EJg_ClvvwYoh4TyyvvMfVGg";
    private const string GoldenWrite = "w.ZmlsZXM.CfDJ8Gzzvn-K399Jg-vapkb95koMlUuJC6K10ceAY2Af9iWgLpHh2nit3p8RzXvGQOwiofqeGtBC6XJbEzxvTASRussP0dhrXcAnJtKUzkYLEipLIMhTVE4OmNEZA3QYNBhmf8FMYdsF3Bf9fOll76auzas218SwF5pI9PvW6Xjo74ps82nCUlKWrN0qbaUX0TM7X3_wNY_Yk8BH38eI_oOjBgQR7GxaDyUIVGgzNtzYLw9NpeteaPNILVSLN0llyWsyOGU1CKpEgMuV6PI5AjCgLPA";

    [TestMethod]
    public void GoldenTokens_ReadBackWhatTheyWereIssuedWith()
    {
        var codec = Codec(GoldenDataProtection());

        foreach (var (token, access, id) in new[] { (GoldenRead, SignedUrlAccess.Read, "1"), (GoldenCreate, SignedUrlAccess.Create, "2"), (GoldenWrite, SignedUrlAccess.Write, "3") })
        {
            var read = codec.Read(token);
            Assert.IsNotNull(read, token[..1]);
            Assert.AreEqual("files", read.ProviderName);
            CollectionAssert.AreEqual(new[] { "golden", "doc.txt" }, read.Payload.Path);
            Assert.AreEqual(access, read.Payload.Access);
            Assert.AreEqual("text/plain", read.Payload.ContentType);
            Assert.AreEqual("Golden.txt", read.Payload.FileName);
            Assert.AreEqual(new string('0', 31) + id, read.Payload.Id);
            Assert.AreEqual(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), read.ExpiresOn);
        }
    }

    [TestMethod]
    public async Task GoldenTokens_AreServedByTheGateway()
    {
        await using var host = await GatewayHost.StartAsync(configureBuilder: builder =>
            builder.Services.AddSingleton(GoldenDataProtection()));
        await host.WriteAsync("golden/doc.txt", "golden content");

        using (var get = await host.Client.GetAsync(GatewayHost.GatewayUrl(GoldenRead)))
        {
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual("golden content", await get.Content.ReadAsStringAsync());
            Assert.AreEqual("text/plain", get.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("Golden.txt", get.Content.Headers.ContentDisposition?.FileNameStar);
        }
        using (var write = await host.PutAsync(GatewayHost.GatewayUrl(GoldenWrite), "rewritten", "text/plain"))
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        Assert.AreEqual("rewritten", await host.ReadAsync("golden/doc.txt"));
        await host.Provider().DeleteAsync("golden/doc.txt");
        using (var create = await host.PutAsync(GatewayHost.GatewayUrl(GoldenCreate), "created", "text/plain"))
            Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        Assert.AreEqual("created", await host.ReadAsync("golden/doc.txt"));
    }

    [TestMethod]
    [DataRow("r", "Read", "GET")]
    [DataRow("c", "Create", "PUT")]
    [DataRow("w", "Write", "PUT")]
    public async Task TokensBuiltFromLiterals_Redeem(string code, string access, string method)
    {
        await using var host = await GatewayHost.StartAsync();
        if (method == "GET")
            await host.WriteAsync("golden/doc.txt", "golden content");
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("WriteableFileProviders.Gateway.v1", "files", access).ToTimeLimitedDataProtector();
        var json = $$"""{"p":["golden","doc.txt"],"a":"{{access}}","t":"text/plain","f":"Golden.txt","j":"{{Guid.NewGuid():N}}"}""";
        var payload = protector.Protect(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow.AddMinutes(5));
        var token = $"{code}.{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("files"))}.{WebEncoders.Base64UrlEncode(payload)}";

        using var response = method == "GET"
            ? await host.Client.GetAsync(GatewayHost.GatewayUrl(token))
            : await host.PutAsync(GatewayHost.GatewayUrl(token), "uploaded", "text/plain");

        Assert.IsTrue(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.AreEqual(method == "GET" ? "golden content" : "uploaded", await host.ReadAsync("golden/doc.txt"));
    }

    [TestMethod]
    public void TheCodec_WritesTheLiteralFormat()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var token = Codec(dataProtection).Protect("files", new GatewayTokenPayload
        {
            Path = ["golden", "doc.txt"],
            Access = SignedUrlAccess.Write,
            ContentType = "text/plain",
            FileName = "Golden.txt",
            Id = "id",
        }, DateTimeOffset.UtcNow.AddMinutes(5));

        var parts = token.Split('.');
        Assert.AreEqual(3, parts.Length);
        Assert.AreEqual("w", parts[0]);
        Assert.AreEqual("files", Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[1])));
        var json = dataProtection.CreateProtector("WriteableFileProviders.Gateway.v1", "files", "Write").ToTimeLimitedDataProtector()
            .Unprotect(WebEncoders.Base64UrlDecode(parts[2]), out _);
        Assert.AreEqual("""{"p":["golden","doc.txt"],"a":"Write","t":"text/plain","f":"Golden.txt","j":"id"}""", Encoding.UTF8.GetString(json));
    }

    // Issued with the key above by GatewayTokenCodec on 2026-10-11, for the app's default provider (an empty provider
    // segment, and no provider in the purpose chain), path golden/doc.txt, type text/plain, file name Golden.txt, ids
    // 00...04, 00...05 and 00...06, expiring 2100-01-01. Never regenerate them.
    private const string GoldenDefaultRead = "r..CfDJ8Gzzvn-K399Jg-vapkb95kq5yeOmCCs8RFdhdd3WmVh8xiYPTgaeHrLyjYK88gHVVMdiI5jMR5YSm6z5VUsJMrXckaMwCFlOUny_RFPTKFbNHDaM9a9TT2Y24KesXGIlOdVsKSvHZMpgeMXwSHNW88r1kdOPDKrot3-0IEMIlvlBk6cHgpuGsNw49_vPWE5AtCkRsbDZSa5TD7DzFnK4_9FPlomLptO2WilJIpsD1ac3kavF7YEe8NpZGAoKly9NHTYzFZt08CMiPrqBgw3llRM";
    private const string GoldenDefaultCreate = "c..CfDJ8Gzzvn-K399Jg-vapkb95kpoouE0lP_oXvq0fbbcIKGF9Eqk3N1vPpyCeKGy2i2AgDtG4cmZEFMJHWc56ZCT_fWt6Kk8w17glVIo9Nfy8sDHXF7VVGL8ggchi8n9nWE_pKj6WJer_IWQN1zytdji4Dc6d07YN0VXIUcu46W94odChjSptQGuik4rm7-o1_rAgiGIQYcM_-ihUoNJFYe3OkxIWsUlEUKhyWJYTDeyJGq6XKqlP1fH5By_OwCKljPT9YXyDK0dX7MMQSJ2A6f4OI4";
    private const string GoldenDefaultWrite = "w..CfDJ8Gzzvn-K399Jg-vapkb95kq_bB4lABCujVpkltL1HuXys-mFfrH9dCaNtl0cDUBPkXzdY0C0FwKQqP-LNw_y6wQUp5Vuwig_cPYFpM90Od8pgJqrV9t7X5VeufadwXodZ9P9t3jdfb-JoGJu5BfVWF3YeN8fYfBgQsBSi44f9LZ92P0WhMiHaoxh2NT3lLthdwxpj6ht305fx4cylrHJVMg-QvLsErjc2ZkDoH1Iv3EdWmm5elDuquicx7DWf2HMnjQA50EHLNKUYWHjoJmsTBk";

    [TestMethod]
    public void GoldenDefaultProviderTokens_ReadBackWhatTheyWereIssuedWith()
    {
        var codec = Codec(GoldenDataProtection());

        foreach (var (token, access, id) in new[] { (GoldenDefaultRead, SignedUrlAccess.Read, "4"), (GoldenDefaultCreate, SignedUrlAccess.Create, "5"), (GoldenDefaultWrite, SignedUrlAccess.Write, "6") })
        {
            var read = codec.Read(token);
            Assert.IsNotNull(read, token[..1]);
            Assert.IsNull(read.ProviderName);
            CollectionAssert.AreEqual(new[] { "golden", "doc.txt" }, read.Payload.Path);
            Assert.AreEqual(access, read.Payload.Access);
            Assert.AreEqual("text/plain", read.Payload.ContentType);
            Assert.AreEqual("Golden.txt", read.Payload.FileName);
            Assert.AreEqual(new string('0', 31) + id, read.Payload.Id);
            Assert.AreEqual(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), read.ExpiresOn);
        }
    }

    [TestMethod]
    public async Task GoldenDefaultProviderTokens_AreServedByTheGateway_FromTheDefaultProvider()
    {
        await using var host = await GatewayHost.StartAsync(configureBuilder: builder =>
            builder.Services.AddSingleton(GoldenDataProtection()));
        await host.WriteAsync("golden/doc.txt", "golden content", provider: null);
        await host.WriteAsync("golden/doc.txt", "the keyed provider's");

        using (var get = await host.Client.GetAsync(GatewayHost.GatewayUrl(GoldenDefaultRead)))
        {
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual("golden content", await get.Content.ReadAsStringAsync());
            Assert.AreEqual("text/plain", get.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("Golden.txt", get.Content.Headers.ContentDisposition?.FileNameStar);
        }
        using (var write = await host.PutAsync(GatewayHost.GatewayUrl(GoldenDefaultWrite), "rewritten", "text/plain"))
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        Assert.AreEqual("rewritten", await host.ReadAsync("golden/doc.txt", provider: null));
        await host.Provider(null).DeleteAsync("golden/doc.txt");
        using (var create = await host.PutAsync(GatewayHost.GatewayUrl(GoldenDefaultCreate), "created", "text/plain"))
            Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        Assert.AreEqual("created", await host.ReadAsync("golden/doc.txt", provider: null));
        Assert.AreEqual("the keyed provider's", await host.ReadAsync("golden/doc.txt"));
    }

    [TestMethod]
    [DataRow("r", "Read", "GET")]
    [DataRow("c", "Create", "PUT")]
    [DataRow("w", "Write", "PUT")]
    public async Task DefaultProviderTokensBuiltFromLiterals_Redeem(string code, string access, string method)
    {
        await using var host = await GatewayHost.StartAsync();
        if (method == "GET")
            await host.WriteAsync("golden/doc.txt", "golden content", provider: null);
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("WriteableFileProviders.Gateway.v1", access).ToTimeLimitedDataProtector();
        var json = $$"""{"p":["golden","doc.txt"],"a":"{{access}}","t":"text/plain","f":"Golden.txt","j":"{{Guid.NewGuid():N}}"}""";
        var payload = protector.Protect(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow.AddMinutes(5));
        var token = $"{code}..{WebEncoders.Base64UrlEncode(payload)}";

        using var response = method == "GET"
            ? await host.Client.GetAsync(GatewayHost.GatewayUrl(token))
            : await host.PutAsync(GatewayHost.GatewayUrl(token), "uploaded", "text/plain");

        Assert.IsTrue(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.AreEqual(method == "GET" ? "golden content" : "uploaded", await host.ReadAsync("golden/doc.txt", provider: null));
    }

    [TestMethod]
    public void TheCodec_WritesTheLiteralFormat_ForTheDefaultProvider()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var token = Codec(dataProtection).Protect(null, new GatewayTokenPayload
        {
            Path = ["golden", "doc.txt"],
            Access = SignedUrlAccess.Write,
            ContentType = "text/plain",
            FileName = "Golden.txt",
            Id = "id",
        }, DateTimeOffset.UtcNow.AddMinutes(5));

        var parts = token.Split('.');
        Assert.AreEqual(3, parts.Length);
        Assert.AreEqual("w", parts[0]);
        Assert.AreEqual("", parts[1]);
        var json = dataProtection.CreateProtector("WriteableFileProviders.Gateway.v1", "Write").ToTimeLimitedDataProtector()
            .Unprotect(WebEncoders.Base64UrlDecode(parts[2]), out _);
        Assert.AreEqual("""{"p":["golden","doc.txt"],"a":"Write","t":"text/plain","f":"Golden.txt","j":"id"}""", Encoding.UTF8.GetString(json));
    }

    // The codec with the gateway's default options, so the default root purpose: the one these tokens were issued under.
    private static GatewayTokenCodec Codec(IDataProtectionProvider dataProtection) =>
        new(dataProtection, Options.Create(new WriteableFileProviderGatewayOptions()));

    private static IDataProtectionProvider GoldenDataProtection()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("WriteableFileProviders.Gateway.Golden").DisableAutomaticKeyGeneration();
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = new FixedXmlRepository(XElement.Parse(KeyRing)));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    /// <summary>A key ring that holds the one key above and never stores another.</summary>
    private sealed class FixedXmlRepository(XElement key) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => [key];

        public void StoreElement(XElement element, string friendlyName) =>
            throw new InvalidOperationException("The golden key ring is fixed.");
    }
}
