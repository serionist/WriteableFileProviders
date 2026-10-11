# WriteableFileProviders.Minio

A writeable `IFileProvider` over a MinIO bucket, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the local disk, Azure Blob Storage, Amazon S3, Google Cloud Storage, Box and SharePoint
providers. Since 2027 it runs on the AWS SDK for .NET v4 (the S3 protocol MinIO speaks), on the same code as
WriteableFileProviders.AmazonS3; the Minio SDK is no longer a dependency.

## Install

```
dotnet add package WriteableFileProviders.Minio
```

It needs .NET 10 and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in an application
must be on the same major version: a `2026.10.10.x` package does not load against `WriteableFileProviders.Common` 2027.

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

From configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or
`services.AddWriteableFileProvider(section)`:

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

With a client you build yourself (an `AmazonS3Client` with `ServiceURL` and `ForcePathStyle = true`), which the
provider never disposes:

```csharp
var files = new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "files" }, client);
```

With a client, the options give only the bucket, the root and `CreateBucketIfMissing`, and `AllowInsecureHttp` when
the client's own endpoint is plain `http` toward a host that is not loopback. The provider class is sealed: to change
what it does, wrap it, or derive your own provider from `WriteableFileProvider<TOptions>`.

## Configuration

| Key | Meaning |
|---|---|
| `HostAndPort` | The server, as `host:port` or `host`, without a scheme, a path, a query or user info. Required. |
| `UseSSL` | HTTPS when true. Plain HTTP (false) is allowed toward a loopback host only, unless `AllowInsecureHttp` is set. |
| `AllowInsecureHttp` | Allows plain HTTP toward any host, such as a server on a private network, through `UseSSL` false or a client's own endpoint. New in 2027. |
| `AccessKey`, `SecretKey` | The key pair, set together (one without the other throws `ArgumentException`). Without either, requests are anonymous and cannot be presigned. |
| `BucketName` | The bucket. Required. |
| `Region` | The server's region (`MINIO_SITE_REGION`). When empty, the provider asks the server once, on first use. New in 2027. |
| `CreateBucketIfMissing` | Create the bucket on first use. Otherwise a missing bucket makes every member throw `InvalidOperationException`. New in 2027; until then the provider created it on its first write. |
| `RootDirectoryPath` (or `RootDirectorySegments`) | The folder the provider is confined to; nothing outside it can be reached. |

The options added in 2027 are written to JSON only when set, so a configuration that uses none of them keeps its
2026 shape. The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

A MinIO key pair (`AccessKey`, `SecretKey`), or none for anonymous access. For anything else, pass your own
`IAmazonS3` (see Use).

## How it behaves

- **Folders** are key prefixes: a folder exists while a key is under it (or a `dir/` marker key another tool made), and
  an empty one does not. Names are case-sensitive. So a file and a folder may share a name: a file written at a
  folder's path, or a folder under a file's path, is stored and reads back by path. MinIO then lists only the file, and
  hides what is under the folder from every listing while the file exists; deleting the file shows the folder again.
  `GetFileInfoAsync` of such a name describes the file. Deleting it removes the file first, and a second delete the
  folder; a writeable local stream of a folder's path still throws `IOException`.
- **Listings** leave out keys a path cannot name: an empty or whitespace segment, one of only dots and spaces such as
  `..`, a backslash, a control character, or a percent-encoded form of one of these. They carry no content type;
  `GetFileInfoAsync` does.
- **Reads** stream from the server (2026 downloaded the whole file to a temp file first). `ReadRangeAsync` is a ranged
  GET, sent with `If-Match` when `ReadOptions.IfMatch` is set; `OpenReadSeekableAsync` reads ranged GETs pinned with
  `If-Match` to the version it opened, unless `AllowModifications` is set.
- **Writes** up to 8 MiB go in one request, larger ones in a multipart upload. MinIO replaces a file only once all of it
  arrived, so a failed write leaves the old content. The caller's stream is never seeked, rewound or disposed.
- **Conditions:** `IfMatch` and `IfNotExists` are checked by the provider first, which is not atomic, and sent with the
  write, which MinIO enforces where it supports conditional writes. An append rewrites the file with `If-Match` on the
  version it read: on such a server a concurrent write is never lost, and on an older one only a write between the
  check and the rewrite can be.
- **Content type:** every write stores the type given, or `application/octet-stream` without one; an append and a local
  stream's commit keep the file's type when none is given.
- **Copy and move** happen on the server. MinIO ignores `If-None-Match` on copies, so an existing destination is
  checked first, which is not atomic. A move deletes exactly the source key, after checking that the source is still
  the version it copied (MinIO ignores `If-Match` on deletes, so this is not atomic either). A source deleted meanwhile
  completes the move; a source changed meanwhile undoes it, deleting the copy only while it is still the copy, and the
  move throws `PreconditionFailedException`. Copies over 5 GiB keep the content type, language, encoding, disposition,
  cache control, expiry and metadata (not tags).
- No storage class is sent: MinIO refuses the ones it does not know.
- Nothing touches the network before the first call, which asks for the region (unless `Region` names it) and checks
  the bucket.
- **Errors** are the contract's types: the server's 401 and 403, whatever the code (`AccessDenied`,
  `InvalidAccessKeyId`, `SignatureDoesNotMatch`, a code of the server's own, or none, as a HEAD answers), and a session
  token that expired or is not valid (`ExpiredToken`, `InvalidToken`, a 400), throw `UnauthorizedAccessException`,
  except an archived object (`InvalidObjectState`, `ObjectNotInActiveTierError`) and a skewed clock
  (`RequestTimeTooSkewed`); a bucket name another account owns, with `CreateBucketIfMissing`,
  `InvalidOperationException`; any other failure of the server or the network, after the SDK's retries, `IOException`:
  timeouts, a wrong `Region`, the archived object and the skewed clock included. The SDK's `AmazonS3Exception`, or what
  it threw, is the `InnerException`.

## Signed URLs

`GetSignedUrlAsync` presigns read (GET) and write (PUT) URLs for at most 7 days, over the scheme `UseSSL` chooses. A
longer validity throws `NotSupportedException`, so WriteableFileProviders.AspNetCore can issue a gateway URL instead. A
read URL can carry a download file name and a content type, and needs the file to exist; an upload URL with a download
file name throws `NotSupportedException`, and an upload must send every header in `SignedUrl.Headers`. URLs from a
client you passed in need `AllowShorterValidity`, since the provider cannot see when its credentials expire.

A create URL (only if the file does not exist yet) throws `NotSupportedException`: its only guard would be the
`If-None-Match: *` it signs, which MinIO releases without conditional writes ignore, so it could overwrite a file. The
gateway issues create URLs instead, and checks the condition before the write (as `IfNotExists` does here).

## Limits

- Conditions, moves and copies are checked by the provider first, then by MinIO where it can: not atomic. MinIO
  releases older than its conditional-write support ignore `If-Match` and `If-None-Match` on writes too, so on such a
  server a concurrent change can slip in between the check and the write.
- A server with a site region and a bucket that does not exist yet needs `Region`, so the provider can create the
  bucket with `CreateBucketIfMissing`.
- 10,000 parts per file, as on S3.
- Change notifications (`Watch`) are not supported.

## More

- [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): every package, the provider
  matrix, versioning.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#minio): what
  2027.0.0 changed for this provider.
