string lockPath = args.Single();
Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
bool usesExplicitByteRangeLock = !OperatingSystem.IsMacOS();
bool usesExclusiveOpenFileLock = !OperatingSystem.IsWindows();
await using var stream = new FileStream(
    lockPath,
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    usesExclusiveOpenFileLock
        ? FileShare.None
        : FileShare.ReadWrite | FileShare.Delete,
    bufferSize: 1,
    FileOptions.None);
if (usesExplicitByteRangeLock)
{
    stream.Lock(0, 1);
}

Console.WriteLine("LOCKED");
await Console.Out.FlushAsync();
_ = await Console.In.ReadLineAsync();
