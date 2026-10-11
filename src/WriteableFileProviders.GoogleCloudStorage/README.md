# WriteableFileProviders.GoogleCloudStorage

A writeable `IFileProvider` over a Google Cloud Storage bucket, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the local disk, Azure Blob Storage, Amazon S3, MinIO, Box and SharePoint providers.
Built on `Google.Cloud.Storage.V1` 5.

## Install

```
dotnet add package WriteableFileProviders.GoogleCloudStorage
```

It needs .NET 10 and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in an application
must be on the same major version: a `2026.10.10.x` package does not load against `WriteableFileProviders.Common` 2027.

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

From configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or
`services.AddWriteableFileProvider(section)`:

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

With a client you build yourself (custom credentials, retries, HTTP handling, a customer-supplied encryption key),
which the provider never disposes:

```csharp
var client = await new StorageClientBuilder { /* ... */ }.BuildAsync();
var files = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = "my-bucket" }, client);
```

With a client, only `BucketName`, the root, `CreateBucketIfMissing` and `ProjectId` are read from the options: the
credentials and the endpoint are the client's. The provider class is sealed: to change what it does, wrap it, or
derive your own provider from `WriteableFileProvider<TOptions>`.

## Configuration

| Key | Meaning |
|---|---|
| `BucketName` | The bucket. Required. |
| `RootDirectoryPath` / `RootDirectorySegments` | The folder inside the bucket the provider is confined to; empty for the whole bucket. |
| `ClientEmail` | A service account's email (`client_email` of its key). Set with `PrivateKey`; either one alone is ignored. |
| `PrivateKey` | The service account's PEM private key (`private_key`). A secret. Escaped newlines (`\n`) are accepted. |
| `CredentialsJson` | The whole service account key file as one string, used when `ClientEmail` and `PrivateKey` are not both set. Another kind of credential file throws `ArgumentException`. A secret. |
| `ProjectId` | The project. `CreateBucketIfMissing` needs it unless a service account key names its project: the key in `CredentialsJson`, or the key file Application Default Credentials point to. Application Default Credentials on GKE, Compute Engine or Cloud Run, `ClientEmail` with `PrivateKey`, and a client of yours always need it. |
| `Endpoint` | New in 2027. The root URL of another Cloud Storage endpoint, such as `http://localhost:4443` for [fake-gcs-server](https://github.com/fsouza/fake-gcs-server) or a Private Service Connect endpoint; empty for Google's. Plain `http` only toward a loopback host, unless `AllowInsecureHttp` is set. |
| `AllowInsecureHttp` | New in 2027. `true` allows an `http` `Endpoint` toward a host that is not loopback, such as an emulator elsewhere on a private network; the credentials' tokens then travel in clear text. |
| `CreateBucketIfMissing` | New in 2027. `true` creates the bucket on the first call when it does not exist, with Cloud Storage's defaults: the `US` multi-region, the Standard storage class and the project's default access settings (create it yourself for anything else). `false` (the default) makes every member that reaches the bucket throw `InvalidOperationException` naming it. |

The keys new in 2027 are written by `ToJson` only when they are set, so a configuration that uses none of them keeps
its 2026 shape. The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

The first credential that is set wins: `ClientEmail` with `PrivateKey`, then `CredentialsJson`, then Application
Default Credentials. The provider's constructor does no network I/O: it reads a key, but the client is built, and
Application Default Credentials are looked up, on the first call, which throws `InvalidOperationException` where there
are none; a failure there is tried again by the next call. A key that cannot be read, a missing bucket name, an
`Endpoint` that is not a root URL, or a plain-`http` `Endpoint` toward a host that is not loopback without
`AllowInsecureHttp` throws `ArgumentException` from the constructor.

The credentials go to the `Endpoint` as they go to Google, so use `https` except for a local emulator. Against
fake-gcs-server, any service account key works: the client signs its own tokens and never asks Google for one.

**Permissions:** the provider needs `storage.objects.list` besides reading and writing objects (the
`roles/storage.objectViewer` and `roles/storage.objectAdmin` roles have it), because it lists objects to tell a folder,
or a missing bucket, from a missing file. A credential without it gets `UnauthorizedAccessException` (Cloud Storage's
403, with the `GoogleApiException` inside) from every member that has to: `GetFileInfoAsync` of a path that is not a
file, listings, deletes of a folder or a missing path, copies and moves, appends to a new file, and seekable reads,
local streams and read URLs of a missing file. Only `ReadFileAsync` and `ReadRangeAsync` of a missing file still answer
`FileNotFoundException`.

## How it behaves

The rules every provider shares (prefix folders, a file and a folder sharing a name, the names a listing leaves out, a
missing bucket, the exceptions) are in the guide:
[paths](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md), [reading and
writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md), and
[errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#google-cloud-storage) with
this provider's own mapping. What follows is this provider's own.

- **Folders:** an object named like a folder (`dir/`, as the console makes) is an empty folder.
- **Versions:** a file's `ETag` is its generation. `UpdateFileOptions.IfMatch` and `IfNotExists` are generation
  preconditions Cloud Storage checks atomically.
- **Writes** are resumable uploads: the object appears only when every byte arrived, so a write that fails leaves the
  old content. The caller's stream is read from its position and left open. With no content type given, a write stores
  `application/octet-stream`; an append and a local stream's commit keep the type the file has.
- **Appends** rewrite the object (Cloud Storage has no append), conditional on the generation read, so an append never
  loses a concurrent write: it throws `PreconditionFailedException` instead.
- **Reads** stream the object as it downloads, with the client's customer-supplied encryption key if it has one.
  `ReadRangeAsync` downloads only the bytes asked for, pinned to a generation when `ReadOptions.IfMatch` names one;
  `OpenReadSeekableAsync` fetches ranges as it is read, pinned to the generation it opened unless `AllowModifications`
  is set. The download is not checked against the object's hash; TLS protects it in transit.
- **Move and copy** copy on the server, only if the destination does not exist (`FileExistsException` otherwise). A
  move then deletes exactly the generation it copied; if the source was written in between, the newer content stays, the
  copy is removed, and the move throws `PreconditionFailedException`. The copy is never retried, because a retry after a
  lost answer would find its own copy and report the destination as taken. So when a copy or a move fails with a server
  error or a timeout, the copy may still have landed: check the destination before you try again (after a move, the
  source is still there).

## Signed URLs

V4 signed URLs for reading, creating and writing, from one second to seven days, which need a credential that can
sign: a service account key, or an identity that holds `iam.serviceAccounts.signBlob` on its service account. With one
that cannot, or whose signing IAM refuses or cannot be reached, the gateway takes over. An upload must send the headers in `SignedUrl.Headers`. The details are in
[Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#google-cloud-storage).

## Limits

- No change notifications: `Watch` throws `NotSupportedException`.
- No native move: a move is a copy and a delete, so a large object takes as long as its copy.
- An append rewrites the whole object: its cost grows with the file.
- Content type is the only metadata the provider writes or reports.

## Learn more

- [Providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md): what
  every store can do, side by side.
- The guide's [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [paths and listings](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md)
  and [dependency injection](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#google-cloud-storage):
  what 2027.0.0 changed for this provider.
