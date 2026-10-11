// WriteableFileProviders in a native AOT app. CI publishes it with native AOT, which fails on any trim or AOT warning
// (WriteableFileProviders.Aot.csproj), then runs the binary, which uses each package for real:
//
// - Configuration: options bound by the configuration binder's source generator, and a provider built from them in
//   code, the configuration path of an AOT app (WriteableFileProviderSerializer warns there).
// - Physical: the round trip in Checks, in a temp folder.
// - AspNetCore: the gateway on Kestrel, on loopback, serving a download URL and an upload URL over HTTP.
// - SharePoint: the round trip on the in-memory Microsoft Graph of the SharePoint tests.
// - Azure Blob, Amazon S3 and MinIO: the round trip, and their own signed URLs used over HTTP, on Azurite and MinIO
//   when WFP_AOT_AZURITE and WFP_AOT_MINIO hold their host:port (CI starts both); skipped otherwise.
//
// It prints a line per check and exits with 1 on the first failure.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using WriteableFileProviders;
using WriteableFileProviders.AmazonS3;
using WriteableFileProviders.AspNetCore;
using WriteableFileProviders.AzureBlob;
using WriteableFileProviders.Minio;
using WriteableFileProviders.Physical;
using WriteableFileProviders.SharePoint;
using WriteableFileProviders.SharePoint.Tests;

var temp = Directory.CreateTempSubdirectory("wfp-aot-");
try
{
    Checks.Configuration();

    await using (var physical = new WriteablePhysicalFileProvider(Path.Combine(temp.FullName, "physical")))
        await Checks.RoundTripAsync("Physical", physical);

    await Checks.GatewayAsync(Path.Combine(temp.FullName, "gateway"));

    var graph = new GraphFake();
    using (var client = new GraphServiceClient(new HttpClient(graph.CreateHandler()), new AnonymousAuthenticationProvider(), GraphFake.BaseUrl))
    await using (var sharePoint = new WriteableSharePointFileProvider(new SharePointFileProviderOptions { SiteUrl = Checks.SiteUrl, DocumentLibraryName = "Documents" }, client))
        await Checks.RoundTripAsync("SharePoint", sharePoint);

    if (Environment.GetEnvironmentVariable("WFP_AOT_AZURITE") is { Length: > 0 } azurite)
    {
        await using var azure = new WriteableAzureBlobFileProvider(new AzureBlobFileProviderOptions
        {
            AccountName = Checks.AzuriteAccount,
            AccountKey = Checks.AzuriteKey,
            ContainerName = "wfp-aot",
            Protocol = "http",
            Url = $"{azurite}/{Checks.AzuriteAccount}",
            CreateContainerIfMissing = true,
        });
        await Checks.RoundTripAsync("Azure Blob", azure);
        await Checks.SignedUrlsAsync("Azure Blob", azure, azure.GetSignedUrlAsync);
    }
    else
    {
        Console.WriteLine("skip  Azure Blob: WFP_AOT_AZURITE is not set");
    }

    if (Environment.GetEnvironmentVariable("WFP_AOT_MINIO") is { Length: > 0 } minio)
    {
        await using var s3 = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions("s3", Checks.MinioUser, Checks.MinioPassword, "wfp-aot", "us-east-1")
        {
            ServiceUrl = $"http://{minio}",
            ForcePathStyle = true,
            CreateBucketIfMissing = true,
        });
        await Checks.RoundTripAsync("Amazon S3", s3);
        await Checks.SignedUrlsAsync("Amazon S3", s3, s3.GetSignedUrlAsync);

        await using var minioFiles = new WriteableMinioFileProvider(new MinioFileProviderOptions("minio", minio, Checks.MinioUser, Checks.MinioPassword, "wfp-aot")
        {
            CreateBucketIfMissing = true,
        });
        await Checks.RoundTripAsync("MinIO", minioFiles);
        await Checks.SignedUrlsAsync("MinIO", minioFiles, minioFiles.GetSignedUrlAsync);
    }
    else
    {
        Console.WriteLine("skip  Amazon S3 and MinIO: WFP_AOT_MINIO is not set");
    }

    Console.WriteLine("All checks passed.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAILED: {e}");
    return 1;
}
finally
{
    temp.Delete(recursive: true);
}

/// <summary>What the app does with each provider.</summary>
internal static class Checks
{
    public const string SiteUrl = "https://contoso.sharepoint.com/sites/Team%20Site";

    // Azurite's well-known development account and MinIO's documented default root credentials: public test values, not
    // secrets.
    public const string AzuriteAccount = "devstoreaccount1";
    public const string AzuriteKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    public const string MinioUser = "minioadmin";
    public const string MinioPassword = "minioadmin";

