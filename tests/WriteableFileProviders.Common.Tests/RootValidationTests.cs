using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Common.Tests;

/// <summary>
/// The root a provider is built with goes through the same rules as a caller's path, once, in the base class: a root
/// with a dot segment would otherwise name a place outside itself on a store that resolves dot segments.
/// </summary>
[TestClass]
public sealed class RootValidationTests
{
    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("a/..")]
    [DataRow("tenants/../other")]
    [DataRow("../secrets")]
    [DataRow("./x")]
    [DataRow("%2e%2e")]
    [DataRow("a/%2E%2E/b")]
    [DataRow("%252e%252e")]
    [DataRow("a%2F..%2Fb")]
    [DataRow(@"a\..\b")]
    [DataRow(" ")]
    [DataRow(". .")]
    [DataRow("x\u0001y")]
    public void ARootAPathCouldNotHold_IsRefusedWhenTheProviderIsBuilt(string root)
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() => new StubProvider(new StubOptions { RootDirectoryPath = root }));
        Assert.AreEqual("options", e.ParamName);
        StringAssert.Contains(e.Message, "RootDirectoryPath");
    }

    [TestMethod]
    public void RootSegmentsAreCheckedAsTheyAreGiven()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new StubProvider(new StubOptions { RootDirectorySegments = ["a", ""] }));
        Assert.ThrowsExactly<ArgumentException>(() => new StubProvider(new StubOptions { RootDirectorySegments = ["a", null!] }));
        Assert.ThrowsExactly<ArgumentException>(() => new StubProvider(new StubOptions { RootDirectorySegments = [@"a\.."] }));
        var e = Assert.ThrowsExactly<ArgumentException>(() => new StubProvider(new StubOptions { RootDirectorySegments = ["a", "x\u0001y"] }));
        Assert.IsFalse(e.Message.Contains('\u0001', StringComparison.Ordinal), "A control character is not printed");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("a")]
    [DataRow("tenants/acme")]
    [DataRow("/tenants/acme/")]
    [DataRow("a%20b")]
    [DataRow(".hidden/a.b")]
    [DataRow("..a/b..")]
    public void AValidRoot_IsKeptAsItWasGiven(string? root)
    {
        var options = new StubOptions { RootDirectoryPath = root };
        var segments = options.RootDirectorySegments;
        using var provider = new StubProvider(options);
        CollectionAssert.AreEqual(segments, provider.Options.RootDirectorySegments);
    }

    [TestMethod]
    public void TheProvidersOwnRule_AppliesToTheRoot()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ColonRefusingProvider(new StubOptions { RootDirectoryPath = "a:b" }));
        Assert.ThrowsExactly<ArgumentException>(() => new ColonRefusingProvider(new StubOptions { RootDirectoryPath = "a%3Ab" }));
        using var provider = new ColonRefusingProvider(new StubOptions { RootDirectoryPath = "a/b" });
        Assert.AreEqual("a/b/", provider.Options.RootDirectoryPath);
    }

    [TestMethod]
    public void AProviderWhoseRootIsNoStorePath_ChecksItItself()
    {
        using var provider = new LocalDiskLikeProvider(new StubOptions { RootDirectoryPath = "../x" });
        CollectionAssert.AreEqual(new[] { "..", "x" }, provider.Options.RootDirectorySegments);
    }

    [TestMethod]
    public void AStoredConfiguration_WithADotSegmentRoot_NoLongerBuilds()
    {
        var json = """{ "Type": "StubProvider", "RootDirectoryPath": "tenants/../other" }""";
        var e = Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson(json, typeof(StubProvider).Assembly));
        StringAssert.Contains(e.Message, "RootDirectoryPath");
    }

    /// <summary>A provider whose own rule refuses <c>:</c>, as Physical and SharePoint do; the rule needs no state.</summary>
    private sealed class ColonRefusingProvider(StubOptions options) : WriteableFileProvider<StubOptions>(options)
    {
        protected override bool IsSegmentAllowed(string part) => !part.Contains(':', StringComparison.Ordinal);

        protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A provider whose root is a folder of the disk, as Physical's is: the base class leaves it alone.</summary>
    private sealed class LocalDiskLikeProvider(StubOptions options) : WriteableFileProvider<StubOptions>(options)
    {
        protected override bool ValidatesRootDirectory => false;

        protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
