using System.Net;
using System.Text;
using System.Web;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Cloud.Storage.V1;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>Options, construction and signed URLs, offline: nothing here reaches a server.</summary>
[TestClass]
public sealed class GcsOptionsTests
{
    private static string Pem => FakeGcs.PrivateKey;
    private static string EscapedLf => Pem.Replace("\n", "\\n", StringComparison.Ordinal);
    private static string EscapedCrLf => Pem.Replace("\n", "\\r\\n", StringComparison.Ordinal);
    private static string EscapedCr => Pem.Replace("\n", "\\r", StringComparison.Ordinal);

    private static GoogleCloudStorageFileProviderOptions Options(string? privateKey, string? root = null) =>
        new("wfp-test-bucket", FakeGcs.ClientEmail, privateKey, FakeGcs.ProjectId, root);

    // ----- Keys -----

    [TestMethod]
    public void NormalizePrivateKey_LeavesEmptyAndValidKeysUnchanged()
    {
        Assert.IsNull(WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(null));
        Assert.AreEqual("", WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(""));
        Assert.AreSame(Pem, WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(Pem));
        var crlf = Pem.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.AreEqual(crlf, WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(crlf));
    }

    [TestMethod]
    public void NormalizePrivateKey_TurnsEscapedNewlinesIntoNewlines()
    {
        Assert.AreEqual(Pem, WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(EscapedLf));
        Assert.AreEqual(Pem, WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(EscapedCrLf));
        Assert.AreEqual(Pem, WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(EscapedCr));
    }

    [TestMethod]
    public void NormalizePrivateKey_IsIdempotent()
    {
        foreach (var key in new[] { Pem, EscapedLf, EscapedCrLf, EscapedCr })
        {
            var once = WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(key);
            Assert.AreEqual(once, WriteableGoogleCloudStorageFileProvider.NormalizePrivateKey(once));
        }
    }

    [TestMethod]
    public void EscapedKey_IsRejectedByTheCredentialStackWithoutNormalisation() =>
        Assert.ThrowsExactly<FormatException>(() => new ServiceAccountCredential.Initializer(FakeGcs.ClientEmail).FromPrivateKey(EscapedLf));

    [TestMethod]
    public void Provider_BuildsWithRealAndEscapedKeys()
    {
        foreach (var key in new[] { Pem, Pem.Replace("\n", "\r\n", StringComparison.Ordinal), EscapedLf, EscapedCrLf, EscapedCr })
        {
            using var provider = new WriteableGoogleCloudStorageFileProvider(Options(key));
            Assert.IsNotNull(provider);
        }
    }

