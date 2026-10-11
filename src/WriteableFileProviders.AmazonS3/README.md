# WriteableFileProviders.AmazonS3

A writeable `IFileProvider` over an Amazon S3 bucket, or a bucket on any S3-compatible server, on the AWS SDK for .NET
v4. Part of [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): the same API and the same
behaviour as every other provider. All WriteableFileProviders packages must be on the same major version.

```
dotnet add package WriteableFileProviders.AmazonS3
```

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AmazonS3;

await using var files = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions(
    rootDirectory: "tenants/acme", accessKey: "...", secretKey: "...", bucketName: "my-bucket", bucketRegion: "eu-west-1"));

await files.UpdateFileAsync("reports/2026.csv", stream);
await using var slice = await files.ReadRangeAsync("reports/2026.csv", 0, 1024);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) });
```

Or from configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or
`services.AddWriteableFileProvider("files", section)`:

```json
{
  "Files": {
    "Type": "WriteableAmazonS3FileProvider",
    "BucketName": "my-bucket",
    "BucketRegion": "eu-west-1",
    "AccessKey": "...",
    "SecretKey": "...",
    "RootDirectoryPath": "tenants/acme"
  }
}
```

## Configuration keys

| Key | Meaning |
|---|---|
| `BucketName` | The bucket. Required. |
| `BucketRegion` | The bucket's region, such as `eu-west-1`. Required unless `ServiceUrl` is set, where it is the signing region (us-east-1 when empty). `""`, `US` and `EU`, S3's old names, read as us-east-1 and eu-west-1. |
| `AccessKey`, `SecretKey` | A long-term key pair. |
| `IsAnonymus` | Unsigned requests, for a public bucket (the spelling is kept for stored configurations). The constructors without a key set it. |
| `UseDefaultCredentials` | The AWS SDK's default credential chain: environment variables, profiles, web identity, ECS, EC2 instance metadata, resolved on first use. Takes precedence over `IsAnonymus`; cannot be combined with `AccessKey`. New in 2027. |
| `StorageClassForNewFiles` | The storage class of new files and copies, by name (`STANDARD_IA`); `STANDARD` by default. The 2026 object form `{"Value":"STANDARD_IA"}` still reads. |
| `ServiceUrl` | An S3-compatible server instead of AWS, such as `http://localhost:9000`. Requests then send checksums only where S3 requires them, and presigned URLs keep the endpoint's scheme. New in 2027. |
| `ForcePathStyle` | Address the bucket in the path (`host/bucket/key`), as most S3-compatible servers need. New in 2027. |
| `CreateBucketIfMissing` | Create the bucket on first use. Otherwise a missing bucket makes every member throw `InvalidOperationException`. New in 2027. |
| `RootDirectoryPath` (or `RootDirectorySegments`) | The folder the provider is confined to; nothing outside it can be reached. |

The options added in 2027 are written to JSON only when set, so a configuration that uses none of them keeps its
2026 shape. The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

Sign-in, first match wins: `UseDefaultCredentials`, then `IsAnonymus`, then `AccessKey` with `SecretKey`. A
configuration with none of them is refused when the provider is built. For anything else (assumed roles, custom
retries, a shared client), build an `IAmazonS3` yourself and pass it:

```csharp
var files = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "my-bucket" }, client);
```

The provider never disposes a client it was given. Its options then give only the bucket, the root, the storage class
and `CreateBucketIfMissing`.

## Behaviour

- **Folders** are key prefixes: a folder exists while a key is under it, and an empty one does not. Names are
  case-sensitive. Keys a path cannot name (an empty segment, `..`, a backslash) are left out of listings.
- **Writes** up to 8 MiB go in one request, larger ones in a multipart upload of 8 MiB parts (larger for files over
  80 GiB). S3 replaces a file only once all of it arrived, so a failed write leaves the old content. The caller's stream
  is read from its position and never seeked, rewound or disposed.
- **Conditions:** `IfMatch` and `IfNotExists` go to S3 with the write, which AWS checks atomically. Toward an
  S3-compatible server (`ServiceUrl`), which may ignore some of them, the provider also checks them first, which is not
  atomic. An append rewrites the file with `If-Match` on the version it read, so a concurrent write is not lost: the
  append throws `PreconditionFailedException` instead.
- **Content type:** the type given is stored; without one a new file is `application/octet-stream`, and an append keeps
  the file's type. Listings carry no content type; `GetFileInfoAsync` does.
- **Copy and move** happen on the server. An existing destination is refused, atomically on AWS (`If-None-Match`);
  S3-compatible servers that ignore that header on copies are still checked first. Files over 5 GiB are copied in
  parts, keeping the content type, language, encoding, disposition, cache control, expiry and metadata (not tags). A
  move deletes exactly the source key, with `If-Match` on the version it copied. A source deleted meanwhile completes
  the move. A source changed meanwhile undoes it: the copy is deleted with `If-Match` on its own version, and the move
  throws `PreconditionFailedException`; a copy that another writer has replaced meanwhile is left as it is, and the
  exception says so. Atomic on AWS; elsewhere each version is also checked just before its delete.
- **Ranged and seekable reads** are ranged GETs, pinned to the version with `If-Match`.
- **Requester-pays** buckets on AWS work: every request accepts the charge.
- **Versions:** `ReadFileVersionAsync(versionId, path)` reads a version in a bucket with versioning.
- Nothing touches the network before the first call, which resolves the credentials (if asked) and checks the bucket.

## Signed URLs

`GetSignedUrlAsync` presigns read (GET), create (PUT with a signed `If-None-Match: *`) and write (PUT) URLs, SigV4,
for at most 7 days. A longer validity throws `NotSupportedException`, so WriteableFileProviders.AspNetCore can issue a
gateway URL instead.

- A read URL can carry a download file name and a content type (`response-content-disposition`, `-type`).
- An upload URL with a `ContentType` signs it. The upload must send every header in `SignedUrl.Headers`.
- A URL never outlives the credential that signs it. Long-term keys live as asked. A credential that expires sooner (STS,
  instance metadata), or whose expiry cannot be known (a session token, a client you passed in), makes the URL shorter
  than asked: it is issued only with `AllowShorterValidity`, with `ExpiresOn` set when the expiry is known.
- Anonymous providers cannot presign.

## Limits

- 10,000 parts per file: up to 5 TiB from a stream that knows its length, about 430 GiB from one that does not.
- Copies over 5 GiB take one request per 512 MiB part.
- A missing bucket throws `InvalidOperationException` naming the bucket and `CreateBucketIfMissing`. The bucket is
  checked once, on first use; a bucket deleted later is reported by the next call that finds it gone.
- Off AWS (`ServiceUrl`), conditions are checked first and then sent: atomic only where the server enforces them.
- The SDK's configured endpoints (`AWS_ENDPOINT_URL_S3`) redirect a provider without `ServiceUrl`, as they do every AWS
  client. Such a provider still counts as AWS: it leaves conditions to the endpoint, which must then enforce them as AWS
  does. Point at another server with `ServiceUrl` instead.

Moving from 2026: see
[docs/migration-2027.md](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md).
