using System.Runtime.InteropServices;

namespace WriteableFileProviders.Physical;

// The C library calls the provider makes on Linux and macOS, where .NET has no equivalent. Never called on Windows.
internal static partial class NativeMethods
{
    // The error link(2) gives when anything has the new name; 17 on Linux and on macOS.
    internal const int AlreadyExists = 17;

    // The error link(2) gives when the existing file is not there; 2 on Linux and on macOS.
    internal const int NoSuchFile = 2;

    // A second name for a file, refused with AlreadyExists when anything, even a link to nothing, has the new name: the
    // name is taken atomically, and holds the content from the start.
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Link(string existingPath, string newPath);
}
