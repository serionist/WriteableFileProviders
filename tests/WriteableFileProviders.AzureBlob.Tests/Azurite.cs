using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testcontainers.Azurite;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// The Azurite blob emulator, started once per test run through Testcontainers and removed when the run ends. Without
/// Docker the tests that need it are inconclusive, unless WFP_REQUIRE_DOCKER=1 (set in CI), which makes them fail.
/// </summary>
internal static class Azurite
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.37.0";
    public const string AccountName = "devstoreaccount1";

    // Azurite's well-known development account key, published by Microsoft; not a secret.
    public const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static readonly Lazy<Task<AzuriteContainer>> _container = new(async () =>
    {
        // --skipApiVersionCheck: Azure.Storage.Blobs sends an x-ms-version newer than this Azurite knows.
        var container = new AzuriteBuilder(Image).WithCommand("--skipApiVersionCheck").Build();
        await container.StartAsync();
        return container;
    });

    public static bool DockerRequired => Environment.GetEnvironmentVariable("WFP_REQUIRE_DOCKER") == "1";

    /// <summary>Whether Azurite was started in this run; cleanup does nothing when it was not.</summary>
    public static bool Started => _container.IsValueCreated && _container.Value.IsCompletedSuccessfully;

    /// <summary>host:port/devstoreaccount1 of the running emulator: the form of the options' <c>Url</c>.</summary>
    public static string Url => new Uri(Start().GetBlobEndpoint()).Authority + "/" + AccountName;

    public static Uri ServiceUri => new($"http://{Url}");

    public static string NewContainerName() => "wfp-" + Guid.NewGuid().ToString("N");

    /// <summary>Options for a provider on Azurite, through the configuration path consumers use.</summary>
    public static AzureFileProviderOptions Options(string containerName, string? root = null) => new()
    {
        AccountName = AccountName,
        AccountKey = AccountKey,
        ContainerName = containerName,
        Protocol = "http",
        Url = Url,
        RootDirectoryPath = root,
    };

    /// <summary>A shared-key container client, through the given test handler when there is one.</summary>
    public static BlobContainerClient Container(string containerName, DelegatingHandler? handler = null, int retries = 2) =>
        new BlobServiceClient(ServiceUri, new StorageSharedKeyCredential(AccountName, AccountKey), ClientOptions(handler, retries)).GetBlobContainerClient(containerName);

    public static BlobClientOptions ClientOptions(DelegatingHandler? handler, int retries = 2)
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = retries;
        options.Retry.Delay = TimeSpan.FromMilliseconds(20);
        if (handler is not null)
        {
            handler.InnerHandler ??= new HttpClientHandler();
            options.Transport = new HttpClientTransport(new HttpClient(handler));
        }
        return options;
    }

    public static async Task<BlobContainerClient> CreateContainerAsync(string containerName)
    {
        var container = Container(containerName);
        await container.CreateIfNotExistsAsync();
        return container;
    }

    public static async Task DeleteContainerAsync(string containerName)
    {
        if (Started)
            await Container(containerName).DeleteIfExistsAsync();
    }

    /// <summary>
    /// Uploads a blob named by the names joined with '/', past the provider. Not a name with a control character, which an
    /// XML listing cannot carry back.
    /// </summary>
    /// <returns>False for a name that cannot be listed back.</returns>
    public static async Task<bool> StoreRawAsync(string containerName, IReadOnlyList<string> names, string text)
    {
        if (names.Any(n => n.Any(char.IsControl)))
            return false;
        await Container(containerName).GetBlobClient(string.Join('/', names)).UploadAsync(BinaryData.FromString(text), overwrite: true);
        return true;
    }

    private static AzuriteContainer Start()
    {
        try
        {
            return _container.Value.GetAwaiter().GetResult();
        }
        catch (Exception e) when (!DockerRequired)
        {
            Assert.Inconclusive($"Azurite could not start through Testcontainers (is Docker running?): {e.Message}");
            throw;
        }
    }
}
