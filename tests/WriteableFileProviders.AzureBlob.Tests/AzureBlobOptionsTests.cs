using System.Net;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>Options and construction, offline: no request is sent unless a test answers it itself.</summary>
[TestClass]
public sealed class AzureBlobOptionsTests
{
    private static AzureBlobFileProviderOptions Valid() => new()
    {
        AccountName = "account",
        AccountKey = Azurite.AccountKey,
        ContainerName = "files",
    };

    [TestMethod]
    public void Constructor_SendsNothing()
    {
        // An endpoint that cannot answer: a constructor that sent a request would fail or hang.
        var options = Valid();
        options.Protocol = "http";
        options.Url = "127.0.0.1:1/account";
        using var provider = new WriteableAzureBlobFileProvider(options);
        Assert.AreEqual(new Uri("http://127.0.0.1:1/account/files"), provider.Container.Uri);
        Assert.IsTrue(provider.Container.CanGenerateSasUri);
    }

    [TestMethod]
    public void Endpoint_DefaultsToTheAccountOverHttps()
    {
        using var provider = new WriteableAzureBlobFileProvider(Valid());
        Assert.AreEqual(new Uri("https://account.blob.core.windows.net/files"), provider.Container.Uri);
        Assert.AreEqual("account", provider.Container.AccountName);
    }

    [TestMethod]
    [DataRow(null, "https")]
    [DataRow("", "https")]
    [DataRow("http", "http")]
    [DataRow("https", "https")]
    [DataRow("HTTPS", "https")]
    [DataRow("Http", "http")]
    public void Protocol_AcceptsHttpAndHttpsInAnyCase(string? protocol, string scheme)
    {
        var options = Valid();
        options.Protocol = protocol;
        options.Url = "127.0.0.1:10000/account";
        using var provider = new WriteableAzureBlobFileProvider(options);
        Assert.AreEqual(scheme, provider.Container.Uri.Scheme);
        Assert.AreEqual(protocol, provider.Options.Protocol, "The options are never changed");
    }

