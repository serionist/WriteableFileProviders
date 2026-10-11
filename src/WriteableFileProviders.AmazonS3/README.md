# WriteableFileProviders.AmazonS3

A writeable `IFileProvider` over an Amazon S3 bucket, or a bucket on any S3-compatible server, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the local disk, Azure Blob Storage, MinIO, Google Cloud Storage, Box and SharePoint
providers. Built on the AWS SDK for .NET v4 (`AWSSDK.S3` 4).

## Install

```
dotnet add package WriteableFileProviders.AmazonS3
```

It needs .NET 10 and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in an application
must be on the same major version: a `2026.10.10.x` package does not load against `WriteableFileProviders.Common` 2027.

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

From configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or
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

With a client you build yourself (assumed roles, custom retries, a shared client), which the provider never disposes:

```csharp
var files = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "my-bucket" }, client);
```

With a client, the credentials and `ForcePathStyle` are the client's; the options give the bucket, the root, the
storage class and `CreateBucketIfMissing`, and, when set, `ServiceUrl` (the provider then checks conditions itself
before a write, as it does toward any endpoint named, and, unless the endpoint is one of S3's own host names, sends no
requester-pays header) and `BucketRegion` (where `CreateBucketIfMissing` creates the bucket on S3). A client whose own
`ServiceURL` is set is treated the same way. The provider class is sealed: to change what it does, wrap it, or derive
your own provider from `WriteableFileProvider<TOptions>`.

## Configuration

| Key | Meaning |
|---|---|
| `BucketName` | The bucket. Required. |
| `BucketRegion` | The bucket's region, such as `eu-west-1`. Required unless `ServiceUrl` is set, where it is the signing region (us-east-1 when empty). `""`, `US` and `EU`, S3's old names, read as us-east-1 and eu-west-1. |
| `AccessKey`, `SecretKey` | A long-term key pair. |
| `IsAnonymus` | Unsigned requests, for a public bucket (the spelling is kept for stored configurations). The constructors without a key set it. |
| `UseDefaultCredentials` | The AWS SDK's default credential chain: environment variables, profiles, web identity, ECS, EC2 instance metadata, resolved on first use. Takes precedence over `IsAnonymus`; cannot be combined with `AccessKey`. New in 2027. |
| `StorageClassForNewFiles` | The storage class of new files and copies, by name (`STANDARD_IA`); `STANDARD` by default. The 2026 object form `{"Value":"STANDARD_IA"}` still reads from JSON; in a configuration file, give the name as a string. |
| `ServiceUrl` | An S3-compatible server instead of AWS, such as `http://localhost:9000`. Requests then send checksums only where S3 requires them, presigned URLs keep the endpoint's scheme, and the provider checks conditions before a write as well as sending them. An endpoint that is one of S3's own host names (under `amazonaws.com` or `amazonaws.com.cn`, with a label `s3`, such as `s3.eu-west-1.amazonaws.com`, a dual-stack or a VPC endpoint) still accepts requester-pays charges and gives a bucket it creates its region; the legacy dashed names (`s3-eu-west-1`, `s3-external-1`, `s3-fips`) and any other host under `amazonaws.com` (a load balancer, an instance, API Gateway) do not. New in 2027. |
| `ForcePathStyle` | Address the bucket in the path (`host/bucket/key`), as most S3-compatible servers need. New in 2027. |
| `CreateBucketIfMissing` | Create the bucket on first use. Otherwise a missing bucket makes every member throw `InvalidOperationException`. New in 2027. |
| `RootDirectoryPath` (or `RootDirectorySegments`) | The folder the provider is confined to; nothing outside it can be reached. |

The options added in 2027 are written to JSON only when set, so a configuration that uses none of them keeps its
2026 shape. The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

Sign-in, first match wins: `UseDefaultCredentials`, then `IsAnonymus`, then `AccessKey` with `SecretKey`. A
configuration with none of them is refused when the provider is built. For anything else, pass your own `IAmazonS3`
(see Use).

## How it behaves

- **Folders** are key prefixes: a folder exists while a key is under it (or a `dir/` marker key another tool made), and
  an empty one does not. Names are case-sensitive.
- **Listings** leave out keys a path cannot name: an empty or whitespace segment, one of only dots and spaces such as
  `..`, a backslash, a control character, or a percent-encoded form of one of these. They carry no content type;
  `GetFileInfoAsync` does. A recursive listing is one flat, paged listing of the prefix.
