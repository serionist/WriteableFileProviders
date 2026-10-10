using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// The root folder's forms and the configuration shape (AGENTS.md, one-way doors): what 2026 stored reads the same, and
/// <see cref="PhysicalFileProviderOptions.RootDirectoryPath"/> is the real path on every system (F79). Nothing here
/// touches the disk.
/// </summary>
[TestClass]
public sealed class PhysicalOptionsTests
{
    private static readonly string _separator = Path.DirectorySeparatorChar.ToString();

    private static string[] SegmentsOf(string? path) => new PhysicalFileProviderOptions { RootDirectoryPath = path }.RootDirectorySegments;

    private static string? PathOf(params string[] segments) => new PhysicalFileProviderOptions { RootDirectorySegments = segments }.RootDirectoryPath;

    [TestMethod]
    public void RootForms_ParseAs2026StoredThem()
    {
        Assert.IsEmpty(SegmentsOf(""));
        Assert.IsEmpty(SegmentsOf(null));
        CollectionAssert.AreEqual(new[] { "\\", "server", "share", "x" }, SegmentsOf(@"\\server\share\x"));
        CollectionAssert.AreEqual(new[] { "root:", "abs", "path" }, SegmentsOf("/abs/path"));
        CollectionAssert.AreEqual(new[] { "root:", "abs", "path" }, SegmentsOf("root:/abs/path"), "The form 2026 wrote on Linux and macOS");
        CollectionAssert.AreEqual(new[] { "C:", "data", "x" }, SegmentsOf(@"C:\data\x"));
        CollectionAssert.AreEqual(new[] { "C:", "data", "x" }, SegmentsOf("C:/data/x/"));
        CollectionAssert.AreEqual(new[] { "relative", "dir" }, SegmentsOf("relative/dir"));
    }

    [TestMethod]
    public void RootDirectoryPath_IsTheRealPath()
    {
        // F79: 2026 returned "root:/abs/path" for an absolute path on Linux and macOS.
        Assert.AreEqual("", PathOf());
        Assert.AreEqual(_separator + "abs" + _separator + "path", PathOf("root:", "abs", "path"));
        Assert.AreEqual(_separator, PathOf("root:"));
        Assert.AreEqual("C:" + _separator + "data", PathOf("C:", "data"));
        Assert.AreEqual("C:" + _separator, PathOf("C:"), "A drive alone is its root, not its current folder");
        Assert.AreEqual("relative" + _separator + "dir", PathOf("relative", "dir"));
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(@"\\server\share\x", PathOf("\\", "server", "share", "x"));
    }

    [TestMethod]
    public void Constructor_ResolvesTheRootOnceAndWritesItBack()
    {
        var options = new PhysicalFileProviderOptions { RootDirectoryPath = "relative-root" + _separator };
        using var provider = new WriteablePhysicalFileProvider(options);
        Assert.AreSame(options, provider.Options);
        Assert.AreEqual(Path.GetFullPath("relative-root"), options.RootDirectoryPath);
        Assert.IsTrue(Path.IsPathFullyQualified(provider.Options.RootDirectoryPath!));
        Assert.IsFalse(Directory.Exists(Path.GetFullPath("relative-root")), "Nothing on disk is touched");
    }

    [TestMethod]
    public void Constructor_RefusesAMissingRoot()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new WriteablePhysicalFileProvider(""));
        Assert.ThrowsExactly<ArgumentException>(() => new WriteablePhysicalFileProvider(new PhysicalFileProviderOptions()));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteablePhysicalFileProvider((PhysicalFileProviderOptions)null!));
    }

    [TestMethod]
    public void ShapeRoundTrips()
    {
        // Physical's root is a path on this system, so its golden is built here: the same three keys on every system, and
        // since 2027 the real path in RootDirectoryPath on every system too (F79; 2026 wrote "root:/..." on Unix).
        var root = Path.Combine(Path.GetTempPath(), "wfp-shape-" + Guid.NewGuid().ToString("N"));
        using var provider = new WriteablePhysicalFileProvider(root);
        var json = WriteableFileProviderSerializer.ToJson(provider);
        CollectionAssert.AreEquivalent(new[] { "RootDirectoryPath", "RootDirectorySegments", "Type" }, json.Select(p => p.Key).ToArray());
        Assert.AreEqual("WriteablePhysicalFileProvider", json["Type"]!.GetValue<string>());
        Assert.AreEqual(Path.GetFullPath(root), json["RootDirectoryPath"]!.GetValue<string>());
        CollectionAssert.AreEqual(SegmentsOf(Path.GetFullPath(root)), json["RootDirectorySegments"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray());

        using var fromJson = WriteableFileProviderSerializer.FromJson(json);
        using var fromConfiguration = WriteableFileProviderSerializer.FromConfiguration(Configuration(new()
        {
            ["Type"] = "WriteablePhysicalFileProvider",
            ["RootDirectoryPath"] = root,
        }));
        foreach (var each in new IWriteableFileProvider[] { provider, fromJson, fromConfiguration })
            Assert.AreEqual(Path.GetFullPath(root), each.Options.RootDirectoryPath);
    }

    [TestMethod]
    public void StoredRootsRead()
    {
        // What 2026 wrote: on Linux and macOS both keys carry the "root:" marker. On Windows that form is a path from the
        // current drive's root, as 2026 resolved it.
        var expected = Path.GetFullPath(_separator + "wfp-stored" + _separator + "files");
        var stored = """{"RootDirectoryPath":"root:/wfp-stored/files","RootDirectorySegments":["root:","wfp-stored","files"],"Type":"WriteablePhysicalFileProvider"}""";
        using var fromJson = WriteableFileProviderSerializer.FromJson(stored);
        Assert.AreEqual(expected, fromJson.Options.RootDirectoryPath);

        using var fromSegments = WriteableFileProviderSerializer.FromConfiguration(Configuration(new()
        {
            ["type"] = "writeablephysicalfileprovider",
            ["RootDirectorySegments:0"] = "root:",
            ["RootDirectorySegments:1"] = "wfp-stored",
            ["RootDirectorySegments:2"] = "files",
        }));
        Assert.AreEqual(expected, fromSegments.Options.RootDirectoryPath);

        using var fromPath = WriteableFileProviderSerializer.FromConfiguration(Configuration(new()
        {
            ["Type"] = "WriteablePhysicalFileProvider",
            ["RootDirectoryPath"] = "root:/wfp-stored/files",
        }));
        Assert.AreEqual(expected, fromPath.Options.RootDirectoryPath);
    }

    [TestMethod]
    public void JsonWrittenBy2027ReadsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "wfp-written");
        using var provider = new WriteablePhysicalFileProvider(root);
        var text = WriteableFileProviderSerializer.ToJsonString(provider);
        using var read = WriteableFileProviderSerializer.FromJson(text);
        Assert.AreEqual(Path.GetFullPath(root), read.Options.RootDirectoryPath);
        CollectionAssert.AreEqual(provider.Options.RootDirectorySegments, read.Options.RootDirectorySegments);
        Assert.DoesNotContain("root:/", JsonNode.Parse(text)!["RootDirectoryPath"]!.GetValue<string>());
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
