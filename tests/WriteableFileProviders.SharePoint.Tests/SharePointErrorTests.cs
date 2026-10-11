using System.Net;
using System.Text;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// What a member lets out when signing in or the site lookup fails: only the contract's types. Azure.Identity raises its
/// errors inside each request, as the SDK asks for the token, past the provider's own mapping of Graph's answers.
/// </summary>
[TestClass]
public sealed class SharePointErrorTests
{
    [TestMethod]
    public async Task ACredentialMicrosoftEntraIdRefuses_IsUnauthorizedAccess()
    {
        var fake = new GraphFake();
        using var client = new GraphServiceClient(GraphClientFactory.Create(finalHandler: fake.CreateHandler()),
            new BaseBearerTokenAuthenticationProvider(new RefusingTokenProvider()), GraphFake.BaseUrl);
        await using var provider = TestGraph.Provider(client, TestGraph.Options());
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.IsInstanceOfType<AuthenticationFailedException>(e.InnerException);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.UpdateFileAsync("a.txt", new MemoryStream([1])));
    }

    [TestMethod]
    public async Task ASignInThatFindsNoCredential_IsAStoreNotSetUpAsConfigured()
    {
        var fake = new GraphFake();
        using var client = new GraphServiceClient(GraphClientFactory.Create(finalHandler: fake.CreateHandler()),
            new BaseBearerTokenAuthenticationProvider(new RefusingTokenProvider(new CredentialUnavailableException("DefaultAzureCredential failed to retrieve a token from the included credentials."))),
            GraphFake.BaseUrl);
        await using var provider = TestGraph.Provider(client, TestGraph.Options());
        // The caller built the client, and its sign-in: the message names the client, not the options.
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.IsInstanceOfType<CredentialUnavailableException>(e.InnerException);
        StringAssert.Contains(e.Message, "given client");
        Assert.IsFalse(e.Message.Contains(nameof(SharePointFileProviderOptions.UseDefaultAzureCredential), StringComparison.Ordinal), e.Message);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.UpdateFileAsync("a.txt", new MemoryStream([1])));
    }

    [TestMethod]
    public void ASignInThatFindsNoCredential_NamesWhatTheCallerSetUp()
    {
        var unavailable = new CredentialUnavailableException("No credential to use.");
        var fromOptions = GraphErrors.Wrap(unavailable, "Reading 'a.txt'");
        Assert.IsInstanceOfType<InvalidOperationException>(fromOptions);
        StringAssert.Contains(fromOptions.Message, nameof(SharePointFileProviderOptions.UseDefaultAzureCredential));
        var fromClient = GraphErrors.Wrap(unavailable, "Reading 'a.txt'", givenClient: true);
        Assert.IsInstanceOfType<InvalidOperationException>(fromClient);
        StringAssert.Contains(fromClient.Message, "given client");
        Assert.AreSame(unavailable, fromClient.InnerException);
    }

    [TestMethod]
    public async Task ASiteAnswerWithoutAnId_IsTheServicesFailure_AndAskedAgain()
    {
        var fake = new GraphFake();
        await using var provider = TestGraph.OwnProvider(fake, TestGraph.Options());
        fake.Intercept = r => r.Method == "GET" && r.Path.Contains("/sites/", StringComparison.Ordinal) && !r.Path.Contains("/drives", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
            : null;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("a.txt"));
        StringAssert.Contains(e.Message, "no id");
        fake.Intercept = null;
        Assert.IsFalse((await provider.GetFileInfoAsync("a.txt")).Exists, "The next call asks again");
    }

    /// <summary>Azure.Identity's provider when Microsoft Entra ID refuses the app's secret, or finds no credential.</summary>
    private sealed class RefusingTokenProvider(AuthenticationFailedException? failure = null) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new([GraphFake.GraphHost]);

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(failure ?? new AuthenticationFailedException("ClientSecretCredential authentication failed: AADSTS7000215: Invalid client secret provided."));
    }
}
