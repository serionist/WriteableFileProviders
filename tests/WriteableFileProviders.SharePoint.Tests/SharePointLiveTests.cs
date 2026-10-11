using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// The conformance suite against a real SharePoint document library. Without <c>WFP_LIVE=1</c> it is inconclusive; with
/// it, a missing account fails the run, naming what is missing. The account comes from environment variables (<c>WFP_SHAREPOINT_SITE_URL</c>, <c>WFP_SHAREPOINT_LIBRARY</c>,
/// <c>WFP_SHAREPOINT_TENANT_ID</c>, <c>WFP_SHAREPOINT_CLIENT_ID</c>, <c>WFP_SHAREPOINT_CLIENT_SECRET</c>,
/// <c>WFP_SHAREPOINT_CERTIFICATE_BASE64</c>, <c>WFP_SHAREPOINT_CERTIFICATE_PASSWORD</c>,
/// <c>WFP_SHAREPOINT_USE_DEFAULT_CREDENTIAL</c>, <c>WFP_SHAREPOINT_WORK_FOLDER</c>) or from an optional
/// <c>testsettings.json</c> next to this project (see testsettings.sample.json; git-ignored). Each test works in a
/// <c>wfp-&lt;id&gt;</c> folder under the work folder and deletes it; deleted items go to the site's recycle bin.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class SharePointLiveTests : ProviderConformanceTests
{
    private static readonly Lazy<(SharePointFileProviderOptions Options, string[] Missing)> _settings = new(Read);

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool KeepsContentType => false;

    // Set to true once a live run shows Graph refuses a stale If-Match on a simple upload (D15, D19).
    protected override bool DetectsConcurrentAppends => false;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableSharePointFileProvider.DownloadUrlLifetime;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    protected override IWriteableFileProvider CreateProvider(string? root)
    {
        if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1")
            Assert.Inconclusive("The live SharePoint suite runs with WFP_LIVE=1 and an account in WFP_SHAREPOINT_* or testsettings.json.");
        var (settings, missing) = _settings.Value;
        if (missing.Length > 0)
            Assert.Fail($"WFP_LIVE=1, but the SharePoint account is not configured: set {string.Join(", ", missing)} (WFP_SHAREPOINT_* or testsettings.json).");
        return new WriteableSharePointFileProvider(new SharePointFileProviderOptions(settings.SiteUrl, settings.DocumentLibraryName, settings.TenantId,
            settings.ClientId, settings.ClientSecret, settings.WorkFolderPath)
        {
            ClientCertificateBase64 = settings.ClientCertificateBase64,
            ClientCertificatePassword = settings.ClientCertificatePassword,
            UseDefaultAzureCredential = settings.UseDefaultAzureCredential,
            RootDirectoryPath = root,
        });
    }

    private static (SharePointFileProviderOptions Options, string[] Missing) Read()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "testsettings.json");
        using var json = File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)) : null;
        string? Value(string variable, string key) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment ? fromEnvironment
            : json?.RootElement.TryGetProperty(key, out var value) == true && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } fromFile ? fromFile
            : null;

        var options = new SharePointFileProviderOptions
        {
            SiteUrl = Value("WFP_SHAREPOINT_SITE_URL", "SharePointFileProvider_SiteUrl"),
            DocumentLibraryName = Value("WFP_SHAREPOINT_LIBRARY", "SharePointFileProvider_DocumentLibraryName") ?? "Documents",
            TenantId = Value("WFP_SHAREPOINT_TENANT_ID", "SharePointFileProvider_TenantId"),
            ClientId = Value("WFP_SHAREPOINT_CLIENT_ID", "SharePointFileProvider_ClientId"),
            ClientSecret = Value("WFP_SHAREPOINT_CLIENT_SECRET", "SharePointFileProvider_ClientSecret"),
            ClientCertificateBase64 = Value("WFP_SHAREPOINT_CERTIFICATE_BASE64", "SharePointFileProvider_ClientCertificateBase64"),
            ClientCertificatePassword = Value("WFP_SHAREPOINT_CERTIFICATE_PASSWORD", "SharePointFileProvider_ClientCertificatePassword"),
            UseDefaultAzureCredential = Value("WFP_SHAREPOINT_USE_DEFAULT_CREDENTIAL", "SharePointFileProvider_UseDefaultAzureCredential") == "true",
            WorkFolderPath = Value("WFP_SHAREPOINT_WORK_FOLDER", "SharePointFileProvider_WorkFolderPath") ?? "wfp-live",
        };
        var missing = new List<string>();
        if (options.SiteUrl is null)
            missing.Add("WFP_SHAREPOINT_SITE_URL");
        if (!options.UseDefaultAzureCredential)
        {
            if (options.TenantId is null)
                missing.Add("WFP_SHAREPOINT_TENANT_ID");
            if (options.ClientId is null)
                missing.Add("WFP_SHAREPOINT_CLIENT_ID");
            if (options.ClientSecret is null && options.ClientCertificateBase64 is null)
                missing.Add("WFP_SHAREPOINT_CLIENT_SECRET or WFP_SHAREPOINT_CERTIFICATE_BASE64 (or WFP_SHAREPOINT_USE_DEFAULT_CREDENTIAL=true)");
        }
        return (options, [.. missing]);
    }
}
