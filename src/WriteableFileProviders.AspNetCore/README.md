# WriteableFileProviders.AspNetCore

Signed URLs for every [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders) provider, in an
ASP.NET Core app:

- **`IWriteableFileUrlService`** issues a URL that lets its holder download or upload one file for a limited time. It
  asks the provider for its native URL first (an Azure SAS, an S3 or MinIO presigned URL, a GCS V4 signed URL, a Box or
  SharePoint download URL), and when the provider cannot sign that URL it issues a gateway URL instead.
- **The gateway** is a set of endpoints in your app that serve those URLs: `GET` and `HEAD` download a file, with HTTP
  ranges, and `PUT` uploads one. It works for any provider, Physical included.

Every WriteableFileProviders package must be on the same major version; this one needs .NET 10.

## Install and wire up

```
dotnet add package WriteableFileProviders.AspNetCore
```

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWriteableFileProvider("files", builder.Configuration.GetSection("Files"));
builder.Services.AddWriteableFileProviderGateway(options =>
{
    // Required, and never taken from the request. A host name of its own, which this app also answers on, and which
    // the app's cookies (scoped to app.example.com) are never sent to.
    options.PublicBaseUri = new Uri("https://files.example.com/");
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapWriteableFileProviderGateway();                               // GET, HEAD, PUT {BasePath}/{token}/{fileName?}

// A signed URL is a bearer credential: issue one only to a caller who may read the file.
app.MapGet("/reports/{id}/link", async (string id, ClaimsPrincipal user, IReportAccess reports, IWriteableFileUrlService urls) =>
{
    if (!await reports.CanReadAsync(user, id))
        return Results.Forbid();
    var url = await urls.GetSignedUrlAsync("files", $"reports/{id}.pdf", new SignedUrlOptions
    {
        Validity = TimeSpan.FromMinutes(15),
        DownloadFileName = $"Report {id}.pdf",
        ContentType = "application/pdf",
    });
    return Results.Ok(url.Url);
}).RequireAuthorization();

app.Run();
```

`IReportAccess` stands for your own check that this user may see this report. The gateway's URLs need no sign-in:
whoever holds one can use it until it expires, so the endpoint that hands them out is where access is decided.

An upload URL works the same way: `Permission = SignedUrlAccess.Create` (only if the file does not exist yet) or
`SignedUrlAccess.Write` (create or replace). The client sends `PUT` to `SignedUrl.Url` with the headers in
`SignedUrl.Headers`.

## Which URL you get

`GetSignedUrlAsync(providerName, path, options)` looks the provider up by the name it was registered under, then:

1. Unless `PreferGateway` is set, it calls the provider's `GetSignedUrlAsync`. If that returns, you get the provider's
   own URL (`SignedUrl.Kind` is `Presigned` or `PreAuthenticated`).
2. If the provider throws `NotSupportedException` (no native URLs, not for this access, not for this long, not with a
   download file name or content type, or only a URL that lives shorter than asked; see each provider's README), the
   gateway issues a URL (`Kind` is `Gateway`).

Any other error is yours: `FileNotFoundException` for a download of a missing file, `ArgumentException` for a path the
provider refuses, `ArgumentOutOfRangeException` for a validity that is not positive. A gateway download URL honours
`DownloadFileName` and `ContentType`; a gateway upload URL with a `ContentType` requires the upload to send exactly that
`Content-Type`, and its `Headers` say so.

**The gateway's rules bind gateway URLs only** (`Kind` is `Gateway`): `OneTimeUploadTokens`, `MaxReadValidity` and
`MaxUploadValidity`. A native URL follows its provider: a native upload URL can be used again until it expires (a
`Create` URL fails once the file exists), and it can live up to the provider's own maximum, 7 days. An app that needs
the same upload rules on every provider sets `PreferGateway`.

## Options

| Option | Default | What it does |
|---|---|---|
| `PublicBaseUri` | required | The absolute URL the gateway is reachable at from outside, including any path base behind a proxy. Gateway URLs are built from it, never from the request's `Host`, so a forged header cannot redirect them. `https`, or `http` on a loopback host. Use a host name of its own (see "Serve it from a host of its own"). |
| `BasePath` | `/files` | Where the endpoints are mapped. Pick one that does not clash with your routes. |
| `MaxReadValidity` | 7 days | The longest a gateway download URL lives. At most 7 days. |
| `MaxUploadValidity` | 1 hour | The longest a gateway upload URL lives. At most 7 days. |
| `OneTimeUploadTokens` | `true` | A gateway upload URL works for one upload. See "One-time uploads". |
| `MaxUploadBytes` | 100 MiB | The largest upload body. The gateway raises the server's own body limit to this for its uploads. |
| `PreferGateway` | `false` | Always issue gateway URLs, even for providers that sign natively. |
| `AllowInsecureHttp` | `false` | Accept a plain `http` `PublicBaseUri` on a host that is not loopback. Tokens are credentials: do not. |

A validity longer than the maximum throws `NotSupportedException`, or, with `SignedUrlOptions.AllowShorterValidity`,
gives a URL that lives the maximum, with its true `ExpiresOn`. The options are validated when the app starts.

## The endpoints

A gateway URL is `{PublicBaseUri}{BasePath}/{token}/{fileName}`. The file name segment is cosmetic: it makes the URL
readable and is ignored.

**`GET` and `HEAD`** answer with the file:

- `Content-Disposition: attachment`, with the file name from the token (the `DownloadFileName` asked for, else the
  file's name), encoded per RFC 6266: an ASCII `filename` and a UTF-8 `filename*`. Editing the URL's file name segment
  changes nothing. `<img>`, `<video>` and `fetch` still use the URL; only a top-level navigation downloads.
- `Content-Type`: the content type the read token names (`SignedUrlOptions.ContentType`), else
  `application/octet-stream`. Never a type the store keeps that the token did not name: to have a browser show an image
  or play a video, put its type in the read URL.
- `Content-Security-Policy: default-src 'none'; sandbox`, `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer` and `Cache-Control: private, no-store`, on every gateway response.
- `Accept-Ranges`, `ETag` and `Last-Modified`, with `Range`, `If-Range` and `If-None-Match` handled (206, 416, 304).
  The gateway reads the file's `ETag` first, then reads the bytes it serves with one `ReadRangeAsync` of the provider,
  with `IfMatch` on that `ETag`: the whole file, or the one HTTP range, is one request to the store, and a file that
  changes meanwhile fails the response rather than mixing two versions. A provider that keeps no version of a file (no
  `ETag`) cannot pin, so the gateway serves that file whole, without ranges or an `ETag`.

**`PUT`** stores the request body: a `Create` URL with `IfNotExists` (201 Created, or 412 if the file exists), a `Write`
URL by overwriting (200). It answers 413 when the body is larger than `MaxUploadBytes` (a declared `Content-Length` is
refused before anything is read; a chunked body is cut off, and the file keeps its old content), and 415 when the
token names a content type and the request's `Content-Type` differs (compared without case, parameters included).
The content type, the token's or else the request's own, is stored where the provider keeps one (see its README); a
store without one ignores it. Either way the uploader's type is never served back: a download is served as its read
token names, or as `application/octet-stream`.

Whatever is wrong with the token itself (malformed, tampered with, expired, issued for another provider or another
access, already used, or naming a path the provider refuses) answers 404, so a caller learns nothing about why.

The endpoints allow anonymous access, because the token is the credential: a fallback authorization policy does not
apply to them. They are excluded from output caching (a cached response would outlive its token) and from API
descriptions. Add rate limiting, CORS or host filtering through the builder `MapWriteableFileProviderGateway` returns.

## Serve it from a host of its own

The gateway serves whatever was uploaded, from the origin in `PublicBaseUri`. Point a host name of its own at the app,
such as `files.example.com` beside `app.example.com`, and keep the app's cookies scoped to the app's host (not to
`.example.com`), so a download never runs, or reads cookies, on the app's origin. `RequireHost("files.example.com")` on
the builder `MapWriteableFileProviderGateway` returns keeps the gateway off the app's own host. The gateway's headers
(the sandboxing `Content-Security-Policy`, `nosniff`, `attachment`, and only the token's content type) are the second
line.

## Tokens and Data Protection

A token holds the provider's name, the path, the access, the content type, the file name and a unique id, encrypted
and signed with ASP.NET Core Data Protection, and expires with the URL. The provider's name and the access also travel
in the clear at the start of the token, to pick the key purpose; changing them breaks the token. The path comes only
from the token, and the provider validates it again on every request, so no token can reach outside the provider's
root.

Tokens use your app's Data Protection key ring, which `AddWriteableFileProviderGateway` registers with
`AddDataProtection()`. Configure it as you would for cookies:

- **Several instances** must share the key ring and the application name (`PersistKeysToAzureBlobStorage`,
  `PersistKeysToFileSystem`, `SetApplicationName`, ...), or a URL issued by one instance answers 404 on another.
- **Keys kept only in memory** lose every issued URL when the app restarts.
- **Revoking a key** invalidates every URL protected with it.

The token format and its purpose chain (`WriteableFileProviders.Gateway.v1`, the provider name, the access) are kept
stable: a URL issued by one version redeems on the next.

## One-time uploads

With `OneTimeUploadTokens` (the default), the gateway records each upload token it accepts in an
`IGatewayReplayStore`, after checking the request's size and content type and before reading the body. A second
upload with the same URL answers 404, even when the first upload failed halfway; ask for a new URL then.

The default store keeps the used tokens in the memory of one app instance. It is exact there, but **another instance
of your app knows nothing of them**, and a restart forgets them. An app on several instances either:

- registers a shared store: `services.AddSingleton<IGatewayReplayStore, DistributedCacheGatewayReplayStore>()` uses
  your `IDistributedCache` (Redis, SQL Server, ...). It is best effort: `IDistributedCache` has no atomic add-if-absent,
  so two instances that receive the same URL at the same moment can both accept it. For exactly-once across instances,
  implement `IGatewayReplayStore.TryConsumeAsync` on your store's own atomic operation (Redis `SET NX`, a unique key).
- or sets `OneTimeUploadTokens = false`, so an upload URL works until it expires.

## Keep tokens out of your logs

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
- **Outside the app**: W3C logging, reverse proxies, load balancers and CDNs log request lines. Redact the segment
  after `BasePath` there too.

The gateway itself logs nothing.

## Limits

- At most 7 days of validity, like the native URLs.
- An upload is one `PUT` of the whole body: no multipart, no resume. Large uploads are bounded by `MaxUploadBytes` and
  by the provider (see its README).
- `Content-Type` on a typed upload URL must match exactly, parameters included: send `SignedUrl.Headers`.
- Downloads are always `attachment`.

Moving from 2026? See the
[migration guide](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md); this package is
new in 2027.0.0.
