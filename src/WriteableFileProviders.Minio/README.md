# WriteableFileProviders.Minio

A writeable `IFileProvider` over a MinIO bucket. Since 2027 it runs on the AWS SDK for .NET v4 (the S3 protocol MinIO
speaks), on the same code as WriteableFileProviders.AmazonS3; the Minio SDK is no longer a dependency. Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): the same API and the same behaviour as
every other provider. All WriteableFileProviders packages must be on the same major version.

```
dotnet add package WriteableFileProviders.Minio
```

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Minio;

await using var files = new WriteableMinioFileProvider(new MinioFileProviderOptions(
    rootDirectory: "tenants/acme", hostAndPort: "minio.example.net:9000", accessKey: "...", secretKey: "...", bucketName: "files")
{
    UseSSL = true,
});

await files.UpdateFileAsync("reports/2026.csv", stream);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) });
```

Or from configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or
`services.AddWriteableFileProvider("files", section)`:

```json
{
  "Files": {
    "Type": "WriteableMinioFileProvider",
    "HostAndPort": "minio.example.net:9000",
    "UseSSL": true,
    "AccessKey": "...",
    "SecretKey": "...",
    "BucketName": "files",
    "RootDirectoryPath": "tenants/acme"
  }
}
```

## Configuration keys

| Key | Meaning |
|---|---|
| `HostAndPort` | The server, as `host:port` or `host`, without a scheme or a path. Required. |
| `UseSSL` | HTTPS when true. Plain HTTP (false) is allowed toward a loopback host only, unless `AllowInsecureHttp` is set. |
| `AllowInsecureHttp` | Allows plain HTTP toward any host, such as a server on a private network. New in 2027. |
| `AccessKey`, `SecretKey` | The key pair; without both, requests are anonymous and cannot be presigned. |
| `BucketName` | The bucket. Required. |
| `Region` | The server's region (`MINIO_SITE_REGION`). When empty, the provider asks the server once, on first use. New in 2027. |
| `CreateBucketIfMissing` | Create the bucket on first use. Otherwise a missing bucket makes every member throw `InvalidOperationException`. New in 2027; until then the provider created it on its first write. |
| `RootDirectoryPath` (or `RootDirectorySegments`) | The folder the provider is confined to; nothing outside it can be reached. |

The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

A MinIO key pair (`AccessKey`, `SecretKey`), or none for anonymous access. For anything else, build an `IAmazonS3`
yourself (an `AmazonS3Client` with `ServiceURL` and `ForcePathStyle = true`) and pass it:

```csharp
var files = new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "files" }, client);
```

The provider never disposes a client it was given. Its options then give only the bucket, the root and
`CreateBucketIfMissing`.

## Behaviour

- **Folders** are key prefixes: a folder exists while a key is under it, and an empty one does not. Names are
  case-sensitive. Keys a path cannot name are left out of listings.
- **Reads** stream from the server (2026 downloaded the whole file to a temp file first). Ranged and seekable reads are
  ranged GETs, pinned to the version with `If-Match`.
- **Writes** up to 8 MiB go in one request, larger ones in a multipart upload. MinIO replaces a file only once all of it
  arrived, so a failed write leaves the old content. The caller's stream is never seeked, rewound or disposed.
- **Conditions:** `IfMatch` and `IfNotExists` are sent to the server, which checks them atomically. An append rewrites
  the file with `If-Match` on the version it read, so a concurrent write is never lost.
- **Content type:** the type given is stored; without one a new file is `application/octet-stream`, and an append keeps
  the file's type.
- **Copy and move** happen on the server. MinIO ignores `If-None-Match` on copies, so an existing destination is
  checked first, which is not atomic. A move deletes exactly the source key.
- No storage class is sent: MinIO refuses the ones it does not know.
- Nothing touches the network before the first call, which asks for the region (unless `Region` names it) and checks
  the bucket.

## Signed URLs

`GetSignedUrlAsync` presigns read (GET), create (PUT with a signed `If-None-Match: *`) and write (PUT) URLs for at most
7 days, over the scheme `UseSSL` chooses. A longer validity throws `NotSupportedException`, so
WriteableFileProviders.AspNetCore can issue a gateway URL instead. A read URL can carry a download file name and a
content type; an upload must send every header in `SignedUrl.Headers`. URLs from a client you passed in need
`AllowShorterValidity`, since the provider cannot see when its credentials expire.

## Limits

- MinIO releases older than its conditional-write support may ignore `If-Match` and `If-None-Match`: on such a server
  the conditions, and the protection of concurrent appends, do not hold.
- A server with a site region and a bucket that does not exist yet needs `Region`, so the provider can create the
  bucket with `CreateBucketIfMissing`.
- 10,000 parts per file, as on S3.

Moving from 2026: see
[docs/migration-2027.md](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md).
