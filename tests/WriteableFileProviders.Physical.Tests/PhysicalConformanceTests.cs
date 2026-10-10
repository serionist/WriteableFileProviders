using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>The conformance suite on the local disk, under a temp folder of this class's own, deleted when it ends.</summary>
[TestClass]
public sealed class PhysicalConformanceTests : ProviderConformanceTests
{
    private static readonly string _base = Path.Combine(Path.GetTempPath(), "wfp-physical-" + Guid.NewGuid().ToString("N")[..12]);

    protected override bool HasRealFolders => true;

    // NTFS and APFS match names without regard to case by default; the provider says the same.
    protected override bool IsCaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    protected override bool SupportsWatch => true;

    protected override bool HasPhysicalPaths => true;

    // The version check before the rename is not atomic (D15, D19): PhysicalProviderTests pins that it catches a change
    // made while the append copies, which is what the shared test races.
    protected override bool DetectsConcurrentAppends => false;

    protected override string? LocalTempFolder => _base;

    [ClassCleanup]
    public static void DeleteBase()
    {
        if (Directory.Exists(_base))
            Directory.Delete(_base, recursive: true);
    }

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new WriteablePhysicalFileProvider(root is null ? _base : Path.Combine(_base, root));
}
