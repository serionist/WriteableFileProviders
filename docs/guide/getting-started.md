# Getting started

Four steps in one app: local files, download links, a cloud store, then several stores.

```
dotnet new web -o Files
cd Files
dotnet add package WriteableFileProviders.Physical
```

Steps 1 and 2 are each a whole `Program.cs`; steps 3 and 4 change step 2's. Run it with `dotnet run`.

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

Paths are relative to the provider's root, the `files` folder, and can't reach outside it
([Paths](paths-and-listings.md#paths)). Writes are atomic and create missing folders.

## 2. Download links for local files

```
dotnet add package WriteableFileProviders.AspNetCore
```

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

Run `dotnet run --urls http://localhost:5000` and open `http://localhost:5000/link/reports/2026.csv` (the file step 1
wrote). You get a link that downloads the file for an hour.

- The `Uri` is the gateway's public address, which every link starts with. Use `https`, or `http` only on a loopback
  host such as `localhost`.
- `/link` hands a link to anyone who asks. In a real app, check that the caller may read the file first.

## 3. The same code on a cloud store

Change only the registration, and describe the store in `appsettings.json`:

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

Add `WriteableFileProviders.AzureBlob`, and `/link` returns a user delegation SAS that downloads straight from Azure.
That needs a Storage Blob Data role at the account, or Storage Blob Delegator. Without it you get a gateway link
instead ([AzureBlob README](../../src/WriteableFileProviders.AzureBlob/README.md)).

Another store is another `Type` and its keys, listed in its package README.

## 4. Several stores

```csharp
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("archive",
    (_, _) => WriteableFileProviderSerializer.FromConfiguration(builder.Configuration.GetSection("Archive")));
```

Inject it with `[FromKeyedServices("archive")]`, and pass the key for links:
`urls.GetSignedUrlAsync(path, options, providerKey: "archive")`. Register every provider the gateway serves as a
singleton ([why](signed-urls.md#the-url-service)). Other ways to register several:
[Dependency injection](dependency-injection.md#register-several-providers).
