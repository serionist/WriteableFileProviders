using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>Builds providers and Graph clients over a <see cref="GraphFake"/>.</summary>
internal static class TestGraph
{
    /// <summary>The site URL the fake serves, written as a browser would (the space escaped).</summary>
    public const string SiteUrl = "https://contoso.sharepoint.com/sites/Team%20Site";

    /// <summary>
    /// A Graph client whose pipeline is the SDK's own (retries, redirects) in front of the fake, with a token provider
    /// that, like the SDK's Azure Identity one, signs requests to Graph's host only.
    /// </summary>
    public static GraphServiceClient Client(GraphFake fake) =>
        new(GraphClientFactory.Create(finalHandler: fake.CreateHandler()), new BaseBearerTokenAuthenticationProvider(new GraphOnlyTokenProvider()), GraphFake.BaseUrl);

    /// <summary>Options for the fake's site.</summary>
    public static SharePointFileProviderOptions Options(string? root = null, string? workFolder = null, string? library = "Documents") =>
        new(SiteUrl, library, "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002", "fake-secret", workFolder)
        {
            RootDirectoryPath = root,
        };

    /// <summary>A provider over a caller's client, with thresholds small enough for upload sessions in tests.</summary>
    public static WriteableSharePointFileProvider Provider(GraphServiceClient client, SharePointFileProviderOptions options) =>
        Small(new WriteableSharePointFileProvider(options, client));

    /// <summary>A provider built from options only (its own HTTP client and credential), over the fake.</summary>
    public static WriteableSharePointFileProvider OwnProvider(GraphFake fake, SharePointFileProviderOptions options) =>
        Small(new WriteableSharePointFileProvider(options, fake.CreateHandler(), GraphFake.BaseUrl));

    /// <summary>Lowers the provider's thresholds: files over 256 KiB go through sessions of 320 KiB fragments.</summary>
    public static WriteableSharePointFileProvider Small(WriteableSharePointFileProvider provider)
    {
        provider.SimpleUploadLimit = 256 * 1024;
        provider.UploadFragmentSize = GraphFake.FragmentMultiple;
        provider.CopyPollDelay = TimeSpan.FromMilliseconds(1);
        provider.MaxCopyPollDelay = TimeSpan.FromMilliseconds(4);
        return provider;
    }

    private sealed class GraphOnlyTokenProvider : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new([GraphFake.GraphHost]);

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(AllowedHostsValidator.IsUrlHostValid(uri) ? "fake-graph-token" : "");
    }
}
