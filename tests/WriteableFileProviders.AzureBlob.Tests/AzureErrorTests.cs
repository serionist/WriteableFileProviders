using System.Net;
using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// What a member lets out when Azure fails, offline: only the contract's types, with Azure's exception inside. A
/// refused credential or permission is <see cref="UnauthorizedAccessException"/>, anything else <see cref="IOException"/>.
/// </summary>
[TestClass]
public sealed class AzureErrorTests
{
    private static AzureFileProviderOptions SharedKey() => new() { AccountName = "account", AccountKey = Azurite.AccountKey, ContainerName = "files" };

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")]
    [DataRow(HttpStatusCode.Forbidden, "AuthenticationFailed")]
    [DataRow(HttpStatusCode.Unauthorized, "InvalidAuthenticationInfo")]
    public async Task ARefusal_IsUnauthorizedAccess(HttpStatusCode status, string code)
    {
        using var provider = new WriteableAzureBlobFileProvider(SharedKey(), AzureBlobOptionsTests.Offline(new StubHandler(_ => AzureHandler.Error(status, code))), null);
        var info = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.AreEqual((int)status, Assert.IsInstanceOfType<RequestFailedException>(info.InnerException).Status);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.ReadFileAsync("a.txt"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("a.txt"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.UpdateFileAsync("a.txt", new MemoryStream([1])));
    }

    [TestMethod]
    public async Task ACredentialMicrosoftEntraIdRefuses_IsUnauthorizedAccess()
    {
        var options = new AzureFileProviderOptions { AccountName = "account", ContainerName = "files", UseDefaultAzureCredential = true };
        using var provider = new WriteableAzureBlobFileProvider(options, AzureBlobOptionsTests.Offline(new StubHandler(_ => StubHandler.BlobProperties())), new RefusingCredential());
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.IsInstanceOfType<AuthenticationFailedException>(e.InnerException);
    }

    [TestMethod]
    public async Task ASignInThatFindsNoCredential_IsAStoreNotSetUpAsConfigured()
    {
        var options = new AzureFileProviderOptions { AccountName = "account", ContainerName = "files", UseDefaultAzureCredential = true };
        using var provider = new WriteableAzureBlobFileProvider(options, AzureBlobOptionsTests.Offline(new StubHandler(_ => StubHandler.BlobProperties())), new UnavailableCredential());
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.IsInstanceOfType<CredentialUnavailableException>(e.InnerException);
        StringAssert.Contains(e.Message, nameof(AzureFileProviderOptions.UseDefaultAzureCredential));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await provider.EnumerateAsync("").ToListAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.UpdateFileAsync("a.txt", new MemoryStream([1])));
        // A write URL asks for a user delegation key, the first request of the call.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write }));
    }

    [TestMethod]
    public async Task AnyOtherFailure_IsIOException()
    {
        using var provider = new WriteableAzureBlobFileProvider(SharedKey(),
            AzureBlobOptionsTests.Offline(new StubHandler(_ => AzureHandler.Error(HttpStatusCode.InternalServerError, "InternalError"))), null);
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.AreEqual(500, Assert.IsInstanceOfType<RequestFailedException>(e.InnerException).Status);
        StringAssert.StartsWith(e.Message, "Getting 'a.txt'");
    }

    /// <summary>A sign-in that finds no credential to use, as DefaultAzureCredential reports it from inside each request.</summary>
    private sealed class UnavailableCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new CredentialUnavailableException("DefaultAzureCredential failed to retrieve a token from the included credentials.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromException<AccessToken>(new CredentialUnavailableException("DefaultAzureCredential failed to retrieve a token from the included credentials."));
    }

    /// <summary>Microsoft Entra ID refusing the app, as Azure.Identity reports it from inside each request.</summary>
    private sealed class RefusingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("AADSTS7000215: Invalid client secret provided.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromException<AccessToken>(new AuthenticationFailedException("AADSTS7000215: Invalid client secret provided."));
    }
}
