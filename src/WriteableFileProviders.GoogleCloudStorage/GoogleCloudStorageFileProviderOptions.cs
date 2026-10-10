namespace WriteableFileProviders.GoogleCloudStorage;

/// <summary>
/// The options of <see cref="WriteableGoogleCloudStorageFileProvider"/>. Their property names are the configuration shape
/// consumers store, so they never change.
/// </summary>
/// <remarks>
/// <para>
/// Google Cloud Storage is a flat object store: object names may contain <c>/</c>, and the provider shows them as
/// folders. A folder exists while some object has its prefix.
/// </para>
/// <para>
/// The credentials are plain values, so they fit a configuration file or a form. The first that is set wins:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="ClientEmail"/> and <see cref="PrivateKey"/>: a service account, from the <c>client_email</c> and
/// <c>private_key</c> fields of its key file.
/// </description></item>
/// <item><description><see cref="CredentialsJson"/>: the whole service account key file as one string.</description></item>
/// <item><description>
/// Neither: Application Default Credentials, such as Workload Identity on GKE or the metadata server on Compute Engine
/// and Cloud Run, which keep secrets out of the configuration. They are looked up on the first call, never in the
/// constructor.
/// </description></item>
/// </list>
/// <para>
/// Signed URLs (<see cref="IWriteableFileProvider.GetSignedUrlAsync"/>) need a credential that can sign: a service
/// account key, or an identity that holds <c>iam.serviceAccounts.signBlob</c> on its service account.
/// </para>
/// </remarks>
public sealed class GoogleCloudStorageFileProviderOptions : FileProviderOptions
{
    /// <summary>Creates empty options, to fill through the properties.</summary>
    public GoogleCloudStorageFileProviderOptions()
    {
    }

    /// <summary>Creates options for a service account.</summary>
    /// <param name="bucketName">The bucket.</param>
    /// <param name="clientEmail">The service account's email.</param>
    /// <param name="privateKey">The service account's private key, in PEM form.</param>
    /// <param name="projectId">The project, if known.</param>
    /// <param name="rootDirectory">The root folder inside the bucket, or null for the whole bucket.</param>
    public GoogleCloudStorageFileProviderOptions(string bucketName, string? clientEmail, string? privateKey,
        string? projectId = null, string? rootDirectory = null)
    {
        BucketName = bucketName;
        ClientEmail = clientEmail;
        PrivateKey = privateKey;
        ProjectId = projectId;
        RootDirectoryPath = rootDirectory;
    }

    /// <summary>The bucket's name. Required.</summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// The service account's email, the <c>client_email</c> field of its key. Set it together with
    /// <see cref="PrivateKey"/>.
    /// </summary>
    public string? ClientEmail { get; set; }

    /// <summary>
    /// The service account's private key in PEM form, the <c>private_key</c> field of its key: a secret. Set it together
    /// with <see cref="ClientEmail"/>. Escaped newlines (a literal <c>\n</c>, as environment variables and secret stores
    /// often hold them) are accepted; the value itself is never changed.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// The project, the <c>project_id</c> field of a key. Optional, except that <see cref="CreateBucketIfMissing"/> needs it
    /// when the credential does not name its project.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// The whole service account key file as one string, such as a secret store returns it: a secret. Used only when
    /// <see cref="ClientEmail"/> and <see cref="PrivateKey"/> are not both set. Leave every credential empty to use
    /// Application Default Credentials.
    /// </summary>
    public string? CredentialsJson { get; set; }

    /// <summary>
    /// The root URL of the Cloud Storage endpoint, such as <c>http://localhost:4443</c> for the fake-gcs-server emulator
    /// or a Private Service Connect endpoint; null for Google's public endpoint. A URL that ends in <c>/storage/v1/</c>
    /// is taken as it is; any other path is refused. The credentials are sent to it as to Google, so use <c>https</c>
    /// except for a local emulator. Added in 2027.0.0.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Whether the provider creates the bucket on its first call when it does not exist; false (the default) makes every
    /// member throw <see cref="InvalidOperationException"/> instead. Creating needs <see cref="ProjectId"/> or a service
    /// account key that names its project, and permission to create buckets. Added in 2027.0.0.
    /// </summary>
    public bool CreateBucketIfMissing { get; set; }
}
