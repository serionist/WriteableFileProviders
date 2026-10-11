using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// The conformance suite on Azurite, with providers built from options as configuration builds them. Blocks of 1 MiB make
/// the large-file test stage and commit 16 blocks.
/// </summary>
[TestClass]
public sealed class AzuriteConformanceTests : ProviderConformanceTests
{
    private static readonly string _containerName = Azurite.NewContainerName();

    protected override bool KeepsContentType => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    protected override long LargeFileSize => 16L << 20;

    [ClassCleanup]
    public static Task DeleteContainer() => Azurite.DeleteContainerAsync(_containerName);

    protected override IWriteableFileProvider CreateProvider(string? root)
    {
        Azurite.CreateContainerAsync(_containerName).GetAwaiter().GetResult();
        return new WriteableAzureBlobFileProvider(Azurite.Options(_containerName, root)) { BlockSize = 1 << 20 };
    }

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableAzureBlobFileProvider(Azurite.Options(Azurite.NewContainerName()));

    // Every request fails in the pipeline, as the network can; the SDK retries what it takes for transient (a timeout).
    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { RootDirectoryPath = RootName },
            Azurite.Container(_containerName, new AzureHandler { Intercept = _ => throw failure }));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => Azurite.StoreRawAsync(_containerName, names, text);
}

/// <summary>
/// The conformance suite again, on a provider built over a container client (D17) whose pipeline refuses what Azure
/// refuses and Azurite allows (<see cref="AzureHandler"/>), with Azure's block limit lowered to 6: large writes size
/// their blocks to fit, and appends past the limit restage the file's content on the server first.
/// </summary>
[TestClass]
public sealed class AzuriteStrictConformanceTests : ProviderConformanceTests
{
    private const int BlockLimit = 6;
    private static readonly string _containerName = Azurite.NewContainerName();

    protected override bool KeepsContentType => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    protected override long LargeFileSize => 16L << 20;

    [ClassCleanup]
    public static Task DeleteContainer() => Azurite.DeleteContainerAsync(_containerName);

    protected override IWriteableFileProvider CreateProvider(string? root)
    {
        Azurite.CreateContainerAsync(_containerName).GetAwaiter().GetResult();
        var client = Azurite.Container(_containerName, new AzureHandler { MaxBlocks = BlockLimit });
        return new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { RootDirectoryPath = root }, client)
        {
            BlockSize = 1 << 20,
            MaxCommittedBlocks = BlockLimit,
            CopyRangeSize = 1 << 20,
        };
    }

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableAzureBlobFileProvider(null, Azurite.Container(Azurite.NewContainerName(), new AzureHandler()));

    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { RootDirectoryPath = RootName },
            Azurite.Container(_containerName, new AzureHandler { MaxBlocks = BlockLimit, Intercept = _ => throw failure }));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => Azurite.StoreRawAsync(_containerName, names, text);
}
