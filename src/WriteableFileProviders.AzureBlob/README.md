# WriteableFileProviders.AzureBlob

A writeable `IFileProvider` over an Azure Blob Storage container, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the local disk, Amazon S3, MinIO, Google Cloud Storage, Box and SharePoint providers.
Built on `Azure.Storage.Blobs` 12, on block blobs.

## Install

```
dotnet add package WriteableFileProviders.AzureBlob
```

It needs .NET 10 and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in an application
must be on the same major version: a `2026.10.10.x` package does not load against `WriteableFileProviders.Common` 2027.

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
await files.UpdateFileAsync("logs/today.log", line, UpdateFileOptions.Append);
await using var slice = await files.ReadRangeAsync("reports/2026.csv", 0, 4096);
var url = await files.GetSignedUrlAsync("reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

From configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or dependency injection:

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "contoso", "AccountKey": "...", "ContainerName": "files", "RootDirectoryPath": "tenants/acme" } }
```

```csharp
services.AddWriteableFileProvider(configuration.GetSection("Files"));
```

With a client you build yourself (a managed identity, custom retries or transport, Aspire), which the provider never
disposes:

```csharp
var container = new BlobContainerClient(new Uri("https://contoso.blob.core.windows.net/files"), new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
await using var files = new WriteableAzureBlobFileProvider(new AzureBlobFileProviderOptions { RootDirectoryPath = "tenants/acme" }, container);
```

The options come first, as on every provider's client constructor. With a client, only the root and
`CreateContainerIfMissing` are read from them (pass `new()` for the container's root; null throws
`ArgumentNullException`); a `ContainerName` that differs from the client's throws `ArgumentException`. The provider class is sealed: to change what it does, wrap it,
or derive your own provider from `WriteableFileProvider<TOptions>`.

## Configuration

| Key | Meaning |
|---|---|
| `AccountName` | The storage account. Required with a shared key; with `UseDefaultAzureCredential`, required unless `Url` names the endpoint. |
| `AccountKey` | The account's shared key (base64). Required unless `UseDefaultAzureCredential`; refused together with it. A secret. |
| `ContainerName` | The container. Required. |
| `Protocol` | `http` or `https` (any case); empty means `https`. |
| `Url` | The endpoint's host, port and path, such as `127.0.0.1:10000/devstoreaccount1` for Azurite; a full `https://...` URL is taken as it is, and must then use the scheme `Protocol` names, if any. Empty means `{AccountName}.blob.core.windows.net`. |
| `RootDirectoryPath` / `RootDirectorySegments` | The folder inside the container the provider is confined to. Empty is the container's root. |
| `CreateContainerIfMissing` | `true` lets the first write create a missing container. Default `false`: a missing container makes every member throw `InvalidOperationException`. Reads never create it. New in 2027. |
| `UseDefaultAzureCredential` | `true` signs in with Microsoft Entra ID through `DefaultAzureCredential` instead of the shared key. Needs `https`. New in 2027. |

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

## How it behaves

- **Block blobs.** An overwrite stages its content as blocks while it reads your stream, and commits them in one
  request: a write that fails leaves the old content, and readers never see half of it. `IfMatch` and `IfNotExists` are
  checked by that commit, atomically. The commit sets the content type you give, or `application/octet-stream`; the
  blob's other headers, its metadata and a tier set on it are not carried over.
- **Two writers at once.** Azure keeps only the blocks a commit lists, so when another writer commits the same file
  while your overwrite is being staged, your blocks are gone. An unconditional overwrite whose content fits in one block
  (8 MiB) is staged again, up to five attempts in all, and the last writer wins; a longer one throws `IOException` and
  stores nothing. An overwrite with `IfMatch` or `IfNotExists` throws `PreconditionFailedException` at once. Write it
  again, or serialize the writers.
- **Appends** copy the new content to a local temp file first, then commit the file's block list with the new blocks
  after it, on the condition that the file has not changed since it was read. After a concurrent change the append
  reads the file again, up to five attempts in all, then throws `PreconditionFailedException`: it never loses a
  concurrent write. A blob without this provider's block list (written in one request, or in blocks by another tool)
  is first copied into blocks on the server (no download), or downloaded and uploaded again when the provider cannot
  sign a SAS for the copy or Azure refuses it.
- **Content type:** stored when you give one, `application/octet-stream` otherwise, and reported by `GetFileInfoAsync`
  and listings. An append keeps the stored type unless it gives one, and keeps the encoding, cache control, language,
  disposition, metadata and a tier set on the blob.
- **Folders** are name prefixes: a folder exists while some blob is under it (or a `dir/` marker blob another tool
  made). Names are case-sensitive. So a file and a folder may share a name: a blob written at a folder's path, or under
  a blob's path, is stored, reads back by path, and a listing shows both. `GetFileInfoAsync` of such a name describes
  the file. Deleting it removes the file first, and a second delete the folder; a writeable local stream of a folder's
  path still throws `IOException`.
- **Listings** leave out blob names no path can address (an empty or whitespace segment, one of only dots and spaces
  such as `..`, a control character, a backslash, or a percent-encoded form of one of these) and the provider's own
  temporary folder, `.wfp-tmp` at the provider root. Every other name, one of yours that starts with `.wfp-` included,
  is listed. A recursive listing is one flat listing of the prefix.
- **Reads:** `ReadRangeAsync` asks Azure for the range, checking `ReadOptions.IfMatch` atomically.
  `OpenReadSeekableAsync` is the SDK's own seekable stream, pinned to the version it opened unless modifications are
  allowed: a change throws `PreconditionFailedException`, a delete `FileNotFoundException`.
- **Moves and copies** run on the server and refuse an existing destination atomically (`FileExistsException`). A move
  deletes the source only if it has not changed since it was copied; otherwise it removes the copy and throws
  `PreconditionFailedException`. A copy still pending after 10 minutes is aborted, and throws `IOException`.
- **Snapshots are never deleted.** Deleting or moving a blob that has snapshots throws `IOException` and leaves it as
  it was; delete the snapshots first. A folder delete removes its blobs page by page, so it can stop there with some
  already gone. The one exception is the conversion of a 2026 append blob, below.
- **A missing container** makes every member that reaches it throw `InvalidOperationException`, naming it and
  `CreateContainerIfMissing`.
- **Errors:** Azure's 401 and 403 and a refused sign-in throw `UnauthorizedAccessException`, and any other failure of
  Azure or the network, after the SDK's retries, `IOException`
  ([Errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#azure-blob-storage)).

### Append blobs written by 2026.10.10.x

They stay readable. Azure cannot turn an append blob into a block blob in place, so the first write or append to one
goes through a temporary blob under `.wfp-tmp/` in the provider root: the new content is committed there, the append
blob is deleted (with its snapshots) if it has not changed, the temporary blob is copied to its name, and then removed.
Meanwhile the file is missing, or empty while the server copy runs. If the copy fails, the exception names the temporary
blob, which holds the content and which the provider can read at that path. Listings hide the `.wfp-tmp` folder at
the provider root. From then on the file is a block blob, and a 2026.10.10.x provider can no longer append to it
(`InvalidBlobType`): upgrade every writer of a shared file together. A copy of an append blob is an append blob too,
converted on its first write.

## Signed URLs

SAS URLs for reading, creating and writing, for up to 7 days: a service SAS with a shared key, a user delegation SAS
with Microsoft Entra ID. Read URLs carry a download file name and a content type; upload URLs take neither, so such a
request goes to the gateway. The details, the start time and a user delegation key's lifetime among them, are in
[Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#azure-blob-storage).

## Limits

- One write from a stream that cannot seek is at most 50,000 blocks of 8 MiB (about 390 GiB); a stream that can seek
  sizes its blocks to fit, up to 256 MiB each. An overwrite holds up to five blocks in memory while it stages them.
- An append to a file near Azure's 50,000-block limit first copies its content into fewer, larger blocks, on the
  server where the provider can sign a SAS for it, else through the client.
- An upload URL is a single `PUT`, up to Azure's 5,000 MiB limit for one request.
- Page blobs can be read, not written (`NotSupportedException`).
- Change notifications (`Watch`) are not supported.

## Learn more

- [Providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md): what
  every store can do, side by side.
- The guide's [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [paths and listings](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md)
  and [dependency injection](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#azure-blob-storage):
  what 2027.0.0 changed for this provider.
