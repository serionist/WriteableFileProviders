using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AmazonS3;
using WriteableFileProviders.AzureBlob;
using WriteableFileProviders.Box;
using WriteableFileProviders.GoogleCloudStorage;
using WriteableFileProviders.Minio;
using WriteableFileProviders.Physical;
using WriteableFileProviders.SharePoint;

namespace WriteableFileProviders.Common.Tests.Serialization;

/// <summary>
/// Pins the configuration shape (AGENTS.md, one-way doors): the "Type" key and every options property name, as
/// consumers have stored them. The files in Golden/ were written by the Newtonsoft-based serializer of the 2026.10.10.x
/// line; the System.Text.Json serializer must read them as that one did. They are never regenerated.
/// </summary>
[TestClass]
public sealed class ConfigurationShapeTests
{
    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Serialization", "Golden", name));

    private static JsonObject GoldenNode(string name) => JsonNode.Parse(Golden(name))!.AsObject();

    // Fake values only: the shape matters, not the content.
    public static IEnumerable<object[]> Shapes =>
    [
        ["WriteableAzureBlobFileProvider", new AzureBlobFileProviderOptions { AccountName = "account", AccountKey = "ZmFrZS1rZXk=", ContainerName = "container", Protocol = "https", Url = "account.blob.example.net", RootDirectoryPath = "root/dir" }],
        ["WriteableAmazonS3FileProvider", new AmazonS3FileProviderOptions("root/dir", "test-access-key", "test-secret-key", "bucket", "eu-west-1", S3StorageClass.StandardInfrequentAccess)],
        ["WriteableMinioFileProvider", new MinioFileProviderOptions("root/dir", "minio.example.net:9000", "test-access-key", "test-secret-key", "bucket") { UseSSL = true }],
        ["WriteableGoogleCloudStorageFileProvider", new GoogleCloudStorageFileProviderOptions("bucket", "service@project.example", "fake-private-key", "project", "root/dir") { CredentialsJson = "{}" }],
        ["WriteableBoxFileProvider", new BoxFileProviderOptions("client-id", "client-secret", "123456", "public-key-id", "fake-private-key", "passphrase", "0") { RootDirectoryPath = "root/dir" }],
        ["WriteableSharePointFileProvider", new SharePointFileProviderOptions("https://tenant.sharepoint.example/sites/files", "Documents", "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002", "client-secret", "Main/Work") { RootDirectoryPath = "root/dir" }],
    ];

    [TestMethod]
    [DynamicData(nameof(Shapes))]
    public void GoldenReadsBackToTheSameOptions(string type, object options)
    {
        var read = JsonSerializer.Deserialize(Golden(type + ".json"), options.GetType(), OptionsJson.Options)!;
        AssertSameOptions(options, read);
    }

    [TestMethod]
    [DynamicData(nameof(Shapes))]
    public void WrittenShapeMatchesGoldenExceptStringConvertibles(string type, object options)
    {
        var written = JsonSerializer.SerializeToNode(options, options.GetType(), OptionsJson.Options)!.AsObject();
        written["Type"] = type;
        var golden = GoldenNode(type + ".json");
        // The 2026 serializer wrote S3's storage class as {"Value":...} and could not read it back (F78); 2027 writes the
        // string, which is also what 2026 reads correctly.
        if (golden["StorageClassForNewFiles"] is JsonObject storageClass)
            golden["StorageClassForNewFiles"] = storageClass["Value"]!.GetValue<string>();
        Assert.IsTrue(JsonNode.DeepEquals(golden, written), $"Written: {written.ToJsonString()}");
    }

    [TestMethod]
    public void ProvidersThatConstructOfflineRoundTripThroughTheSerializer()
    {
        foreach (var name in new[] { "WriteableAzureBlobFileProvider", "WriteableMinioFileProvider", "WriteableAmazonS3FileProvider" })
        {
            var expected = Shapes.Single(s => (string)s[0] == name)[1];
            using var provider = WriteableFileProviderSerializer.FromJson(Golden(name + ".json"));
            Assert.AreEqual(name, provider.GetType().Name);
            AssertSameOptions(expected, provider.Options);
            using var again = WriteableFileProviderSerializer.FromJson(WriteableFileProviderSerializer.ToJson(provider));
            AssertSameOptions(expected, again.Options);
        }
    }

