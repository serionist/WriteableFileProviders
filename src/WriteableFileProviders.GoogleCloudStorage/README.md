# WriteableFileProviders.GoogleCloudStorage

A writeable `IFileProvider` over a Google Cloud Storage bucket, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): the same `IWriteableFileProvider` API, with
the same meaning, as the local disk, Azure Blob Storage, Amazon S3, MinIO, Box and SharePoint providers. Built on
`Google.Cloud.Storage.V1` 5.

```
dotnet add package WriteableFileProviders.GoogleCloudStorage
```

Every `WriteableFileProviders.*` package must be on the same major version: a `2026.10.10.x` package does not load
against `WriteableFileProviders.Common` 2027.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.GoogleCloudStorage;

await using var files = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions
{
    BucketName = "my-bucket",
    RootDirectoryPath = "tenants/acme",          // optional: everything stays under this prefix
    // No credential: Application Default Credentials (Workload Identity, the metadata server, gcloud).
});

await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
await using var slice = await files.ReadRangeAsync("reports/2026.csv", 0, 1024);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

From configuration (`WriteableFileProviderSerializer.FromConfiguration`, or `services.AddWriteableFileProvider(name,
section)`):

```json
{
  "Files": {
    "Type": "WriteableGoogleCloudStorageFileProvider",
    "BucketName": "my-bucket",
    "ClientEmail": "files@my-project.iam.gserviceaccount.com",
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n...",
    "ProjectId": "my-project",
    "RootDirectoryPath": "tenants/acme"
  }
}
```

To bring your own client (custom credentials, retries, HTTP handling), pass a `StorageClient`; the provider never
disposes it:

```csharp
var client = await new StorageClientBuilder { /* ... */ }.BuildAsync();
var files = new WriteableGoogleCloudStorageFileProvider(client, new GoogleCloudStorageFileProviderOptions { BucketName = "my-bucket" });
```

## Configuration keys

| Key | Meaning |
|---|---|
| `BucketName` | The bucket. Required. |
| `RootDirectoryPath` / `RootDirectorySegments` | The folder inside the bucket the provider is confined to; empty for the whole bucket. |
| `ClientEmail` | A service account's email (`client_email` of its key). Set with `PrivateKey`. |
| `PrivateKey` | The service account's PEM private key (`private_key`). A secret. Escaped newlines (`\n`) are accepted. |
| `CredentialsJson` | The whole service account key file as one string, used when `ClientEmail` and `PrivateKey` are not both set. A secret. |
| `ProjectId` | The project. Needed by `CreateBucketIfMissing` when the key does not name it. |
| `Endpoint` | Since 2027.0.0. The root URL of another Cloud Storage endpoint, such as `http://localhost:4443` for [fake-gcs-server](https://github.com/fsouza/fake-gcs-server) or a Private Service Connect endpoint; empty for Google's. |
| `CreateBucketIfMissing` | Since 2027.0.0. `true` creates the bucket on the first call when it does not exist; `false` (the default) makes every member throw `InvalidOperationException` naming the bucket. |

With a `StorageClient` given to the constructor, only `BucketName`, the root, `CreateBucketIfMissing` and `ProjectId`
are used: the credentials and the endpoint are the client's.

## Authentication

The first credential that is set wins: `ClientEmail` with `PrivateKey`, then `CredentialsJson`, then Application
Default Credentials. The provider's constructor does no network I/O: it reads a key, but the client is built, and
Application Default Credentials are looked up, on the first call. A key that cannot be read, a missing bucket name or an
`Endpoint` that is not a root URL throws `ArgumentException` from the constructor.

The credentials go to the `Endpoint` as they go to Google, so use `https` except for a local emulator. Against
fake-gcs-server, any service account key works: the client signs its own tokens and never asks Google for one.

## What it does

- **Folders** are prefixes: a folder exists while an object is under it, and lists as missing once the last one goes.
  An object named like a folder (`dir/`, as the console makes) is an empty folder. Objects whose names no path can
  address (such as `a//b` or `x/../y`) are left out of listings.
- **Versions:** a file's `ETag` is its generation. `UpdateFileOptions.IfMatch` and `IfNotExists` are generation
  preconditions Cloud Storage checks atomically.
- **Writes** are resumable uploads: the object appears only when every byte arrived, so a write that fails leaves the
  old content. The caller's stream is read from its position and left open. With no content type given, the object is
  stored as `application/octet-stream`.
- **Appends** rewrite the object (Cloud Storage has no append), conditional on the generation read, so an append never
  loses a concurrent write: it throws `PreconditionFailedException` instead.
- **Reads** stream the object as it downloads. `ReadRangeAsync` and `OpenReadSeekableAsync` download only the bytes
  asked for, pinned to the generation they started from. The download is not checked against the object's hash; TLS
  protects it in transit.
- **Move and copy** copy on the server, only if the destination does not exist (`FileExistsException` otherwise). A move
  then deletes exactly the generation it copied; if the source was written in between, the newer content stays, the
  copy is removed, and the move throws `PreconditionFailedException`.
- **Missing bucket:** every member throws `InvalidOperationException` naming it, unless `CreateBucketIfMissing` is set.

## Signed URLs

`GetSignedUrlAsync` issues V4 signed URLs, which need a credential that can sign: a service account key, or an identity
that holds `iam.serviceAccounts.signBlob` on its service account (Workload Identity, Compute Engine, Cloud Run). With a
credential that cannot sign (a user, an access token), it throws `NotSupportedException`, and the
WriteableFileProviders.AspNetCore gateway can take over.

| Permission | URL | The request must send |
|---|---|---|
| `Read` | GET; `DownloadFileName` and `ContentType` become `response-content-disposition` and `response-content-type` | nothing |
| `Create` | PUT, only if the file does not exist | `x-goog-if-generation-match: 0`, and `Content-Type` when one was given |
| `Write` | PUT, creating or replacing | `Content-Type` when one was given |

`SignedUrl.Headers` lists the headers to send; they are signed. A URL lives the validity asked, rounded down to whole
seconds: at least one second, at most seven days. A longer or shorter one throws `NotSupportedException`, and a validity
that is not positive throws `ArgumentOutOfRangeException`. `ExpiresOn` is read from the URL itself. A read URL for a
missing file throws `FileNotFoundException`; a download name on an upload URL throws `NotSupportedException`.

## Limits

- No change notifications: `Watch` throws `NotSupportedException`.
- No native move: a move is a copy and a delete, so a large object takes as long as its copy.
- An append rewrites the whole object: its cost grows with the file.
- Content type is the only metadata kept.
