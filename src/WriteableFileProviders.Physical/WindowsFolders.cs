using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WriteableFileProviders.Physical;

// A folder delete's calls on Windows: NtCreateFile opens an entry relative to its folder's handle, with
// FILE_OPEN_REPARSE_POINT so a link is opened as itself; its attributes and reparse tag say what it is (WindowsRules);
// it is deleted through its own handle, with POSIX semantics unless asked otherwise (FolderDelete decides when). Only
// the calls are here, each with no branch but its own success: everything they report is read by shared code.
[ExcludeFromCodeCoverage(Justification = "Runs only on Windows, where CI runs the tests without coverage; the decisions on what it reports are shared code, covered on Linux.")]
internal sealed unsafe partial class WindowsFolders : FolderSystem
{
    private const uint Delete = 0x10000;
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x100000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenDisposition = 1;
    private const uint SynchronousIo = 0x20;
    private const uint ForBackupIntent = 0x4000;
    private const uint OpenReparsePoint = 0x200000;
    private const uint CaseInsensitive = 0x40;
    private const int AttributeTagInfo = 9;
    private const int DispositionInfo = 4;
    private const int DispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;

    public override SafeHandle? OpenPath(string path, out int error)
    {
        var handle = CreateFile(WindowsRules.ExtendedPath(path), ReadAttributes | Synchronize, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (!handle.IsInvalid)
            return handle;
        handle.Dispose();
        return null;
    }

    public override Entry Open(SafeHandle folder, string name, bool forRemoval)
    {
        var handle = OpenRelative(folder, name, (forRemoval ? Delete : 0) | ReadAttributes | Synchronize, out var error);
        if (handle is null)
            return Entry.Failed(error);
        uint* info = stackalloc uint[2];
        if (GetFileInformationByHandleEx(handle, AttributeTagInfo, info, 8) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return Entry.Failed(error);
        }
        return new Entry(WindowsRules.KindOf(info[0], info[1]), handle, 0);
    }

    public override int Remove(SafeHandle folder, string name, Entry entry, bool classic)
    {
        var flags = DispositionDelete | DispositionPosixSemantics;
        byte delete = 1;
        var done = classic
            ? SetFileInformationByHandle(entry.Handle!, DispositionInfo, &delete, 1)
            : SetFileInformationByHandle(entry.Handle!, DispositionInfoEx, &flags, 4);
        return done != 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    public override Failure Classify(int error) => WindowsRules.Classify(error);

    public override bool FallsBack(int error) => WindowsRules.FallsBack(error);

    public override string ListingPath(string path) => WindowsRules.ExtendedPath(path);

    // Opens 'name' in the folder whose handle is given, as itself: a link, a junction or a mount point is not followed.
    private static SafeFileHandle? OpenRelative(SafeHandle folder, string name, uint access, out int error)
    {
        var added = false;
        folder.DangerousAddRef(ref added);
        try
        {
            fixed (char* chars = name)
            {
                var length = (ushort)(name.Length * sizeof(char));
                var objectName = new UnicodeString { Length = length, MaximumLength = length, Buffer = chars };
                var attributes = new ObjectAttributes
                {
                    Length = sizeof(ObjectAttributes),
                    RootDirectory = folder.DangerousGetHandle(),
                    ObjectName = &objectName,
                    Attributes = CaseInsensitive,
                };
                IoStatusBlock status;
                var result = NtCreateFile(out var handle, access, &attributes, &status, null, 0, ShareAll, OpenDisposition,
                    SynchronousIo | ForBackupIntent | OpenReparsePoint, null, 0);
                error = result >= 0 ? 0 : (int)RtlNtStatusToDosError(result);
                return result >= 0 ? new SafeFileHandle(handle, ownsHandle: true) : null;
            }
        }
        finally
        {
            if (added)
                folder.DangerousRelease();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public void* SecurityDescriptor;
        public void* SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out nint handle, uint access, ObjectAttributes* attributes, IoStatusBlock* status, long* allocationSize,
        uint fileAttributes, uint share, uint disposition, uint options, void* extendedAttributes, uint extendedAttributesLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetFileInformationByHandleEx(SafeHandle handle, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetFileInformationByHandle(SafeHandle handle, int informationClass, void* information, uint size);
}
