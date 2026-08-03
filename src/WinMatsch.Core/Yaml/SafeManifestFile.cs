using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinMatsch.Core.Yaml;

/// <summary>
/// Opens a manifest without following reparse points or symbolic links. Windows parent
/// directories are held without delete sharing until the read completes; Unix paths are opened
/// component-by-component relative to already-open directory descriptors.
/// </summary>
internal static partial class SafeManifestFile
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileGenericRead = 0x00120089;
    private const uint Synchronize = 0x00100000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfo = 9;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;

    public static SafeManifestFileLease OpenRead(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || relative == ".")
        {
            throw new InvalidDataException("A manifest path must identify a file below its allowed root.");
        }

        return OperatingSystem.IsWindows()
            ? OpenWindows(root, path, segments)
            : OpenUnix(root, segments);
    }

    public static SafeManifestDirectoryLease OpenDirectory(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        string[] segments = relative == "."
            ? []
            : relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        return OperatingSystem.IsWindows()
            ? OpenWindowsDirectoryPath(root, path, segments)
            : OpenUnixDirectoryPath(root, path, segments);
    }

    private static SafeManifestFileLease OpenWindows(
        string root,
        string path,
        string[] segments)
    {
        var directoryHandles = new List<SafeFileHandle>(segments.Length);
        SafeFileHandle? fileHandle = null;
        try
        {
            directoryHandles.Add(OpenWindowsRootDirectory(root));
            for (int index = 0; index < segments.Length - 1; index++)
            {
                directoryHandles.Add(OpenWindowsRelative(
                    directoryHandles[^1],
                    segments[index],
                    directory: true));
            }

            fileHandle = OpenWindowsRelative(
                directoryHandles[^1],
                segments[^1],
                directory: false);
            FileAttributeTagInformation information = GetWindowsInformation(fileHandle, path);
            if ((information.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
            {
                throw new InvalidDataException(
                    $"Manifest path '{path}' must be a regular file and cannot be a reparse point.");
            }

            var stream = new FileStream(
                fileHandle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false);
            fileHandle = null;
            return new SafeManifestFileLease(stream, directoryHandles);
        }
        catch
        {
            fileHandle?.Dispose();
            DisposeAll(directoryHandles);
            throw;
        }
    }

    private static SafeManifestDirectoryLease OpenWindowsDirectoryPath(
        string root,
        string path,
        string[] segments)
    {
        var handles = new List<SafeFileHandle>(segments.Length + 1);
        try
        {
            handles.Add(OpenWindowsRootDirectory(root));
            foreach (string segment in segments)
            {
                handles.Add(OpenWindowsRelative(handles[^1], segment, directory: true));
            }

            return new SafeManifestDirectoryLease(handles, path);
        }
        catch
        {
            DisposeAll(handles);
            throw;
        }
    }

    private static SafeFileHandle OpenWindowsRootDirectory(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            FileListDirectory | FileReadAttributes,
            FileShareRead,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        try
        {
            EnsureValidWindowsHandle(handle, path);
            FileAttributeTagInformation information = GetWindowsInformation(handle, path);
            if ((information.FileAttributes & FileAttributeReparsePoint) != 0
                || (information.FileAttributes & FileAttributeDirectory) == 0)
            {
                throw new InvalidDataException(
                    $"Manifest path '{path}' cannot traverse a symbolic link or reparse point.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenWindowsRelative(
        SafeFileHandle parent,
        string segment,
        bool directory)
    {
        using var name = new NativeUnicodeString(segment);
        var attributes = new ObjectAttributes
        {
            Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.DangerousGetHandle(),
            ObjectName = name.Structure,
            Attributes = ObjectCaseInsensitive,
        };
        uint options = FileSynchronousIoNonAlert
            | FileOpenReparsePoint
            | (directory ? FileDirectoryFile : FileNonDirectoryFile);
        int status = NtCreateFile(
            out SafeFileHandle handle,
            (directory ? FileListDirectory | FileReadAttributes : FileGenericRead) | Synchronize,
            ref attributes,
            out _,
            0,
            0,
            FileShareRead,
            FileOpen,
            options,
            0,
            0);
        if (status >= 0)
        {
            FileAttributeTagInformation information = GetWindowsInformation(handle, segment);
            bool invalid = directory
                ? (information.FileAttributes & FileAttributeReparsePoint) != 0
                    || (information.FileAttributes & FileAttributeDirectory) == 0
                : (information.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0;
            if (invalid)
            {
                handle.Dispose();
                throw new InvalidDataException(
                    directory
                        ? $"Manifest path component '{segment}' cannot be a symbolic link or reparse point."
                        : $"Manifest path component '{segment}' must be a regular file.");
            }

            return handle;
        }

        handle.Dispose();
        uint error = RtlNtStatusToDosError(status);
        throw new IOException(
            $"Unable to open manifest path component '{segment}' safely "
            + $"(NTSTATUS 0x{status:X8}, Win32 error {error}).");
    }

    private static void EnsureValidWindowsHandle(SafeFileHandle handle, string path)
    {
        if (!handle.IsInvalid)
        {
            return;
        }

        int error = Marshal.GetLastPInvokeError();
        throw new IOException(
            $"Unable to open manifest path '{path}' safely: {new Win32Exception(error).Message} (Win32 error {error}).");
    }

    private static FileAttributeTagInformation GetWindowsInformation(
        SafeFileHandle handle,
        string path)
    {
        if (GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out FileAttributeTagInformation information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            return information;
        }

        int error = Marshal.GetLastPInvokeError();
        throw new IOException(
            $"Unable to inspect manifest path '{path}': {new Win32Exception(error).Message} (Win32 error {error}).");
    }

    private static SafeManifestFileLease OpenUnix(
        string root,
        string[] segments)
    {
        int directoryFlags = UnixDirectoryFlags();
        int fileFlags = UnixFileFlags();
        SafeFileHandle? directoryHandle = null;
        SafeFileHandle? fileHandle = null;
        try
        {
            directoryHandle = OpenUnixHandle(root, directoryFlags, "allowed manifest root");
            for (int index = 0; index < segments.Length - 1; index++)
            {
                SafeFileHandle child = OpenUnixAtHandle(
                    directoryHandle,
                    segments[index],
                    directoryFlags,
                    "manifest directory");
                directoryHandle.Dispose();
                directoryHandle = child;
            }

            fileHandle = OpenUnixAtHandle(
                directoryHandle,
                segments[^1],
                fileFlags,
                "manifest file");
            FileAttributes attributes = File.GetAttributes(fileHandle);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    $"Manifest path component '{segments[^1]}' must be a regular file.");
            }

            var stream = new FileStream(
                fileHandle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                fileHandle = null;
                throw new InvalidDataException(
                    $"Manifest path component '{segments[^1]}' must be a regular seekable file.");
            }

            fileHandle = null;
            return new SafeManifestFileLease(stream, [directoryHandle]);
        }
        catch
        {
            fileHandle?.Dispose();
            directoryHandle?.Dispose();
            throw;
        }
    }

    private static SafeManifestDirectoryLease OpenUnixDirectoryPath(
        string root,
        string path,
        string[] segments)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = OpenUnixHandle(root, UnixDirectoryFlags(), "allowed manifest root");
            foreach (string segment in segments)
            {
                SafeFileHandle child = OpenUnixAtHandle(
                    handle,
                    segment,
                    UnixDirectoryFlags(),
                    "manifest directory");
                handle.Dispose();
                handle = child;
            }

            var lease = new SafeManifestDirectoryLease([handle], path, unixDirectoryHandle: handle);
            handle = null;
            return lease;
        }
        catch
        {
            handle?.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenUnixHandle(
        string path,
        int flags,
        string description)
    {
        int descriptor = Open(path, flags);
        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        throw UnsafeUnixPath(description, path, error);
    }

    private static SafeFileHandle OpenUnixAtHandle(
        SafeFileHandle directory,
        string segment,
        int flags,
        string description)
    {
        int descriptor = OpenAt(directory.DangerousGetHandle().ToInt32(), segment, flags);
        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        throw UnsafeUnixPath(description, segment, error);
    }

    private static InvalidDataException UnsafeUnixPath(
        string description,
        string path,
        int error)
        => new(
            $"Unable to open {description} '{path}' without traversing a symbolic link or reparse point "
            + $"(OS error {error}).");

    private static int UnixDirectoryFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x00100000 | 0x00000100 | 0x01000000;
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return 0x00020000 | 0x00000100 | 0x00100000;
        }

        return 0x00010000 | 0x00020000 | 0x00080000;
    }

    private static int UnixFileFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x00000100 | 0x00000004 | 0x01000000;
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return 0x00000100 | 0x00000004 | 0x00100000;
        }

        return 0x00020000 | 0x00000800 | 0x00080000;
    }

    private static void DisposeAll(IEnumerable<IDisposable> values)
    {
        foreach (IDisposable value in values)
        {
            value.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

#pragma warning disable SYSLIB1054 // Source-generated interop would require unsafe blocks project-wide.
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    internal static extern int DuplicateFileDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    internal static extern nint OpenDirectoryStream(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    internal static extern nint ReadDirectoryEntry(nint directoryStream);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    internal static extern int CloseDirectoryStream(nint directoryStream);
#pragma warning restore SYSLIB1054
}

internal sealed class NativeUnicodeString : IDisposable
{
    private readonly nint _buffer;

    public NativeUnicodeString(string value)
    {
        _buffer = Marshal.StringToHGlobalUni(value);
        var unicode = new UnicodeString
        {
            Length = checked((ushort)(value.Length * sizeof(char))),
            MaximumLength = checked((ushort)((value.Length + 1) * sizeof(char))),
            Buffer = _buffer,
        };
        Structure = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        Marshal.StructureToPtr(unicode, Structure, fDeleteOld: false);
    }

    public nint Structure { get; }

    public void Dispose()
    {
        Marshal.FreeHGlobal(Structure);
        Marshal.FreeHGlobal(_buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }
}

internal sealed class SafeManifestFileLease(
    FileStream stream,
    IReadOnlyList<SafeFileHandle> directoryHandles)
    : IDisposable
{
    public FileStream Stream { get; } = stream;

    public void Dispose()
    {
        Stream.Dispose();
        foreach (SafeFileHandle handle in directoryHandles)
        {
            handle.Dispose();
        }
    }
}

internal sealed class SafeManifestDirectoryLease(
    IReadOnlyList<SafeFileHandle> handles,
    string enumerationPath,
    SafeFileHandle? unixDirectoryHandle = null)
    : IDisposable
{
    public string EnumerationPath { get; } = enumerationPath;

    public IEnumerable<string> EnumerateFileSystemEntries()
    {
        if (unixDirectoryHandle is null)
        {
            return Directory.EnumerateFileSystemEntries(EnumerationPath);
        }

        return EnumerateUnixEntries(unixDirectoryHandle, EnumerationPath);
    }

    public void Dispose()
    {
        foreach (SafeFileHandle handle in handles)
        {
            handle.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateUnixEntries(
        SafeFileHandle directoryHandle,
        string directoryPath)
    {
        int duplicate = SafeManifestFile.DuplicateFileDescriptor(
            directoryHandle.DangerousGetHandle().ToInt32());
        if (duplicate < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to duplicate pinned manifest directory '{directoryPath}' (OS error {error}).");
        }

        nint stream = SafeManifestFile.OpenDirectoryStream(duplicate);
        if (stream == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            _ = Close(duplicate);
            throw new IOException(
                $"Unable to enumerate pinned manifest directory '{directoryPath}' (OS error {error}).");
        }

        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                nint entry = SafeManifestFile.ReadDirectoryEntry(stream);
                if (entry == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw new IOException(
                            $"Unable to read pinned manifest directory '{directoryPath}' (OS error {error}).");
                    }

                    yield break;
                }

                int nameOffset = OperatingSystem.IsMacOS()
                    ? 21
                    : OperatingSystem.IsFreeBSD()
                        ? 24
                        : 19;
                string name = Marshal.PtrToStringUTF8(entry + nameOffset)
                    ?? throw new IOException(
                        $"Pinned manifest directory '{directoryPath}' contains an invalid entry name.");
                if (name is not "." and not "..")
                {
                    yield return Path.Combine(directoryPath, name);
                }
            }
        }
        finally
        {
            _ = SafeManifestFile.CloseDirectoryStream(stream);
        }
    }

#pragma warning disable SYSLIB1054 // Source-generated interop would require unsafe blocks project-wide.
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
#pragma warning restore SYSLIB1054
}
