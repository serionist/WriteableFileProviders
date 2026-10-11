using System.Net;
using System.Text;
using System.Text.Json;
using Google.Cloud.Storage.V1;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>
/// A real bucket for the live suites, from environment variables or an optional <c>testsettings.json</c> next to the
/// test assembly (git-ignored): run them only against a bucket you own. They run only with <c>WFP_LIVE=1</c>; otherwise,
/// or without a bucket and a service account key, they are inconclusive. Each test works under its own folder, which it
/// deletes.
/// </summary>
/// <remarks>
/// Environment: <c>WFP_GCS_BUCKET</c>, <c>WFP_GCS_CLIENT_EMAIL</c> and <c>WFP_GCS_PRIVATE_KEY</c> (or
/// <c>WFP_GCS_CREDENTIALS_JSON</c>), optionally <c>WFP_GCS_PROJECT_ID</c> and <c>WFP_GCS_ROOT</c>. In
/// <c>testsettings.json</c>: the same as <c>GoogleCloudStorageFileProvider_BucketName</c>, <c>_ClientEmail</c>,
/// <c>_PrivateKey</c>, <c>_CredentialsJson</c>, <c>_ProjectId</c> and <c>_RootPath</c>.
/// </remarks>
internal static class GcsLive
{
    private static readonly Lazy<GoogleCloudStorageFileProviderOptions?> _options = new(Load);

    /// <summary>Options for the live bucket, under the given folder of its root.</summary>
    /// <param name="root">A folder under the configured root, or null for the root.</param>
    public static GoogleCloudStorageFileProviderOptions Options(string? root = null)
    {
        if (_options.Value is not { } settings)
        {
            Assert.Inconclusive("The live suite runs with WFP_LIVE=1 and a bucket and service account key in WFP_GCS_* or testsettings.json.");
            throw new InvalidOperationException();
        }
        return new GoogleCloudStorageFileProviderOptions
        {
            BucketName = settings.BucketName,
            ClientEmail = settings.ClientEmail,
            PrivateKey = settings.PrivateKey,
            CredentialsJson = settings.CredentialsJson,
            ProjectId = settings.ProjectId,
            RootDirectorySegments = [.. settings.RootDirectorySegments, .. root is null ? [] : root.Split('/')],
        };
    }

    /// <summary>A client of the live bucket, signed in with the same key.</summary>
    /// <param name="httpClientFactory">An HTTP seam, or null.</param>
    public static StorageClient CreateClient(Google.Apis.Http.IHttpClientFactory? httpClientFactory = null)
    {
        var options = Options();
        var credential = !string.IsNullOrEmpty(options.CredentialsJson)
            ? Google.Apis.Auth.OAuth2.CredentialFactory.FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(options.CredentialsJson).ToGoogleCredential()
            : new Google.Apis.Auth.OAuth2.ServiceAccountCredential(new Google.Apis.Auth.OAuth2.ServiceAccountCredential.Initializer(options.ClientEmail)
                .FromPrivateKey(WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(options.PrivateKey))).ToGoogleCredential();
        var builder = new StorageClientBuilder { GoogleCredential = credential };
        if (httpClientFactory is not null)
            builder.HttpClientFactory = httpClientFactory;
        return builder.Build();
    }

    private static GoogleCloudStorageFileProviderOptions? Load()
    {
        if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1")
            return null;
        var file = Path.Combine(AppContext.BaseDirectory, "testsettings.json");
        var settings = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file)) ?? [] : [];
        string? Get(string variable, string key) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
            : settings.TryGetValue("GoogleCloudStorageFileProvider_" + key, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text ? text
            : null;
        var options = new GoogleCloudStorageFileProviderOptions
        {
            BucketName = Get("WFP_GCS_BUCKET", "BucketName"),
            ClientEmail = Get("WFP_GCS_CLIENT_EMAIL", "ClientEmail"),
            PrivateKey = Get("WFP_GCS_PRIVATE_KEY", "PrivateKey"),
            CredentialsJson = Get("WFP_GCS_CREDENTIALS_JSON", "CredentialsJson"),
            ProjectId = Get("WFP_GCS_PROJECT_ID", "ProjectId"),
            RootDirectoryPath = Get("WFP_GCS_ROOT", "RootPath"),
        };
        var hasKey = (options.ClientEmail is not null && options.PrivateKey is not null) || options.CredentialsJson is not null;
        return options.BucketName is not null && hasKey ? options : null;
    }
}