    [TestMethod]
    [DataRow("ftp")]
    [DataRow("https://")]
    [DataRow("file")]
    [DataRow(" http")]
    public void Protocol_RefusesOtherSchemes(string protocol)
    {
        var options = Valid();
        options.Protocol = protocol;
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options));
        StringAssert.Contains(e.Message, "Protocol");
    }

    [TestMethod]
    public void Url_MayBeAFullUrl()
    {
        var options = Valid();
        options.Url = "https://contoso.blob.example.net";
        using var provider = new WriteableAzureBlobFileProvider(options);
        Assert.AreEqual(new Uri("https://contoso.blob.example.net/files"), provider.Container.Uri);

        options.Protocol = "http";
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options), "A scheme that contradicts Protocol");
        options.Url = "http:// not a host";
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options));
    }

    [TestMethod]
    public void RequiredOptions_AreNamed()
    {
        static string Refusal(Action<AzureBlobFileProviderOptions> change)
        {
            var options = Valid();
            change(options);
            return Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options)).Message;
        }
        StringAssert.Contains(Refusal(o => o.ContainerName = null), "ContainerName");
        StringAssert.Contains(Refusal(o => o.ContainerName = " "), "ContainerName");
        StringAssert.Contains(Refusal(o => o.AccountKey = null), "AccountKey");
        StringAssert.Contains(Refusal(o => o.AccountName = ""), "AccountName");
        StringAssert.Contains(Refusal(o => o.AccountKey = "not base64!"), "AccountKey");
        StringAssert.Contains(Refusal(o => o.UseDefaultAzureCredential = true), "not both");
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableAzureBlobFileProvider((AzureBlobFileProviderOptions)null!));
    }

    [TestMethod]
    public void RefusalsNeverShowTheKey()
    {
        var options = Valid();
        options.AccountKey = "c2VjcmV0!!";
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options));
        Assert.IsFalse(e.ToString().Contains("c2VjcmV0", StringComparison.Ordinal), e.ToString());
    }

    [TestMethod]
    public void DefaultAzureCredential_NeedsHttpsAndAnEndpoint()
    {
        var options = new AzureBlobFileProviderOptions { ContainerName = "files", UseDefaultAzureCredential = true };
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options)).Message, "AccountName or Url");

        options.AccountName = "account";
        options.Protocol = "http";
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options)).Message, "https");

        options.Protocol = null;
        using var provider = new WriteableAzureBlobFileProvider(options);
        Assert.AreEqual(new Uri("https://account.blob.core.windows.net/files"), provider.Container.Uri);
        Assert.IsFalse(provider.Container.CanGenerateSasUri, "A token credential signs no service SAS");

        options.AccountName = null;
        options.Url = "custom.blob.example.net";
        using var byUrl = new WriteableAzureBlobFileProvider(options);
        Assert.AreEqual(new Uri("https://custom.blob.example.net/files"), byUrl.Container.Uri);
    }

    [TestMethod]
    public async Task DefaultAzureCredential_SignsRequestsWithABearerToken()
    {
        var credential = new FakeCredential();
        var handler = new StubHandler(_ => StubHandler.BlobProperties());
        var options = new AzureBlobFileProviderOptions { AccountName = "account", ContainerName = "files", UseDefaultAzureCredential = true };
        using var provider = new WriteableAzureBlobFileProvider(options, Offline(handler), credential);

        await provider.GetFileInfoAsync("a.txt");
        var request = handler.Requests.First();
        Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
        Assert.AreEqual("fake-token", request.Headers.Authorization?.Parameter);
        Assert.AreEqual("https://account.blob.core.windows.net/files/a.txt", request.RequestUri!.GetLeftPart(UriPartial.Path));
        Assert.IsGreaterThan(0, credential.Calls);
    }

    [TestMethod]
    public void ClientConstructor_TakesTheClientAsItIs()
    {
        var client = new BlobContainerClient(new Uri("http://127.0.0.1:1/account/files"), new StorageSharedKeyCredential("account", Azurite.AccountKey));
        using var provider = new WriteableAzureBlobFileProvider(new(), client);
        Assert.AreSame(client, provider.Container);
        Assert.AreEqual(0, provider.Options.RootDirectorySegments.Length);

        using var rooted = new WriteableAzureBlobFileProvider(new AzureBlobFileProviderOptions { ContainerName = "files", RootDirectoryPath = "a/b" }, client);
        CollectionAssert.AreEqual(new[] { "a", "b" }, rooted.Options.RootDirectorySegments);

        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(new AzureBlobFileProviderOptions { ContainerName = "other" }, client));
        StringAssert.Contains(e.Message, "other");
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableAzureBlobFileProvider(new(), (BlobContainerClient)null!));
        var noOptions = Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableAzureBlobFileProvider(null!, client));
        Assert.AreEqual("options", noOptions.ParamName);
    }

    [TestMethod]
    public void TheObsolete2026OptionsName_BuildsTheSameProvider_AndWritesTheSameConfiguration()
    {
#pragma warning disable CS0618 // the 2026 name is what this test pins
        var old = new AzureFileProviderOptions { AccountName = "account", AccountKey = Azurite.AccountKey, ContainerName = "files", RootDirectoryPath = "a" };
#pragma warning restore CS0618
        var current = new AzureBlobFileProviderOptions { AccountName = "account", AccountKey = Azurite.AccountKey, ContainerName = "files", RootDirectoryPath = "a" };

        using var fromOld = new WriteableAzureBlobFileProvider(old);
        using var fromCurrent = new WriteableAzureBlobFileProvider(current);
        var json = WriteableFileProviderSerializer.ToJsonString(fromOld);

        Assert.AreEqual(WriteableFileProviderSerializer.ToJsonString(fromCurrent), json);
        using var readBack = (WriteableAzureBlobFileProvider)WriteableFileProviderSerializer.FromJson(json);
        Assert.AreEqual(typeof(AzureBlobFileProviderOptions), readBack.Options.GetType());
        Assert.AreEqual(json, WriteableFileProviderSerializer.ToJsonString(readBack));
    }

    [TestMethod]
    public async Task Dispose_LeavesAGivenClientUsable()
    {
        var handler = new StubHandler(_ => AzureHandler.Error(HttpStatusCode.NotFound, "BlobNotFound"));
        var client = new BlobContainerClient(new Uri("http://127.0.0.1:1/account/files"), new StorageSharedKeyCredential("account", Azurite.AccountKey), Offline(handler));
        var provider = new WriteableAzureBlobFileProvider(new(), client);
        await provider.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.IsFalse((await client.GetBlobClient("a.txt").ExistsAsync()).Value, "The client still works after the provider is disposed");
    }

    /// <summary>Client options whose every request the handler answers, with no retries.</summary>
    internal static BlobClientOptions Offline(HttpMessageHandler handler)
    {
        var options = Azurite.ClientOptions(null, retries: 0);
        options.Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(handler));
        return options;
    }
}
