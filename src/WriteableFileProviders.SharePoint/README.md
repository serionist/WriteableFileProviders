# WriteableFileProviders.SharePoint

A writeable `IFileProvider` over a SharePoint document library, through Microsoft Graph, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the local disk, Azure Blob Storage, Amazon S3, MinIO, Google Cloud Storage and Box
providers. Built on `Microsoft.Graph` 6 and `Azure.Identity`.

## Install

```
dotnet add package WriteableFileProviders.SharePoint
```

It needs .NET 10 and brings `WriteableFileProviders.Common`, Microsoft.Graph 6 and Azure.Identity. Every
WriteableFileProviders package in an application must be on the same major version: a `2026.10.10.x` package does not
load against `WriteableFileProviders.Common` 2027.

## Use

```json
{
  "Files": {
    "Type": "WriteableSharePointFileProvider",
    "SiteUrl": "https://contoso.sharepoint.com/sites/Finance",
    "DocumentLibraryName": "Documents",
    "TenantId": "<tenant id>",
    "ClientId": "<app client id>",
    "ClientSecret": "<from a secret store, never from source control>",
    "WorkFolderPath": "Apps/Reports",
    "RootDirectoryPath": "tenants/acme"
  }
}
```

```csharp
using WriteableFileProviders;

services.AddWriteableFileProvider("files", configuration.GetSection("Files"));
// or: await using var files = WriteableFileProviderSerializer.FromConfiguration(configuration.GetSection("Files"));
```

To bring your own Graph client (custom retries or handlers, a national cloud, tests), pass it in. The provider then
reads only the site, the library, the work folder and the root from the options, and never disposes the client:

```csharp
var graph = new GraphServiceClient(new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned), ["https://graph.microsoft.com/.default"]);
await using var files = new WriteableSharePointFileProvider(
    new SharePointFileProviderOptions { SiteUrl = "https://contoso.sharepoint.com/sites/Finance", DocumentLibraryName = "Documents" }, graph);
```

Downloads, upload fragments and copy monitoring go through that client's request adapter to SharePoint hosts that are
not Graph's. The SDK's Azure Identity authentication sends tokens to Graph's hosts only; a custom authentication
provider must do the same. The provider class is sealed: to change what it does, wrap it, or derive your own provider
from `WriteableFileProvider<TOptions>`.

## Configuration

| Key | Required | Meaning |
|---|---|---|
| `SiteUrl` | yes | The site's absolute URL; only its host and path are used. |
| `DocumentLibraryName` | no | The library as Graph names it (`Documents` for the default library, whose URL says `Shared Documents`), ignoring case. Empty takes the site's first library. |
| `TenantId` | for a secret or a certificate | The Microsoft Entra tenant of the app. With `UseDefaultAzureCredential`, the tenant to sign in to, when set. |
| `ClientId` | for a secret or a certificate | The app's client id; with `UseDefaultAzureCredential`, a user-assigned managed identity's client id (empty: the system-assigned one). |
| `ClientSecret` | one way to sign in | The app's client secret. |
| `ClientCertificateBase64`, `ClientCertificatePassword` | one way to sign in | The app's certificate with its private key, as a base64 PKCS#12 (.pfx) file, and its password. New in 2027. |
| `UseDefaultAzureCredential` | one way to sign in | `true` to sign in with `DefaultAzureCredential`: managed identity, workload identity, environment variables, or a developer sign-in. New in 2027. |
| `WorkFolderPath` | no | A folder of the library that holds the root; created on the first write. Names as they are, never percent-escaped (`Team Files`, not `Team%20Files`, which is refused). |
| `RootDirectoryPath` (or `RootDirectorySegments`) | no | The provider's root, inside the work folder. Nothing outside it can be reached. Not percent-escaped either. |

The provider's root is `WorkFolderPath` followed by `RootDirectoryPath`. When the root starts with the work folder
(ignoring case), that one leading copy is dropped, so a 2026 configuration that set both to the same folder keeps its
files. A root that is only the start of the work folder (`WorkFolderPath` `Main/Work`, `RootDirectoryPath` `Main`) is
refused. Caller paths are never rewritten: `Main/Work/a.txt` is a folder `Main/Work` inside the root, as on every other
provider.

The three options new in 2027 are written to JSON only when set, so a configuration that uses none of them keeps its
2026 shape. The options hold secrets: never log them, nor the output of `ToJson`.

## Authentication

The provider signs in with the first of these that is configured:

1. `UseDefaultAzureCredential`;
2. a certificate (`ClientCertificateBase64`, `ClientCertificatePassword`);
3. a client secret (`ClientSecret`).

