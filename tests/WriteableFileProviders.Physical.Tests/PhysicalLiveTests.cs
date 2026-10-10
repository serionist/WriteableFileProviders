using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// The conformance suite in a folder you name: a network share, another volume, another file system. The folder comes from
/// the <c>WFP_PHYSICAL_ROOT</c> environment variable, or <c>PhysicalFileProvider_RootPath</c> in an optional
/// testsettings.json next to this project; without either, every test fails. Each test works in a folder of its own under
/// it and deletes it; the folder itself is left as it was.
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

    // The version check before the rename is not atomic (D15, D19).
    protected override bool DetectsConcurrentAppends => false;

    protected override IWriteableFileProvider CreateProvider(string? root)
    {
        var folder = _base.Value ?? throw new AssertFailedException(
            "Set WFP_PHYSICAL_ROOT, or PhysicalFileProvider_RootPath in tests/WriteableFileProviders.Physical.Tests/testsettings.json, to the folder to run in.");
        return new WriteablePhysicalFileProvider(root is null ? folder : Path.Combine(folder, root));
    }

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
