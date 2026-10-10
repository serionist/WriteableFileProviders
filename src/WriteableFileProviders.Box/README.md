# WriteableFileProviders.Box

A writeable `IFileProvider` over a [Box](https://www.box.com) folder, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): read, write, append, list, move, copy and
delete files in Box through `IWriteableFileProvider`, the same interface every WriteableFileProviders package implements.
It runs on the Box .NET SDK (`Box.V2.Core` 10, namespace `Box.Sdk.Gen`).

Every WriteableFileProviders package must be on the same version: a 2027 provider needs the 2027 Common.

## Install

```
dotnet add package WriteableFileProviders.Box
```

## Authentication

The provider signs in as a Box app's service account, with server authentication (JWT):

1. In the [Box developer console](https://app.box.com/developers/console), create a Custom App with "Server
   Authentication (with JWT)".
2. Under Configuration, generate a public/private key pair. Box downloads the app's JSON configuration, which holds every
   value below.
3. Have a Box admin authorize the app in the enterprise (Admin Console, Apps, Custom Apps Manager).
4. Share the folder the provider should work in with the app's service account (its address is on the app's General
   Settings page), and use that folder's id as `RootFolderId`. The service account's own files start at folder `0`.

Sign-in happens on the first call that needs Box: building the provider does no network I/O (it reads the private key,
so a wrong key or passphrase fails there). Box's access tokens last an hour; when Box answers 401, the SDK fetches a new
one and retries.

To sign in another way (OAuth, a developer token, as-user calls), to use a proxy or custom retries, build the Box client
yourself and pass it in; the provider never disposes it:

```csharp
using Box.Sdk.Gen;
using WriteableFileProviders.Box;

var client = new BoxClient(new BoxJwtAuth(JwtConfig.FromConfigFile("box-config.json"))).WithAsUserHeader("12345");
var files = new WriteableBoxFileProvider(new BoxFileProviderOptions { RootFolderId = "123456789" }, client);
```

## Configuration

`Type` is `WriteableBoxFileProvider`; the keys are the options' property names, read without regard to case.

| Key | Required | What |
|---|---|---|
| `ClientId` | yes | The app's client id (`boxAppSettings.clientID` in the app's JSON configuration). |
| `ClientSecret` | yes | The app's client secret (`boxAppSettings.clientSecret`). |
| `EnterpriseId` | yes | The enterprise the app signs in to as its service account (`enterpriseID`). |
| `PublicKeyId` | yes | The id of the app's public key (`boxAppSettings.appAuth.publicKeyID`). |
| `PrivateKey` | yes | The app's private key in PEM (`boxAppSettings.appAuth.privateKey`), encrypted or not. Escaped line breaks (`\n`) are turned into real ones, so the key can be pasted from the JSON configuration or an environment variable. |
| `Passphrase` | for an encrypted key | The private key's passphrase (`boxAppSettings.appAuth.passphrase`). |
| `RootFolderId` | no | The Box folder the provider works in; `0` (the service account's root) when empty. |
| `RootDirectoryPath` | no | A folder path under `RootFolderId` to confine the provider to, such as `tenants/acme`. `RootDirectorySegments` is the same as segments. |

When the provider is built with a ready Box client, only `RootFolderId` and the root folder are read.

`ClientSecret`, `PrivateKey` and `Passphrase` are secrets: keep them in a secret store, and never log the provider's
`ToJson()` output, which contains them.

## Example

```json
{
  "Files": {
    "Type": "WriteableBoxFileProvider",
    "ClientId": "abc123",
    "ClientSecret": "from a secret store",
    "EnterpriseId": "987654",
    "PublicKeyId": "k1x2y3",
    "PrivateKey": "-----BEGIN ENCRYPTED PRIVATE KEY-----\n...\n-----END ENCRYPTED PRIVATE KEY-----\n",
    "Passphrase": "from a secret store",
    "RootFolderId": "123456789",
    "RootDirectoryPath": "tenants/acme"
  }
}
```

```csharp
using WriteableFileProviders;

services.AddWriteableFileProvider("files", configuration.GetSection("Files"));

// [FromKeyedServices("files")] IWriteableFileProvider files
await files.UpdateFileAsync("reports/2026.csv", stream);              // parent folders are created
await using var slice = await files.ReadRangeAsync("big.bin", 1_000_000, 4096);
await files.MoveAsync("reports/2026.csv", "archive/2026.csv");         // one server-side call
```

## How it behaves

- **Folders are real:** an empty folder exists, and `GetFileInfoAsync` on a folder says it exists and is a directory.
- **Names ignore case:** a folder cannot hold `a.txt` and `A.txt`; moving `a.txt` to `A.txt` renames it. Writing
  `A.TXT` over `a.txt` keeps the stored name.
- **Paths are resolved on every call:** Box has no lookup by path, so each call lists the folders on its path from the
  root folder down (with marker paging, 1000 entries a page). Deep paths and very large folders cost one or more calls
  per level; nothing is cached between calls, so a change made elsewhere is seen at once.
- **Writes:** the caller's stream is copied to a local temp file from its current position, then uploaded: in one
  request below 20 MiB, through a chunked upload session at or above it. Box makes the new version only once the upload is
  complete, so a write that fails leaves the old content. An append rewrites the file on the version it read.
- **Content types:** Box keeps none, so `UpdateFileOptions.ContentType` is ignored and `ContentType` is null.
- **Conditions:** `IfNotExists` is atomic (Box refuses a name that is taken). `IfMatch` is compared with the file's
  `ETag` just before the write, which is not atomic: a write that lands in between is replaced. The same holds for the
  version an append rewrites.
- **Reads** stream the download; `ReadRangeAsync` and `OpenReadSeekableAsync` fetch byte ranges of the file version
  they were pinned to, and throw `PreconditionFailedException` once the file has changed.
- **Move and copy** are single server-side calls; they refuse an existing destination with `FileExistsException`.
- **Delete** moves the file or folder (recursively) to the Box trash, where Box keeps it until it is purged.
- **Watch** is not supported.

## Signed URLs

Read only: Box's pre-authenticated download URL (`dl.boxcloud.com`), which Box makes valid for about 15 minutes whatever
is asked. Since a URL must never outlive what was asked:

- a validity under 15 minutes throws `NotSupportedException` (with the ASP.NET Core gateway, its URL is used instead);
- a validity of 15 minutes or more throws `NotSupportedException` unless `AllowShorterValidity` is set; then the URL is
  returned with `ExpiresOn` 15 minutes after the call and `Kind` `PreAuthenticated`;
- `DownloadFileName` and `ContentType` cannot be set on Box's URL, and throw `NotSupportedException`;
- write and create URLs are not supported.

Anyone holding the URL can download that version of the file until it expires.

## Limits

- Box's own limits apply: the account's maximum file size (from 250 MB on free accounts to several GB), names of at most
  255 characters, no `/` or `\`, no trailing spaces, and the API's rate limits (the SDK retries 429 and 5xx answers).
- Box answers some calls with "not ready yet" (a download right after an upload, the commit of a large upload); the
  provider waits as Box asks and tries again, up to 60 times.
- Every operation on a path walks the path from the root folder, so latency grows with depth.
- The `PublicKeyId` is not sent in the JWT header by the Box SDK; Box accepts that while the app has one public key.

See [docs/migration-2027.md](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) to move
from 2026.
