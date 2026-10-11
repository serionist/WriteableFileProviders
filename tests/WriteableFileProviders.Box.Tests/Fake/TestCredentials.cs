using System.Security.Cryptography;
using Box.Sdk.Gen;

namespace WriteableFileProviders.Box.Tests.Fake;

/// <summary>
/// A Box app's credentials for the fake: a key pair made when the tests start, never a real one. The private key comes in
/// the forms Box apps and .NET export.
/// </summary>
internal sealed class TestCredentials
{
    private static readonly Lazy<TestCredentials> _default = new(() => new TestCredentials());

    private TestCredentials()
    {
        using var key = RSA.Create(2048);
        PublicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        // Few iterations: every sign-in decrypts the key, and the tests sign in often.
        EncryptedPrivateKeyPem = key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        Pkcs8PrivateKeyPem = key.ExportPkcs8PrivateKeyPem();
        RsaPrivateKeyPem = key.ExportRSAPrivateKeyPem();
    }

    public static TestCredentials Default => _default.Value;

    public string ClientId { get; } = "test-client-id";

    public string ClientSecret { get; } = "test-client-secret";

    public string EnterpriseId { get; } = "4242";

    public string PublicKeyId { get; } = "test-key-id";

    public string Passphrase { get; } = "test-passphrase";

    public string PublicKeyPem { get; }

    /// <summary>A PKCS#8 key encrypted with <see cref="Passphrase"/>, as Box's developer console generates.</summary>
    public string EncryptedPrivateKeyPem { get; }

    /// <summary>The key unencrypted, PKCS#8.</summary>
    public string Pkcs8PrivateKeyPem { get; }

    /// <summary>The key unencrypted, PKCS#1 (<c>RSA PRIVATE KEY</c>).</summary>
    public string RsaPrivateKeyPem { get; }

    /// <summary>Options that sign in to the fake.</summary>
    public BoxFileProviderOptions Options(string? root = null, string? rootFolderId = null) =>
        new(ClientId, ClientSecret, EnterpriseId, PublicKeyId, EncryptedPrivateKeyPem, Passphrase, rootFolderId) { RootDirectoryPath = root };

    /// <summary>A network session that sends every call to the fake, with retries that do not wait.</summary>
    public static NetworkSession Session(FakeBox box) =>
        new(baseUrls: new BaseUrls(FakeBox.ApiUrl, FakeBox.UploadUrl, FakeBox.AccountUrl))
        {
            NetworkClient = new FakeNetworkClient(box),
            RetryStrategy = new BoxRetryStrategy(retryBaseInterval: 0),
        };

    /// <summary>A Box client signed in to the fake, as a caller would build one.</summary>
    public BoxClient Client(FakeBox box)
    {
        var config = new JwtConfig(ClientId, ClientSecret, PublicKeyId, EncryptedPrivateKeyPem, Passphrase) { EnterpriseId = EnterpriseId };
        return new BoxClient(new BoxJwtAuth(config), Session(box));
    }

    /// <summary>A provider that signs in to the fake through its options, tuned so the tests do not wait.</summary>
    public WriteableBoxFileProvider Provider(FakeBox box, string? root = null, string? rootFolderId = null) =>
        new(Options(root, rootFolderId), Session(box)) { PollDelay = TimeSpan.Zero };
}
