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

    /// <summary>Azure.Identity's provider when Microsoft Entra ID refuses the app's secret.</summary>
    private sealed class RefusingTokenProvider : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new([GraphFake.GraphHost]);

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new AuthenticationFailedException("ClientSecretCredential authentication failed: AADSTS7000215: Invalid client secret provided."));
    }
}