- **Writes** up to 8 MiB go in one request, larger ones in a multipart upload of 8 MiB parts. Parts grow past
  10,000 × 8 MiB (about 78 GiB) for a stream that knows its length, and past the first 1,000 parts (about 7.8 GiB) for
  one that does not. S3 replaces a file only once all of it arrived, so a failed write leaves the old content. The
  caller's stream is read from its position and never seeked, rewound or disposed.
- **Conditions:** `IfMatch` and `IfNotExists` go to S3 with the write, which AWS checks atomically. Toward any
  endpoint named by `ServiceUrl` or a client's `ServiceURL`, which may be a server that ignores some of them, the
  provider also checks them first, which is not atomic. An append rewrites the file with `If-Match` on the version it
  read, so a concurrent write is not lost: the append throws `PreconditionFailedException` instead.
- **Content type:** every write stores the type given, or `application/octet-stream` without one; an append and a local
  stream's commit keep the file's type when none is given.
- **Copy and move** happen on the server. As on every provider, the destination is first checked not to exist; the
  copy also sends `If-None-Match`, which AWS enforces atomically and some S3-compatible servers ignore. Files
  over 5 GiB are copied in parts of 512 MiB or more, keeping the content type, language, encoding, disposition, cache
  control, expiry and metadata (not tags). A move deletes exactly the source key, with `If-Match` on the version it
  copied. A source deleted meanwhile completes the move. A source changed meanwhile undoes it: the copy is deleted with
  `If-Match` on its own version, and the move throws `PreconditionFailedException`; a copy that another writer has
  replaced meanwhile is left as it is, and the exception says so. Atomic on AWS without a `ServiceUrl`; toward a named
  endpoint each version is also checked just before its delete.
- **Reads** stream from S3. `ReadRangeAsync` is a ranged GET, sent with `If-Match` when `ReadOptions.IfMatch` is set.
  `OpenReadSeekableAsync` reads ranged GETs pinned with `If-Match` to the version it opened, unless
  `AllowModifications` is set.
- **Requester-pays** buckets on AWS work: object and listing requests accept the charge. Presigned URLs do not carry
  it.
- **Versions:** `ReadFileVersionAsync(versionId, path)` reads a version in a bucket with versioning.
- Nothing touches the network before the first call, which resolves the credentials (if asked) and checks the bucket.

## Signed URLs

`GetSignedUrlAsync` presigns read (GET), create (PUT with a signed `If-None-Match: *`) and write (PUT) URLs, SigV4,
for at most 7 days. A longer validity throws `NotSupportedException`, so WriteableFileProviders.AspNetCore can issue a
gateway URL instead.

- A read URL can carry a download file name and a content type (`response-content-disposition`, `-type`), and needs
  the file to exist (`FileNotFoundException`). An upload URL with a download file name throws
  `NotSupportedException`.
- An upload URL with a `ContentType` signs it. The upload must send every header in `SignedUrl.Headers`.
- A URL never outlives the credential that signs it. Long-term keys live as asked. A credential that expires sooner (STS,
  instance metadata), or whose expiry cannot be known (a session token, a client you passed in), makes the URL shorter
  than asked: it is issued only with `AllowShorterValidity`, with `ExpiresOn` set when the expiry is known.
- Anonymous providers cannot presign.

## Limits

- 10,000 parts per file: up to 5 TiB from a stream that knows its length, about 430 GiB from one that does not.
- Copies over 5 GiB take one request per part, plus two.
- A missing bucket throws `InvalidOperationException` naming the bucket and `CreateBucketIfMissing`. The bucket is
  checked once, on first use; a bucket deleted later is reported by the next call that finds it gone.
- Toward an endpoint named by `ServiceUrl` or a client's `ServiceURL`, conditions are checked first and then sent,
  atomic only where the server enforces them; this holds for S3's own host names too, since a name under
  `amazonaws.com` may front another server. Unless the endpoint is one of S3's own host names (under `amazonaws.com` or
  `amazonaws.com.cn`, with a label `s3`; not the legacy dashed `s3-` names), requests also do not accept requester-pays
  charges, and the provider gives a bucket it creates no location (the AWS SDK may still add one it reads from a host
  under `amazonaws.com`).
- The SDK's configured endpoints (`AWS_ENDPOINT_URL_S3`) redirect a provider without `ServiceUrl`, as they do every AWS
  client. Such a provider still counts as AWS: it leaves conditions to the endpoint, which must then enforce them as AWS
  does. Point at another server with `ServiceUrl` instead.
- Change notifications (`Watch`) are not supported.

## More

- [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): every package, the provider
  matrix, versioning.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#amazon-s3):
  what 2027.0.0 changed for this provider.
