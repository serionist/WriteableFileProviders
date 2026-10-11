using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Cloud.Storage.V1;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>
/// What a member lets out when Google refuses the credential, offline: the token endpoint answers before any request
/// reaches Cloud Storage, so no server is needed.
/// </summary>
[TestClass]
public sealed class GcsErrorTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest, "invalid_grant")]
    [DataRow(HttpStatusCode.Unauthorized, "unauthorized_client")]
    public async Task ACredentialTheTokenEndpointRefuses_IsUnauthorizedAccess(HttpStatusCode status, string error)
    {
        // A refresh token that was revoked: Google's token endpoint answers 400 invalid_grant. (A service account key signs
        // its own tokens, so the token endpoint is asked only by credentials such as these.)
        var tokens = new FaultInjection
        {
            OnRequest = _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(status)
            {
                Content = new StringContent($$"""{"error":"{{error}}","error_description":"Token has been expired or revoked."}""", Encoding.UTF8, "application/json"),
            }),
        };
        var credential = CredentialFactory.FromJson<UserCredential>("""{"type":"authorized_user","client_id":"id","client_secret":"secret","refresh_token":"revoked"}""")
            .ToGoogleCredential().CreateWithHttpClientFactory(tokens);
        using var client = new StorageClientBuilder { BaseUri = "http://127.0.0.1:9/storage/v1/", GoogleCredential = credential }.Build();
        using var provider = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "wfp-test-bucket" }, client);

        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("f.txt"));
        Assert.IsInstanceOfType<TokenResponseException>(e.InnerException);
        Assert.IsNotEmpty(tokens.Requests, "The token was asked for");
    }
}
