using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

[TestClass]
public sealed class SerializerTests
{
    private static readonly System.Reflection.Assembly _here = typeof(StubProvider).Assembly;

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [TestMethod]
    public void Json_RoundTripsEveryKindOfValue()
    {
        var options = new StubOptions
        {
            Name = "n", Tier = "COLD", Region = new Region("eu"), Flag = true, Maybe = false, Count = 3, Size = 5, Day = DayOfWeek.Friday,
            Endpoint = new Uri("https://example.test/x"), Timeout = TimeSpan.FromMinutes(2), Id = Guid.NewGuid(), When = DateTimeOffset.UnixEpoch,
            Price = 1.5m, Tags = ["a", "b"], RootDirectoryPath = "r/s",
        };
        using var provider = new StubProvider(options);
        var json = provider.ToJson();
        Assert.AreEqual("StubProvider", json["Type"]!.GetValue<string>());
        Assert.AreEqual("COLD", json["Tier"]!.GetValue<string>());
        Assert.AreEqual("eu", json["Region"]!.GetValue<string>());
        Assert.AreEqual("r/s/", json["RootDirectoryPath"]!.GetValue<string>());

        using var read = (StubProvider)WriteableFileProviderSerializer.FromJson(json, _here);
        var o = read.Options;
        Assert.AreEqual("COLD", o.Tier!.Value);
        Assert.AreEqual("eu", o.Region!.Name);
        Assert.AreEqual(options.Id, o.Id);
        CollectionAssert.AreEqual(new[] { "a", "b" }, o.Tags);
        CollectionAssert.AreEqual(new[] { "r", "s" }, o.RootDirectorySegments);

        var text = WriteableFileProviderSerializer.ToJsonString(provider, indented: true);
        StringAssert.Contains(text, "\n");
        using var fromText = (StubProvider)WriteableFileProviderSerializer.FromJson(text, _here);
        Assert.AreEqual(options.Timeout, fromText.Options.Timeout);
        using var fromElement = (StubProvider)WriteableFileProviderSerializer.FromJson(JsonDocument.Parse(text).RootElement, _here);
        Assert.AreEqual(DayOfWeek.Friday, fromElement.Options.Day);
    }

    [TestMethod]
    public void Json_IsLenient()
    {
        var json = """
        {
          // a comment
          "type": "stubprovider",
          "name": 42,
          "flag": "true",
          "maybe": 1,
          "count": "7",
          "tier": { "Value": "HOT", "Other": [1, 2] },
          "unknown": { "nested": true },
        }
        """;
        using var provider = (StubProvider)WriteableFileProviderSerializer.FromJson(json, _here);
        Assert.AreEqual("42", provider.Options.Name);
        Assert.IsTrue(provider.Options.Flag);
        Assert.IsTrue(provider.Options.Maybe);
        Assert.AreEqual(7, provider.Options.Count);
        Assert.AreEqual("HOT", provider.Options.Tier!.Value);

        using var nulls = (StubProvider)WriteableFileProviderSerializer.FromJson("""{"Type":"StubProvider","Name":null,"Tier":null,"Flag":false,"Region":"us"}""", _here);
        Assert.IsNull(nulls.Options.Name);
        Assert.IsNull(nulls.Options.Tier);
        Assert.IsFalse(nulls.Options.Flag);
        Assert.AreEqual("us", nulls.Options.Region!.Name);
        using var emptyTier = (StubProvider)WriteableFileProviderSerializer.FromJson("""{"Type":"StubProvider","Tier":{"Other":1},"Name":true}""", _here);
        Assert.IsNull(emptyTier.Options.Tier);
        Assert.AreEqual("true", emptyTier.Options.Name);
    }

