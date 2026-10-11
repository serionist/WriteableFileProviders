using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>The root a provider was built with, offline: a later change to the options does not move it.</summary>
[TestClass]
public sealed class AzureRootTests
{
    [TestMethod]
    public async Task TheRoot_SetAfterTheBuild_DoesNotMoveTheRequests()
    {
        var handler = new StubHandler(_ => StubHandler.BlobProperties());
        var options = new AzureBlobFileProviderOptions { AccountName = "account", AccountKey = Azurite.AccountKey, ContainerName = "files", RootDirectoryPath = "tenants/acme" };
        using var provider = new WriteableAzureBlobFileProvider(options, AzureBlobOptionsTests.Offline(handler), null);
        options.RootDirectoryPath = "../secrets";
        provider.Options.RootDirectorySegments[0] = "..";
        await provider.GetFileInfoAsync("x.txt");
        Assert.AreEqual("/files/tenants/acme/x.txt", handler.Requests.Single().RequestUri!.AbsolutePath);
    }
}
