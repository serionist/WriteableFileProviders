# WriteableFileProviders.AmazonS3

An Amazon S3 bucket, or a bucket on an S3-compatible server, as an `IWriteableFileProvider`, on `AWSSDK.S3` 4. Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme).

## Install

```
dotnet add package WriteableFileProviders.AmazonS3
```

Needs .NET 10, and is native AOT compatible. Keep every WriteableFileProviders package in an app on the same
major version.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AmazonS3;

await using var files = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions
{
    BucketName = "my-bucket",
    BucketRegion = "eu-west-1",
    UseDefaultCredentials = true,
    RootDirectoryPath = "tenants/acme",
});

await files.UpdateFileAsync("reports/2026.csv", stream);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) });
```

From configuration (`services.AddWriteableFileProvider(section)`):

```json
{ "Files": { "Type": "WriteableAmazonS3FileProvider", "BucketName": "my-bucket", "BucketRegion": "eu-west-1",
             "AccessKey": "...", "SecretKey": "..." } }
```

Or pass an `IAmazonS3` you build (assumed roles, retries, a shared client), which the provider never disposes:
`new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "my-bucket" }, s3)`. The client's
credentials and endpoint are used; the options give the bucket, the root, the storage class and `CreateBucketIfMissing`.

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `BucketName` | required | The bucket. |
| `BucketRegion` | | The bucket's region, such as `eu-west-1`. Required unless `ServiceUrl` is set; then it is the signing region (`us-east-1` when empty). |
| `AccessKey`, `SecretKey` | | A key pair. The secret key is a secret. |
| `UseDefaultCredentials` | `false` | Use the AWS SDK's default credential chain: environment, profiles, web identity, ECS, EC2. Not with `AccessKey`. |
| `IsAnonymus` | `false` | Send unsigned requests, for a public bucket. The spelling is kept for stored configurations. |
| `StorageClassForNewFiles` | `STANDARD` | The storage class of new files and copies, by name (`STANDARD_IA`). |
| `ServiceUrl` | AWS | An S3-compatible endpoint instead of AWS, such as `http://localhost:9000`. |
| `ForcePathStyle` | `false` | Put the bucket in the path (`host/bucket/key`), as most S3-compatible servers need. |
| `AllowInsecureHttp` | `false` | Allow plain `http` to a host that isn't loopback. Without it, such an endpoint throws `ArgumentException`. |
| `CreateBucketIfMissing` | `false` | Create the bucket on first use. Without it, a missing bucket throws `InvalidOperationException`. |
| `RootDirectoryPath` | the bucket | The folder in the bucket the provider is confined to. |

## Authentication

The first that applies: `UseDefaultCredentials`, then `IsAnonymus`, then `AccessKey` with `SecretKey`. None of them,
or `UseDefaultCredentials` with `AccessKey`, throws `ArgumentException` when the provider is built. For anything else,
pass your own `IAmazonS3`. Nothing touches the network until the first call.

## What's different

- **Writes** up to 8 MiB are one `PUT`, larger ones a multipart upload. A failed write leaves the old content.
- **Conditions** (`IfMatch`, `IfNotExists`) go to S3 with the write, which checks them atomically. Toward a
  `ServiceUrl`, the provider also checks them first: not atomic, since the server may ignore them.
- **Appends** rewrite the file with `If-Match` on the version read. After a concurrent write the append reads the file
  again and appends after it, up to five attempts in all, then throws `PreconditionFailedException`.
- **Copies and moves** run on the server, files over 5 GiB in parts of 512 MiB or more. A move deletes the source
  with `If-Match` and undoes the copy if the source changed meanwhile.
- **Listings** carry no content type; `GetFileInfoAsync` does. Requester-pays buckets work on AWS.
- **Old versions:** `ReadFileVersionAsync(versionId, path)` reads one in a bucket with versioning.
- **Signed URLs** are presigned (SigV4) read and write URLs, up to 7 days. Create URLs are native only on AWS without
  a `ServiceUrl`; elsewhere the gateway issues them. A URL never outlives its credential: with temporary credentials,
  or your own client, set `AllowShorterValidity`. An anonymous provider can't presign.
- **`AWS_ENDPOINT_URL_S3`**, `AWS_ENDPOINT_URL` or a profile's `endpoint_url` redirect the provider like a
  `ServiceUrl` does. Set `AWS_IGNORE_CONFIGURED_ENDPOINT_URLS` to ignore them.
- **Limits:** 10,000 parts per file: 5 TiB from a stream with a known length, about 430 GiB without. `Watch` throws
  `NotSupportedException`.

## Learn more

- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#amazon-s3),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#amazon-s3),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#amazon-s3).
