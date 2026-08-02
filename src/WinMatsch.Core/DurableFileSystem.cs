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
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        string destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
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

        if (Rename(source, destination) != 0)
        {
            throw new IOException(
                $"Durable file replacement failed with operating-system error {Marshal.GetLastPInvokeError()}.");
        }

        FlushDirectory(destinationDirectory);
        if (!string.Equals(sourceDirectory, destinationDirectory, StringComparison.Ordinal))
        {
            FlushDirectory(sourceDirectory);
        }
    }

    public static void MoveFile(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        string destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        if (!string.Equals(
                sourceDirectory,
                destinationDirectory,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new IOException(
                "Durable atomic file moves must remain within the same directory.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(source, destination, MoveFileWriteThrough))
            {
                throw new IOException(
                    $"Durable file move failed with operating-system error {Marshal.GetLastPInvokeError()}.");
            }

            return;
        }

        File.Move(source, destination);
        FlushDirectory(destinationDirectory);
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

    [LibraryImport(
        "libc",
        EntryPoint = "rename",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Rename(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fileDescriptor);
}
