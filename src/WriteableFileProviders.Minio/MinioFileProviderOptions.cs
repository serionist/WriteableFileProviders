namespace WriteableFileProviders.Minio;

/// <summary>
/// The options of <see cref="WriteableMinioFileProvider"/>: the server, the bucket and the key pair. The property names
/// are the configuration shape consumers store (AGENTS.md, one-way doors).
/// </summary>
public class MinioFileProviderOptions : FileProviderOptions
{
    /// <summary>Creates the options.</summary>
    /// <param name="rootDirectory">The root folder in the bucket, such as <c>tenants/acme</c>; null for the bucket's root.</param>
    /// <param name="hostAndPort">The server, as <c>host:port</c>.</param>
    /// <param name="accessKey">The access key; null or empty, with the secret key, for anonymous access.</param>
    /// <param name="secretKey">The secret key.</param>
    /// <param name="bucketName">The bucket.</param>
    public MinioFileProviderOptions(string? rootDirectory, string? hostAndPort, string? accessKey, string? secretKey, string? bucketName)
    {
        RootDirectoryPath = rootDirectory;
        AccessKey = accessKey;
        SecretKey = secretKey;
        BucketName = bucketName;
        HostAndPort = hostAndPort;
    }

    /// <summary>Creates empty options, for configuration binding.</summary>
    public MinioFileProviderOptions()
    {
    }

    /// <summary>The server, as <c>host:port</c> (or <c>host</c>), without a scheme or a path. Required.</summary>
    public string? HostAndPort { get; set; }

    /// <summary>
    /// Connects over HTTPS when true. When false the provider uses plain HTTP, which exposes the files to the network:
    /// it is allowed toward a loopback host (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>), and toward any other host
    /// only with <see cref="AllowInsecureHttp"/>.
    /// </summary>
    public bool UseSSL { get; set; }

    /// <summary>The access key. It needs <see cref="SecretKey"/>; without both, requests are anonymous.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The secret key. A secret: keep it out of logs and out of source control.</summary>
    public string? SecretKey { get; set; }

    /// <summary>The bucket. Required.</summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// Allows plain HTTP (<see cref="UseSSL"/> false) toward a host that is not loopback, such as a server on a private
    /// network. Off by default, so a configuration that forgot <see cref="UseSSL"/> fails instead of sending files in
    /// clear text. New in 2027.0.0.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// The server's region (<c>MINIO_SITE_REGION</c>), which signs every request. When empty, the provider asks the server
    /// for the bucket's region once, on first use. New in 2027.0.0.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Creates the bucket on first use when it does not exist. When false (the default), every member throws
    /// <see cref="InvalidOperationException"/> naming the bucket. New in 2027.0.0; until then the provider created a
    /// missing bucket on its first write.
    /// </summary>
    public bool CreateBucketIfMissing { get; set; }
}
