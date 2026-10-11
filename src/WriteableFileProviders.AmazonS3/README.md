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
`services.AddWriteableFileProvider(section)`:

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
before a write and issues no native create URL, as it does toward any endpoint named, and, unless the endpoint is one
of S3's own host names, sends no requester-pays header) and `BucketRegion` (where `CreateBucketIfMissing` creates the
bucket on S3). A client whose own `ServiceURL` is set is treated the same way, and one that sends plain `http` to a host
that is not loopback needs `AllowInsecureHttp`. The provider class is sealed: to change what it does, wrap it, or
derive your own provider from `WriteableFileProvider<TOptions>`.

## Configuration

| Key | Meaning |
|---|---|
| `BucketName` | The bucket. Required. |
| `BucketRegion` | The bucket's region, such as `eu-west-1`. Required unless `ServiceUrl` is set, where it is the signing region (us-east-1 when empty). `""`, `US` and `EU`, S3's old names, read as us-east-1 and eu-west-1. |
| `AccessKey`, `SecretKey` | A long-term key pair. |
| `IsAnonymus` | Unsigned requests, for a public bucket (the spelling is kept for stored configurations). The constructors without a key set it. |
| `UseDefaultCredentials` | The AWS SDK's default credential chain: environment variables, profiles, web identity, ECS, EC2 instance metadata, resolved on first use. Takes precedence over `IsAnonymus`; cannot be combined with `AccessKey`. New in 2027. |
| `StorageClassForNewFiles` | The storage class of new files and copies, by name (`STANDARD_IA`); `STANDARD` by default. The 2026 object form `{"Value":"STANDARD_IA"}` still reads from JSON; in a configuration file, give the name as a string. |
| `ServiceUrl` | An S3-compatible server instead of AWS, such as `http://localhost:9000`. Requests then send checksums only where S3 requires them, presigned URLs keep the endpoint's scheme, the provider checks conditions before a write as well as sending them, and it issues no native create URL (the gateway does). Plain `http` toward a host that is not loopback needs `AllowInsecureHttp`. An endpoint that is one of S3's own host names (under `amazonaws.com` or `amazonaws.com.cn`, with a label `s3` or `s3-fips`, such as `s3.eu-west-1.amazonaws.com`, a FIPS, a dual-stack or a VPC endpoint) still accepts requester-pays charges and gives a bucket it creates its region; the legacy dashed regional names (`s3-eu-west-1`, `s3-external-1`) and any other host under `amazonaws.com` (a load balancer, an instance, API Gateway) do not. New in 2027. |
| `AllowInsecureHttp` | Allows a `ServiceUrl`, or a client's own endpoint, with plain `http` toward a host that is not loopback, such as a server on a private network: files, session tokens and presigned URLs then cross the network in clear text. Off by default, so such an endpoint throws `ArgumentException` when the provider is built. New in 2027. |
| `ForcePathStyle` | Address the bucket in the path (`host/bucket/key`), as most S3-compatible servers need. New in 2027. |
| `CreateBucketIfMissing` | Create the bucket on first use. Otherwise a missing bucket makes every member throw `InvalidOperationException`. New in 2027. |
| `RootDirectoryPath` (or `RootDirectorySegments`) | The folder the provider is confined to; nothing outside it can be reached. |

The options added in 2027 are written to JSON only when set, so a configuration that uses none of them keeps its
2026 shape. The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

Sign-in: `UseDefaultCredentials` (the AWS SDK's default credential chain; it cannot be combined with `AccessKey`), else
`IsAnonymus` (unsigned requests, ignoring any `AccessKey`), else `AccessKey` with `SecretKey`. A configuration with
none of them, or with `UseDefaultCredentials` and `AccessKey` both set, is refused when the provider is built. For
anything else, pass your own `IAmazonS3` (see Use).

## How it behaves

- **Folders** are key prefixes: a folder exists while a key is under it (or a `dir/` marker key another tool made), and
  an empty one does not. Names are case-sensitive. So a file and a folder may share a name: a file written at a
  folder's path, or a folder under a file's path, is stored, reads back by path, and a listing shows both (where the
  server is MinIO, which lists only the file and hides what is under the folder while the file exists, see the MinIO
  package). `GetFileInfoAsync` of such a name describes the file. Deleting it removes the file first, and a second
  delete the folder; a writeable local stream of a folder's path still throws `IOException`.
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
- **Errors:** S3's 401 and 403, and an expired or invalid session token, throw `UnauthorizedAccessException`, and any
  other failure of S3 or the network, after the SDK's retries, `IOException`
  ([Errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#amazon-s3) has the codes).

## Signed URLs

Presigned (SigV4) URLs for reading and writing, for up to 7 days, and for creating on AWS only: toward a named
endpoint, which may ignore the condition that guards a create URL, the gateway issues those. A URL never outlives the
credential that signs it, so a credential that expires sooner, or whose expiry cannot be known, needs
`AllowShorterValidity`; anonymous providers cannot presign. The details are in
[Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#amazon-s3).

## Limits

- 10,000 parts per file: up to 5 TiB from a stream that knows its length, about 430 GiB from one that does not.
- Copies over 5 GiB take one request per part, plus two.
- A missing bucket throws `InvalidOperationException` naming the bucket and `CreateBucketIfMissing`. The bucket is
  checked once, on first use; a bucket deleted later is reported by the next call that finds it gone.
- Toward an endpoint named by `ServiceUrl` or a client's `ServiceURL`, conditions are checked first and then sent,
  atomic only where the server enforces them, and create URLs come from the gateway; this holds for S3's own host
  names too, since a name under `amazonaws.com` may front another server. Unless the endpoint is one of S3's own host
  names (under `amazonaws.com` or `amazonaws.com.cn`, with a label `s3` or `s3-fips`; not the legacy dashed regional
  names such as `s3-eu-west-1`), requests also do not accept requester-pays charges, and the provider gives a bucket
  it creates no location (the AWS SDK may still add one it reads from a host under `amazonaws.com`).
- Plain `http` toward a host that is not loopback, through `ServiceUrl` or a client's own endpoint, is refused when the
  provider is built unless `AllowInsecureHttp` is set.
- An endpoint the AWS SDK takes from its own configuration (`AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, or a profile's
  `endpoint_url`) when the provider is built redirects a provider without `ServiceUrl`, as it does every AWS client,
  and counts as a named endpoint, as `ServiceUrl` does: requests send checksums only where S3 requires them, conditions
  are checked first, create URLs come from the gateway, plain `http` toward a host that is not loopback needs
  `AllowInsecureHttp`, and requester-pays and a created bucket's location apply only to S3's own host names. One
  configured later does not redirect a provider already built. Set `AWS_IGNORE_CONFIGURED_ENDPOINT_URLS` to have the
  SDK ignore it.
- Change notifications (`Watch`) are not supported.

## Learn more

- [Providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md): what
  every store can do, side by side.
- The guide's [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [paths and listings](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md)
  and [dependency injection](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#amazon-s3):
  what 2027.0.0 changed for this provider.