    [TestMethod]
    public void Provider_DoesNotModifyThePrivateKeyOption()
    {
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(EscapedLf));
        Assert.AreEqual(EscapedLf, provider.Options.PrivateKey);
        Assert.AreEqual(EscapedLf, (string?)provider.ToJson()["PrivateKey"]);
        using var restored = (WriteableGoogleCloudStorageFileProvider)WriteableFileProviderSerializer.FromJson(
            WriteableFileProviderSerializer.ToJson(provider), typeof(WriteableGoogleCloudStorageFileProvider).Assembly);
        Assert.AreEqual(EscapedLf, restored.Options.PrivateKey);
    }

    [TestMethod]
    public void Serializer_RoundTripsTheOptionsAdded()
    {
        var options = Options(EscapedLf, "root/dir");
        options.Endpoint = "http://localhost:4443";
        options.CreateBucketIfMissing = true;
        using var provider = new WriteableGoogleCloudStorageFileProvider(options);
        var json = WriteableFileProviderSerializer.ToJson(provider);
        Assert.AreEqual("WriteableGoogleCloudStorageFileProvider", (string?)json["Type"]);
        Assert.AreEqual("http://localhost:4443", (string?)json["Endpoint"]);
        Assert.IsTrue((bool)json["CreateBucketIfMissing"]!);

        // A stored configuration may hold the flag as a string; the type is found without the assembly being passed.
        json["CreateBucketIfMissing"] = "true";
        using var read = (WriteableGoogleCloudStorageFileProvider)WriteableFileProviderSerializer.FromJson(json.ToJsonString());
        Assert.AreEqual("http://localhost:4443", read.Options.Endpoint);
        Assert.IsTrue(read.Options.CreateBucketIfMissing);
        CollectionAssert.AreEqual(new[] { "root", "dir" }, read.Options.RootDirectorySegments);
        Assert.AreEqual(EscapedLf, read.Options.PrivateKey);
    }

    [TestMethod]
    public void Provider_RefusesAKeyItCannotRead()
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableGoogleCloudStorageFileProvider(Options("not a key")));
        Assert.AreEqual("options", e.ParamName);
        StringAssert.Contains(e.Message, "PrivateKey");
        Assert.IsNotNull(e.InnerException);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not json")]
    [DataRow("""{"type":"authorized_user","client_id":"x","client_secret":"y","refresh_token":"z"}""")]
    [DataRow("""{"type":"service_account","client_email":"a@b.example","private_key":"not a key"}""")]
    public void Provider_RefusesCredentialsJsonThatIsNotAServiceAccountKey(string json)
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() =>
            new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b", CredentialsJson = json }));
        StringAssert.Contains(e.Message, "CredentialsJson");
    }

    [TestMethod]
    public void Provider_TakesAServiceAccountKeyFile()
    {
        using var provider = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b", CredentialsJson = FakeGcs.CredentialsJson() });
        Assert.IsNotNull(provider);
    }

    // ----- Construction -----

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void Provider_NeedsABucket(string? bucket)
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = bucket }));
        StringAssert.Contains(e.Message, "BucketName");
        using var client = Unauthenticated();
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = bucket }, client));
    }

    [TestMethod]
    public void Provider_RefusesNullArguments()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableGoogleCloudStorageFileProvider(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b" }, null!));
    }

    [TestMethod]
    public async Task Provider_WithoutCredentialsDoesNothingUntilItsFirstCall()
    {
        // 2026 looked up Application Default Credentials in the constructor, which threw where there were none.
        // Disposing a provider that never ran builds nothing either.
        var provider = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b" });
        Assert.IsNotNull(provider.ToJson());
        await provider.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetFileInfoAsync("f.txt"));
    }

    [TestMethod]
    public async Task Provider_DisposedDuringACallBuildsNoClient()
    {
        // An enumeration resolves its path when it is asked for and reaches the provider only when it is walked: after a
        // dispose, it must not build a client (here, look up Application Default Credentials) that nothing would dispose.
        var provider = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b" });
        var entries = provider.EnumerateAsync("dir");
        provider.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in entries)
            {
            }
        });
    }

    [TestMethod]
    public void Provider_IsSealed() =>
        // A custom provider derives from WriteableFileProvider<TOptions>, not from a provider.
        Assert.IsTrue(typeof(WriteableGoogleCloudStorageFileProvider).IsSealed);

    [TestMethod]
    public void NewOptions_AreWrittenOnlyWhenSet()
    {
        // A configuration that uses none of the 2027 options keeps its 2026 shape.
        string[] added = ["Endpoint", "AllowInsecureHttp", "CreateBucketIfMissing"];
        using (var plain = new WriteableGoogleCloudStorageFileProvider(Options(Pem)))
        {
            var json = plain.ToJson();
            Assert.IsFalse(added.Any(json.ContainsKey), json.ToJsonString());
        }
        var options = Options(Pem);
        options.Endpoint = "http://gcs.internal:4443";
        options.AllowInsecureHttp = true;
        options.CreateBucketIfMissing = true;
        using var set = new WriteableGoogleCloudStorageFileProvider(options);
        var written = set.ToJson();
        Assert.IsTrue(added.All(written.ContainsKey), written.ToJsonString());
        using var read = (WriteableGoogleCloudStorageFileProvider)WriteableFileProviderSerializer.FromJson(written);
        Assert.AreEqual("http://gcs.internal:4443", read.Options.Endpoint);
        Assert.IsTrue(read.Options.AllowInsecureHttp && read.Options.CreateBucketIfMissing);
    }

    // ----- Endpoint -----

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("http://localhost:4443", "http://localhost:4443/storage/v1/")]
    [DataRow("http://localhost:4443/", "http://localhost:4443/storage/v1/")]
    [DataRow(" http://localhost:4443 ", "http://localhost:4443/storage/v1/")]
    [DataRow("http://127.0.0.1:4443/storage/v1/", "http://127.0.0.1:4443/storage/v1/")]
    [DataRow("http://127.0.0.1:4443/storage/v1", "http://127.0.0.1:4443/storage/v1/")]
    [DataRow("https://storage-psc.p.googleapis.com", "https://storage-psc.p.googleapis.com/storage/v1/")]
    [DataRow("HTTPS://Storage.Example:8443/", "https://storage.example:8443/storage/v1/")]
    public void Endpoint_BecomesTheClientBaseUri(string? endpoint, string? expected) =>
        Assert.AreEqual(expected, WriteableGoogleCloudStorageFileProvider.BaseUriFor(new GoogleCloudStorageFileProviderOptions { Endpoint = endpoint }));

    [TestMethod]
    [DataRow("http://gcs.internal:4443")]
    [DataRow("http://10.0.0.5:4443/storage/v1/")]
    [DataRow("http://storage.googleapis.com")]
    public void Endpoint_PlainHttpOnlyTowardLoopbackUnlessAllowed(string endpoint)
    {
        // As on MinIO: the credentials' tokens travel with every request, so plain http goes only toward this machine
        // unless allowed.
        var options = new GoogleCloudStorageFileProviderOptions { BucketName = "b", Endpoint = endpoint };
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableGoogleCloudStorageFileProvider(options));
        StringAssert.Contains(e.Message, "AllowInsecureHttp");
        options.AllowInsecureHttp = true;
        using var allowed = new WriteableGoogleCloudStorageFileProvider(options);
        StringAssert.EndsWith(WriteableGoogleCloudStorageFileProvider.BaseUriFor(options), "/storage/v1/");
        foreach (var loopback in new[] { "http://localhost:4443", "http://127.0.0.1:4443", "http://[::1]:4443" })
        {
            using var local = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b", Endpoint = loopback });
            Assert.IsNotNull(local);
        }
    }

    [TestMethod]
    [DataRow("localhost:4443")]
    [DataRow("/storage/v1/")]
    [DataRow("ftp://localhost:4443")]
    [DataRow("http://user:secret@localhost:4443")]
    [DataRow("http://localhost:4443/?x=1")]
    [DataRow("http://localhost:4443/#f")]
    [DataRow("http://localhost:4443/prefix")]
    [DataRow("http://localhost:4443/upload/storage/v1")]
    public void Endpoint_MustBeARootUrl(string endpoint)
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() =>
            new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "b", Endpoint = endpoint }));
        Assert.AreEqual("options", e.ParamName);
        Assert.IsFalse(e.Message.Contains("secret", StringComparison.Ordinal), "The endpoint is never echoed");
    }

    // ----- Signed URLs (their expiry is checked offline, where the key signs locally) -----

    [TestMethod]
    public async Task SignedUrl_LivesTheValidityWhateverTheProvidersClock()
    {
        // The SDK signs with its own clock: a provider clock years off gives ExpiresOn, and changes nothing Cloud Storage
        // checks.
        var clock = new StandingClock(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null, "base"), client) { Clock = clock };
        var url = await provider.GetSignedUrlAsync("dir/file.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) });
        Assert.AreEqual("300", HttpUtility.ParseQueryString(url.Url.Query)["X-Goog-Expires"]);
        Assert.AreEqual(clock.GetUtcNow().AddMinutes(5), url.ExpiresOn);
    }

    private sealed class StandingClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [TestMethod]
    public async Task SignedUrl_ReadLivesExactlyAsLongAsAsked()
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null, "base"), client);
        var before = DateTimeOffset.UtcNow;
        var url = await provider.GetSignedUrlAsync("dir/file.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) });

        Assert.AreEqual("GET", url.Method);
        Assert.AreEqual(SignedUrlKind.Presigned, url.Kind);
        Assert.AreEqual(0, url.Headers.Count);
        Assert.AreEqual("storage.googleapis.com", url.Url.Host);
        Assert.AreEqual("/wfp-test-bucket/base/dir/file.txt", url.Url.AbsolutePath);
        var query = HttpUtility.ParseQueryString(url.Url.Query);
        Assert.AreEqual("300", query["X-Goog-Expires"]);
        Assert.AreEqual("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        StringAssert.StartsWith(query["X-Goog-Credential"], FakeGcs.ClientEmail + "/");
        var signedAt = DateTimeOffset.ParseExact(query["X-Goog-Date"]!, "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.AreEqual(signedAt.AddSeconds(300), url.ExpiresOn);
        Assert.IsTrue(url.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5) && url.ExpiresOn >= before.AddMinutes(5).AddSeconds(-1), $"ExpiresOn {url.ExpiresOn}");
    }

    [TestMethod]
    [DataRow(1.0, "1")]
    [DataRow(90.7, "90")]
    [DataRow(604800.0, "604800")]
    public async Task SignedUrl_ValidityIsRoundedDownToWholeSeconds(double seconds, string expires)
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromSeconds(seconds), Access = SignedUrlAccess.Write });
        Assert.AreEqual(expires, HttpUtility.ParseQueryString(url.Url.Query)["X-Goog-Expires"]);
    }

    [TestMethod]
    public async Task SignedUrl_OutsideOneSecondToSevenDaysIsNotSupported()
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMilliseconds(900), Access = SignedUrlAccess.Write }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(7).Add(TimeSpan.FromSeconds(1)), Access = SignedUrlAccess.Write }));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.Zero, Access = SignedUrlAccess.Write }));
    }

    [TestMethod]
    public async Task SignedUrl_ReadCarriesTheDownloadNameAndContentType()
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions
        {
            Validity = TimeSpan.FromMinutes(1),
            DownloadFileName = "résumé \"final\";\r\n.pdf",
            ContentType = "application/pdf",
        });
        var query = HttpUtility.ParseQueryString(url.Url.Query);
        Assert.AreEqual("application/pdf", query["response-content-type"]);
        var disposition = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(query["response-content-disposition"]!);
        Assert.AreEqual("attachment", disposition.DispositionType);
        Assert.AreEqual("résumé \"final\";\r\n.pdf", disposition.FileNameStar);
        Assert.AreEqual("r_sum_ _final_;__.pdf", disposition.FileName, "The ASCII fallback");
        // The overrides are part of what is signed.
        StringAssert.Contains(url.Url.Query, "response-content-disposition=");
    }

    [TestMethod]
    public async Task SignedUrl_CreateSignsTheIfGenerationMatchHeader()
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        var url = await provider.GetSignedUrlAsync("new.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Create });
        Assert.AreEqual("PUT", url.Method);
        CollectionAssert.AreEquivalent(new[] { "x-goog-if-generation-match" }, url.Headers.Keys.ToArray());
        Assert.AreEqual("0", url.Headers["x-goog-if-generation-match"]);
        Assert.AreEqual("host;x-goog-if-generation-match", HttpUtility.ParseQueryString(url.Url.Query)["X-Goog-SignedHeaders"]);

        var typed = await provider.GetSignedUrlAsync("new.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Create, ContentType = "text/csv" });
        Assert.AreEqual("text/csv", typed.Headers["Content-Type"]);
        Assert.AreEqual("content-type;host;x-goog-if-generation-match", HttpUtility.ParseQueryString(typed.Url.Query)["X-Goog-SignedHeaders"]);
    }

    [TestMethod]
    public async Task SignedUrl_WriteSignsOnlyTheContentTypeItWasGiven()
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Write });
        Assert.AreEqual("PUT", url.Method);
        Assert.AreEqual(0, url.Headers.Count);
        Assert.AreEqual("host", HttpUtility.ParseQueryString(url.Url.Query)["X-Goog-SignedHeaders"]);

        var typed = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Write, ContentType = "text/csv" });
        Assert.AreEqual("text/csv", typed.Headers["Content-Type"]);
        Assert.AreEqual("content-type;host", HttpUtility.ParseQueryString(typed.Url.Query)["X-Goog-SignedHeaders"]);
    }

    [TestMethod]
    [DataRow(SignedUrlAccess.Create)]
    [DataRow(SignedUrlAccess.Write)]
    public async Task SignedUrl_UploadRefusesADownloadName(SignedUrlAccess access)
    {
        using var client = Offline();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = access, DownloadFileName = "x.txt" }));
    }

    [TestMethod]
    [DataRow(SignedUrlAccess.Read)]
    [DataRow(SignedUrlAccess.Create)]
    [DataRow(SignedUrlAccess.Write)]
    public async Task SignedUrl_WithoutASignerIsNotSupported(SignedUrlAccess access)
    {
        // A client whose credential cannot sign refuses with NotSupportedException (so the gateway can
        // take over), before any request: this client has no server to reach.
        using var client = Unauthenticated();
        using var provider = new WriteableGoogleCloudStorageFileProvider(Options(null), client);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = access }));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow((HttpStatusCode)0)]
    public async Task SignedUrl_ThatIamRefusesOrCannotReachIsNotSupported(HttpStatusCode status)
    {
        // An identity that signs through IAM's signBlob, here Compute Engine's: a refusal (403 without
        // iam.serviceAccounts.signBlob), another error answer or a network failure of the signing is
        // NotSupportedException with the cause inside, so the URL service falls back to the gateway. All of it through
        // the client's HTTP seam.
        var (provider, http) = OverComputeEngine(Token, _ => status == 0
            ? throw new HttpRequestException("Injected network failure")
            : Task.FromResult<HttpResponseMessage?>(FaultInjection.Error(status, "Permission 'iam.serviceAccounts.signBlob' denied")));
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Write }));
            Assert.IsNotNull(e.InnerException, "The cause travels inside");
            Assert.IsTrue(http.Requests.Any(r => r.Contains(":signBlob", StringComparison.Ordinal)), string.Join('\n', http.Requests) + '\n' + e.InnerException);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SignedUrl_OfARefusedCredentialIsNotTakenForAMissingSigner(bool atTheTokenEndpoint)
    {
        // A credential refused (a token request answered invalid_grant, or signBlob answering 401) is not a signer the
        // identity lacks: it is not NotSupportedException, which would have the URL service issue a gateway URL that
        // fails later, but UnauthorizedAccessException with the store's error inside, as on every other member.
        Func<HttpRequestMessage, Task<HttpResponseMessage?>> token = atTheTokenEndpoint
            ? _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant","error_description":"injected"}""", Encoding.UTF8, "application/json"),
            })
            : Token;
        var (provider, http) = OverComputeEngine(token, _ => Task.FromResult<HttpResponseMessage?>(FaultInjection.Error(HttpStatusCode.Unauthorized, "injected")));
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Access = SignedUrlAccess.Write }));
            if (atTheTokenEndpoint)
            {
                Assert.IsInstanceOfType<TokenResponseException>(e.InnerException, e.ToString());
            }
            else
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, Assert.IsInstanceOfType<GoogleApiException>(e.InnerException, e.ToString()).HttpStatusCode);
                Assert.IsTrue(http.Requests.Any(r => r.Contains(":signBlob", StringComparison.Ordinal)), string.Join('\n', http.Requests));
            }
        }
    }

    // A provider over a client signed in as Compute Engine's identity, whose metadata server and IAM answer through the
    // client's HTTP seam. The universe is named, so the credential asks the metadata server nothing but the email and
    // the token.
    private static (WriteableGoogleCloudStorageFileProvider Provider, FaultInjection Http) OverComputeEngine(
        Func<HttpRequestMessage, Task<HttpResponseMessage?>> token, Func<HttpRequestMessage, Task<HttpResponseMessage?>> signBlob)
    {
        var http = new FaultInjection
        {
            OnRequest = request => request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/service-accounts/default/email", StringComparison.Ordinal) => Metadata("wfp@example-project.iam.gserviceaccount.com"),
                var p when p.EndsWith("/service-accounts/default/token", StringComparison.Ordinal) => token(request),
                var p when p.EndsWith(":signBlob", StringComparison.Ordinal) => signBlob(request),
                _ => Task.FromResult<HttpResponseMessage?>(FaultInjection.Error(HttpStatusCode.NotFound)),
            },
        };
        var credential = new ComputeCredential(new ComputeCredential.Initializer { HttpClientFactory = http }).ToGoogleCredential().CreateWithUniverseDomain("googleapis.com");
        var client = new StorageClientBuilder { GoogleCredential = credential, HttpClientFactory = http }.Build();
        return (new WriteableGoogleCloudStorageFileProvider(Options(null), client), http);
    }

    private static Task<HttpResponseMessage?> Token(HttpRequestMessage request) =>
        Metadata("""{"access_token":"fake-token","expires_in":3600,"token_type":"Bearer"}""", "application/json");

    private static Task<HttpResponseMessage?> Metadata(string body, string type = "text/plain")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
        response.Headers.Add("Metadata-Flavor", "Google");
        return Task.FromResult<HttpResponseMessage?>(response);
    }

    // A client signed in with the made-up key, for Google's endpoint, whose HTTP never leaves the process: it answers
    // every metadata request with one small object, which is all a read URL asks the server.
    private static StorageClient Offline()
    {
        var http = new FaultInjection
        {
            OnRequest = _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"kind":"storage#object","name":"f","bucket":"wfp-test-bucket","generation":"1","size":"1","updated":"2026-10-11T00:00:00.000Z"}""", Encoding.UTF8, "application/json"),
            }),
        };
        return new StorageClientBuilder { GoogleCredential = FakeGcs.Credential(), HttpClientFactory = http }.Build();
    }

    private static StorageClient Unauthenticated() =>
        new StorageClientBuilder { UnauthenticatedAccess = true, BaseUri = "http://127.0.0.1:9/storage/v1/" }.Build();

    [TestMethod]
    public void ForwardReadStream_ReadsForwardAndNeverSeeksOrDisposes()
    {
        var inner = TestStreams.Text("abcdef");
        using (var stream = new ForwardReadStream(inner))
        {
            Assert.IsTrue(stream.CanRead);
            Assert.IsFalse(stream.CanSeek);
            Assert.IsFalse(stream.CanWrite);
            Assert.ThrowsExactly<NotSupportedException>(() => stream.Length);
            Assert.ThrowsExactly<NotSupportedException>(() => stream.Position);
            Assert.ThrowsExactly<NotSupportedException>(() => stream.Position = 0);
            Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
            Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));
            Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
            stream.Flush();
            var buffer = new byte[2];
            Assert.AreEqual(2, stream.Read(buffer, 0, 2));
            Assert.AreEqual(2, stream.Read(buffer.AsSpan()));
            Assert.AreEqual(1, stream.ReadAsync(buffer, 0, 1).GetAwaiter().GetResult());
        }
        Assert.AreEqual(5, inner.Position);
        Assert.IsTrue(inner.CanRead, "The caller's stream is left open");
    }
}
