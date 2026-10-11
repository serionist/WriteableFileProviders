# Signed URLs

A signed URL lets whoever holds it download or upload one file for a limited time, without your credentials. A
provider signs its own URLs where its store can (a SAS on Azure, a presigned URL on S3 and MinIO, a V4 signed URL on
Google Cloud Storage, a download URL on Box and SharePoint). For everything else, Physical included, the
`WriteableFileProviders.AspNetCore` package adds a **gateway** to your ASP.NET Core app that serves the file itself,
and a URL service that picks between the two.

## Asking for a URL

```csharp
SignedUrl url = await files.GetSignedUrlAsync("reports/2026.pdf", new SignedUrlOptions
{
    Validity = TimeSpan.FromMinutes(15),       // required, more than zero
    Access = SignedUrlAccess.Read,             // Read (the default), Create or Write
    DownloadFileName = "Report 2026.pdf",      // read URLs: the name a browser saves it as
    ContentType = "application/pdf",           // read URLs: the type served; upload URLs: the type the upload sends
});
```

- **`Access`:** `Read` downloads with `GET`; `Create` uploads with `PUT` only if the file does not exist yet; `Write`
  uploads with `PUT`, creating or replacing.
- **What you get:** `Url`, the `Method` to use (`GET` or `PUT`), the `Headers` the request must send (an upload sends
  every one of them), `ExpiresOn` (null where the service sets the lifetime and does not say), and `Kind`:
  `Presigned` (signed with the provider's credential), `PreAuthenticated` (the service's own URL, with a lifetime it
  sets) or `Gateway`.
- **A URL never lives longer than asked.** A provider that cannot sign the URL asked for (no native URLs, not for this
  access, not for this long, not with a download name or content type) throws `NotSupportedException` instead of
  returning another one. A URL whose lifetime the service sets, which may stop working before `Validity` ends, is
  returned only with `AllowShorterValidity`.
- A validity that is not positive throws `ArgumentOutOfRangeException`; a read URL for a missing file, or a folder,
  throws `FileNotFoundException`.

A signed URL is a bearer credential: issue one only to a caller who may read or write that file.

## Download links for local files

