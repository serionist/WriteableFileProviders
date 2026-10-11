# WriteableFileProviders.SharePoint

A SharePoint document library as an `IWriteableFileProvider`, through Microsoft Graph (`Microsoft.Graph` 6). Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme).

## Install

```
dotnet add package WriteableFileProviders.SharePoint
```

Needs .NET 10, and is native AOT compatible: in an ASP.NET Core app, Microsoft Graph adds one harmless warning
([Native AOT](https://github.com/serionist/WriteableFileProviders#native-aot)). Keep every WriteableFileProviders package in an app on the same major version.

## Use

```json
{
  "Files": {
    "Type": "WriteableSharePointFileProvider",
    "SiteUrl": "https://contoso.sharepoint.com/sites/Finance",
    "DocumentLibraryName": "Documents",
    "TenantId": "<tenant id>",
    "ClientId": "<app client id>",
    "ClientSecret": "<from a secret store>",
    "WorkFolderPath": "Apps/Reports"
  }
}
```

```csharp
services.AddWriteableFileProvider(configuration.GetSection("Files"));   // then inject IWriteableFileProvider
```

Or pass a `GraphServiceClient` you build (managed identity, national clouds, custom handlers). The provider never
disposes it, and reads only the site, the library, the work folder and the root from the options. Downloads and uploads
go through it to hosts other than Graph's, so its authentication must send tokens to Graph only, as Azure Identity does:

```csharp
var graph = new GraphServiceClient(new DefaultAzureCredential(), ["https://graph.microsoft.com/.default"]);
await using var files = new WriteableSharePointFileProvider(new SharePointFileProviderOptions { SiteUrl = siteUrl }, graph);
```

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `SiteUrl` | required | The site's absolute URL. |
| `DocumentLibraryName` | first library | The library as Graph names it: `Documents` for the default one (URL `Shared Documents`). |
| `TenantId`, `ClientId` | | The app's Microsoft Entra tenant and client id; required with a secret or a certificate. With `UseDefaultAzureCredential`, `ClientId` picks a user-assigned managed identity. |
| `ClientSecret` | | The app's client secret. A secret. |
| `ClientCertificateBase64`, `ClientCertificatePassword` | | The app's certificate with its private key, as a base64 .pfx, and its password. A secret. |
| `UseDefaultAzureCredential` | `false` | Sign in with `DefaultAzureCredential`: managed identity, workload identity, environment, developer sign-in. |
| `WorkFolderPath` | the library | A folder of the library that holds the root, created on the first write. Plain names (`Team Files`), never `%20`. |
| `RootDirectoryPath` | the work folder | The folder inside the work folder the provider is confined to. Plain names too. |

The root is `WorkFolderPath` then `RootDirectoryPath`. If the root starts with the work folder, that copy is dropped
(`Apps/Reports` twice is one `Apps/Reports`); a root that is only the start of it (`Apps`) is refused.

## Authentication

The first configured wins: `UseDefaultAzureCredential`, a certificate, a client secret. With a certificate or a secret
the app signs in as itself: grant it the Graph application permission `Sites.ReadWrite.All`, or `Sites.Selected` with
write access to the site. Building the provider sends nothing; the site and library are looked up on the first call.

## What's different

- **Real folders, case ignored.** Deletes go to the site's recycle bin.
- **Names** can't contain `:` (Graph's path syntax). A `%` is literal: `a%41` names `a%41`, not `aA`.
- **Writes** go through a local temp file, then up in one request up to 4 MiB, or an upload session in 5 MiB
  fragments. A fragment that hits a server or network error is sent once more. A failed write leaves the old content.
- **Conditions aren't atomic.** `IfMatch` and `IfNotExists` are checked before the write and also sent to Graph.
- **Content type** comes from the file name. `UpdateFileOptions.ContentType` is ignored.
- **Lengths are real.** A file whose size Graph doesn't report costs one extra ranged request to measure.
- **Copies** run on the server and are polled for about 10 minutes, then throw `IOException`; the copy may still
  finish later.
- **Signed URLs** are read only: Graph's download URL, which lives up to an hour. It's returned only with
  `AllowShorterValidity` and a validity of an hour or more, with `ExpiresOn` null. Every other URL comes from the
  gateway.
- **Limits:** Graph's own (fragments under 60 MiB, SharePoint's name and path lengths). `Watch` throws
  `NotSupportedException`.

## Learn more

- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#sharepoint),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#sharepoint),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#sharepoint).
