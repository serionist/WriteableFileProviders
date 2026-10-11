# WriteableFileProviders.GoogleCloudStorage

A Google Cloud Storage bucket as an `IWriteableFileProvider`, on `Google.Cloud.Storage.V1` 5. Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme).

## Install

```
dotnet add package WriteableFileProviders.GoogleCloudStorage
```

Needs .NET 10. Keep every WriteableFileProviders package in an app on the same major version.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.GoogleCloudStorage;

// No credentials in the options: Application Default Credentials (Workload Identity, the metadata server, gcloud).
await using var files = new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions
{
    BucketName = "my-bucket",
    RootDirectoryPath = "tenants/acme",
});

await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

From configuration (`services.AddWriteableFileProvider(section)`):

```json
{ "Files": { "Type": "WriteableGoogleCloudStorageFileProvider", "BucketName": "my-bucket", "ProjectId": "my-project" } }
```

Or pass a `StorageClient` you build (other credentials, retries, an encryption key). The provider never disposes it,
and reads only the bucket, the root, `CreateBucketIfMissing` and `ProjectId` from the options.

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `BucketName` | required | The bucket. |
| `ClientEmail`, `PrivateKey` | | A service account's `client_email` and PEM `private_key`. Set both. Escaped `\n` in the key is fine. A secret. |
| `CredentialsJson` | | A whole service account key file as one string. Other credential files throw `ArgumentException`. A secret. |
| `ProjectId` | | The project. Only `CreateBucketIfMissing` needs it, and not when `CredentialsJson` (or the key file Application Default Credentials use) names it. |
| `Endpoint` | Google's | Another Cloud Storage endpoint, such as `http://localhost:4443` for fake-gcs-server. Plain `http` only toward loopback. |
| `AllowInsecureHttp` | `false` | Allow an `http` `Endpoint` toward a host that isn't loopback. Tokens then travel in clear text. |
| `CreateBucketIfMissing` | `false` | Create a missing bucket on the first call, in the `US` multi-region with the Standard class. Without it, a missing bucket throws `InvalidOperationException`. |
| `RootDirectoryPath` | the bucket | The folder in the bucket the provider is confined to. |

## Authentication

The first credential set wins: `ClientEmail` with `PrivateKey`, then `CredentialsJson`, then Application Default
Credentials. The constructor does no network I/O: Application Default Credentials are looked up on the first call,
which throws `InvalidOperationException` when there are none.

The credential needs `storage.objects.list` as well as object read and write (`roles/storage.objectViewer` and
`roles/storage.objectAdmin` have it): the provider lists to tell a folder from a missing file. Without it, what has to
list throws `UnauthorizedAccessException`: listings, `GetFileInfoAsync` of a path that isn't a file, deletes of a
folder or a missing path, copies, moves, appends to a new file, and seekable reads, local streams and read URLs of a
missing file. `ReadFileAsync` and `ReadRangeAsync` of a missing file still throw `FileNotFoundException`.

## What's different

- **ETags are generations.** `IfMatch` and `IfNotExists` are generation preconditions, atomic. Appends rewrite the
  object on the generation read, so they never lose a concurrent write.
- **Writes are resumable uploads.** The object appears only when every byte has arrived.
- **A `dir/` object** (the console's "create folder") is an empty folder.
- **Reads** aren't checked against the object's hash.
- **Moves** copy on the server, then delete the exact generation copied; a source changed meanwhile undoes the move.
  A copy is never retried: after a server error or timeout it may have landed, so check the destination first.
- **Signed URLs** are V4 read, create and write URLs, from 1 second to 7 days. They need a service account key, or
  `iam.serviceAccounts.signBlob` on the identity's service account; otherwise the gateway issues them. Uploads must
  send `SignedUrl.Headers`.
- **Content type** is the only metadata written. `Watch` throws `NotSupportedException`.

## Learn more

- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#google-cloud-storage),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#google-cloud-storage),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#google-cloud-storage).
