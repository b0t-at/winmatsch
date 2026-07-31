using System.Reflection;

namespace WinMatsch.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            string version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
            Console.WriteLine(version);
            return 0;
        }

        Console.WriteLine("winmatsch command host");
        return 0;
    }
}
