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

    // The root is a folder of the disk, which the system resolves (dot segments included); paths under it stay inside it.
    protected override bool ChecksItsRoot => false;

    // The shared test writes from the appended stream, which Physical copies before it opens the file, so the write lands
    // before the append reads the file, not between the read and the rename; PhysicalProviderTests stages that change
    // from TempCreated, and pins that the check before the rename (not atomic) catches it and the append reads again.
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

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => Task.FromResult(RawFiles.Store(_base, names, text));
}

/// <summary>Files written straight to disk, past the provider, under names it may refuse.</summary>
internal static class RawFiles
{
    /// <summary>Writes a file and its folders under <paramref name="folder"/>, unless this system cannot hold a name.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="names">The file's names under it, one per level.</param>
    /// <param name="text">The content.</param>
    /// <returns>False when a name cannot be held here, by this system or by the file system the folder is on.</returns>
    public static bool Store(string folder, IReadOnlyList<string> names, string text)
    {
        if (!names.All(CanHold))
            return false;
        var path = Path.Combine([folder, .. names]);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file system that refuses the name (exFAT, vfat or SMB from Linux refuse ':' and others), as a store
            // answering 400 does.
            return false;
        }
    }

    // Windows refuses its reserved characters (where ':' would name a stream and '\' separates) and drops a trailing dot
    // or space; Linux and macOS hold any name without '/', though not every file system they mount does.
    private static bool CanHold(string name) =>
        !OperatingSystem.IsWindows() || (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' '));
}
