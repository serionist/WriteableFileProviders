# WriteableFileProviders.Minio

A MinIO bucket as an `IWriteableFileProvider`. Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme). It runs on `AWSSDK.S3` 4, the
S3 protocol MinIO speaks, not on the Minio SDK.

## Install

```
dotnet add package WriteableFileProviders.Minio
```

Needs .NET 10, and is native AOT compatible. Keep every WriteableFileProviders package in an app on the same
major version.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Minio;

await using var files = new WriteableMinioFileProvider(new MinioFileProviderOptions
{
    HostAndPort = "minio.example.net:9000",
    UseSSL = true,
    AccessKey = configuration["Minio:AccessKey"],
    SecretKey = configuration["Minio:SecretKey"],
    BucketName = "files",
});

await files.UpdateFileAsync("reports/2026.csv", stream);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) });
```

From configuration (`services.AddWriteableFileProvider(section)`):

```json
{ "Files": { "Type": "WriteableMinioFileProvider", "HostAndPort": "minio.example.net:9000", "UseSSL": true,
             "AccessKey": "...", "SecretKey": "...", "BucketName": "files" } }
```

Or pass an `IAmazonS3` you build, such as an `AmazonS3Client` with `ServiceURL` and `ForcePathStyle = true`. The
provider never disposes it, and reads only the bucket, the root, `CreateBucketIfMissing` and `AllowInsecureHttp` from
the options.

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `HostAndPort` | required | The server, as `host:port` or `host`: no scheme, path or query. |
| `UseSSL` | `false` | Use HTTPS. Plain HTTP is allowed only toward a loopback host, unless `AllowInsecureHttp` is set. |
| `AllowInsecureHttp` | `false` | Allow plain HTTP toward any host, such as a server on a private network. |
| `AccessKey`, `SecretKey` | anonymous | The key pair. Set both or neither. The secret key is a secret. |
| `BucketName` | required | The bucket. |
| `Region` | asked from the server | The server's region (`MINIO_SITE_REGION`). Empty: the provider asks once, on first use. |
| `CreateBucketIfMissing` | `false` | Create the bucket on first use. Without it, a missing bucket throws `InvalidOperationException`. |
| `RootDirectoryPath` | the bucket | The folder in the bucket the provider is confined to. |

## Authentication

A MinIO key pair, or none for anonymous access, which can't presign URLs. For anything else, pass your own
`IAmazonS3`. Nothing touches the network until the first call.

## What's different

- **Conditions aren't atomic.** `IfMatch` and `IfNotExists` are checked by the provider first, then sent with the
  write. MinIO releases without conditional writes ignore them, so a concurrent change can slip in between.
- **Copies and moves aren't atomic either:** MinIO ignores `If-None-Match` on copies and `If-Match` on deletes, so the
  provider checks the destination and the source's version itself. A source changed meanwhile undoes the move.
- **A file and a folder with the same name:** MinIO lists only the file, and hides what's under the folder until the
  file is deleted.
- **Writes** up to 8 MiB are one request, larger ones a multipart upload (10,000 parts at most). A failed write leaves
  the old content. Appends rewrite the file with `If-Match` on the version read, and after a concurrent write read it
  again and append after it, up to five attempts in all.
- **Listings** carry no content type; `GetFileInfoAsync` does.
- **No storage class** is sent: MinIO refuses the ones it doesn't know.
- **Signed URLs** are presigned read and write URLs, up to 7 days, over the scheme `UseSSL` picks. Create URLs come
  from the gateway. With your own client, set `AllowShorterValidity`.
- **A server with a site region** needs `Region` for `CreateBucketIfMissing` to create a missing bucket.
- **`Watch`** throws `NotSupportedException`.

## Learn more

- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#minio),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#minio),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#minio).