/// <summary>The conformance suite against a real bucket (WFP_LIVE=1); never run by CI.</summary>
[TestClass]
[TestCategory("Live")]
public sealed class GcsLiveConformanceTests : ProviderConformanceTests
{
    protected override bool KeepsContentType => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    protected override IWriteableFileProvider CreateProvider(string? root) => new WriteableGoogleCloudStorageFileProvider(GcsLive.Options(root));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer()
    {
        var options = GcsLive.Options();
        options.BucketName = FakeGcs.MissingBucketName();
        return new WriteableGoogleCloudStorageFileProvider(options);
    }
}

/// <summary>
/// What fake-gcs-server does not enforce, so only a real bucket checks it (WFP_LIVE=1): the conditions on copies, on
/// deletes and on signed uploads, and the signature and expiry of signed URLs.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class GcsLiveAtomicityTests
{
    private readonly List<IDisposable> _disposables = [];
    private string _root = "";
    private WriteableGoogleCloudStorageFileProvider? _cleanup;

    [TestInitialize]
    public void StartTest()
    {
        _root = "wfp-" + Guid.NewGuid().ToString("N")[..12];
        _cleanup = new WriteableGoogleCloudStorageFileProvider(GcsLive.Options());
    }

    [TestCleanup]
    public async Task EndTest()
    {
        try
        {
            if (_cleanup is not null)
                await _cleanup.DeleteAsync(_root);
        }
        finally
        {
            _cleanup?.Dispose();
            foreach (var disposable in _disposables)
                disposable.Dispose();
        }
    }

    private WriteableGoogleCloudStorageFileProvider Provider(FaultInjection http)
    {
        var client = GcsLive.CreateClient(http);
        _disposables.Add(client);
        var provider = new WriteableGoogleCloudStorageFileProvider(client, GcsLive.Options(_root));
        _disposables.Add(provider);
        return provider;
    }

    private static async Task<string> Read(IWriteableFileProvider provider, string path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyAndMove_RefuseADestinationWrittenAfterTheCheck(bool move)
    {
        // The emulator ignores ifGenerationMatch=0 on a copy and would overwrite; Cloud Storage refuses it.
        var http = new FaultInjection();
        var provider = Provider(http);
        var racer = Provider(new FaultInjection());
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        http.OnRequest = async request =>
        {
            if (FaultInjection.IsCopy(request))
            {
                http.OnRequest = null;
                await racer.UpdateFileAsync("b.txt", TestStreams.Text("theirs"));
            }
            return null;
        };
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => move ? provider.MoveAsync("a.txt", "b.txt") : provider.CopyAsync("a.txt", "b.txt"));
        Assert.AreEqual("theirs", await Read(provider, "b.txt"));
        Assert.AreEqual("a", await Read(provider, "a.txt"));
    }

    [TestMethod]
    public async Task SignedCreateUrl_RefusesAnOverwrite()
    {
        var provider = Provider(new FaultInjection());
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("first"));
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Permission = SignedUrlAccess.Create });
        using var http = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, url.Url) { Content = new StringContent("second") };
        foreach (var (name, value) in url.Headers)
            put.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(put);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.AreEqual("first", await Read(provider, "f.txt"));
    }

    [TestMethod]
    public async Task SignedReadUrl_WorksUntilItExpires()
    {
        var provider = Provider(new FaultInjection());
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("content"));
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromSeconds(3), DownloadFileName = "named.txt", ContentType = "text/csv" });
        using var http = new HttpClient();
        using (var response = await http.GetAsync(url.Url))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("content", await response.Content.ReadAsStringAsync());
            Assert.AreEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("named.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        }
        await Task.Delay(TimeSpan.FromSeconds(5));
        using (var response = await http.GetAsync(url.Url))
            Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task PinnedRead_OfAGenerationThatIsGoneThrows()
    {
        // The emulator honours only the generation selector; Cloud Storage also checks ifGenerationMatch.
        var provider = Provider(new FaultInjection());
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("0123456789"));
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("changed!!!"));
        await Assert.ThrowsAsync<IOException>(() => provider.ReadRangeAsync("f.txt", 2, 2, new ReadOptions { IfMatch = etag }));
    }
}
