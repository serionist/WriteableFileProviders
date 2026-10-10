using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// The configuration shape (AGENTS.md, one-way doors) for SharePoint, moved here from ConfigurationShapeTests in S9: the
/// 2026 golden reads back as it did, 2027 writes the same keys plus the three D17 additions, and the provider now builds
/// from configuration with no network call (F13), so it round-trips through the serializer.
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
    public void WrittenShape_IsTheGoldenPlusTheD17Additions()
    {
        using var provider = WriteableFileProviderSerializer.FromJson(Golden());
        var written = WriteableFileProviderSerializer.ToJson(provider);
        var golden = JsonNode.Parse(Golden())!.AsObject();
        foreach (var (key, value) in golden)
            Assert.IsTrue(JsonNode.DeepEquals(value, written[key]), $"{key}: {written[key]?.ToJsonString()}");
        CollectionAssert.AreEquivalent(golden.Select(p => p.Key).Concat(_additions).ToList(), written.Select(p => p.Key).ToList());
        Assert.IsNull(written["ClientCertificateBase64"]);
        Assert.IsNull(written["ClientCertificatePassword"]);
        Assert.IsFalse(written["UseDefaultAzureCredential"]!.GetValue<bool>());
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
