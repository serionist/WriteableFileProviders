using System.Text.Json.Serialization;

namespace WriteableFileProviders.SharePoint;

/// <summary>
/// The options of <see cref="WriteableSharePointFileProvider"/>: the site and document library, how to sign in, and the
/// folder the provider works in. The property names are the configuration shape consumers store (AGENTS.md, one-way
/// doors): <c>Type</c> is <c>WriteableSharePointFileProvider</c>, and each property below is a key.
/// </summary>
/// <remarks>
/// <para>
/// The provider signs in as an app (client credentials) with the first of these that is configured:
/// <see cref="UseDefaultAzureCredential"/>; a certificate (<see cref="ClientCertificateBase64"/> and
/// <see cref="ClientCertificatePassword"/>); a client secret (<see cref="ClientSecret"/>). The app needs the Microsoft
/// Graph application permission <c>Sites.ReadWrite.All</c>, or <c>Sites.Selected</c> with write access to the site.
/// </para>
/// <para>
/// The provider's root is <see cref="WorkFolderPath"/> followed by <see cref="FileProviderOptions.RootDirectorySegments"/>,
/// inside the document library. When the root segments start with the work folder (compared without regard to case),
/// that one leading copy is dropped, so a configuration that sets both to the same folder keeps the root it had. A root
/// that is a strict prefix of the work folder is refused, because the 2026 provider placed some paths of such a
/// configuration elsewhere (docs/migration-2027.md). Neither may hold a percent-escape such as <c>%20</c>: write the
/// names as they are.
/// </para>
/// </remarks>
public class SharePointFileProviderOptions : FileProviderOptions
{
    /// <summary>Creates options with every property unset.</summary>
    public SharePointFileProviderOptions()
    {
    }

    /// <summary>Creates options for client-secret sign-in.</summary>
    /// <param name="siteUrl">The site's URL; see <see cref="SiteUrl"/>.</param>
    /// <param name="documentLibraryName">The document library; see <see cref="DocumentLibraryName"/>.</param>
    /// <param name="tenantId">The Microsoft Entra tenant; see <see cref="TenantId"/>.</param>
    /// <param name="clientId">The app's client id; see <see cref="ClientId"/>.</param>
    /// <param name="clientSecret">The app's client secret; see <see cref="ClientSecret"/>.</param>
    /// <param name="workFolderPath">The folder the provider works in; see <see cref="WorkFolderPath"/>.</param>
    public SharePointFileProviderOptions(string? siteUrl, string? documentLibraryName, string? tenantId, string? clientId, string? clientSecret, string? workFolderPath)
    {
        SiteUrl = siteUrl;
        DocumentLibraryName = documentLibraryName;
        TenantId = tenantId;
        ClientId = clientId;
        ClientSecret = clientSecret;
        WorkFolderPath = workFolderPath;
    }

    /// <summary>
    /// The SharePoint site's absolute URL, such as <c>https://contoso.sharepoint.com/sites/Finance</c>. Required.
    /// </summary>
    public string? SiteUrl { get; set; }

    /// <summary>
    /// The document library's name as Microsoft Graph lists it (the drive's <c>name</c>: <c>Documents</c> for a site's
    /// default library, whose URL segment is <c>Shared Documents</c>), compared without regard to case. Empty or null
    /// takes the site's first library.
    /// </summary>
    public string? DocumentLibraryName { get; set; }

    /// <summary>
    /// The Microsoft Entra tenant (directory) id of the app. Required for a client secret or a certificate; optional with
    /// <see cref="UseDefaultAzureCredential"/>.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The app's client (application) id. Required for a client secret or a certificate; with
    /// <see cref="UseDefaultAzureCredential"/>, the client id of a user-assigned managed identity, or empty for the
    /// system-assigned one (or the one in <c>AZURE_CLIENT_ID</c>).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>The app's client secret, when it signs in with one. A secret: never log it.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The app's certificate with its private key, as a base64-encoded PKCS#12 (.pfx) file, when it signs in with a
    /// certificate. Takes precedence over <see cref="ClientSecret"/>. A secret: never log it.
    /// </summary>
    /// <remarks>New in 2027: written to JSON only when set, so a configuration that does not use it keeps its 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ClientCertificateBase64 { get; set; }

    /// <summary>The password of <see cref="ClientCertificateBase64"/>, if it has one. A secret: never log it.</summary>
    /// <remarks>New in 2027: written to JSON only when set, so a configuration that does not use it keeps its 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ClientCertificatePassword { get; set; }

    /// <summary>
    /// Sign in with <c>DefaultAzureCredential</c> (managed identity, workload identity, environment variables, or a
    /// developer sign-in) instead of a secret or a certificate. False by default; takes precedence over both when true.
    /// </summary>
    /// <remarks>New in 2027: written to JSON only when set, so a configuration that does not use it keeps its 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseDefaultAzureCredential { get; set; }

    /// <summary>
    /// A folder of the document library, such as <c>Main/Work</c>, that holds the provider's root: the root
    /// (<see cref="FileProviderOptions.RootDirectoryPath"/>) is a folder inside it. Separators may be <c>/</c> or
    /// <c>\</c>. Empty or null is the library's top level. Missing folders are created on the first write. Write names
    /// as they are, not percent-escaped: a value holding an escape such as <c>%20</c> is refused.
    /// </summary>
    public string? WorkFolderPath { get; set; }
}