The gateway gives every provider URLs, the local disk included. The README tutorial's
[step 2](../../README.md#2-download-links-for-local-files) is the minimal app: a Physical provider, the gateway at
`http://localhost:5000`, and a `/link/{**path}` endpoint that returns `GetSignedUrlAsync(path, ...).Url.AbsoluteUri`.
`GET /link/reports/2026.csv` answers with a URL such as `http://localhost:5000/files/<token>/2026.csv`, which downloads
`files/reports/2026.csv` for an hour.

- **`PublicBaseUri`**, the `Uri` given to `AddWriteableFileProviderGateway`, is the URL your app is reached at from
  outside and every gateway URL is built from, never the request's: `https`, or plain `http` to a loopback host such
  as `localhost`. Run the app at that address, such as `dotnet run --urls http://localhost:5000`.
- **Return `Url.AbsoluteUri`**, the escaped form, when you hand a link out as text: `Uri.ToString()` unescapes it.
- **The `/link` endpoint hands a bearer link to anyone who asks.** A real app checks that the caller may read the file
  first, as in the [AspNetCore README](../../src/WriteableFileProviders.AspNetCore/README.md#use).

## The URL service

`IWriteableFileUrlService` issues a URL for the app's `IWriteableFileProvider`, or for one registered under a key:

```csharp
SignedUrl url = await urls.GetSignedUrlAsync("reports/2026.pdf", options);             // the IWriteableFileProvider
SignedUrl other = await urls.GetSignedUrlAsync("2026.zip", options, providerKey: "archive"); // AddKeyedSingleton("archive", ...)
```

Without a key, the URL is for the provider the container gives for `IWriteableFileProvider` (the last one registered,
when there are several); none registered throws `InvalidOperationException`. With a key, it is for the provider
registered with `AddKeyedSingleton<IWriteableFileProvider>(key, ...)`; a key with none throws `ArgumentException`. The
gateway serves each URL from the same provider. Every provider the gateway serves, default or keyed, must be a
singleton, or resolve to the same store in every scope: the URL service resolves it from the root container when it
issues a URL, and the gateway from the request's services when it redeems one, so a scoped or transient provider per
tenant would let a URL be redeemed against another tenant's store. Then:

1. Unless `PreferGateway` is set, the service calls the provider's `GetSignedUrlAsync`. If that returns, you get the
   provider's own URL (`Kind` is `Presigned` or `PreAuthenticated`).
2. If the provider throws `NotSupportedException`, the gateway issues a URL (`Kind` is `Gateway`).

Any other error is yours: `FileNotFoundException` for a download of a missing file, `ArgumentException` for a path the
provider refuses, `ArgumentOutOfRangeException` for a validity that is not positive. A gateway download URL honours
`DownloadFileName` and `ContentType`; a gateway upload URL with a `ContentType` requires the upload to send that
`Content-Type` (compared without case), and its `Headers` say so.

**The gateway's rules bind gateway URLs only:** `OneTimeUploadTokens`, `MaxReadValidity` and `MaxUploadValidity`. A
native URL follows its provider: a native upload URL can be used again until it expires (a `Create` URL fails once the
file exists), and it can live up to the provider's own maximum, 7 days. An app that needs the same upload rules on
every provider sets `PreferGateway`.

## The gateway

`AddWriteableFileProviderGateway` registers the URL service, the options (validated when the app starts), ASP.NET Core
Data Protection for the tokens and the in-memory store of used upload tokens; `MapWriteableFileProviderGateway` maps
the endpoints. Its options are in the [AspNetCore README](../../src/WriteableFileProviders.AspNetCore/README.md#configuration).
A validity longer than the gateway's maximum throws `NotSupportedException`, or, with `AllowShorterValidity`, gives a
URL that lives the maximum, with its true `ExpiresOn`.

### The endpoints

A gateway URL is `{PublicBaseUri}{BasePath}/{token}/{fileName}`. The file name segment is cosmetic: it makes the URL
readable and is ignored.

**`GET` and `HEAD`** answer with the file:

- `Content-Disposition: attachment`, with the file name from the token (the `DownloadFileName` asked for, else the
  file's name), encoded per RFC 6266: an ASCII `filename` and a UTF-8 `filename*`. Editing the URL's file name segment
  changes nothing. `<img>`, `<video>` and `fetch` still use the URL; only a top-level navigation downloads.
- `Content-Type`: the content type the read token names (`SignedUrlOptions.ContentType`), else
  `application/octet-stream`. Never a type the store keeps that the token did not name: to have a browser show an image
  or play a video, put its type in the read URL.
- `Accept-Ranges`, `ETag` and `Last-Modified`, with `Range`, `If-Range` and `If-None-Match` handled (206, 416, 304).
  The gateway reads the file's `ETag` first, then reads the bytes it serves with one `ReadRangeAsync` of the provider,
  with `IfMatch` on that `ETag`: the whole file, or the one HTTP range, is one request to the store, and a file that
  changes meanwhile fails the response rather than mixing two versions. A provider that keeps no version of a file (no
  `ETag`) cannot pin, so the gateway serves that file whole, without ranges or an `ETag`.

**`PUT`** stores the request body: a `Create` URL with `IfNotExists` (201 Created, or 412 if the file exists), a `Write`
URL by overwriting (200). It answers 413 when the body is larger than `MaxUploadBytes` (a declared `Content-Length` is
refused before anything is read; a chunked body is cut off, and the file keeps its old content), and 415 when the token
names a content type and the request's `Content-Type` differs (compared without case, parameters included), or when
the token names none and the request's `Content-Type` is not a valid media type. The content type, the token's or else
the request's own, is stored where the provider keeps one; a store without one ignores it. Either way the uploader's
type is never served back: a download is served as its read token names, or as `application/octet-stream`.

Whatever is wrong with the token itself (malformed, tampered with, expired, issued for another provider or another
access, already used, or naming a path the provider refuses) answers 404, so a caller learns nothing about why.

The endpoints allow anonymous access, because the token is the credential: a fallback authorization policy does not
apply to them. They are excluded from output caching (a cached response would outlive its token) and from API
descriptions. Add rate limiting, CORS or host filtering through the builder `MapWriteableFileProviderGateway` returns.

### Security headers, and a host of its own

Every answer of the gateway's endpoints, 404s included, carries `Content-Security-Policy: default-src 'none'; sandbox`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: private, no-store`.

The gateway serves whatever was uploaded, from the origin in `PublicBaseUri`. Point a host name of its own at the app,
such as `files.example.com` beside `app.example.com`, and keep the app's cookies scoped to the app's host (not to
`.example.com`), so a download never runs, or reads cookies, on the app's origin. `RequireHost("files.example.com")` on
the builder `MapWriteableFileProviderGateway` returns keeps the gateway off the app's own host. The gateway's headers
(the sandboxing `Content-Security-Policy`, `nosniff`, `attachment`, and only the token's content type) are the second
line.

### Tokens and Data Protection

A token holds the path, the access, the content type, the file name and a unique id, encrypted and signed with
ASP.NET Core Data Protection, and expires with the URL. The provider's key, when it has one, and the access travel in
the clear at the start of the token, to pick the key purpose: they are part of what the payload is protected for, so
changing either breaks the token. The path comes only from the token, and the provider validates it again on every
request, so no token can reach outside the provider's root.

Tokens use your app's Data Protection key ring, which `AddWriteableFileProviderGateway` registers with
`AddDataProtection()`. Configure it as you would for cookies:

- **Several instances** must share the key ring and the application name (`PersistKeysToAzureBlobStorage`,
  `PersistKeysToFileSystem`, `SetApplicationName`, ...), or a URL issued by one instance answers 404 on another.
- **Keys kept only in memory** lose every issued URL when the app restarts.
- **Revoking a key** invalidates every URL protected with it.

The token format and its purpose chain (`WriteableFileProviders.Gateway.v1`, the provider's key when it has one, the
access) are kept stable: a URL issued by one version redeems on the next.

### One-time uploads

With `OneTimeUploadTokens` (the default), the gateway records each upload token it accepts in an
`IGatewayReplayStore`, after checking the request's size and content type and before reading the body. A second upload
with the same URL answers 404, even when the first upload failed halfway; ask for a new URL then.

The default store keeps the used tokens in the memory of one app instance. It is exact there, but **another instance of
your app knows nothing of them**, and a restart forgets them. An app on several instances either:

- registers a shared store: `services.AddSingleton<IGatewayReplayStore, DistributedCacheGatewayReplayStore>()` uses your
  `IDistributedCache` (Redis, SQL Server, ...). It is best effort: `IDistributedCache` has no atomic add-if-absent, so
  two instances that receive the same URL at the same moment can both accept it. For exactly-once across instances,
  implement `IGatewayReplayStore.TryConsumeAsync` on your store's own atomic operation (Redis `SET NX`, a unique key).
- or sets `OneTimeUploadTokens = false`, so an upload URL works until it expires.

### Keep tokens out of your logs

A token is a bearer credential that travels in the URL path, so anything that records request paths records tokens.

- **HTTP logging** (`UseHttpLogging`): the gateway replaces the path of its requests with
  `{BasePath}/{token}/{fileName}`.
- **Hosting diagnostics**: `Microsoft.AspNetCore.Hosting.Diagnostics` logs "Request starting ... GET
  https://.../files/{token}/..." at `Information`. The default templates set `Microsoft.AspNetCore` to `Warning`; keep
  it there in production, or at least `"Microsoft.AspNetCore.Hosting.Diagnostics": "Warning"`.
- **Log scopes**: ASP.NET Core puts `RequestPath` in the scope of every log written during a request; a logger that
  writes scopes (`IncludeScopes`, most JSON and OpenTelemetry exporters) writes the token. Filter that scope value, or
  leave scopes out for the gateway's requests.
- **Tracing**: OpenTelemetry's ASP.NET Core instrumentation records `url.path`; redact it with an enrichment or filter
  callback. `http.route` holds the route template and is safe.
- **W3C logging** (`UseW3CLogging`) records the request path as it is: leave its `UriStem` field out, or keep the
  gateway's requests out of it.
- **Outside the app**: the web server's own logs (IIS), reverse proxies, load balancers and CDNs log request lines.
  Redact the segment after `BasePath` there too.

The gateway itself logs nothing.

### Limits

The gateway's limits (validity, one `PUT` per upload, typed uploads, attachment downloads) are in the
[AspNetCore README](../../src/WriteableFileProviders.AspNetCore/README.md#limits).

## Native URLs, provider by provider

Where a row says the gateway, the provider throws `NotSupportedException` and the URL service issues a gateway URL.
The [provider comparison](providers.md#urls-copies-notifications) has the same in one table.

### Physical

None natively: `GetSignedUrlAsync` throws `NotSupportedException`. The gateway issues URLs for this provider instead,
for reading, creating and writing files.

### Azure Blob Storage

`GetSignedUrlAsync` signs a SAS natively for `Read` (GET), `Create` (PUT only if the blob does not exist: SAS permission
`c`) and `Write` (PUT, creating or replacing), for up to 7 days; a longer validity throws `NotSupportedException`. A
shared key signs a service SAS; Microsoft Entra ID signs a user delegation SAS; a client from a SAS URL, or anonymous,
signs none.

- The SAS starts 15 minutes in the past, so a clock that runs behind does not reject it (and a SAS expiration policy,
  which requires a start time, accepts it). It ends exactly at the requested validity: `ExpiresOn` says when. On an
  `https` endpoint it is valid over HTTPS only.
- A user delegation SAS never outlives its key. If the key would expire first, the call throws `NotSupportedException`
  unless `AllowShorterValidity` is set, and then `ExpiresOn` is the key's expiry. Azure's keys last at most 7 days, so
  the provider asks for one that ends 5 minutes sooner: a 7-day user delegation URL needs `AllowShorterValidity`. A key
  Azure refuses (401, 403) is not asked for again for 10 minutes; meanwhile the call throws `NotSupportedException`.
- A read URL carries `DownloadFileName` (as an RFC 6266 `Content-Disposition`) and `ContentType`. An upload URL takes
  neither, because a SAS cannot make the uploader send a content type: such a request throws `NotSupportedException`,
  and the gateway takes over.
- An upload is a single `PUT` that sends `x-ms-blob-type: BlockBlob` (in `SignedUrl.Headers`), up to Azure's 5,000 MiB
  limit for one request. It cannot replace a 2026.10.10.x append blob: write that file once through the provider first.
- A read URL for a missing file throws `FileNotFoundException`. An upload URL into a missing container throws
  `InvalidOperationException`, or creates the container with `CreateContainerIfMissing`.

### Amazon S3

`GetSignedUrlAsync` presigns read (GET), create (PUT with a signed `If-None-Match: *`) and write (PUT) URLs, SigV4,
for at most 7 days. A longer validity throws `NotSupportedException`.

- A create URL is issued only on AWS (no `ServiceUrl`, no client with its own `ServiceURL`, and no endpoint in the
  SDK's configuration), which enforces the `If-None-Match: *` it signs. Toward a named endpoint, a server may ignore
  that header and let the URL overwrite a file, so a create URL throws `NotSupportedException` there, and the gateway
  issues one, which checks the condition before the write (as `IfNotExists` does toward that endpoint). Read and write
  URLs stay native.
- A read URL can carry a download file name and a content type (`response-content-disposition`, `-type`), and needs
  the file to exist (`FileNotFoundException`). An upload URL with a download file name throws
  `NotSupportedException`.
- An upload URL with a `ContentType` signs it. The upload must send every header in `SignedUrl.Headers`.
- A URL never outlives the credential that signs it. Long-term keys live as asked. A credential that expires sooner
  (STS, instance metadata), or whose expiry cannot be known (a session token, a client you passed in), makes the URL
  shorter than asked: it is issued only with `AllowShorterValidity`, with `ExpiresOn` set when the expiry is known.
- Presigned URLs do not carry a requester-pays charge.
- Anonymous providers cannot presign.

### MinIO

`GetSignedUrlAsync` presigns read (GET) and write (PUT) URLs for at most 7 days, over the scheme `UseSSL` chooses. A
longer validity throws `NotSupportedException`. A read URL can carry a download file name and a content type, and needs
the file to exist; an upload URL with a download file name throws `NotSupportedException`, and an upload must send every
header in `SignedUrl.Headers`. URLs from a client you passed in need `AllowShorterValidity`, since the provider cannot
see when its credentials expire. Anonymous providers cannot presign.

A create URL (only if the file does not exist yet) throws `NotSupportedException`: its only guard would be the
`If-None-Match: *` it signs, which MinIO releases without conditional writes ignore, so it could overwrite a file. The
gateway issues create URLs instead, and checks the condition before the write (as `IfNotExists` does here).

### Google Cloud Storage

`GetSignedUrlAsync` issues V4 signed URLs, which need a credential that can sign: a service account key, or an identity
that holds `iam.serviceAccounts.signBlob` on its service account (Workload Identity, Compute Engine, Cloud Run). With a
credential that cannot sign (a user, an access token), or when IAM's `signBlob` refuses the signing (403, an identity
without the permission), answers another error than 401, or cannot be reached, it throws `NotSupportedException` with
the cause inside, and the gateway takes over. A refused credential (401, a token error) and a timeout are not this:
they fail as on every other member, with `UnauthorizedAccessException` and `IOException`.

| Access | URL | The request must send |
|---|---|---|
| `Read` | GET; `DownloadFileName` and `ContentType` become `response-content-disposition` and `response-content-type` | nothing |
| `Create` | PUT, only if the file does not exist | `x-goog-if-generation-match: 0`, and `Content-Type` when one was given |
| `Write` | PUT, creating or replacing | `Content-Type` when one was given |

`SignedUrl.Headers` lists the headers to send; they are signed. A URL lives the validity asked, rounded down to whole
seconds: at least one second, at most seven days. A longer or shorter one throws `NotSupportedException`, and a validity
that is not positive throws `ArgumentOutOfRangeException`. `ExpiresOn` is read from the URL itself. A read URL for a
missing file throws `FileNotFoundException`; a download name on an upload URL throws `NotSupportedException`. Create and
write URLs are signed without asking the bucket, so with a missing bucket they are issued, and fail only when used.

### Box

Read only: Box's pre-authenticated download URL (`dl.boxcloud.com`) for the file's current version, which Box makes
valid for about 15 minutes whatever is asked. Since a URL must never outlive what was asked:

- a validity under 15 minutes throws `NotSupportedException`, and the gateway serves it;
- a validity from 15 minutes to 7 days throws `NotSupportedException` unless `AllowShorterValidity` is set; then the
  URL is returned with `ExpiresOn` 15 minutes after the call and `Kind` `PreAuthenticated`. Over 7 days always throws;
- `DownloadFileName` and `ContentType` cannot be set on Box's URL, and throw `NotSupportedException`;
- write and create URLs are not supported.

Anyone holding the URL can download that version of the file until it expires.

### SharePoint

Read only (`SignedUrlKind.PreAuthenticated`): the file's download URL, which Graph says lives up to an hour and may stop
working within minutes. A URL never lives longer than asked, so a validity under an hour throws `NotSupportedException`
(the gateway serves those), one from an hour to 7 days needs `SignedUrlOptions.AllowShorterValidity`, and one over 7
days always throws. `ExpiresOn` is null, because the service does not say. Anyone holding the URL can download the file
until it ends, and removing someone's access may not end a URL already issued. `DownloadFileName` and `ContentType` are
not supported on these URLs, and write and create URLs not at all.
