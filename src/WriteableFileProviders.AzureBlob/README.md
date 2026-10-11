# WriteableFileProviders.AzureBlob

An Azure Blob Storage container as an `IWriteableFileProvider`, on `Azure.Storage.Blobs` 12. Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme).

## Install

```
dotnet add package WriteableFileProviders.AzureBlob
```

Needs .NET 10, and is native AOT compatible. Keep every WriteableFileProviders package in an app on the same
major version.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AzureBlob;

await using var files = new WriteableAzureBlobFileProvider(new AzureBlobFileProviderOptions
{
    AccountName = "contoso",
    AccountKey = configuration["Storage:Key"],
    ContainerName = "files",
    RootDirectoryPath = "tenants/acme",
});

await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

From configuration (`services.AddWriteableFileProvider(section)`):

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "contoso", "AccountKey": "...", "ContainerName": "files" } }
```

Or pass a `BlobContainerClient` you build (managed identity, retries, Aspire). The provider never disposes it, and reads
only `RootDirectoryPath` and `CreateContainerIfMissing` from the options:

```csharp
var container = new BlobContainerClient(new Uri("https://contoso.blob.core.windows.net/files"), new DefaultAzureCredential());
await using var files = new WriteableAzureBlobFileProvider(new AzureBlobFileProviderOptions(), container);
```

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `AccountName` | | The storage account. Required, except with `UseDefaultAzureCredential` and a `Url`. |
| `AccountKey` | | The account's shared key (base64). Required unless `UseDefaultAzureCredential`, and refused with it. A secret. |
| `ContainerName` | required | The container. |
| `Protocol` | `https` | `http` or `https`. |
| `Url` | `{AccountName}.blob.core.windows.net` | The endpoint's host, port and path, such as `127.0.0.1:10000/devstoreaccount1` for Azurite. A full `https://…` URL is taken as it is. |
| `RootDirectoryPath` | the container | The folder in the container the provider is confined to. |
| `CreateContainerIfMissing` | `false` | Let the first write create a missing container. Without it, a missing container throws `InvalidOperationException`. |
| `UseDefaultAzureCredential` | `false` | Sign in with Microsoft Entra ID through `DefaultAzureCredential` instead of the key. Needs `https`. |

## Authentication

- **Shared key** (`AccountName`, `AccountKey`). Signed URLs are service SAS.
- **Microsoft Entra ID** (`UseDefaultAzureCredential`, or your client with a `TokenCredential`). Give the identity a
  data role on the container, such as Storage Blob Data Contributor. Signed URLs are user delegation SAS, which also
  need `generateUserDelegationKey` at the account scope (Storage Blob Delegator grants it).
- **A SAS URL or anonymous client.** Everything the SAS allows works; signed URLs throw `NotSupportedException`.

## What's different

- **Files are block blobs.** A write commits its blocks in one request, so readers never see half a file, and
  `IfMatch` and `IfNotExists` are atomic.
- **Two writers at once:** if another commit lands while yours stages, a write up to 8 MiB is staged again (5 attempts
  in all). A larger one throws `IOException` and stores nothing; a conditional one, `PreconditionFailedException`.
- **Appends** add blocks only to an unchanged file, retrying up to 5 times, then throw `PreconditionFailedException`.
- **Append blobs written by 2026** stay readable. The first write turns one into a block blob through a temp blob in
  `.wfp-tmp/` (hidden from listings); after that, a 2026 provider can't append to it. Upgrade all writers together.
- **Moves and copies** run on the server. A move deletes the source only if it is unchanged; otherwise it removes the
  copy. A copy still pending after 10 minutes is aborted with `IOException`.
- **Snapshots are kept.** Deleting or moving a blob that has snapshots throws `IOException` and leaves it as it was.
- **Signed URLs** are SAS for read, create and write, up to 7 days. A 7-day user delegation SAS needs
  `AllowShorterValidity`, as its key lives just under 7 days. Upload URLs can't carry a content type; the gateway's can.
- **Limits:** a write from a stream that can't seek is at most 50,000 blocks of 8 MiB (about 390 GiB). Page blobs are
  read-only. `Watch` throws `NotSupportedException`.

## Learn more

- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#azure-blob-storage),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#azure-blob-storage),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#azure-blob-storage).
