# Signed URLs

A signed URL lets whoever holds it download or upload one file for a limited time, without your credentials. Treat it
as a password: issue one only to a caller who may use that file. Providers sign their own where the store can; for the
rest, Physical included, `WriteableFileProviders.AspNetCore` adds a gateway to your app that serves the file.

## Ask for a URL

```csharp
SignedUrl url = await files.GetSignedUrlAsync("reports/2026.pdf", new SignedUrlOptions
{
    Validity = TimeSpan.FromMinutes(15),       // required, more than zero
    Access = SignedUrlAccess.Read,             // Read (GET, the default), Create (PUT if absent) or Write (PUT)
    DownloadFileName = "Report 2026.pdf",      // read URLs: the name a browser saves it as
    ContentType = "application/pdf",           // read URLs: the type served; upload URLs: the type the upload sends
});
```

You get `Url`, `Method`, the `Headers` an upload must send, `ExpiresOn` (null when the service doesn't say) and `Kind`:
`Presigned`, `PreAuthenticated` (the service's own URL) or `Gateway`.

- A URL never lives longer than asked. A provider that can't sign exactly what you asked for (this access, this long,
  a download name or content type) throws `NotSupportedException`.
- A URL that may stop working sooner (Box and SharePoint URLs, or a credential that expires first) comes only with
  `AllowShorterValidity`.

## The URL service

```csharp
SignedUrl url = await urls.GetSignedUrlAsync("reports/2026.pdf", options);                     // IWriteableFileProvider
SignedUrl other = await urls.GetSignedUrlAsync("2026.zip", options, providerKey: "archive"); // AddKeyedSingleton("archive", ...)
```

`IWriteableFileUrlService` asks the provider first. When the provider throws `NotSupportedException`, or with
`PreferGateway` set, it issues a gateway URL. Other errors reach you as the provider threw them.

- No `IWriteableFileProvider` registered throws `InvalidOperationException`; an unknown key, `ArgumentException`.
- **Register every provider the gateway serves as a singleton** (or one that gives the same store in every scope). URLs
  are issued from the root container and redeemed from the request's scope: a scoped provider per tenant would let a
  URL reach another tenant's store.
- A gateway URL longer than `MaxReadValidity` or `MaxUploadValidity` throws `NotSupportedException`, or with
  `AllowShorterValidity` lives the maximum. These limits and `OneTimeUploadTokens` bind gateway URLs only: a native
  upload URL works until it expires, up to 7 days. Set `PreferGateway` for the same upload rules on every store.
- Hand out `Url.AbsoluteUri`; `ToString()` unescapes ([example](getting-started.md#2-download-links-for-local-files)).

## The gateway

`AddWriteableFileProviderGateway` registers the URL service, Data Protection and the store of used upload tokens, and
checks its [options](../../src/WriteableFileProviders.AspNetCore/README.md) at startup.
`MapWriteableFileProviderGateway` maps `{BasePath}/{token}/{fileName}`; the file name is cosmetic.

- **`GET`, `HEAD`** send the file as an `attachment` with the token's file name, typed as the read URL asked, else
  `application/octet-stream`, never as the store has it. Set `ContentType` for a browser to show an image or a video.
- They handle `Range`, `If-Range` and `If-None-Match` (206, 416, 304). Each response is one `ReadRangeAsync` pinned to
  the file's `ETag`, so a file that changes meanwhile fails the response. A file without an `ETag` is served whole.
- **`PUT`** stores the body: 201 for `Create` (412 if the file exists), 200 for `Write`. 413 over `MaxUploadBytes`: a
  declared `Content-Length` before anything is read, a chunked body when it gets there, leaving the old content.
- 415 when the URL names a content type and the request's differs (case aside, parameters included), or when it names
  none and the request's isn't a valid media type. The type is stored where the store keeps one.
- Any problem with the token (malformed, tampered, expired, another provider or access, used, a refused path): 404.
- The endpoints allow anonymous access (the token is the credential), so a fallback authorization policy doesn't apply.
  They skip output caching and API descriptions. Add rate limiting, CORS or host filtering on the returned builder.

Tokens are encrypted and signed with your app's Data Protection keys, and the provider checks a token's path on every
request. Share the key ring and application name across instances, or a URL from one answers 404 on another.

- Keys kept only in memory lose every URL on restart. Revoking a key invalidates its URLs.
- The token format and purpose chain (`WriteableFileProviders.Gateway.v1`, the provider's key if any, the access) stay
  stable: a URL issued by one version redeems on the next.

### Security headers, and a host of its own

Every gateway response, 404s included, sends `Content-Security-Policy: default-src 'none'; sandbox`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: private, no-store`.

The gateway serves whatever was uploaded, so give it a host of its own (`files.example.com` beside `app.example.com`).
Scope the app's cookies to the app's host, not `.example.com`, and call `RequireHost("files.example.com")` on the
gateway's builder. A download then never runs, or reads cookies, on the app's origin.

### One-time uploads

With `OneTimeUploadTokens` (the default), the gateway records each upload token it accepts, after the size and content
type checks and before reading the body. Reusing the URL answers 404, even after a failed upload: ask for a new one.

The default `IGatewayReplayStore` is in memory: exact on one instance, unknown to others, forgotten on restart. On
several instances, set `OneTimeUploadTokens = false`, or register `DistributedCacheGatewayReplayStore` as the
`IGatewayReplayStore` singleton, over your `IDistributedCache`. It is best effort: two instances can both accept a URL
at once. For exactly once, implement `TryConsumeAsync` on an atomic operation (Redis `SET NX`, a unique key).

### Keep tokens out of your logs

Tokens travel in the URL path, so anything that records paths records them. The gateway itself logs nothing.

| Where | What to do |
|---|---|
| `UseHttpLogging` | Nothing: the gateway replaces its path with `{BasePath}/{token}/{fileName}`. |
| Hosting diagnostics | Keep `Microsoft.AspNetCore.Hosting.Diagnostics` at `Warning` or above. |
| Log scopes (`RequestPath`) | Filter that value, or leave scopes out for the gateway's requests. |
| OpenTelemetry | Redact `url.path`. `http.route` is safe. |
| `UseW3CLogging` | Leave out `UriStem`, or the gateway's requests. |
| IIS, proxies, load balancers, CDNs | Redact the segment after `BasePath`. |

## Native URLs by provider

The gateway issues what a provider can't. Physical signs nothing, and no upload URL takes a `DownloadFileName`.

### Azure Blob Storage

- `Read`, `Create` and `Write`, up to 7 days: a service SAS with a shared key, a user delegation SAS with Microsoft
  Entra ID. A client from a SAS URL, or anonymous, can't sign. Upload URLs take no `ContentType`.
- A SAS starts 15 minutes in the past, for clock skew, and ends exactly at `Validity`. On `https` it is HTTPS-only.
- A user delegation SAS ends with its key, which lasts 7 days less 5 minutes: a 7-day URL needs `AllowShorterValidity`.
  After Azure refuses a key (401, 403), the provider waits 10 minutes before asking again.
- An upload is one `PUT` with `x-ms-blob-type: BlockBlob`, up to 5,000 MiB. It can't replace a 2026.10.10.x append
  blob: write that file once through the provider first. An upload URL into a missing container throws
  `InvalidOperationException`, unless `CreateContainerIfMissing` is set.

### Amazon S3

- `Read` and `Write`, SigV4, up to 7 days. Upload URLs sign the content type.
- `Create` (a signed `If-None-Match: *`) on AWS only. Toward a named endpoint (`ServiceUrl`, a client's `ServiceURL`,
  the SDK's configuration) the server may ignore that header, so the gateway issues it and checks first.
- A credential that expires sooner (STS, instance metadata), or whose expiry is unknown (a session token, your own
  client), needs `AllowShorterValidity`. Anonymous providers can't sign; URLs carry no requester-pays charge.

### MinIO

- `Read` and `Write`, up to 7 days, over the scheme `UseSSL` sets. A client you passed in needs `AllowShorterValidity`.
  Anonymous providers can't sign.
- No `Create`: older MinIO releases ignore the `If-None-Match: *` it would sign. The gateway issues it and checks first.

### Google Cloud Storage

- `Read`, `Create` (signs `x-goog-if-generation-match: 0`) and `Write`, V4, 1 second to 7 days, rounded down to whole
  seconds. Create and write URLs are signed without asking the bucket, so a missing bucket fails only when used.
- Needs a service account key, or an identity with `iam.serviceAccounts.signBlob` (Workload Identity, Compute Engine,
  Cloud Run). Otherwise, or when `signBlob` refuses (403), fails with another status or can't be reached, the gateway
  takes over. A refused credential (401) and a timeout fail as on any other member.

### Box

`Read` only, without `DownloadFileName` or `ContentType`: Box's download URL for the current version, valid about 15
minutes whatever you ask. A validity under 15 minutes throws; up to 7 days needs `AllowShorterValidity`, and
`ExpiresOn` is 15 minutes after the call.

### SharePoint

`Read` only, without `DownloadFileName` or `ContentType`: Graph's download URL, which lives up to an hour and may stop
within minutes. `ExpiresOn` is null. A validity under an hour throws; up to 7 days needs `AllowShorterValidity`.
Removing someone's access may not end a URL already issued.
