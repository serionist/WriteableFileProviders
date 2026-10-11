using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// The Azure provider's configuration shape (AGENTS.md, one-way doors), past the shared goldens in
/// WriteableFileProviders.Common.Tests: the 2026 files read the same, a configuration that uses no 2027 option is written
/// exactly as 2026 wrote it, and the two new options round-trip through JSON and configuration.
/// </summary>
[TestClass]
public sealed class AzureConfigurationShapeTests
{
    private static string Golden(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    [TestMethod]
    public void Golden2026_ReadsAndWritesBackUnchanged()
    {
        using var provider = WriteableFileProviderSerializer.FromJson(Golden("WriteableAzureBlobFileProvider.json"));
        var options = ((WriteableAzureBlobFileProvider)provider).Options;
        Assert.AreEqual("account", options.AccountName);
        Assert.AreEqual("ZmFrZS1rZXk=", options.AccountKey);
        Assert.AreEqual("container", options.ContainerName);
        Assert.AreEqual("https", options.Protocol);
        Assert.AreEqual("account.blob.example.net", options.Url);
        CollectionAssert.AreEqual(new[] { "root", "dir" }, options.RootDirectorySegments);
        Assert.IsFalse(options.CreateContainerIfMissing);
        Assert.IsFalse(options.UseDefaultAzureCredential);

        var written = WriteableFileProviderSerializer.ToJson(provider);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(Golden("WriteableAzureBlobFileProvider.json")), written), written.ToJsonString());
    }

    [TestMethod]
    public void ConfigurationGolden2026_BuildsTheSameProvider()
    {
        var section = new ConfigurationBuilder().SetBasePath(Path.Combine(AppContext.BaseDirectory, "Golden"))
            .AddJsonFile("Config.WriteableAzureBlobFileProvider.json").Build().GetSection("Files");
        using var provider = (WriteableAzureBlobFileProvider)WriteableFileProviderSerializer.FromConfiguration(section);
        Assert.AreEqual("container", provider.Options.ContainerName);
        Assert.AreEqual(new Uri("https://account.blob.example.net/container"), provider.Container.Uri);
        CollectionAssert.AreEqual(new[] { "root", "dir" }, provider.Options.RootDirectorySegments);
    }

    [TestMethod]
    public void NewOptions_RoundTripThroughJson()
    {
        var options = new AzureBlobFileProviderOptions
        {
            AccountName = "account",
            ContainerName = "container",
            CreateContainerIfMissing = true,
            UseDefaultAzureCredential = true,
            RootDirectoryPath = "a/b",
        };
        using var provider = new WriteableAzureBlobFileProvider(options);
        var json = WriteableFileProviderSerializer.ToJson(provider);
        Assert.IsTrue(json["CreateContainerIfMissing"]!.GetValue<bool>());
        Assert.IsTrue(json["UseDefaultAzureCredential"]!.GetValue<bool>());
        Assert.AreEqual("WriteableAzureBlobFileProvider", json["Type"]!.GetValue<string>());

        using var read = (WriteableAzureBlobFileProvider)WriteableFileProviderSerializer.FromJson(json);
        Assert.IsTrue(read.Options.CreateContainerIfMissing);
        Assert.IsTrue(read.Options.UseDefaultAzureCredential);
        Assert.IsNull(read.Options.AccountKey);
        CollectionAssert.AreEqual(new[] { "a", "b" }, read.Options.RootDirectorySegments);
    }

    [TestMethod]
    public void NewOptions_ReadFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Type"] = "WriteableAzureBlobFileProvider",
            ["AccountName"] = "account",
            ["ContainerName"] = "container",
            ["createcontainerifmissing"] = "true",
            ["UseDefaultAzureCredential"] = "True",
        }).Build();
        using var provider = (WriteableAzureBlobFileProvider)WriteableFileProviderSerializer.FromConfiguration(configuration);
        Assert.IsTrue(provider.Options.CreateContainerIfMissing);
        Assert.IsTrue(provider.Options.UseDefaultAzureCredential);
        Assert.IsFalse(provider.Container.CanGenerateSasUri, "Signed in with a token, not a key");
    }

    [TestMethod]
    public void LenientJson_ReadsQuotedBooleansAndOtherCasing()
    {
        const string json = """{ "type": "writeableazureblobfileprovider", "accountname": "account", "AccountKey": "ZmFrZS1rZXk=", "containerName": "c", "CreateContainerIfMissing": "true" }""";
        using var provider = (WriteableAzureBlobFileProvider)WriteableFileProviderSerializer.FromJson(json);
        Assert.IsTrue(provider.Options.CreateContainerIfMissing);
        Assert.AreEqual("c", provider.Options.ContainerName);
    }
}
