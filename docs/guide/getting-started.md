# Getting started

Four short steps in one project: files on the local disk, download links for them, the same code on a cloud store,
and one provider or several. The [README](../../README.md) lists every operation and provider; this page puts them to
work.

One project carries the four steps: `dotnet new web -o Files`, then, in `Files`,
`dotnet add package WriteableFileProviders.Physical`. Each step's code is its `Program.cs`, run with `dotnet run`.

## 1. Files on the local disk

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Physical;

await using var files = new WriteablePhysicalFileProvider("files");

await files.UpdateFileAsync("reports/2026.csv", new MemoryStream("year,total\n2026,42\n"u8.ToArray()));

await using (var csv = await files.ReadFileAsync("reports/2026.csv"))
using (var reader = new StreamReader(csv))
    Console.WriteLine(await reader.ReadToEndAsync());

await foreach (var entry in files.EnumerateAsync("reports"))
    Console.WriteLine($"{entry.Name}: {entry.Length} bytes");
```

Paths are relative to the provider's root, the `files` folder, and never reach outside it (symbolic links inside it
are followed: [Paths](paths-and-listings.md#paths)). A write is atomic, so a write that fails leaves the old
content, and it creates the folders above the file.

## 2. Download links for local files

The gateway in `WriteableFileProviders.AspNetCore` serves signed links for any store, the local disk included. In the
same project, where step 1 wrote `files/reports/2026.csv`, `dotnet add package WriteableFileProviders.AspNetCore`, and
`Program.cs` becomes:

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AspNetCore;
using WriteableFileProviders.Physical;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWriteableFileProvider(_ => new WriteablePhysicalFileProvider("files"));
builder.Services.AddWriteableFileProviderGateway(new Uri("http://localhost:5000"));
var app = builder.Build();
app.MapWriteableFileProviderGateway();
app.MapGet("/link/{**path}", async (string path, IWriteableFileUrlService urls) =>
    (await urls.GetSignedUrlAsync(path, new SignedUrlOptions { Validity = TimeSpan.FromHours(1) })).Url.AbsoluteUri);
app.Run();
```

Run it with `dotnet run --urls http://localhost:5000` and open `/link/reports/2026.csv`: the answer is a link that
downloads the file for an hour.

- The `Uri` is the gateway's public address, the URL the app is reached at and every link is built from: `https`, or
  plain `http` to a loopback host such as `localhost`, as here.
- `/link` hands a working link to anyone who asks. A real app checks that the caller may read the file first.

## 3. The same code on a cloud store

Only the registration changes. Register the provider from configuration:

```csharp
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));
```

```json
{
  "Files": {
    "Type": "WriteableAzureBlobFileProvider",
    "AccountName": "contoso",
    "ContainerName": "files",
    "UseDefaultAzureCredential": true
  }
}
```

With `WriteableFileProviders.AzureBlob` added, `/link` now answers with a user delegation SAS, which downloads
straight from the store, when the identity holds a Storage Blob Data role at the account (or Storage Blob Delegator);
otherwise with the gateway's link ([Authentication](../../src/WriteableFileProviders.AzureBlob/README.md#authentication)).
Another store is another `Type` and its keys, listed in its package's README.

## 4. One provider, or several

`IWriteableFileProvider` is all most apps need: register one, inject it anywhere. With several stores, tell them apart
your own way, with keyed services, `GetServices<IWriteableFileProvider>()` or a type of your own:

```csharp
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("archive",
    (_, _) => WriteableFileProviderSerializer.FromConfiguration(builder.Configuration.GetSection("Archive")));
```

Links for it name the key: `urls.GetSignedUrlAsync(path, options, providerKey: "archive")`. Every provider the gateway
serves is a [singleton](signed-urls.md#the-url-service).