    [TestMethod]
    [DataRow("""{"Type":"StubProvider","Flag":"yes"}""")]
    [DataRow("""{"Type":"StubProvider","Flag":2}""")]
    [DataRow("""{"Type":"StubProvider","Name":[1]}""")]
    [DataRow("""{"Type":"StubProvider","Tier":[1]}""")]
    [DataRow("""{"Type":"StubProvider","Count":"many"}""")]
    public void Json_ThatDoesNotFitThrowsArgumentException(string json) =>
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson(json, _here));

    [TestMethod]
    public void Json_MustBeAnObjectWithAKnownType()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson(JsonDocument.Parse("[]").RootElement));
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson("""{"Name":"x"}"""));
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson("""{"Type":"NoSuchProvider"}""", _here));
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderSerializer.FromJson((string)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderSerializer.FromJson((JsonObject)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderSerializer.ToJson(null!));
    }

    [TestMethod]
    public void Configuration_ConvertsEveryKindOfValue()
    {
        var id = Guid.NewGuid();
        using var provider = (StubProvider)WriteableFileProviderSerializer.FromConfiguration(Config(new()
        {
            ["Type"] = "StubProvider",
            ["Name"] = "n",
            ["Tier"] = "COLD",
            ["Region"] = "eu",
            ["Flag"] = "1",
            ["Maybe"] = "False",
            ["Count"] = "3",
            ["Size"] = "9",
            ["Day"] = "friday",
            ["Endpoint"] = "https://example.test/x",
            ["Timeout"] = "00:02:00",
            ["Id"] = id.ToString(),
            ["When"] = "2026-01-02T03:04:05Z",
            ["Price"] = "1.5",
            ["Tags:0"] = "a",
            ["Tags:1"] = "b",
            ["RootDirectoryPath"] = "r/s",
            ["Unknown"] = "ignored",
        }), _here);
        var o = provider.Options;
        Assert.AreEqual("n", o.Name);
        Assert.AreEqual("COLD", o.Tier!.Value);
        Assert.AreEqual("eu", o.Region!.Name);
        Assert.IsTrue(o.Flag);
        Assert.IsFalse(o.Maybe);
        Assert.AreEqual(3, o.Count);
        Assert.AreEqual(9L, o.Size);
        Assert.AreEqual(DayOfWeek.Friday, o.Day);
        Assert.AreEqual(new Uri("https://example.test/x"), o.Endpoint);
        Assert.AreEqual(TimeSpan.FromMinutes(2), o.Timeout);
        Assert.AreEqual(id, o.Id);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), o.When);
        Assert.AreEqual(1.5m, o.Price);
        CollectionAssert.AreEqual(new[] { "a", "b" }, o.Tags);
        CollectionAssert.AreEqual(new[] { "r", "s" }, o.RootDirectorySegments);
    }

    [TestMethod]
    [DataRow("Flag", "maybe")]
    [DataRow("Count", "x")]
    [DataRow("Count", "99999999999999")]
    [DataRow("Day", "Blursday")]
    [DataRow("Id", "not-a-guid")]
    public void Configuration_ValueThatDoesNotConvertThrows(string key, string value) =>
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromConfiguration(Config(new() { ["Type"] = "StubProvider", [key] = value }), _here));

    [TestMethod]
    public void Configuration_NeedsAType()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromConfiguration(Config([])));
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderSerializer.FromConfiguration(null!));
    }

    [TestMethod]
    public void Resolution_GivenAssembliesThenBuiltInsThenRegistered()
    {
        Assert.AreEqual(typeof(StubProvider), WriteableFileProviderSerializer.ResolveProviderType("STUBPROVIDER", _here));
        // A built-in provider is found without passing its assembly: the serializer loads the package's assembly itself.
        Assert.AreEqual("WriteablePhysicalFileProvider", WriteableFileProviderSerializer.ResolveProviderType("writeablephysicalfileprovider").Name);
        // Loaded assemblies are not searched: a third-party provider is passed in or registered.
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.ResolveProviderType("InMemoryFileProvider"));
        WriteableFileProviderSerializer.Register<InMemoryFileProvider>();
        WriteableFileProviderSerializer.Register<InMemoryFileProvider>();
        Assert.AreEqual(typeof(InMemoryFileProvider), WriteableFileProviderSerializer.ResolveProviderType("InMemoryFileProvider"));
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.ResolveProviderType(""));
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.ResolveProviderType(null));
    }

    [TestMethod]
    public void Resolution_LoadsABuiltInPackageNobodyHasLoaded()
    {
        // A fresh load context has loaded nothing: resolving the Azure provider has to load its assembly by name.
        var context = new IsolatedLoadContext();
        try
        {
            var common = context.LoadFromAssemblyPath(typeof(WriteableFileProviderSerializer).Assembly.Location);
            Assert.IsFalse(context.Assemblies.Any(a => a.GetName().Name == "WriteableFileProviders.AzureBlob"));
            var resolve = common.GetType("WriteableFileProviders.WriteableFileProviderSerializer", throwOnError: true)!
                .GetMethod("ResolveProviderType", BindingFlags.Static | BindingFlags.NonPublic)!;
            var type = (Type)resolve.Invoke(null, ["WriteableAzureBlobFileProvider", Array.Empty<Assembly>()])!;
            Assert.AreEqual("WriteableFileProviders.AzureBlob.WriteableAzureBlobFileProvider", type.FullName);
            Assert.AreSame(context, AssemblyLoadContext.GetLoadContext(type.Assembly));
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void Resolution_RefusesAmbiguousAndUnusableTypes()
    {
        Assert.ThrowsExactly<ArgumentException>(WriteableFileProviderSerializer.Register<IWriteableFileProvider>);
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.ResolveProviderType("DuplicateProvider", _here));
        WriteableFileProviderSerializer.Register<Ambiguous.First.DuplicateProvider>();
        Assert.ThrowsExactly<ArgumentException>(WriteableFileProviderSerializer.Register<Ambiguous.Second.DuplicateProvider>);
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson("""{"Type":"ForeignProvider"}""", _here));
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson("""{"Type":"NoOptionsConstructorProvider"}""", _here));
    }

    [TestMethod]
    public void Construction_ErrorsComeThroughUnwrapped() =>
        Assert.ThrowsExactly<ArgumentException>(() => WriteableFileProviderSerializer.FromJson("""{"Type":"RefusingProvider"}""", _here));

    private sealed class IsolatedLoadContext() : AssemblyLoadContext("wfp-isolated", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
