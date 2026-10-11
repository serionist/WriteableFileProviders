using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

/// <summary>
/// The conformance suite against the in-memory provider configured as Box and SharePoint behave: names that differ only in
/// case are one name, and signed URLs have a fixed lifetime. It covers the base class's case-only rename, and URLs
/// whose lifetime the store sets: refused when they would outlive what was asked, shorter only when the caller accepts
/// it.
/// </summary>
[TestClass]
public sealed class InMemoryVariantConformanceTests : ProviderConformanceTests
{
    private static readonly string _store = "conformance-variant-" + Guid.NewGuid().ToString("N");

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool KeepsContentType => true;

    protected override long LargeFileSize => 1L * 1024 * 1024;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    protected override TimeSpan? FixedSignedUrlLifetime => TimeSpan.FromMinutes(15);

    [ClassCleanup]
    public static void ClearStore() => InMemoryFileProvider.ClearStore(_store);

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new InMemoryFileProvider(new InMemoryFileProviderOptions
        {
            StoreName = _store,
            RootDirectoryPath = root,
            CaseInsensitive = true,
            FixedSignedUrlLifetime = TimeSpan.FromMinutes(15),
        });

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
