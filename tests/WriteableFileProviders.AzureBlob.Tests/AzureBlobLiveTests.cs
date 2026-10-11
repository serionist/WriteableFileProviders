using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// The conformance suite against a real storage account: run it only against an account you own. Without WFP_LIVE=1 it is
/// inconclusive; with it, it takes the account from environment variables or from an optional testsettings.json next to
/// this project (see testsettings.sample.json), fails naming what is missing, and works under its own
/// <c>wfp-test-&lt;guid&gt;/</c> folder, deleted afterwards.
/// </summary>
/// <remarks>
/// Environment variables: WFP_AZURE_ACCOUNT_NAME, WFP_AZURE_CONTAINER, and either WFP_AZURE_ACCOUNT_KEY or
/// WFP_AZURE_USE_DEFAULT_CREDENTIAL=1 (Microsoft Entra ID through DefaultAzureCredential); optionally WFP_AZURE_URL and
/// WFP_AZURE_ROOT. The testsettings.json keys are AzureBlobFileProvider_AccountName, _AccountKey, _ContainerName and
/// _RootPath, as the 2026 test settings named them.
/// </remarks>
[TestClass]
[TestCategory("Live")]
public sealed class AzureBlobLiveTests : ProviderConformanceTests
{
    private static readonly string _base = "wfp-test-" + Guid.NewGuid().ToString("N")[..12];

    protected override bool KeepsContentType => true;

    // Folders are name prefixes: a file and a folder may share a name.
    protected override bool FileAndFolderMayShareAName => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    [ClassCleanup]
    public static async Task DeleteBase()
    {
        if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1" || LiveSettings.TryRead() is not { } settings)
            return;
        using var provider = new WriteableAzureBlobFileProvider(settings.Options(null));
        await provider.DeleteAsync(_base);
    }

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new WriteableAzureBlobFileProvider(LiveSettings.Require().Options(root is null ? _base : $"{_base}/{root}"));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer()
    {
        var options = LiveSettings.Require().Options(null);
        options.ContainerName = "wfp-missing-" + Guid.NewGuid().ToString("N")[..12];
        return new WriteableAzureBlobFileProvider(options);
    }

    /// <summary>The live account's settings.</summary>
    private sealed record LiveSettings(string? AccountName, string? AccountKey, string Container, string? Url, string? Root, bool UseDefaultCredential)
    {
        public AzureFileProviderOptions Options(string? path) => new()
        {
            AccountName = AccountName,
            AccountKey = UseDefaultCredential ? null : AccountKey,
            ContainerName = Container,
            Url = Url,
            UseDefaultAzureCredential = UseDefaultCredential,
            RootDirectoryPath = string.Join('/', new[] { Root, path }.Where(p => !string.IsNullOrEmpty(p))),
        };

        public static LiveSettings Require()
        {
            if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1")
                Assert.Inconclusive("The live suite runs only with WFP_LIVE=1, against an account you own.");
            var settings = Read();
            var missing = settings.Missing();
            if (missing.Count > 0)
                Assert.Fail($"WFP_LIVE=1, but the Azure storage account is not configured: set {string.Join(", ", missing)} in the environment or in testsettings.json.");
            return settings;
        }

        public static LiveSettings? TryRead()
        {
            var settings = Read();
            return settings.Missing().Count == 0 ? settings : null;
        }

        private static LiveSettings Read()
        {
            var file = ReadFile();
            string? Get(string variable, string key) =>
                Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : file.GetValueOrDefault(key);
            return new LiveSettings(
                Get("WFP_AZURE_ACCOUNT_NAME", "AzureBlobFileProvider_AccountName"),
                Get("WFP_AZURE_ACCOUNT_KEY", "AzureBlobFileProvider_AccountKey"),
                Get("WFP_AZURE_CONTAINER", "AzureBlobFileProvider_ContainerName") ?? "",
                Environment.GetEnvironmentVariable("WFP_AZURE_URL"),
                Get("WFP_AZURE_ROOT", "AzureBlobFileProvider_RootPath"),
                Environment.GetEnvironmentVariable("WFP_AZURE_USE_DEFAULT_CREDENTIAL") == "1");
        }

        private List<string> Missing()
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(AccountName) && string.IsNullOrEmpty(Url))
                missing.Add("WFP_AZURE_ACCOUNT_NAME (or WFP_AZURE_URL)");
            if (Container.Length == 0)
                missing.Add("WFP_AZURE_CONTAINER");
            if (!UseDefaultCredential && string.IsNullOrEmpty(AccountKey))
                missing.Add("WFP_AZURE_ACCOUNT_KEY (or WFP_AZURE_USE_DEFAULT_CREDENTIAL=1)");
            return missing;
        }

        private static Dictionary<string, string> ReadFile()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "testsettings.json");
            if (!File.Exists(path))
                return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 })
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
    }
}
