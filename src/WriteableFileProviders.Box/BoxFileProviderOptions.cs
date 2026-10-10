namespace WriteableFileProviders.Box;

/// <summary>
/// The options of <see cref="WriteableBoxFileProvider"/>: a Box app's JWT credentials, and the folder the provider works
/// in. The property names are the configuration shape consumers store (the <c>Type</c> key is
/// <c>WriteableBoxFileProvider</c>); <see cref="WriteableFileProviderSerializer"/> reads them case-insensitively.
/// </summary>
/// <remarks>
/// The app signs in as its service account (the enterprise subject), with Box's server authentication (JWT): create a
/// Box app with "Server Authentication (with JWT)", generate a key pair, and authorize the app in the enterprise. The app's
/// JSON configuration holds every value below. Sign-in happens on the first call that needs Box, never in the
/// constructor. When the provider is built with a ready Box client, only <see cref="RootFolderId"/> and the root folder
/// are read; the credentials are ignored.
/// </remarks>
public class BoxFileProviderOptions : FileProviderOptions
{
    /// <summary>Creates options from every value at once.</summary>
    /// <param name="clientId">The app's client id.</param>
    /// <param name="clientSecret">The app's client secret.</param>
    /// <param name="enterpriseId">The enterprise the app signs in to, as its service account.</param>
    /// <param name="publicKeyId">The id of the app's public key.</param>
    /// <param name="privateKey">The app's private key, in PEM.</param>
    /// <param name="passphrase">The private key's passphrase; null or empty for a key that is not encrypted.</param>
    /// <param name="rootFolderId">The id of the Box folder the provider's root lives in; null for "0", the account's root.</param>
    public BoxFileProviderOptions(string? clientId, string? clientSecret, string? enterpriseId, string? publicKeyId,
        string? privateKey, string? passphrase, string? rootFolderId)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        EnterpriseId = enterpriseId;
        PublicKeyId = publicKeyId;
        PrivateKey = privateKey;
        Passphrase = passphrase;
        RootFolderId = rootFolderId;
    }

    /// <summary>Creates empty options, for configuration binding and the serializer.</summary>
    public BoxFileProviderOptions()
    {
    }

    /// <summary>The app's client id (<c>boxAppSettings.clientID</c> in the app's JSON configuration). Required.</summary>
    public string? ClientId { get; set; }

    /// <summary>The app's client secret (<c>boxAppSettings.clientSecret</c>). Required. A secret: never log it.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The enterprise id (<c>enterpriseID</c>). Required: the provider signs in as the app's service account in this
    /// enterprise.
    /// </summary>
    public string? EnterpriseId { get; set; }

    /// <summary>
    /// The id of the app's public key (<c>boxAppSettings.appAuth.publicKeyID</c>). Required. The Box SDK keeps it but does
    /// not send it in the JWT header, which Box accepts while the app has one public key.
    /// </summary>
    public string? PublicKeyId { get; set; }

    /// <summary>
    /// The app's private key in PEM (<c>boxAppSettings.appAuth.privateKey</c>), encrypted or not. Required. Escaped line
    /// breaks (<c>\n</c>, as in the app's JSON configuration or an environment variable) are turned into real ones. A
    /// secret: never log it.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// The private key's passphrase (<c>boxAppSettings.appAuth.passphrase</c>); null or empty for a key that is not
    /// encrypted. A secret: never log it.
    /// </summary>
    public string? Passphrase { get; set; }

    /// <summary>
    /// The id of the Box folder the provider works in; null or empty for <c>0</c>, the root of the service account's files.
    /// <see cref="FileProviderOptions.RootDirectoryPath"/> is a path under this folder.
    /// </summary>
    public string? RootFolderId { get; set; }
}
