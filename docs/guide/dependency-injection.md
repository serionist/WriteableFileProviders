# Dependency injection and configuration

A provider is an ordinary service: register it as `IWriteableFileProvider`, inject it, and the code that reads and
writes never names the store. The store can then come from configuration, so moving from the local disk to a cloud
store is a change of settings.

## One provider

```csharp
// From configuration (see below): the container builds it on first use and disposes it.
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));

// Or from code, with a factory the container calls once; it disposes what the factory built.
builder.Services.AddWriteableFileProvider(_ => new WriteablePhysicalFileProvider("files"));

// Or an instance you built; the container does not dispose it, since it did not create it.
builder.Services.AddSingleton<IWriteableFileProvider>(new WriteablePhysicalFileProvider("files"));
```

Then inject it:

```csharp
app.MapGet("/reports/{year}", async (int year, IWriteableFileProvider files) =>
    Results.Stream(await files.ReadFileAsync($"reports/{year}.csv"), "text/csv"));
```

`AddWriteableFileProvider` is in the `Microsoft.Extensions.DependencyInjection` namespace, beside `AddSingleton`, so it
needs no `using` of its own. Each call adds a registration, as `AddSingleton` does: injecting `IWriteableFileProvider`
gives the last one registered.

## Several providers

When an app uses more than one store, it tells them apart its own way. Three common ones:

**Keyed services**, a name per provider:

```csharp
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("uploads",
    (_, _) => new WriteablePhysicalFileProvider("uploads"));
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("archive",
    (_, _) => WriteableFileProviderSerializer.FromConfiguration(builder.Configuration.GetSection("Archive")));

app.MapGet("/archive/{**path}", async (string path, [FromKeyedServices("archive")] IWriteableFileProvider archive) =>
    Results.Stream(await archive.ReadFileAsync(path)));
```

The signed-URL service takes the same key, and the gateway serves the URL from that provider:
`urls.GetSignedUrlAsync(path, options, providerKey: "archive")` ([Signed URLs](signed-urls.md#the-url-service)).

**Every provider at once:** each `AddWriteableFileProvider` call adds one, and `GetServices` returns them all, in the
order they were registered:

```csharp
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Primary"));
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Replica"));

foreach (var files in app.Services.GetServices<IWriteableFileProvider>())
    await files.UpdateFileAsync("health/last-start.txt", new MemoryStream("ok"u8.ToArray()));
```

**A type of your own per role,** when a key feels too loose:

```csharp
public sealed class ArchiveFiles(IWriteableFileProvider files) : IDisposable, IAsyncDisposable
{
    public IWriteableFileProvider Files => files;
    public void Dispose() => files.Dispose();
    public ValueTask DisposeAsync() => files.DisposeAsync();
}

builder.Services.AddSingleton(_ => new ArchiveFiles(
    WriteableFileProviderSerializer.FromConfiguration(builder.Configuration.GetSection("Archive"))));
```

**With the gateway, register every provider as a singleton**, whichever way you tell them apart:
`AddWriteableFileProvider`, `AddSingleton` or `AddKeyedSingleton`, never scoped or transient, unless it resolves to
the same store in every scope. The URL service resolves the provider from the root container when it issues a URL,
and the gateway from the request's services when it redeems one, so a provider per scope, such as one per tenant,
would let a URL be redeemed against another tenant's store. The gateway serves only providers registered as
`IWriteableFileProvider`, plain or keyed: links for a provider held by a type of your own need it registered under a
key too.

## Configuration

A provider is described by its `Type`, the provider's class name, and its options' property names, both read without
regard to case:

```json
{
  "Files": {
    "Type": "WriteableAzureBlobFileProvider",
    "AccountName": "contoso",
    "ContainerName": "files",
    "UseDefaultAzureCredential": true,
    "RootDirectoryPath": "tenants/acme"
  }
}
```

| Provider | `Type` | Options class (its properties are the keys) |
|---|---|---|
| Physical | `WriteablePhysicalFileProvider` | `PhysicalFileProviderOptions` |
| Azure Blob | `WriteableAzureBlobFileProvider` | `AzureBlobFileProviderOptions` |
| Amazon S3 | `WriteableAmazonS3FileProvider` | `AmazonS3FileProviderOptions` |
| MinIO | `WriteableMinioFileProvider` | `MinioFileProviderOptions` |
| Google Cloud Storage | `WriteableGoogleCloudStorageFileProvider` | `GoogleCloudStorageFileProviderOptions` |
| Box | `WriteableBoxFileProvider` | `BoxFileProviderOptions` |
| SharePoint | `WriteableSharePointFileProvider` | `SharePointFileProviderOptions` |

Every provider takes `RootDirectoryPath` (or the same as segments, `RootDirectorySegments`), the folder it is confined
to. Each package's README lists its keys. Keep secrets out of the file itself: user secrets, environment variables or
a key vault fill the same keys through .NET configuration.

## The serializer

`WriteableFileProviderSerializer` builds a provider from configuration or JSON, and writes one back as JSON, the shape
stored configurations keep:

```csharp
IWriteableFileProvider files = WriteableFileProviderSerializer.FromConfiguration(configuration.GetSection("Files"));
string json = WriteableFileProviderSerializer.ToJsonString(files);   // holds the options' secrets: never log it
IWriteableFileProvider same = WriteableFileProviderSerializer.FromJson(json);

WriteableFileProviderSerializer.Register<MyOwnProvider>();           // a provider from outside this library
```

- **Finding the type:** a `Type` is looked up among the assemblies passed in, then this library's own providers (their
  package must be referenced; its assembly is loaded when needed), then the types registered with
  `Register<TProvider>()`. Loaded assemblies are not searched, so a provider of your own is registered or its assembly
  passed in. An unknown, missing or ambiguous `Type`, or a value that does not convert, throws `ArgumentException`.
- **Values** are read leniently, as the 2026 versions read them: a number or a boolean into a string, the strings
  `"true"` and `"false"` into a boolean.
- **`ToJson`** returns a `System.Text.Json.Nodes.JsonObject` and `ToJsonString` its text. Options added in 2027 are
  written only when they differ from their defaults, so a configuration that uses none of them keeps its 2026 shape.
- **Stored configurations** written by the 2026 versions read unchanged, except a root with a dot segment
  (`tenants/../other`), which no longer builds ([Paths](paths-and-listings.md#the-root)). JSON written by 2027 is not
  promised to read back on 2026.
- **Building does no network I/O** on any provider: a provider signs in and finds its bucket, container or library on
  its first call, so a configuration that is wrong in those ways fails there rather than at startup.
