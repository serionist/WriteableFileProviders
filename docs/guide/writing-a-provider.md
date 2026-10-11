# Writing a provider

A provider for another store derives from `WriteableFileProvider<TOptions>`, in `WriteableFileProviders.Common`. The
base class validates and resolves every path, protects the root, checks moves, copies and conditions, keeps the error
contract, and calls the protected `...CoreAsync` members you implement. Everything a consumer calls is the base's.

## The members to implement

```csharp
using System.Runtime.CompilerServices;
using WriteableFileProviders;

public sealed class MyStoreOptions : FileProviderOptions
{
    public string? ConnectionString { get; set; }
}

public sealed class MyStoreFileProvider(MyStoreOptions options) : WriteableFileProvider<MyStoreOptions>(options)
{
    // A file, a folder or a missing path: WriteableFileInfo, WriteableFileInfo.Folder or WriteableFileInfo.Missing.
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // One level of a folder; nothing for a missing one. Leave out names IsAddressable refuses.
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(
        ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    // The file's content; NotFound(path) when it is missing.
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // Overwrite or append (options.Mode), atomically, honouring ContentType, IfMatch and IfNotExists.
    protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // A file, or a folder with everything in it; false when nothing was there.
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
```

A `ResolvedPath` is a path the base has validated and placed under the root: `AbsoluteSegments` names it in the store,
`RelativeSegments` and `Path` relative to the root.

For the serializer and `AddWriteableFileProvider` to build it from configuration, give the provider a public
constructor that takes only its `TOptions` (a class with a public parameterless constructor and settable properties)
and does no network I/O, and register it: `WriteableFileProviderSerializer.Register<MyStoreFileProvider>()`, or pass
its assembly to the serializer ([Dependency injection and configuration](dependency-injection.md#the-serializer)).

## Optional overrides

Each has a default that works on any store, through the members above; override it where the store does better:
`ReadRangeCoreAsync`, `OpenReadSeekableCoreAsync`, `CopyCoreAsync`, `MoveCoreAsync` (the default copies, then deletes
exactly the source), `DeleteFileCoreAsync`, `FolderExistsCoreAsync`, `EnumerateRecursiveCoreAsync`,
`GetSignedUrlCoreAsync` (with `SupportsSignedUrl`), `WatchCore`, `IsNotFound`, `IsAccessDenied`,
`IsSegmentAllowed`, `ValidatesRootDirectory`, `Dispose(bool)` and `DisposeAsyncCore`.

What the store can do is declared through these members. Each default makes the base class refuse or weaken an
operation until the provider says otherwise:

| Member | Default | What the default does |
|---|---|---|
| `SupportsAtomicPreconditions` | false | The base checks `IfMatch` and `IfNotExists` just before the write: checked, not atomic. |
| `IsCaseInsensitive` | false | Moving `a.txt` to `A.txt` is an ordinary move. When true it is a rename: the destination check is skipped, and the default `MoveCoreAsync` goes through a temporary name. |
| `SupportsSignedUrl(access)` | false | `GetSignedUrlAsync` throws `NotSupportedException`. |
| `SupportsSignedUrlContentHeaders(access)` | false | A signed URL request that sets `DownloadFileName` or `ContentType` throws `NotSupportedException`. |
| `MaxSignedUrlValidity` | 7 days | A longer validity throws `NotSupportedException`. |
| `TimeProvider` (property) | `TimeProvider.System` | The clock for expiry times; a test can set another. |

## Helpers

- **`AppendByRewriteAsync`**: an append for a store without one, conditional on the version read.
- **`SpoolToTempFileAsync`**: copies a stream to a local temp file, for an SDK that needs the length or a seekable
  stream.
- **`NotFound(path)`**: the `FileNotFoundException` to throw for a missing file, naming the path under the root.
- **`ReadWithContractAsync`**: a read by a public member of your own, under the same error contract as
  `ReadFileAsync`.
- **`IsAddressable(name)`**: true when the provider's own path validation (`IsSegmentAllowed` included) would accept
  the name as one path segment.

## The rules the base cannot enforce for you

- **ETags.** Every file needs an `ETag`: the default seekable read pins it, and `AppendByRewriteAsync` writes with
  `IfMatch` on it. Without one, a pinned seekable read and an append by rewrite throw `NotSupportedException`.
- **Listings** leave out names the provider's own path validation would refuse, as every built-in provider does, so
  that everything listed can be read back: skip an entry whose name `IsAddressable` refuses, and do not walk into such
  a folder. Hide your own temporary names, and only those.
- **Errors.** An exception that escapes a `...CoreAsync` member, a stream or a listing it returns, and is not one of
  the [contract's types](errors.md), becomes one, with it inside: what `IsNotFound` recognises becomes
  `FileNotFoundException`, what `IsAccessDenied` recognises `UnauthorizedAccessException`, and anything else
  `IOException`, a cancellation your caller's token did not ask for (a timeout) included. Override the two for your
  SDK's exceptions, and throw a contract type yourself where you know better: it passes through as it is.
- **The root** is checked when the provider is built, as a path is, and kept: read it through
  `Resolve(ProviderPath.Root)`, not from the options, which a caller can still change. A provider whose root is not a
  path in its store (a folder of the disk) overrides `ValidatesRootDirectory` to false and checks its root itself. The
  base constructor calls `IsSegmentAllowed` and `ValidatesRootDirectory` before your constructor runs, so they must not
  depend on your provider's state.
- **Local streams** are not customisable: `CreateLocalFileStreamAsync` spools through `ReadFileCoreAsync` and commits
  through `UpdateFileCoreAsync`.

## Testing it

`ProviderConformanceTests`, in the repository's `tests/WriteableFileProviders.Testing`, is the contract: one test per
rule, which every built-in provider passes. Derive a test class from it, say how to build your provider and what its
store can do (real folders, case, signed URLs, the large-file size), and it runs every rule against your store.
