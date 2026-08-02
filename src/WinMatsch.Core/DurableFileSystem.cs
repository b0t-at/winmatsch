using System.Runtime.InteropServices;

namespace WinMatsch.Core;

public static partial class DurableFileSystem
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    public static void ReplaceFile(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(
                    source,
                    destination,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new IOException(
                    $"Durable file replacement failed with operating-system error {Marshal.GetLastPInvokeError()}.");
            }

            return;
        }

        File.Move(source, destination, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
    }

    public static void FlushDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int descriptor = Open(directory, flags: 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Opening a directory for durable synchronization failed with operating-system error {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (IsUnsupportedDirectorySync(error))
                {
                    return;
                }

                throw new IOException(
                    $"Directory synchronization failed with operating-system error {error}.");
            }
        }

        finally
        {
            _ = Close(descriptor);
        }
    }

    private static bool IsUnsupportedDirectorySync(int error)
        => error == 22
            || OperatingSystem.IsLinux() && error == 95
            || OperatingSystem.IsMacOS() && error == 45;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    [LibraryImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fileDescriptor);
}
