# WriteableFileProviders.Box

A writeable `IFileProvider` over a [Box](https://www.box.com) folder, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the local disk, Azure Blob Storage, Amazon S3, MinIO, Google Cloud Storage and
SharePoint providers. Built on the Box .NET SDK (`Box.V2.Core` 10, namespace `Box.Sdk.Gen`).

## Install

```
dotnet add package WriteableFileProviders.Box
```

It needs .NET 10 and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in an application
must be on the same major version: a `2026.10.10.x` package does not load against `WriteableFileProviders.Common` 2027.

## Use

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
await files.MoveAsync("reports/2026.csv", "archive/2026.csv");         // on the server
```

To sign in another way (OAuth, a developer token, as-user calls), to use a proxy or custom retries, build the Box client
yourself and pass it in; the provider never disposes it:

```csharp
using Box.Sdk.Gen;
using WriteableFileProviders.Box;

var client = new BoxClient(new BoxJwtAuth(JwtConfig.FromConfigFile("box-config.json"))).WithAsUserHeader("12345");
var files = new WriteableBoxFileProvider(new BoxFileProviderOptions { RootFolderId = "123456789" }, client);
```

With a client, only `RootFolderId` and the root folder are read from the options. A client that is exactly a
`BoxClient` is used through a copy that shares its authentication and session settings (base URLs, extra and as-user
headers, retries, proxy, timeouts), so its errors carry no credentials (see Errors). Any other `IBoxClient`, a class
derived from `BoxClient` included, is used as it is: its errors are its own, and may carry its credentials. The
provider class is sealed: to change what it does, wrap it, or derive your own provider from
`WriteableFileProvider<TOptions>`.

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
| `RootFolderId` | no | The Box folder the provider works in; `0` (the service account's root) when empty. A folder that does not exist, or that the app cannot see, makes every member that reaches Box throw `InvalidOperationException`. |
| `RootDirectoryPath` | no | A folder path under `RootFolderId` to confine the provider to, such as `tenants/acme`. `RootDirectorySegments` is the same as segments. |

A missing credential throws `ArgumentException` naming it when the provider is built. `ClientSecret`, `PrivateKey` and
`Passphrase` are secrets: keep them in a secret store, and never log the provider's `ToJson()` output, which contains
them.

## Authentication

The provider signs in as a Box app's service account, with server authentication (JWT):

1. In the [Box developer console](https://app.box.com/developers/console), create a Custom App with "Server
   Authentication (with JWT)".
2. Under Configuration, generate a public/private key pair. Box downloads the app's JSON configuration, which holds every
   value above.
3. Have a Box admin authorize the app in the enterprise (Admin Console, Apps, Custom Apps Manager).
4. Share the folder the provider should work in with the app's service account (its address is on the app's General
   Settings page), and use that folder's id as `RootFolderId`. The service account's own files start at folder `0`.

Sign-in happens on the first call that needs Box: building the provider does no network I/O (it reads the private key,
so a wrong key or passphrase fails there, with an `ArgumentException`). Box's access tokens last an hour; when Box
answers 401, the SDK fetches a new one and retries. A sign-in Box refuses throws `UnauthorizedAccessException`, with
the SDK's `BoxApiException` inside, without the token request it sent.

## How it behaves

- **Folders are real:** an empty folder exists, and `GetFileInfoAsync` on a folder says it exists and is a directory.
- **Names ignore case:** a folder cannot hold `a.txt` and `A.txt`; moving `a.txt` to `A.txt` renames it. Writing
  `A.TXT` over `a.txt` keeps the stored name.
- **Paths are resolved on every call:** Box has no lookup by path, so each call lists the folders on its path from the
  root folder down (with marker paging, 1000 entries a page). Deep paths and very large folders cost one or more calls
  per level; nothing is cached between calls, so a change made elsewhere is seen at once.
- **Listings** leave out items that are neither files nor folders (web links), and names no path can address: a name of
  only dots and spaces (`...`), or one such as `%2e%2e` that decodes to one.
- **Writes:** the caller's stream is copied to a local temp file from its current position, then uploaded: in one
  request below 20 MiB, through a chunked upload session at or above it (exact part sizes, part and whole-file digests,
  the session aborted on failure). Box makes the new version only once the upload is complete, so a write that fails
  leaves the old content. An append rewrites the file on the version it read. Writing onto a folder, or under a file,
  throws `IOException`; a name Box refuses (too long, or with characters it does not allow) throws `ArgumentException`.
- **Content types:** Box keeps none, so `UpdateFileOptions.ContentType` is ignored and `ContentType` is null.
- **ETags:** a file's `ETag` is the id of its current Box file version, which no other version of any file shares. A
  file deleted and created again under the same name has a new ETag, although Box's own `etag` starts again at `0`.
- **Conditions:** `IfNotExists` is atomic (Box refuses a name that is taken). `IfMatch` is compared with the file's
  `ETag` just before the write, which is not atomic: a write that lands in between is replaced. The same holds for the
  version an append rewrites.
- **Reads** stream the download. `ReadRangeAsync` fetches a byte range of the version it finds, and throws
  `PreconditionFailedException` when `ReadOptions.IfMatch` no longer matches; `OpenReadSeekableAsync` fetches ranges of
  the version it opened, unless `AllowModifications` is set, and throws `PreconditionFailedException` once the file has
  changed. The download itself, from Box's pre-authenticated URL, throws `FileNotFoundException` or `IOException` with
  its HTTP status only: that URL is a credential, so no error carries it.
- **Errors** are the contract's types: Box's 401 and 403, and a sign-in Box refuses (a 400 from its token endpoint),
  throw `UnauthorizedAccessException`; any other failure of Box or the network, `IOException`; with the SDK's
  `BoxApiException` (its status in `ResponseInfo.StatusCode`) or network exception as the `InnerException`. That
  `BoxApiException` keeps the request it sent: the access token in its `Authorization` header, any extra header the
  client was built with (such as a `BoxApi` shared-link password), the query string, and the body (a token request's
  holds the client secret). When the provider built its client, or was given exactly a `BoxClient`, every
  `BoxApiException` it wraps keeps Box's status, code and message, and of the request only its method, its URL without
  the query, and the headers and parameters that diagnose it (`As-User`, `If-Match`, `Range`, the digests, the content
  type; the listing and version parameters). A provider given any other `IBoxClient`, a class derived from `BoxClient`
  included, uses that client as it is: its errors are the client's own and may carry its credentials.
- **Move and copy** run on the server, on files only; they create the destination's folders and refuse an existing
  destination with `FileExistsException`. A move is one call, or, when Box refuses a case-only rename, three through a
  temporary name (`.wfp-rename-<guid>.tmp`), the old name put back if the last one fails.
- **Delete** sends the file or folder (recursively) to the Box trash, or deletes it for good where the enterprise's
  settings say so.
- **Watch** is not supported.

## Signed URLs

Read only: Box's pre-authenticated download URL (`dl.boxcloud.com`) for the file's current version, which Box makes
valid for about 15 minutes whatever is asked. Since a URL must never outlive what was asked:

- a validity under 15 minutes throws `NotSupportedException` (with the ASP.NET Core gateway, its URL is used instead);
- a validity from 15 minutes to 7 days throws `NotSupportedException` unless `AllowShorterValidity` is set; then the
  URL is returned with `ExpiresOn` 15 minutes after the call and `Kind` `PreAuthenticated`. Over 7 days always throws;
- `DownloadFileName` and `ContentType` cannot be set on Box's URL, and throw `NotSupportedException`;
- write and create URLs are not supported.

Anyone holding the URL can download that version of the file until it expires.

## Limits

- Box's own limits apply: the account's maximum file size (from 250 MB on free accounts to several GB), names of at most
  255 characters, no `/` or `\`, no trailing spaces, and the API's rate limits (the SDK retries 429 and 5xx answers).
- Box answers some calls with "not ready yet" (a download right after an upload, the commit of a large upload). For a
  download the provider waits as Box's `Retry-After` asks, up to two minutes; for a commit it asks again every second,
  for up to two minutes plus two more for every whole GiB. Then it throws `IOException`.
- Every operation on a path walks the path from the root folder, so latency grows with depth.
- The `PublicKeyId` is not sent in the JWT header by the Box SDK; Box accepts that while the app has one public key.

## More

- [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): every package, the provider
  matrix, versioning.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#box): what
  2027.0.0 changed for this provider.
