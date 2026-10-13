using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// The conformance suite in a folder you name: a network share, another volume, another file system. Without
/// <c>WFP_LIVE=1</c> it is inconclusive; with it, the folder comes from the <c>WFP_PHYSICAL_ROOT</c> environment
/// variable, or <c>PhysicalFileProvider_RootPath</c> in an optional testsettings.json next to this project (see
/// testsettings.sample.json), and without either every test fails. Each test works in a folder of its own under it and
/// deletes it; the folder itself is left as it was.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class PhysicalLiveTests : ProviderConformanceTests
{
    private static readonly Lazy<string?> _base = new(FindBase);

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    protected override bool SupportsWatch => true;

    protected override bool HasPhysicalPaths => true;

    // The root is a folder of the disk, which the system resolves (dot segments included); paths under it stay inside it.
    protected override bool ChecksItsRoot => false;

    // The shared test writes from the appended stream, which Physical copies before it opens the file, so the write lands
    // before the append reads the file, not between the read and the rename.
    protected override bool DetectsConcurrentAppends => false;

    protected override IWriteableFileProvider CreateProvider(string? root)
    {
        if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1")
            Assert.Inconclusive("The live suite runs only with WFP_LIVE=1, in a folder you name.");
        var folder = _base.Value;
        if (folder is null)
        {
            Assert.Fail("WFP_LIVE=1, but no folder is configured: set WFP_PHYSICAL_ROOT, or PhysicalFileProvider_RootPath in testsettings.json, "
                + "to the folder to run in.");
        }
        return new WriteablePhysicalFileProvider(root is null ? folder : Path.Combine(folder, root));
    }

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) =>
        Task.FromResult(_base.Value is { } folder && RawFiles.Store(folder, names, text));

    private static string? FindBase()
    {
        if (Environment.GetEnvironmentVariable("WFP_PHYSICAL_ROOT") is { Length: > 0 } fromEnvironment)
            return fromEnvironment;
        var settings = Path.Combine(AppContext.BaseDirectory, "testsettings.json");
        if (!File.Exists(settings))
            return null;
        using var document = JsonDocument.Parse(File.ReadAllText(settings));
        return document.RootElement.TryGetProperty("PhysicalFileProvider_RootPath", out var value) && value.GetString() is { Length: > 0 } fromFile
            ? fromFile
            : null;
    }
}
