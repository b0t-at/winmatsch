using WinMatsch.Downloads;

string lockPath = args.Single();
Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
await using FileStream stream = DownloadCacheProcessLock.Open(lockPath, FileMode.OpenOrCreate);

Console.WriteLine("LOCKED");
await Console.Out.FlushAsync();
_ = await Console.In.ReadLineAsync();
