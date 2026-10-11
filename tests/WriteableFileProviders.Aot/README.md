# WriteableFileProviders.Aot

The native AOT test. An app, not a test project: only publishing an executable runs the native AOT compiler, and only
the compiler sees into the SDKs our packages call. The analyzers in `src/` check our own code only.

CI (`ci.yml`, the Native AOT job) does two things:

1. **Publishes it with native AOT.** Every member of each package is kept (`TrimmerRootAssembly`), so the compiler
   analyses all of it and the SDK code it reaches. Any trim or AOT warning fails the publish.
2. **Runs the binary**, which uses each package for real and exits with 1 on the first failure (`Program.cs`):
   options bound by the configuration binder, Physical, the gateway on Kestrel, SharePoint on the tests' in-memory
   Graph (`GraphFake.cs`, linked), and Azure Blob, Amazon S3 and MinIO on Azurite and MinIO, signed URLs included.

GoogleCloudStorage and Box are left out: their SDKs use reflection-based JSON, so they are not AOT compatible.

One warning is expected and does not fail the publish: `IL2104` for `Microsoft.IO.RecyclableMemoryStream`, which
Microsoft Graph brings through Kiota. ASP.NET Core turns EventSource on in AOT apps, and that package's EventSource
logs primitives through `WriteEvent(int, object[])`.

## Run it

Needs the native AOT prerequisites ([Microsoft Learn](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites)).
On Linux:

```
dotnet restore tests/WriteableFileProviders.Aot
dotnet publish tests/WriteableFileProviders.Aot --no-restore -c Release -r linux-x64 -o artifacts/aot
WFP_AOT_AZURITE=127.0.0.1:10000 WFP_AOT_MINIO=127.0.0.1:9000 artifacts/aot/WriteableFileProviders.Aot
```

Restore first: `publish -r` would restore every project for that runtime, which their lock files don't hold. Without
the two variables, Azure Blob, S3 and MinIO are skipped; CI starts both emulators. `dotnet run` runs the same checks
without AOT.
