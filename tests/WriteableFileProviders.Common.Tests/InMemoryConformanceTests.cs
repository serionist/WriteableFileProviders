using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

/// <summary>The conformance suite against the in-memory reference provider: what the base class's defaults deliver.</summary>
[TestClass]
public sealed class InMemoryConformanceTests : ProviderConformanceTests
{
    private static readonly string _store = "conformance-" + Guid.NewGuid().ToString("N");

    protected override bool HasRealFolders => true;

    protected override bool KeepsContentType => true;

    protected override long LargeFileSize => 8L * 1024 * 1024;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    [ClassCleanup]
    public static void ClearStore() => InMemoryFileProvider.ClearStore(_store);

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new InMemoryFileProvider(new InMemoryFileProviderOptions { StoreName = _store, RootDirectoryPath = root });

    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure)
    {
        var provider = (InMemoryFileProvider)CreateProvider(RootName);
        provider.Failure = failure;
        return provider;
    }

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text)
    {
        using var store = (InMemoryFileProvider)CreateProvider(null);
        store.StoreRaw(names, Encoding.UTF8.GetBytes(text));
        return Task.FromResult(true);
    }
}
