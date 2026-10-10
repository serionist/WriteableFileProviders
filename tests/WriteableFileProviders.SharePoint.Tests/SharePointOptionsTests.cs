using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>The options: validation, the three ways to sign in (D17), and where the root sits in the library (D7, P37).</summary>
[TestClass]
public sealed class SharePointOptionsTests
{
    private static SharePointFileProviderOptions Secretless(Action<SharePointFileProviderOptions> configure)
    {
        var options = TestGraph.Options("r");
        options.ClientSecret = null;
        configure(options);
        return options;
    }

    private static async Task AssertWorksOffline(SharePointFileProviderOptions options)
    {
        // The fake's host is not one of Graph's, so the SDK asks the credential for no token: it is built, never called.
        var fake = new GraphFake();
        await using var provider = TestGraph.OwnProvider(fake, options);
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("x"));
        Assert.AreEqual("x", Encoding.UTF8.GetString(fake.GetFile("r/f.txt")!));
    }

    [TestMethod]
    public void Constructors_RefuseNulls()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableSharePointFileProvider(null!));
        using var client = TestGraph.Client(new GraphFake());
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableSharePointFileProvider(null!, client));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableSharePointFileProvider(TestGraph.Options(), null!));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(" ")]
    [DataRow("/sites/Team")]
    [DataRow("http://contoso.sharepoint.com/sites/Team")]
    [DataRow("not a url")]
    public void SiteUrl_MustBeAnAbsoluteHttpsUrl(string? siteUrl)
    {
        var options = TestGraph.Options();
        options.SiteUrl = siteUrl;
        Assert.AreEqual("options", Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(options)).ParamName);
        using var client = TestGraph.Client(new GraphFake());
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(options, client));
    }

    [TestMethod]
    public void ClientIdsAndASecretOrCertificate_AreRequired()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(Secretless(_ => { })));
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(Secretless(o => (o.TenantId, o.ClientSecret) = (null, "s"))));
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(Secretless(o => (o.ClientId, o.ClientSecret) = ("", "s"))));
        // A caller's client needs none of them.
        using var client = TestGraph.Client(new GraphFake());
        using var provider = new WriteableSharePointFileProvider(new SharePointFileProviderOptions { SiteUrl = TestGraph.SiteUrl }, client);
    }

    [TestMethod]
    public async Task ClientSecret_SignsIn() => await AssertWorksOffline(TestGraph.Options("r"));

    [TestMethod]
    public async Task Certificate_FromBase64Pkcs12_SignsIn()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=wfp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var options = Secretless(o =>
        {
            o.ClientCertificateBase64 = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "pfx-password"));
            o.ClientCertificatePassword = "pfx-password";
            o.ClientSecret = "ignored: the certificate comes first";
        });
        await AssertWorksOffline(options);

        // Without its password, with a wrong one, not base64, or without its private key: refused at construction.
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(Secretless(o => (o.ClientCertificateBase64, o.ClientCertificatePassword) = (options.ClientCertificateBase64, "wrong"))));
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(Secretless(o => o.ClientCertificateBase64 = "not base64!")));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        var withoutKey = Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12));
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(Secretless(o => o.ClientCertificateBase64 = withoutKey))).Message, "private key");
    }

    [TestMethod]
    public async Task DefaultAzureCredential_SignsIn_WithOrWithoutIds()
    {
        await AssertWorksOffline(Secretless(o => (o.UseDefaultAzureCredential, o.TenantId, o.ClientId) = (true, null, null)));
        await AssertWorksOffline(Secretless(o => o.UseDefaultAzureCredential = true));
    }

    [TestMethod]
    public async Task ConstructingFromOptions_SendsNoRequest_AndDisposingReleasesTheClient()
    {
        var fake = new GraphFake();
        var provider = TestGraph.OwnProvider(fake, TestGraph.Options("r"));
        Assert.IsEmpty(fake.Requests);
        provider.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetFileInfoAsync("f.txt"));
    }

    // ----- D7: the work folder is the root's first part (P37) -----

    // The 2026 rule, kept here as the oracle: root and caller segments joined, one leading copy of the work folder
    // stripped from the result, the rest placed in the work folder.
    private static string Placed2026(string? workFolder, string? root, string path)
    {
        var work = SharePointPaths.WorkFolderSegments(workFolder);
        string[] absolute = [.. (root ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries), .. path.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        var startsWithWork = work.Length > 0 && absolute.Length >= work.Length
            && work.Select((segment, i) => string.Equals(segment, absolute[i], StringComparison.OrdinalIgnoreCase)).All(x => x);
        return string.Join('/', [.. work, .. startsWithWork ? absolute[work.Length..] : absolute]);
    }

    [TestMethod]
    [DataRow(null, null, "a/f.txt", "a/f.txt", true, DisplayName = "no work folder")]
    [DataRow(null, "r", "f.txt", "r/f.txt", true, DisplayName = "no work folder, a root")]
    [DataRow("Main/Work", null, "f.txt", "Main/Work/f.txt", true, DisplayName = "a work folder")]
    [DataRow(@"\Main\Work\", null, "f.txt", "Main/Work/f.txt", true, DisplayName = "a work folder with other separators")]
    [DataRow("Main/Work", "r", "f.txt", "Main/Work/r/f.txt", true, DisplayName = "a root inside the work folder")]
    [DataRow("Main/Work", "Main/Work", "f.txt", "Main/Work/f.txt", true, DisplayName = "the root is a full copy of the work folder")]
    [DataRow("Main/Work", "main/WORK", "f.txt", "Main/Work/f.txt", true, DisplayName = "the copy differs in case")]
    [DataRow("Main/Work", "Main/Work/r", "f.txt", "Main/Work/r/f.txt", true, DisplayName = "the root continues the work folder")]
    [DataRow("Main/Work", "Main/Work/Main/Work", "f.txt", "Main/Work/Main/Work/f.txt", true, DisplayName = "two copies: one is dropped")]
    [DataRow("Main/Work", "Main/Other", "f.txt", "Main/Work/Main/Other/f.txt", true, DisplayName = "the root shares only a partial prefix")]
    [DataRow("Main/Work", "Main/Work", "Main/Work/f.txt", "Main/Work/Main/Work/f.txt", true, DisplayName = "a full copy, and a caller path that repeats the work folder")]
    [DataRow("Main/Work", null, "Main/Work/f.txt", "Main/Work/Main/Work/f.txt", false, DisplayName = "relocated: a caller path that starts with the work folder (F7)")]
    [DataRow("Reports", null, "reports/2026.csv", "Reports/reports/2026.csv", false, DisplayName = "relocated: a caller path that starts with the work folder in another case")]
    [DataRow("Main/Work", "r", "Main/Work/f.txt", "Main/Work/r/Main/Work/f.txt", true, DisplayName = "a root, and a caller path that repeats the work folder")]
    public async Task WorkFolderAndRoot_PlaceFilesAsTheTableSays(string? workFolder, string? root, string path, string expected, bool sameAs2026)
    {
        var fake = new GraphFake();
        await using (var provider = TestGraph.OwnProvider(fake, TestGraph.Options(root, workFolder)))
            await provider.UpdateFileAsync(path, TestStreams.Text("x"));
        Assert.AreEqual("x", Encoding.UTF8.GetString(fake.GetFile(expected) ?? []), $"Not at '{expected}'");
        Assert.AreEqual(sameAs2026, string.Equals(Placed2026(workFolder, root, path), expected, StringComparison.OrdinalIgnoreCase),
            $"2026 placed it at '{Placed2026(workFolder, root, path)}'");
    }

    [TestMethod]
    [DataRow("Main/Work", "Main")]
    [DataRow("Main/Work", "main")]
    [DataRow("A/B/C", "A/B")]
    public void Root_ThatIsAStrictPrefixOfTheWorkFolder_IsRefused(string workFolder, string root)
    {
        // 2026 put "Work/f.txt" at Main/Work/f.txt and "f.txt" at Main/Work/Main/f.txt: no root keeps both.
        var error = Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(TestGraph.Options(root, workFolder)));
        StringAssert.Contains(error.Message, "migration-2027");
        using var client = TestGraph.Client(new GraphFake());
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(TestGraph.Options(root, workFolder), client));
    }

    [TestMethod]
    [DataRow("a/../b", null)]
    [DataRow("a:b", null)]
    [DataRow(null, "x%41")]
    [DataRow("Main", "a/./b")]
    public void WorkFolderAndRoot_MustBeValidFolders(string? workFolder, string? root) =>
        Assert.AreEqual("options", Assert.ThrowsExactly<ArgumentException>(() => new WriteableSharePointFileProvider(TestGraph.Options(root, workFolder))).ParamName);

    [TestMethod]
    public void Options_KeepWhatWasSet()
    {
        var options = new SharePointFileProviderOptions("https://s.example/sites/x", "Lib", "t", "c", "s", "W")
        {
            ClientCertificateBase64 = "b",
            ClientCertificatePassword = "p",
            UseDefaultAzureCredential = true,
        };
        Assert.AreEqual("https://s.example/sites/x|Lib|t|c|s|W|b|p|True",
            string.Join('|', options.SiteUrl, options.DocumentLibraryName, options.TenantId, options.ClientId, options.ClientSecret, options.WorkFolderPath,
                options.ClientCertificateBase64, options.ClientCertificatePassword, options.UseDefaultAzureCredential));
    }
}