    [TestMethod]
    public void S3StorageClassReadsFromBothFormsAndWritesTheString()
    {
        // F78, fixed: the object form the 2026 serializer wrote reads back, and so does the string form.
        var objectForm = JsonSerializer.Deserialize<AmazonS3FileProviderOptions>(Golden("WriteableAmazonS3FileProvider.json"), OptionsJson.Options)!;
        Assert.AreEqual("STANDARD_IA", objectForm.StorageClassForNewFiles?.Value);
        var stringForm = JsonSerializer.Deserialize<AmazonS3FileProviderOptions>(Golden("Lenient.AmazonS3.json"), OptionsJson.Options)!;
        Assert.AreEqual("STANDARD_IA", stringForm.StorageClassForNewFiles?.Value);
        var written = JsonSerializer.SerializeToNode(objectForm, OptionsJson.Options)!;
        Assert.AreEqual("STANDARD_IA", written["StorageClassForNewFiles"]!.GetValue<string>());
    }

    [TestMethod]
    public void LenientInputsReadLikeTheGolden()
    {
        // Inputs consumers may have stored by hand: other casing, a quoted bool, a numeric id, the root as a path only or as
        // segments only, unknown keys.
        using var minioProvider = WriteableFileProviderSerializer.FromJson(Golden("Lenient.Minio.json"));
        var minio = (MinioFileProviderOptions)minioProvider.Options;
        Assert.IsTrue(minio.UseSSL);
        Assert.AreEqual("minio.example.net:9000", minio.HostAndPort);
        CollectionAssert.AreEqual(new[] { "root", "dir" }, minio.RootDirectorySegments);

        var box = JsonSerializer.Deserialize<BoxFileProviderOptions>(Golden("Lenient.Box.json"), OptionsJson.Options)!;
        Assert.AreEqual("123456", box.EnterpriseId);
        Assert.AreEqual("0", box.RootFolderId);
        CollectionAssert.AreEqual(new[] { "a", "b" }, box.RootDirectorySegments);
    }

