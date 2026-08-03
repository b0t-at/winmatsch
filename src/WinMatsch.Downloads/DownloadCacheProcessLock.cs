namespace WinMatsch.Downloads;

internal static class DownloadCacheProcessLock
{
    public static FileStream Open(string lockPath, FileMode mode)
    {
        bool usesExplicitByteRangeLock = !OperatingSystem.IsMacOS();
        bool usesExclusiveOpenFileLock = !OperatingSystem.IsWindows();
        var stream = new FileStream(
            lockPath,
            mode,
            FileAccess.ReadWrite,
            usesExclusiveOpenFileLock
                ? FileShare.None
                : FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
        bool locked = false;
        try
        {
            if (usesExplicitByteRangeLock)
            {
                stream.Lock(0, 1);
            }

            locked = true;
            return stream;
        }
        finally
        {
            if (!locked)
            {
                stream.Dispose();
            }
        }
    }
}
