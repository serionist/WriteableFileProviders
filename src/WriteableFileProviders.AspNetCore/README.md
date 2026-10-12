# WriteableFileProviders.AspNetCore

Signed download and upload URLs for every [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme)
provider. `IWriteableFileUrlService` returns the store's own URL where it can sign one (Azure SAS, S3 and MinIO
presigned, GCS V4, Box and SharePoint download URLs), else a gateway URL, served by your app for any provider.

## Install

```
dotnet add package WriteableFileProviders.AspNetCore
```

Needs .NET 10, and is native AOT compatible. Keep every WriteableFileProviders package in an app on the same
major version.

## Use

```csharp
using System.Security.Claims;
using WriteableFileProviders;
using WriteableFileProviders.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication().AddCookie();
builder.Services.AddAuthorization();
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));
builder.Services.AddWriteableFileProviderGateway(new Uri("https://files.example.com/"));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapWriteableFileProviderGateway();   // GET, HEAD, PUT /files/{token}/{fileName?}

// Whoever holds a URL can use it until it expires: decide here who gets one.
app.MapGet("/reports/{id}/link", async (string id, ClaimsPrincipal user, IWriteableFileUrlService urls) =>
{
    if (!user.IsInRole("reports"))
        return Results.Forbid();
    var url = await urls.GetSignedUrlAsync($"reports/{id}.pdf", new SignedUrlOptions
    {
        Validity = TimeSpan.FromMinutes(15),
        DownloadFileName = $"Report {id}.pdf",
    });
    return Results.Ok(url.Url);
}).RequireAuthorization();

app.Run();
```

For an upload URL, set `Access` to `SignedUrlAccess.Create` (new file only) or `SignedUrlAccess.Write`; the client
sends `PUT` to `url.Url` with `url.Headers`. For a keyed provider, pass its key:
`GetSignedUrlAsync(path, options, key)`. Every provider the gateway serves must be a singleton.

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `PublicBaseUri` | required | The absolute URL the gateway is reached at from outside, with any path base. URLs are built from it, never from the request's `Host`. `https`, or `http` on loopback. |
| `BasePath` | `/files` | Where the endpoints are mapped. |
| `MaxReadValidity` | 7 days | The longest a gateway download URL lives. At most 7 days. |
| `MaxUploadValidity` | 1 hour | The longest a gateway upload URL lives. At most 7 days. |
| `OneTimeUploadTokens` | `true` | A gateway upload URL works once. |
| `MaxUploadBytes` | 100 MiB | The largest upload; more answers 413. The gateway raises the server's body limit to match, but not a proxy's. |
| `PreferGateway` | `false` | Always issue gateway URLs, even where the store signs its own. |
| `AllowInsecureHttp` | `false` | Accept an `http` `PublicBaseUri` on a host that isn't loopback. Don't. |
| `DataProtectionPurpose` | `WriteableFileProviders.Gateway.v1` | The root Data Protection purpose of every token, for an app that names its purposes its own way. Changing it ends every URL issued under the old one. |

Set them with `AddWriteableFileProviderGateway(options => ...)`; they're validated when the app starts. A validity over
the maximum throws `NotSupportedException`, or with `AllowShorterValidity` gets the maximum.

## Before production

- **Give the gateway a host of its own** (`files.example.com`) that your cookies aren't sent to: it serves uploads.
- **Share and persist the Data Protection key ring.** Otherwise a URL from one instance answers 404 on another, and a
  restart breaks every URL issued.
- **Share the one-time upload store** across instances, or set `OneTimeUploadTokens` to `false`. The default lives in
  one instance's memory; register an `IGatewayReplayStore` such as `DistributedCacheGatewayReplayStore`.
- **Keep tokens out of logs.** The token is in the URL path. The gateway redacts it from `UseHttpLogging`; redact it
  from proxies, traces and other request logs yourself.

## Limits

- A gateway URL lives at most 7 days, as native URLs do.
- An upload is one `PUT` of the whole body: no multipart, no resume.
- An upload URL with a content type needs exactly that `Content-Type` (send `url.Headers`), or answers 415.
- Downloads are always `Content-Disposition: attachment`.

## Learn more

- [Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md): endpoints, status codes, headers, tokens, logs.
- [Dependency injection](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md): one provider or several.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md): this package is new in 2027.0.0.
