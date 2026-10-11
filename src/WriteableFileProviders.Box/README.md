# WriteableFileProviders.Box

A [Box](https://www.box.com) folder as an `IWriteableFileProvider`, on the Box .NET SDK (`Box.V2.Core` 10). Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme).

## Install

```
dotnet add package WriteableFileProviders.Box
```

Needs .NET 10. Keep every WriteableFileProviders package in an app on the same major version.

## Use

```json
{
  "Files": {
    "Type": "WriteableBoxFileProvider",
    "ClientId": "abc123",
    "ClientSecret": "...",
    "EnterpriseId": "987654",
    "PublicKeyId": "k1x2y3",
    "PrivateKey": "-----BEGIN ENCRYPTED PRIVATE KEY-----\n...\n-----END ENCRYPTED PRIVATE KEY-----\n",
    "Passphrase": "...",
    "RootFolderId": "123456789"
  }
}
```

```csharp
services.AddWriteableFileProvider(configuration.GetSection("Files"));   // then inject IWriteableFileProvider
```

To sign in another way (OAuth, a developer token, as-user calls) or set a proxy or retries, pass a Box client you
build. The provider never disposes it, and reads only `RootFolderId` and the root from the options:

```csharp
var client = new BoxClient(new BoxJwtAuth(JwtConfig.FromConfigFile("box-config.json"))).WithAsUserHeader("12345");
await using var files = new WriteableBoxFileProvider(new BoxFileProviderOptions { RootFolderId = "123456789" }, client);
```

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `ClientId`, `ClientSecret` | required | The app's `boxAppSettings.clientID` and `clientSecret` (a secret). |
| `EnterpriseId` | required | The enterprise the app signs in to, as its service account (`enterpriseID`). |
| `PublicKeyId` | required | The app's `appAuth.publicKeyID`. The SDK doesn't send it, so give the app one public key only. |
| `PrivateKey` | required | The app's `appAuth.privateKey` in PEM, encrypted or not. Escaped `\n` line breaks are fine. A secret. |
| `Passphrase` | | The private key's passphrase, for an encrypted key. A secret. |
| `RootFolderId` | `0` | The Box folder the provider works in. `0` is the service account's own root. |
| `RootDirectoryPath` | | A folder under `RootFolderId` to confine the provider to, such as `tenants/acme`. |

## Authentication

The provider signs in as a Box app's service account, with JWT:

1. In the [Box developer console](https://app.box.com/developers/console), create a Custom App with "Server
   Authentication (with JWT)".
2. Generate a key pair under Configuration. Box downloads a JSON file with every value above.
3. Have a Box admin authorize the app (Admin Console, Apps, Custom Apps Manager).
4. Share the folder with the app's service account, and use the folder's id as `RootFolderId`.

Building the provider reads the private key, so a wrong key or passphrase throws `ArgumentException` there. Sign-in
happens on the first call; a sign-in Box refuses throws `UnauthorizedAccessException`.

## What's different

- **Real folders, case ignored.** Writing `A.TXT` over `a.txt` keeps the stored name. Listings skip web links.
- **Every call walks its path** from `RootFolderId`, one listing per folder; nothing is cached. Deep paths cost more.
- **Writes** go through a local temp file, then up in one request below 20 MiB, or a chunked upload session from
  20 MiB. A failed write leaves the old content. Appends rewrite the file. Deletes go to the Box trash.
- **No content types.** `UpdateFileOptions.ContentType` is ignored; `ContentType` is null.
- **ETags** are Box file version ids. `IfNotExists` is atomic; `IfMatch` is checked just before the write: not atomic.
- **Errors** carry no credentials, unless you pass an `IBoxClient` other than a plain `BoxClient`.
- **Signed URLs** are read only: Box's download URL, which lives about 15 minutes. You get it only with
  `AllowShorterValidity`, a validity of 15 minutes or more, and no file name or content type; otherwise, the gateway's.
- **Waits:** when Box says "not ready yet", a download waits up to 2 minutes, a large upload's commit 2 minutes plus 2
  per GiB; then `IOException`.
- **Limits:** the account's maximum file size, names of at most 255 characters, Box's rate limits (the SDK retries
  429 and 5xx). `Watch` throws `NotSupportedException`.

## Learn more

- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#box),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#box),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#box).
