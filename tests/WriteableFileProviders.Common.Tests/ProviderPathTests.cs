using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Common.Tests;

[TestClass]
public sealed class ProviderPathTests
{
    [TestMethod]
    [DataRow("a/b", new[] { "a", "b" })]
    [DataRow("/a/b", new[] { "a", "b" })]
    [DataRow("a\\b", new[] { "a", "b" })]
    [DataRow("a//b/", new[] { "a", "b" })]
    [DataRow("", new string[0])]
    [DataRow("/", new string[0])]
    public void Parse_SplitsOnBothSeparatorsAndDropsEmptyParts(string text, string[] segments)
    {
        CollectionAssert.AreEqual(segments, ProviderPath.Parse(text).Segments.ToArray());
        ProviderPath converted = text;
        CollectionAssert.AreEqual(segments, converted.Segments.ToArray());
        CollectionAssert.AreEqual(segments, ProviderPath.FromString(text).Segments.ToArray());
    }

    [TestMethod]
    public void Array_KeepsSegmentsAsTheyAre()
    {
        ProviderPath path = new[] { "a/b", "" };
        CollectionAssert.AreEqual(new[] { "a/b", "" }, path.Segments.ToArray());
        Assert.AreEqual(path, ProviderPath.FromSegments(["a/b", ""]));
        Assert.IsTrue(new ProviderPath(null).IsRoot);
        Assert.IsTrue(ProviderPath.FromSegments(null).IsRoot);
        Assert.IsTrue(ProviderPath.Parse(null).IsRoot);
    }

    [TestMethod]
    public void Members()
    {
        var path = new ProviderPath("a", "b", "c.txt");
        Assert.AreEqual("c.txt", path.Name);
        Assert.AreEqual(new ProviderPath("a", "b"), path.Parent);
        Assert.AreEqual(ProviderPath.Root, new ProviderPath("a").Parent);
        Assert.AreEqual(ProviderPath.Root, ProviderPath.Root.Parent);
        Assert.AreEqual("", ProviderPath.Root.Name);
        Assert.AreEqual("a/b/c.txt", path.ToString());
        Assert.AreEqual(new ProviderPath("a", "b", "c.txt", "d"), path.Append("d"));
        Assert.IsFalse(path.IsRoot);
        Assert.IsTrue(default(ProviderPath).IsRoot);
        Assert.AreEqual(0, default(ProviderPath).Segments.Count);
    }

    [TestMethod]
    public void Equality_IsOrdinalOnSegments()
    {
        var a = new ProviderPath("a", "b");
        var b = ProviderPath.Parse("a/b");
        Assert.IsTrue(a == b);
        Assert.IsFalse(a != b);
        Assert.IsTrue(a.Equals((object)b));
        Assert.IsFalse(a.Equals((object)"a/b"), "A string is not a ProviderPath object");
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreNotEqual(new ProviderPath("A", "b"), a);
        Assert.AreNotEqual(new ProviderPath("a/b"), a, "Segments are compared as given; validation splits them later");
    }

    [TestMethod]
    public void Copies_TheArrayItIsGiven()
    {
        var segments = new[] { "a", "b" };
        var path = new ProviderPath(segments);
        segments[0] = "changed";
        Assert.AreEqual("a/b", path.ToString());
    }
}
