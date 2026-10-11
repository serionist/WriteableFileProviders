using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// The configuration shape (AGENTS.md, one-way doors) for SharePoint, beside ConfigurationShapeTests in Common.Tests: the
/// 2026 golden builds a provider with no network call and round-trips through the serializer; a configuration that uses
/// none of the 2027 sign-in options writes exactly the 2026 keys, and one that uses them writes them too.
/// </summary>
[TestClass]
public sealed class SharePointConfigurationShapeTests
{
    private static readonly string[] _additions = ["ClientCertificateBase64", "ClientCertificatePassword", "UseDefaultAzureCredential"];

    private static string Golden() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "WriteableSharePointFileProvider.json"));

    private static SharePointFileProviderOptions Expected() =>
        new("https://tenant.sharepoint.example/sites/files", "Documents", "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002", "client-secret", "Main/Work")
        {
            RootDirectoryPath = "root/dir",
        };

    private static void AssertSame(SharePointFileProviderOptions expected, FileProviderOptions actual)
    {
        var read = (SharePointFileProviderOptions)actual;
        Assert.AreEqual(expected.SiteUrl, read.SiteUrl);
        Assert.AreEqual(expected.DocumentLibraryName, read.DocumentLibraryName);
        Assert.AreEqual(expected.TenantId, read.TenantId);
        Assert.AreEqual(expected.ClientId, read.ClientId);
        Assert.AreEqual(expected.ClientSecret, read.ClientSecret);
        Assert.AreEqual(expected.WorkFolderPath, read.WorkFolderPath);
        Assert.AreEqual(expected.RootDirectoryPath, read.RootDirectoryPath);
        CollectionAssert.AreEqual(expected.RootDirectorySegments, read.RootDirectorySegments);
        Assert.AreEqual(expected.ClientCertificateBase64, read.ClientCertificateBase64);
        Assert.AreEqual(expected.ClientCertificatePassword, read.ClientCertificatePassword);
        Assert.AreEqual(expected.UseDefaultAzureCredential, read.UseDefaultAzureCredential);
    }

    [TestMethod]
    public void Golden_BuildsAProviderOffline_AndRoundTrips()
    {
        using var provider = WriteableFileProviderSerializer.FromJson(Golden());
        Assert.IsInstanceOfType<WriteableSharePointFileProvider>(provider);
        AssertSame(Expected(), provider.Options);
        using var again = WriteableFileProviderSerializer.FromJson(WriteableFileProviderSerializer.ToJson(provider));
        AssertSame(Expected(), again.Options);
    }

    [TestMethod]
    public void WrittenShape_IsTheGolden_UntilThe2027OptionsAreUsed()
    {
        using var provider = WriteableFileProviderSerializer.FromJson(Golden());
        var written = WriteableFileProviderSerializer.ToJson(provider);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(Golden()), written), $"Written: {written.ToJsonString()}");

        var options = Expected();
        options.ClientCertificateBase64 = "certificate";
        options.ClientCertificatePassword = "password";
        options.UseDefaultAzureCredential = true;
        using var client = TestGraph.Client(new GraphFake());
        using var withAdditions = new WriteableSharePointFileProvider(options, client);
        var writtenWithAdditions = WriteableFileProviderSerializer.ToJson(withAdditions);
        CollectionAssert.IsSubsetOf(_additions, writtenWithAdditions.Select(p => p.Key).ToList());
        Assert.AreEqual("certificate", writtenWithAdditions["ClientCertificateBase64"]!.GetValue<string>());
        Assert.IsTrue(writtenWithAdditions["UseDefaultAzureCredential"]!.GetValue<bool>());
        using var readBack = WriteableFileProviderSerializer.FromJson(writtenWithAdditions);
        Assert.AreEqual("password", ((SharePointFileProviderOptions)readBack.Options).ClientCertificatePassword);
    }

    [TestMethod]
    public void Configuration_ReadsTheSameShape_AndTheAdditions()
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in JsonNode.Parse(Golden())!.AsObject())
        {
            if (value is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    values[$"{key}:{i}"] = array[i]!.GetValue<string>();
            }
            else if (value is not null)
            {
                values[key] = value.ToString();
            }
        }
        using (var provider = WriteableFileProviderSerializer.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()))
            AssertSame(Expected(), provider.Options);

        values["usedefaultazurecredential"] = "true";
        values["ClientCertificatePassword"] = "p";
        using var withAdditions = WriteableFileProviderSerializer.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var options = (SharePointFileProviderOptions)withAdditions.Options;
        Assert.IsTrue(options.UseDefaultAzureCredential);
        Assert.AreEqual("p", options.ClientCertificatePassword);
    }
}