    /// <summary>Binds options from configuration, as an AOT app does, and builds providers from them.</summary>
    public static void Configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:RootDirectoryPath"] = "tenant/files",
            ["Azure:AccountName"] = AzuriteAccount,
            ["Azure:ContainerName"] = "files",
            ["Azure:UseDefaultAzureCredential"] = "true",
            ["S3:RootDirectoryPath"] = "tenant",
            ["S3:BucketName"] = "bucket",
            ["S3:BucketRegion"] = "eu-west-1",
            ["S3:UseDefaultCredentials"] = "true",
        }).Build();
        var azure = configuration.GetSection("Azure").Get<AzureBlobFileProviderOptions>()!;
        var s3 = configuration.GetSection("S3").Get<AmazonS3FileProviderOptions>()!;
        Check("Configuration", "bind options",
            azure is { AccountName: AzuriteAccount, ContainerName: "files", UseDefaultAzureCredential: true, RootDirectorySegments: ["tenant", "files"] }
            && s3 is { BucketName: "bucket", BucketRegion: "eu-west-1", UseDefaultCredentials: true, RootDirectorySegments: ["tenant"] });
        using (new WriteableAzureBlobFileProvider(azure))
        using (new WriteableAmazonS3FileProvider(s3))
            Check("Configuration", "build providers from bound options", true);
    }

    /// <summary>
    /// Writes, appends, reads, reads a range, copies, moves, lists, edits a local copy and deletes, under aot/.
    /// </summary>
    public static async Task RoundTripAsync(string name, IWriteableFileProvider files)
    {
        await files.UpdateFileAsync("aot/a.txt", Text("hello"));
        await files.UpdateFileAsync("aot/a.txt", Text(" world"), UpdateFileOptions.Append);
        Check(name, "write and append", await ReadAsync(files, "aot/a.txt") == "hello world");

        await using (var range = await files.ReadRangeAsync("aot/a.txt", 6, 5))
        using (var reader = new StreamReader(range))
            Check(name, "read a range", await reader.ReadToEndAsync() == "world");

        await files.CopyAsync("aot/a.txt", "aot/b.txt");
        await files.MoveAsync("aot/b.txt", "aot/sub/c.txt");
        var names = new List<string>();
        await foreach (var entry in files.EnumerateAsync("aot"))
            names.Add(entry.Name);
        names.Sort(StringComparer.Ordinal);
        Check(name, "copy, move and list", names is ["a.txt", "sub"] && (await files.GetFileInfoAsync("aot/sub/c.txt")).Length == 11);

        await using (var local = await files.CreateLocalFileStreamAsync("aot/d.txt", readOnly: false))
            await local.WriteAsync(Encoding.UTF8.GetBytes("local"));
        Check(name, "local file stream", await ReadAsync(files, "aot/d.txt") == "local");

        Check(name, "delete", await files.DeleteAsync("aot") && !(await files.GetFileInfoAsync("aot/a.txt")).Exists);
    }

    /// <summary>A download URL and an upload URL, each used over HTTP.</summary>
    public static async Task SignedUrlsAsync(string name, IWriteableFileProvider files, Func<ProviderPath, SignedUrlOptions, CancellationToken, Task<SignedUrl>> sign)
    {
        await files.UpdateFileAsync("signed/a.txt", Text("signed"));
        using var http = new HttpClient();

        var download = await sign("signed/a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) }, default);
        Check(name, "download URL", await http.GetStringAsync(download.Url) == "signed");

        var upload = await sign("signed/b.txt", new SignedUrlOptions { Access = SignedUrlAccess.Write, Validity = TimeSpan.FromMinutes(5) }, default);
        using var request = new HttpRequestMessage(new HttpMethod(upload.Method), upload.Url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("uploaded")) };
        foreach (var (header, value) in upload.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header, value))
                request.Content.Headers.TryAddWithoutValidation(header, value);
        }
        using var response = await http.SendAsync(request);
        Check(name, "upload URL", response.IsSuccessStatusCode && await ReadAsync(files, "signed/b.txt") == "uploaded");

        await files.DeleteAsync("signed");
    }

    /// <summary>The gateway on Kestrel, over the local disk, which signs no URLs of its own.</summary>
    public static async Task GatewayAsync(string root)
    {
        var port = FreePort();
        var publicBaseUri = new Uri($"http://127.0.0.1:{port}/");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddWriteableFileProvider(_ => new WriteablePhysicalFileProvider(root));
        builder.Services.AddWriteableFileProviderGateway(publicBaseUri);

        await using var app = builder.Build();
        app.MapWriteableFileProviderGateway();
        await app.StartAsync();
        var urls = app.Services.GetRequiredService<IWriteableFileUrlService>();
        await SignedUrlsAsync("Gateway", app.Services.GetRequiredService<IWriteableFileProvider>(), urls.GetSignedUrlAsync);
        await app.StopAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAsync(IWriteableFileProvider files, ProviderPath path)
    {
        await using var stream = await files.ReadFileAsync(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    internal static void Check(string provider, string what, bool passed)
    {
        Console.WriteLine($"{(passed ? "ok  " : "FAIL")}  {provider}: {what}");
        if (!passed)
            throw new InvalidOperationException($"{provider}: {what} failed.");
    }
}