With a certificate or a secret the app signs in as itself (application permissions). Grant it the Microsoft Graph
application permission `Sites.ReadWrite.All`, or `Sites.Selected` with write access to the site. Graph lists
`Sites.ReadWrite.All` as the least privileged application permission for upload sessions, which files over 4 MiB use.

No request is sent when the provider is built: the site and the library are looked up on the first call, and kept once
found; a failed lookup is tried again by the next call. A configuration the provider cannot use (no sign-in, a
certificate it cannot read, a `SiteUrl` that is not absolute) throws `ArgumentException` when it is built. Dispose the
provider to release the HTTP client and certificate it created.

## How it behaves

- **Folders are real:** an empty folder exists, and writes, moves and copies create missing parents. A folder's
  `Length` is -1.
- **Names ignore case:** `a.txt` and `A.txt` are one file; moving one to the other renames it.
- **Path segments** may not contain `:` (Graph's path syntax), escaped or not. A `%` is sent escaped, so `a%41` names an
  item called `a%41`, not `aA`. Listings leave out items whose names the provider cannot address, such as one that
  decodes to `..`.
- **Writes** copy the stream, from its position, to a temp file first, so a source that fails sends nothing. Up to
  4 MiB a file goes in one request; above, through an upload session in 5 MiB fragments that the service commits with
  the last one. A fragment answered 500, 502, 503, 504 or 416, cut off by the network, or answered without progress is
  sent once more from where the session stands; a second failure there ends the upload, and the session is cancelled.
  Either way a failed write leaves the old content, and the caller's stream is left open. Writing onto a folder, or
  under a file, throws `IOException`.
- **Conditions** (`IfMatch`, `IfNotExists`) are checked before the write, which includes the time to copy the stream,
  and also sent to the service (`If-Match`, and the conflict behaviour `fail`). They are not promised to be atomic:
  Graph documents `If-Match` when an upload session starts, not at its commit, and not on a simple upload. An append
  rewrites the file with `If-Match` on the version it read.
- **Content type:** SharePoint names the type from the file name. `UpdateFileOptions.ContentType` is ignored, and
  `IWriteableFileInfo.ContentType` is the type SharePoint reports.
- **Reads** stream from the file's pre-authenticated download URL, with nothing buffered on disk. Ranges are read
  natively, and a range the service ignores is cut from the whole file. A read pinned to an ETag
  (`ReadOptions.IfMatch`, or the seekable stream unless `AllowModifications` is set) checks it before each fetch, which
  is not atomic.
- **Sizes:** a file's `Length` is always its real length. For a file whose size Graph does not report, reading its info
  or listing it costs one extra ranged request for its first byte (after a request for its download URL), whose answer
  gives the length; every other file costs nothing more.
- **Move** is one `PATCH`, after the checks every provider makes and the creation of the destination's folders; it
  refuses a destination that exists. **Copy** runs on the server and is polled until it completes, for about 10 minutes
  at most; then it throws `IOException` naming the copy's last status. The service may still finish that copy
  afterwards and create the destination.
- **Delete** sends items to the site's recycle bin.
- **Errors:** only a 404 means "not found". The SDK retries 429, 503 and 504 three times, honouring `Retry-After`;
  then the call throws `IOException`. 401 and 403 throw `UnauthorizedAccessException`, other failures `IOException`;
  the message names Graph's error, and the SDK's or the network's exception is inside where there is one. A missing
  site or library throws `InvalidOperationException` naming `SiteUrl` or `DocumentLibraryName` from every member that
  reaches the library.

## Signed URLs

`GetSignedUrlAsync` issues read URLs only (`SignedUrlKind.PreAuthenticated`): the file's download URL, which Graph says
lives up to an hour and may stop working within minutes. A URL never lives longer than asked, so a validity under an
hour throws `NotSupportedException` (the WriteableFileProviders.AspNetCore gateway can serve those), one from an hour
to 7 days needs `SignedUrlOptions.AllowShorterValidity`, and one over 7 days always throws. `ExpiresOn` is null, because
the service does not say. Anyone holding the URL can download the file until it ends, and removing someone's access may
not end a URL already issued. `DownloadFileName` and `ContentType` are not supported on these URLs.

## Limits

- Graph's own: a fragment under 60 MiB, a simple upload up to 250 MB, SharePoint's name and path length limits.
- `Watch` is not supported, and version history is not exposed.

## More

- [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): every package, the provider
  matrix, versioning.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#sharepoint):
  what 2027.0.0 changed for this provider.
