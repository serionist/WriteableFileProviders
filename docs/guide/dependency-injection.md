# Dependency injection and configuration

## Register a provider

```csharp
// From configuration: built on first use, disposed by the container.
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));

// From a factory: called once; the container disposes what it returns.
builder.Services.AddWriteableFileProvider(_ => new WriteablePhysicalFileProvider("files"));

// An instance you built: the container doesn't dispose it.
builder.Services.AddSingleton<IWriteableFileProvider>(new WriteablePhysicalFileProvider("files"));
```

Then inject it:

```csharp
app.MapGet("/reports/{year}", async (int year, IWriteableFileProvider files) =>
    Results.Stream(await files.ReadFileAsync($"reports/{year}.csv"), "text/csv"));
```

`AddWriteableFileProvider` registers a singleton. It lives in `Microsoft.Extensions.DependencyInjection`, so it needs no
`using`. Each call adds a registration, as `AddSingleton` does: `IWriteableFileProvider` resolves to the last one.

### Give a provider a clock

Every provider reads the time from its `Clock`, the system clock unless you set it, once, as you build the provider.
Tests use it to check expiry without waiting:

```csharp
builder.Services.AddWriteableFileProvider(sp =>
    new WriteableAzureBlobFileProvider(options) { Clock = sp.GetRequiredService<TimeProvider>() });
```

- A provider built from configuration or JSON keeps the system clock: build it in code to give it one.
- Box and SharePoint also wait on it while they poll. A fake clock you move by hand must be moved, or the wait never
  ends.
- What each store does with it: [Signed URLs](signed-urls.md#native-urls-by-provider).

## Register several providers

Tell them apart your own way.

**Keyed services**, a name per provider:

```csharp
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("uploads",
    (_, _) => new WriteablePhysicalFileProvider("uploads"));
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("archive",
    (_, _) => WriteableFileProviderSerializer.FromConfiguration(builder.Configuration.GetSection("Archive")));

app.MapGet("/archive/{**path}", async (string path, [FromKeyedServices("archive")] IWriteableFileProvider archive) =>
    Results.Stream(await archive.ReadFileAsync(path)));
```

Pass the same key for signed URLs: `urls.GetSignedUrlAsync(path, options, providerKey: "archive")`.

**All of them:** `GetServices` returns every registration, in order.

```csharp
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Primary"));
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Replica"));

foreach (var files in app.Services.GetServices<IWriteableFileProvider>())
    await files.UpdateFileAsync("health/last-start.txt", new MemoryStream("ok"u8.ToArray()));
```

**A type of your own per role:**

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

With the gateway, register every provider it serves as a singleton ([why](signed-urls.md#the-url-service)). The
gateway serves only providers registered as `IWriteableFileProvider`, plain or keyed: for links, register a role type's
provider under a key too.

## Configure a provider

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

`Type` is the provider's class name; the other keys are its options' property names. Both ignore case.

| Provider | `Type` | Options class |
|---|---|---|
| Physical | `WriteablePhysicalFileProvider` | `PhysicalFileProviderOptions` |
| Azure Blob | `WriteableAzureBlobFileProvider` | `AzureBlobFileProviderOptions` |
| Amazon S3 | `WriteableAmazonS3FileProvider` | `AmazonS3FileProviderOptions` |
| MinIO | `WriteableMinioFileProvider` | `MinioFileProviderOptions` |
| Google Cloud Storage | `WriteableGoogleCloudStorageFileProvider` | `GoogleCloudStorageFileProviderOptions` |
| Box | `WriteableBoxFileProvider` | `BoxFileProviderOptions` |
| SharePoint | `WriteableSharePointFileProvider` | `SharePointFileProviderOptions` |

- Every provider takes `RootDirectoryPath` (or `RootDirectorySegments`), the folder it is confined to
  ([The root](paths-and-listings.md#the-root)). Each package README lists the other keys.
- Keep secrets out of the file. User secrets, environment variables or a key vault fill the same keys.

## Build a provider from configuration or JSON

```csharp
IWriteableFileProvider files = WriteableFileProviderSerializer.FromConfiguration(configuration.GetSection("Files"));
string json = WriteableFileProviderSerializer.ToJsonString(files);   // holds the options' secrets: never log it
IWriteableFileProvider same = WriteableFileProviderSerializer.FromJson(json);

WriteableFileProviderSerializer.Register<MyOwnProvider>();           // a provider from outside this library
```

- `Type` is looked up in the assemblies you pass in, then this library's providers (their package must be referenced),
  then the types given to `Register<TProvider>()`. Loaded assemblies aren't searched.
- A missing, unknown or ambiguous `Type`, or a value that doesn't convert, throws `ArgumentException`.
- Values are read leniently: a number or a boolean into a string, `"true"` and `"false"` into a boolean.
- `ToJson` returns a `JsonObject`, `ToJsonString` its text. Options new in 2027 are written only when they aren't the
  default, so a configuration that doesn't use them keeps its 2026 shape.
- Configurations stored by 2026 versions read unchanged, except a root with a dot segment (`tenants/../other`), which
  now throws. JSON written by 2027 isn't promised to read on 2026.
- A reader ignores keys it doesn't know, so an option newer than the version that reads it is dropped without an
  error: Physical's `RefuseLinks`, read by 2027.0.0, leaves links followed.
- Building does no network I/O. A provider signs in and finds its bucket, container or library on its first call, so
  those settings fail there, not at startup.
- It finds types and reads options through reflection, so its members, and `AddWriteableFileProvider(section)`, warn
  in a trimmed or native AOT app. See below.

## In a native AOT app

Bind the options with the configuration binder (its source generator runs in AOT apps) and build the provider in code:

```csharp
var options = builder.Configuration.GetSection("Files").Get<AzureBlobFileProviderOptions>()!;
builder.Services.AddWriteableFileProvider(_ => new WriteableAzureBlobFileProvider(options));
```

The keys are the same, without `Type`. Two differences from the serializer: a boolean is `true` or `false`, not `1` or
`0`, and the binder leaves Amazon S3's `StorageClassForNewFiles` at its default, so set that one in code.
GoogleCloudStorage and Box don't work in AOT apps ([Native AOT](../../README.md#native-aot)).