    [TestMethod]
    public void ConfigurationReadsTheSameShape()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["type"] = "writeableamazons3fileprovider",
            ["accesskey"] = "test-access-key",
            ["SecretKey"] = "test-secret-key",
            ["BucketName"] = "bucket",
            ["BucketRegion"] = "eu-west-1",
            ["IsAnonymus"] = "false",
            ["StorageClassForNewFiles"] = "STANDARD_IA",
            ["RootDirectorySegments:0"] = "root",
            ["RootDirectorySegments:1"] = "dir",
        }).Build();
        using var provider = WriteableFileProviderSerializer.FromConfiguration(configuration);
        AssertSameOptions(Shapes.Single(s => (string)s[0] == "WriteableAmazonS3FileProvider")[1], provider.Options);
    }

    [TestMethod]
    public void ConfigurationReadsEveryGoldenShape()
    {
        foreach (var name in new[] { "WriteableAzureBlobFileProvider", "WriteableMinioFileProvider" })
        {
            var golden = GoldenNode(name + ".json");
            var values = new Dictionary<string, string?>();
            foreach (var (key, value) in golden)
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
            using var provider = WriteableFileProviderSerializer.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            AssertSameOptions(Shapes.Single(s => (string)s[0] == name)[1], provider.Options);
        }
    }

    private static IConfigurationSection ConfigSection(string file) =>
        new ConfigurationBuilder().SetBasePath(Path.Combine(AppContext.BaseDirectory, "Serialization", "Golden")).AddJsonFile(file).Build().GetSection("Files");

    [TestMethod]
    public void ConfigurationGoldenBuildsTheSameOptions()
    {
        // The configuration path binds each options property by name, case-insensitively, from an appsettings section.
        using var azure = WriteableFileProviderSerializer.FromConfiguration(ConfigSection("Config.WriteableAzureBlobFileProvider.json"));
        AssertSameOptions(Shapes.Single(s => (string)s[0] == "WriteableAzureBlobFileProvider")[1], azure.Options);

        using var minio = WriteableFileProviderSerializer.FromConfiguration(ConfigSection("Config.WriteableMinioFileProvider.json"));
        AssertSameOptions(Shapes.Single(s => (string)s[0] == "WriteableMinioFileProvider")[1], minio.Options);

        // F78, fixed on the configuration path too: the storage class converts from its string.
        using var s3 = WriteableFileProviderSerializer.FromConfiguration(ConfigSection("Config.WriteableAmazonS3FileProvider.json"));
        AssertSameOptions(Shapes.Single(s => (string)s[0] == "WriteableAmazonS3FileProvider")[1], s3.Options);
        Assert.AreEqual("STANDARD_IA", ((AmazonS3FileProviderOptions)s3.Options).StorageClassForNewFiles?.Value);
    }

    [TestMethod]
    public void PhysicalShapeRoundTrips()
    {
        // Physical's root is an OS path, so its golden is built at run time: the same three keys on every OS.
        var root = Path.Combine(Path.GetTempPath(), "wfp-shape-" + Guid.NewGuid().ToString("N"));
        using var provider = new WriteablePhysicalFileProvider(root);
        var json = WriteableFileProviderSerializer.ToJson(provider);
        CollectionAssert.AreEquivalent(new[] { "RootDirectoryPath", "RootDirectorySegments", "Type" }, json.Select(p => p.Key).ToArray());
        Assert.AreEqual("WriteablePhysicalFileProvider", json["Type"]!.GetValue<string>());
        using var read = WriteableFileProviderSerializer.FromJson(json);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Type"] = "WriteablePhysicalFileProvider",
            ["RootDirectoryPath"] = root,
        }).Build();
        using var fromConfig = WriteableFileProviderSerializer.FromConfiguration(config);

        // F79, fixed: RootDirectoryPath, and so ToJson, is the real path on every system, where 2026 carried Physical's
        // "root:" marker on Unix. WriteableFileProviders.Physical.Tests reads the "root:" forms 2026 stored.
        Assert.AreEqual(Path.GetFullPath(root), json["RootDirectoryPath"]!.GetValue<string>());
        foreach (var each in new IWriteableFileProvider[] { provider, read, fromConfig })
            Assert.AreEqual(Path.GetFullPath(root), each.Options.RootDirectoryPath);
    }

    [TestMethod]
    public void TypeKeyRules()
    {
        var golden = GoldenNode("WriteableMinioFileProvider.json");

        // The Type value matches the class name case-insensitively.
        var otherCase = golden.DeepClone().AsObject();
        otherCase["Type"] = "WRITEABLEMINIOFILEPROVIDER";
        using (var provider = WriteableFileProviderSerializer.FromJson(otherCase))
            Assert.IsInstanceOfType<WriteableMinioFileProvider>(provider);

        // A missing or unknown Type is refused.
        var missing = golden.DeepClone().AsObject();
        missing.Remove("Type");
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson(missing));
        var unknown = golden.DeepClone().AsObject();
        unknown["Type"] = "NoSuchProvider";
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson(unknown));

        // The 2026 JSON reader needed the key spelled "Type"; the 2027 reader also takes other casings, as the
        // configuration reader always did. It accepts more, never less.
        var lowerKey = golden.DeepClone().AsObject();
        lowerKey.Remove("Type");
        lowerKey["type"] = "WriteableMinioFileProvider";
        using (var provider = WriteableFileProviderSerializer.FromJson(lowerKey))
            Assert.IsInstanceOfType<WriteableMinioFileProvider>(provider);
    }

    public static void AssertSameOptions(object expected, object actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        Assert.AreEqual(expected.GetType(), actual.GetType());
        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead))
        {
            var e = property.GetValue(expected);
            var a = property.GetValue(actual);
            if (e is string[] es)
                CollectionAssert.AreEqual(es, (string[]?)a, property.Name);
            else
                Assert.AreEqual(e?.ToString(), a?.ToString(), property.Name);
        }
    }
}
