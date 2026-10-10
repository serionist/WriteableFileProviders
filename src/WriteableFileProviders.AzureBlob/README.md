# WriteableFileProviders.AzureBlob

A writeable `IFileProvider` over an Azure Blob Storage container, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): the same `IWriteableFileProvider` API as
the local disk, Amazon S3, MinIO, Google Cloud Storage, Box and SharePoint, with one meaning on all of them. Every
WriteableFileProviders package must be on the same major version.

```
dotnet add package WriteableFileProviders.AzureBlob
```

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AzureBlob;

await using var files = new WriteableAzureBlobFileProvider(new AzureFileProviderOptions
{
    AccountName = "contoso",
    AccountKey = configuration["Storage:Key"],
    ContainerName = "files",
    RootDirectoryPath = "tenants/acme",
});

await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
await files.UpdateFileAsync("logs/today.log", line, UpdateFileMode.Append);
await using var slice = await files.ReadRangeAsync("reports/2026.csv", 0, 4096);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

From configuration, with dependency injection:

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "contoso", "AccountKey": "...", "ContainerName": "files", "RootDirectoryPath": "tenants/acme" } }
```

```csharp
services.AddWriteableFileProvider("files", configuration.GetSection("Files"));
```

With a client you build yourself (a managed identity, custom retries or transport, Aspire), which the provider never
disposes:

```csharp
var container = new BlobContainerClient(new Uri("https://contoso.blob.core.windows.net/files"), new ManagedIdentityCredential());
await using var files = new WriteableAzureBlobFileProvider(container, new AzureFileProviderOptions { RootDirectoryPath = "tenants/acme" });
```

## Configuration

| Key | Meaning |
|---|---|
| `AccountName` | The storage account. Required with a shared key; with `UseDefaultAzureCredential`, required unless `Url` names the endpoint. |
| `AccountKey` | The account's shared key (base64). Required unless `UseDefaultAzureCredential`; refused together with it. A secret. |
| `ContainerName` | The container. Required. |
| `Protocol` | `http` or `https` (any case); empty means `https`. |
| `Url` | The endpoint's host, port and path, such as `127.0.0.1:10000/devstoreaccount1` for Azurite; a full `https://...` URL is taken as it is. Empty means `{AccountName}.blob.core.windows.net`. |
| `RootDirectoryPath` / `RootDirectorySegments` | The folder inside the container the provider is confined to. Empty is the container's root. |
| `CreateContainerIfMissing` | `true` lets the first write create a missing container. Default `false`: a missing container makes every member throw `InvalidOperationException`. Reads never create it. |
| `UseDefaultAzureCredential` | `true` signs in with Microsoft Entra ID through `DefaultAzureCredential` instead of the shared key. Needs `https`. |

The two `bool` options are written to JSON only when they are `true`, so a configuration that uses neither is stored
exactly as 2026.10.10.x stored it. `ToJson` output contains the account key: never log it.

## Authentication

- **Shared key** (`AccountName` and `AccountKey`): signed URLs are service SAS.
- **Microsoft Entra ID** (`UseDefaultAzureCredential`, or the client constructor with any `TokenCredential`): the identity
  needs a data role on the container, such as Storage Blob Data Contributor. Signed URLs are user delegation SAS, which
  need the `generateUserDelegationKey` action at the storage account's scope or above (the Storage Blob Data roles have
  it when assigned there; Storage Blob Delegator adds it to a role scoped to the container).
- **A client from a SAS URL, or anonymous:** everything the SAS allows works, but the provider signs no URLs
  (`NotSupportedException`, so the WriteableFileProviders.AspNetCore gateway can issue them).

## How it stores files

- **Block blobs.** A write stages its content as blocks while it reads your stream, and commits them in one request: a
  write that fails leaves the old content, and readers never see half of it. `IfMatch` and `IfNotExists` are checked by
  that commit, atomically.
- **Appends** commit the file's block list with the new blocks after it, on the condition that the file has not
  changed since it was read. After a concurrent change the append reads the file again and retries, up to five times,
  then throws `PreconditionFailedException`: it never loses a concurrent write. A blob written in one request by
  another tool has no block list; its first append copies its content into blocks on the server (no download), or
  downloads and re-uploads it when the provider cannot sign a SAS for the copy.
- **Content type:** stored when you give one, `application/octet-stream` otherwise; an append keeps the stored type,
  cache control, language, disposition and metadata, and a tier set on the blob.
- **Folders** are name prefixes: a folder exists while some blob is under it. Names are case-sensitive.
- **Reads:** `ReadRangeAsync` asks Azure for the range; `OpenReadSeekableAsync` is the SDK's own seekable stream, pinned
  to the version it opened unless modifications are allowed.
- **Moves and copies** run on the server and refuse an existing destination atomically. A move deletes the source only
  if it has not changed since it was copied; otherwise it removes the copy and throws `PreconditionFailedException`.
  Deleting or moving a blob deletes its snapshots too.

### Append blobs written by 2026.10.10.x

They stay readable. Azure cannot turn an append blob into a block blob in place, so the first write or append to one
goes through a temporary blob under `.wfp-tmp/` in the provider root: the new content is committed there, the append
blob is deleted (with its snapshots) if it has not changed, the temporary blob is copied to its name, and then removed.
The file is missing for a moment between the delete and the copy. If the copy fails, the exception names the temporary
blob, which holds the content and which the provider can read at that path. Listings hide `.wfp-` entries. From then
on the file is a block blob, and a 2026.10.10.x provider can no longer append to it (`InvalidBlobType`): upgrade every
writer of a shared file together. A copy of an append blob is an append blob too, converted on its first write.

## Signed URLs

`GetSignedUrlAsync` signs a SAS natively for `Read` (GET), `Create` (PUT only if the blob does not exist: SAS permission
`c`) and `Write` (PUT, creating or replacing), for up to 7 days; a longer validity throws `NotSupportedException`.

- The SAS starts 15 minutes in the past, so a clock that runs behind does not reject it (and a SAS expiration policy,
  which requires a start time, accepts it). It ends exactly at the requested validity: `ExpiresOn` says when.
- A user delegation SAS never outlives its key. If the key would expire first, the call throws `NotSupportedException`
  unless `AllowShorterValidity` is set, and then `ExpiresOn` is the key's expiry.
- A read URL carries `DownloadFileName` (as an RFC 6266 `Content-Disposition`) and `ContentType`. An upload URL takes
  neither, because a SAS cannot make the uploader send a content type: such a request throws `NotSupportedException`,
  and the gateway takes over.
- An upload is a single `PUT` that sends `x-ms-blob-type: BlockBlob` (in `SignedUrl.Headers`), up to Azure's 5,000 MiB
  limit for one request. It cannot replace a 2026.10.10.x append blob: write that file once through the provider first.
- A read URL for a missing file throws `FileNotFoundException`. An upload URL into a missing container throws
  `InvalidOperationException`, or creates the container with `CreateContainerIfMissing`.

## Limits

- One write from a stream that cannot seek is at most 50,000 blocks of 8 MiB (about 390 GiB); a stream that can seek
  sizes its blocks to fit, up to 256 MiB each.
- An append to a file near Azure's 50,000-block limit first copies its content into fewer, larger blocks on the server.
- Blob names the provider cannot address as paths (empty, `.` or `..` segments, control characters, backslashes) are
  left out of listings.
- Page blobs can be read, not written (`NotSupportedException`).
- Change notifications (`Watch`) are not supported.

See the [migration guide](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) to move
from 2026.10.10.x.
