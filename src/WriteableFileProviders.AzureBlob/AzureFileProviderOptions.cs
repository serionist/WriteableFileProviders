using System.Text.Json.Serialization;

namespace WriteableFileProviders.AzureBlob;

/// <summary>
/// The options of <see cref="WriteableAzureBlobFileProvider"/>: the storage account, how the provider signs in, the
/// container, and the root folder inside it (<see cref="FileProviderOptions.RootDirectoryPath"/>).
/// </summary>
/// <remarks>
/// <para>
/// The property names are the configuration shape consumers store (in configuration files and databases), so they never
/// change. The provider signs in with the account's shared key (<see cref="AccountName"/> and <see cref="AccountKey"/>),
/// or with Microsoft Entra ID through <c>DefaultAzureCredential</c> (<see cref="UseDefaultAzureCredential"/>).
/// </para>
/// <para>
/// The endpoint is <c>{Protocol}://{Url}</c>, or <c>https://{AccountName}.blob.core.windows.net</c> when
/// <see cref="Url"/> is empty. For Azurite, set <see cref="Protocol"/> to <c>http</c> and <see cref="Url"/> to
/// <c>127.0.0.1:10000/devstoreaccount1</c>.
/// </para>
/// </remarks>
public class AzureFileProviderOptions : FileProviderOptions
{
    /// <summary>
    /// The storage account's name, such as <c>contoso</c>. Required with a shared key; with
    /// <see cref="UseDefaultAzureCredential"/>, required unless <see cref="Url"/> names the endpoint.
    /// </summary>
    public string? AccountName { get; set; }

    /// <summary>
    /// The account's shared key, base64-encoded, as the Azure portal shows it. Required unless
    /// <see cref="UseDefaultAzureCredential"/> is set, and refused together with it. A secret: never log it.
    /// </summary>
    public string? AccountKey { get; set; }

    /// <summary>The blob container the provider works in. Required.</summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// The endpoint's scheme: <c>http</c> or <c>https</c>, in any case. Empty means <c>https</c>. Signing in with
    /// <see cref="UseDefaultAzureCredential"/> needs <c>https</c>.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// The blob endpoint's host, with its port and path where it has them, such as <c>contoso.blob.core.windows.net</c>
    /// or <c>127.0.0.1:10000/devstoreaccount1</c>; <see cref="Protocol"/> gives its scheme. A full URL
    /// (<c>https://...</c>) is taken as it is. Empty means <c>{AccountName}.blob.core.windows.net</c>.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Whether the first write creates the container when it does not exist; false by default. Without it, every member
    /// of a provider whose container is missing throws <see cref="InvalidOperationException"/>. Reads, listings and deletes
    /// never create the container. Creating one needs a container-level permission.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CreateContainerIfMissing { get; set; }

    /// <summary>
    /// Whether to sign in with Microsoft Entra ID through <c>DefaultAzureCredential</c> (environment variables, workload
    /// identity, managed identity, then developer tools) instead of the shared key; false by default. The identity needs a
    /// data role on the container, such as Storage Blob Data Contributor. Signed URLs are then user delegation SAS, which
    /// need the <c>generateUserDelegationKey</c> action at the storage account's scope or above: the Storage Blob Data
    /// roles have it when assigned there, and Storage Blob Delegator adds it to a role scoped to the container.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseDefaultAzureCredential { get; set; }
}
