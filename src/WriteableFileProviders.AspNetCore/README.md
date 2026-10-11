# WriteableFileProviders.AspNetCore

Signed URLs for every [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme) provider, in
an ASP.NET Core app:

- **`IWriteableFileUrlService`** issues a URL that lets its holder download or upload one file for a limited time. It
  asks the provider for its native URL first (an Azure SAS, an S3 or MinIO presigned URL, a GCS V4 signed URL, a Box or
  SharePoint download URL), and when the provider cannot sign that URL it issues a gateway URL instead.
- **The gateway** is a set of endpoints in your app that serve those URLs: `GET` and `HEAD` download a file, with HTTP
  ranges, and `PUT` uploads one. It works for any provider, Physical included.

## Install

```
dotnet add package WriteableFileProviders.AspNetCore
```

It needs .NET 10 and ASP.NET Core, and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in
an application must be on the same major version: a `2026.10.10.x` package does not load against
`WriteableFileProviders.Common` 2027.

## Use

The shortest form, download links for files in a local folder, is step 2 of
[Getting started](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/getting-started.md#2-download-links-for-local-files).
An app that decides who may read each file:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using WriteableFileProviders;
using WriteableFileProviders.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();   // or your own scheme
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IReportAccess, ReportAccess>();
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));
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
    var url = await urls.GetSignedUrlAsync($"reports/{id}.pdf", new SignedUrlOptions
    {
        Validity = TimeSpan.FromMinutes(15),
        DownloadFileName = $"Report {id}.pdf",
        ContentType = "application/pdf",
    });
    return Results.Ok(url.Url);
}).RequireAuthorization();

app.Run();

// Your own rule for who may read which report.
interface IReportAccess
{
    Task<bool> CanReadAsync(ClaimsPrincipal user, string id);
}

sealed class ReportAccess : IReportAccess
{
    public Task<bool> CanReadAsync(ClaimsPrincipal user, string id) => Task.FromResult(user.IsInRole("reports"));
}
```

`ReportAccess` stands for your own check that this user may see this report. The gateway's URLs need no sign-in:
whoever holds one can use it until it expires, so the endpoint that hands them out is where access is decided.

An upload URL works the same way: `Access = SignedUrlAccess.Create` (only if the file does not exist yet) or
`SignedUrlAccess.Write` (create or replace). The client sends `PUT` to `SignedUrl.Url` with the headers in
`SignedUrl.Headers`. For a provider registered under a key (`AddKeyedSingleton<IWriteableFileProvider>(key, ...)`),
pass the key: `GetSignedUrlAsync(path, options, providerKey)`. Every provider the gateway serves, default or keyed,
must be a singleton ([why](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#the-url-service)).

## Configuration

`AddWriteableFileProviderGateway(new Uri("https://files.example.com/"))` takes the defaults below with that
`PublicBaseUri`; `AddWriteableFileProviderGateway(options => ...)` sets any of them:

| Option | Default | What it does |
|---|---|---|
| `PublicBaseUri` | required | The absolute URL the gateway is reachable at from outside, including any path base behind a proxy. Gateway URLs are built from it, never from the request's `Host`, so a forged header cannot redirect them. `https`, or `http` on a loopback host. Use a host name of its own ([a host of its own](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#security-headers-and-a-host-of-its-own)). |
| `BasePath` | `/files` | Where the endpoints are mapped. Pick one that does not clash with your routes. |
| `MaxReadValidity` | 7 days | The longest a gateway download URL lives. At most 7 days. |
| `MaxUploadValidity` | 1 hour | The longest a gateway upload URL lives. At most 7 days. |
| `OneTimeUploadTokens` | `true` | A gateway upload URL works for one upload ([one-time uploads](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md#one-time-uploads)). |
| `MaxUploadBytes` | 100 MiB | The largest upload body. For its uploads, the gateway sets the server's request body limit to this value where the server lets an app change it; a limit outside the app (a proxy's, IIS request filtering) is yours to raise. |
| `PreferGateway` | `false` | Always issue gateway URLs, even for providers that sign natively. |
| `AllowInsecureHttp` | `false` | Accept a plain `http` `PublicBaseUri` on a host that is not loopback. Tokens are credentials: do not. |

A validity longer than the maximum throws `NotSupportedException`, or, with `SignedUrlOptions.AllowShorterValidity`,
gives a URL that lives the maximum, with its true `ExpiresOn`. The options are validated when the app starts.

## Before production

The guide's [Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md)
page covers which URL you get, the endpoints' status codes and headers, and these, which a production app settles:

- **A host of its own** for the gateway, which serves whatever was uploaded.
- **The Data Protection key ring**, shared by every instance, or a URL issued by one instance answers 404 on another;
  keys kept only in memory lose every issued URL on restart.
- **One-time uploads across instances:** the default store of used upload tokens holds on one instance only.
- **Tokens in logs:** a token is a bearer credential in the URL path, which request logs, traces and proxies record.

## Limits

- At most 7 days of validity, like the native URLs.
- An upload is one `PUT` of the whole body: no multipart, no resume. Large uploads are bounded by `MaxUploadBytes` and
  by the provider (see its README).
- `Content-Type` on a typed upload URL must match, parameters included (case aside): send `SignedUrl.Headers`.
- Downloads are always `attachment`.

## Learn more

- [Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md): native URLs
  provider by provider, the gateway's endpoints, security headers, tokens, one-time uploads and logs.
- [Providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md#urls-copies-notifications):
  which provider signs which URLs natively.
- [Dependency injection](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md):
  one provider or several.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#new-package-writeablefileprovidersaspnetcore):
  this package is new in 2027.0.0.
