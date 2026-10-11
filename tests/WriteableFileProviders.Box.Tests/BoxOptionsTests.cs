using System.Net;
using System.Text.Json.Nodes;
using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;

namespace WriteableFileProviders.Box.Tests;

/// <summary>The options, the configuration shape, and the sign-in: lazy, from the options or from a ready client.</summary>
[TestClass]
public sealed class BoxOptionsTests
{
    private static readonly TestCredentials _credentials = TestCredentials.Default;

    [TestMethod]
    public void Constructor_NullArguments_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableBoxFileProvider(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableBoxFileProvider(new BoxFileProviderOptions(), (IBoxClient)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableBoxFileProvider(null!, _credentials.Client(new FakeBox())));
    }

    [TestMethod]
    [DataRow(nameof(BoxFileProviderOptions.ClientId))]
    [DataRow(nameof(BoxFileProviderOptions.ClientSecret))]
    [DataRow(nameof(BoxFileProviderOptions.EnterpriseId))]
    [DataRow(nameof(BoxFileProviderOptions.PublicKeyId))]
    [DataRow(nameof(BoxFileProviderOptions.PrivateKey))]
    public void Constructor_MissingCredential_NamesIt(string property)
    {
        foreach (var missing in new[] { null, "", " " })
        {
            var options = _credentials.Options();
            typeof(BoxFileProviderOptions).GetProperty(property)!.SetValue(options, missing);
            var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableBoxFileProvider(options));
            Assert.AreEqual("options", e.ParamName);
            StringAssert.Contains(e.Message, property);
        }
    }

    [TestMethod]
    public void Constructor_KeyThatCannotBeRead_ThrowsWithoutTheSecrets()
    {
        var wrongPassphrase = _credentials.Options();
        wrongPassphrase.Passphrase = "not-the-passphrase";
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableBoxFileProvider(wrongPassphrase));
        Assert.AreEqual("options", e.ParamName);
        Assert.IsFalse(e.Message.Contains("not-the-passphrase", StringComparison.Ordinal));
        Assert.IsFalse(e.Message.Contains("PRIVATE KEY", StringComparison.Ordinal));

        var notAKey = _credentials.Options();
        notAKey.PrivateKey = "not a key";
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableBoxFileProvider(notAKey));
    }

    [TestMethod]
    [DataRow("encrypted PKCS#8")]
    [DataRow("encrypted PKCS#8 with escaped line breaks")]
    [DataRow("PKCS#8")]
    [DataRow("PKCS#1")]
    public async Task Constructor_ReadsEveryFormOfKey_AndSignsIn(string form)
    {
        var box = new FakeBox();
        var options = _credentials.Options();
        (options.PrivateKey, options.Passphrase) = form switch
        {
            "encrypted PKCS#8" => (_credentials.EncryptedPrivateKeyPem, _credentials.Passphrase),
            "encrypted PKCS#8 with escaped line breaks" => (_credentials.EncryptedPrivateKeyPem.Replace("\n", "\\n", StringComparison.Ordinal), _credentials.Passphrase),
            "PKCS#8" => (_credentials.Pkcs8PrivateKeyPem, null),
            _ => (_credentials.RsaPrivateKeyPem, ""),
        };
        using var provider = new WriteableBoxFileProvider(options, TestCredentials.Session(box));
        Assert.IsFalse((await provider.GetFileInfoAsync("a.txt")).Exists);
        Assert.AreEqual(1, box.TokensIssued);
    }

    [TestMethod]
    public void NormalizePrivateKey_TurnsEscapedLineBreaksIntoRealOnes()
    {
        const string pem = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nMIIFHDBOBgkqhkiG9w0BBQ0wQTApBgkqhkiG9w0BBQwwHAQI\n-----END ENCRYPTED PRIVATE KEY-----\n";
        Assert.AreEqual(pem, WriteableBoxFileProvider.NormalizePrivateKey(pem.Replace("\n", "\\n", StringComparison.Ordinal)));
        Assert.AreEqual(pem, WriteableBoxFileProvider.NormalizePrivateKey(pem.Replace("\n", "\\r\\n", StringComparison.Ordinal)));
        Assert.AreEqual("a\nb", WriteableBoxFileProvider.NormalizePrivateKey("a\\rb"));
        Assert.AreSame(pem, WriteableBoxFileProvider.NormalizePrivateKey(pem));
        Assert.AreEqual("", WriteableBoxFileProvider.NormalizePrivateKey(""));
    }

    [TestMethod]
    public async Task Construction_DoesNoNetworkIo_AndTheFirstCallSignsIn()
    {
        var box = new FakeBox();
        using var provider = _credentials.Provider(box);
        Assert.AreEqual(0, box.Requests.Count, "The constructor does no network I/O");

        await provider.GetFileInfoAsync("a.txt");
        Assert.AreEqual("/oauth2/token", box.Requests[0].Path);
        Assert.IsNull(box.Requests[0].Header("Authorization"));
        Assert.AreEqual("Bearer token-1", box.Requests[1].Header("Authorization"));

        await provider.GetFileInfoAsync("a.txt");
        Assert.AreEqual(1, box.TokensIssued, "The token is kept");
    }

    [TestMethod]
    public async Task ExpiredToken_IsRefreshedOnce()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = _credentials.Provider(box);
        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);

        box.ExpireTokens();
        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        Assert.AreEqual(2, box.TokensIssued);
    }

    [TestMethod]
    public async Task WrongCredentials_FailOnTheFirstCall_NotInTheConstructor()
    {
        var box = new FakeBox();
        var options = _credentials.Options();
        options.ClientSecret = "wrong-secret";
        using var provider = new WriteableBoxFileProvider(options, TestCredentials.Session(box));

        var e = await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.AreEqual(400, e.ResponseInfo.StatusCode);
        Assert.AreEqual(0, box.TokensIssued);
        AssertCarriesNoCredential(e);
    }

    [TestMethod]
    public async Task RefusedTokenRefresh_CarriesNoCredential()
    {
        var box = new FakeBox();
        using var provider = _credentials.Provider(box);
        await provider.GetFileInfoAsync("a.txt");

        box.ExpireTokens();
        box.Intercept = request => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") }
            : null;

        AssertCarriesNoCredential(await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.GetFileInfoAsync("a.txt")));
    }

    [TestMethod]
    public async Task ReadyClient_IsUsedAsItIs_AndOutlivesTheProvider()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        var client = _credentials.Client(box);

        // No credentials in the options: the client signs in.
        var provider = new WriteableBoxFileProvider(new BoxFileProviderOptions(), client);
        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        await provider.DisposeAsync();

        var items = await client.Folders.GetFolderItemsAsync(FakeBox.RootId);
        Assert.AreEqual(1, items.Entries!.Count, "The provider never disposes a client it was given");
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetFileInfoAsync("a.txt"));
    }

    [TestMethod]
    public async Task RootFolderId_IsWhereTheProviderLives()
    {
        var box = new FakeBox();
        var tenant = box.AddFolder(FakeBox.RootId, "tenant");
        box.AddFile(tenant, "a.txt", "inside");
        box.AddFile(FakeBox.RootId, "a.txt", "outside");

        using var inside = _credentials.Provider(box, rootFolderId: tenant);
        Assert.AreEqual("inside", await ReadAsync(inside, "a.txt"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inside.DeleteAsync(""));

        foreach (var root in new[] { null, "", " " })
        {
            using var atTheTop = _credentials.Provider(box, rootFolderId: root);
            Assert.AreEqual("outside", await ReadAsync(atTheTop, "a.txt"));
        }

        using var below = _credentials.Provider(box, root: "tenant", rootFolderId: FakeBox.RootId);
        Assert.AreEqual("inside", await ReadAsync(below, "a.txt"));
    }

    [TestMethod]
    [DataRow("WriteableBoxFileProvider.json")]
    [DataRow("Lenient.Box.json")]
    public void StoredConfiguration_BuildsTheProvider_Offline(string golden)
    {
        // The 2026 shape, with a real test key in place of the golden's placeholder: building it does no network I/O now.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", golden)))!.AsObject();
        json["PrivateKey"] = _credentials.EncryptedPrivateKeyPem;
        json["Passphrase"] = _credentials.Passphrase;

        using var provider = WriteableFileProviderSerializer.FromJson(json.ToJsonString());
        var options = ((WriteableBoxFileProvider)provider).Options;
        Assert.AreEqual("client-id", options.ClientId);
        Assert.AreEqual("123456", options.EnterpriseId);
        Assert.AreEqual("0", options.RootFolderId);

        var written = WriteableFileProviderSerializer.ToJson(provider);
        Assert.AreEqual("WriteableBoxFileProvider", (string?)written["Type"]);
        foreach (var key in new[] { "ClientId", "ClientSecret", "EnterpriseId", "PublicKeyId", "PrivateKey", "Passphrase", "RootFolderId" })
            Assert.AreEqual((string?)json[key]?.ToString(), (string?)written[key], key);
        using var again = WriteableFileProviderSerializer.FromJson(written);
        CollectionAssert.AreEqual(options.RootDirectorySegments, ((WriteableBoxFileProvider)again).Options.RootDirectorySegments);
    }

    // The SDK keeps the token request in its error: the client secret and the signed assertion.
    private static void AssertCarriesNoCredential(BoxApiException e)
    {
        Assert.IsNull(e.RequestInfo.Body);
        CredentialAssert.CarriesNone(e);
    }

    private static async Task<string> ReadAsync(IWriteableFileProvider provider, string path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
