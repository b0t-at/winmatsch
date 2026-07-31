using WinMatsch.GitHub.Auth;
using Xunit;

namespace WinMatsch.GitHub.Tests.Auth;

public class LinuxSecretServiceTokenStoreTests
{
    [Fact]
    public void Lookup_arguments_never_contain_the_secret()
    {
        var store = new LinuxSecretServiceTokenStore("svc", "acct");

        Assert.Equal(["lookup", "service", "svc", "account", "acct"], store.BuildLookupArguments());
    }

    [Fact]
    public void Store_arguments_never_contain_the_secret()
    {
        var store = new LinuxSecretServiceTokenStore("svc", "acct");

        IReadOnlyList<string> arguments = store.BuildStoreArguments();

        Assert.Equal(["store", "--label", "svc GitHub token", "service", "svc", "account", "acct"], arguments);
    }

    [Fact]
    public void Clear_arguments_target_the_service_and_account()
    {
        var store = new LinuxSecretServiceTokenStore("svc", "acct");

        Assert.Equal(["clear", "service", "svc", "account", "acct"], store.BuildClearArguments());
    }

    [Fact]
    public void FindExecutable_returns_null_for_empty_path()
    {
        Assert.Null(LinuxSecretServiceTokenStore.FindExecutable("secret-tool", null));
        Assert.Null(LinuxSecretServiceTokenStore.FindExecutable("secret-tool", ""));
    }

    [Fact]
    public void FindExecutable_locates_a_file_on_the_search_path()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winmatsch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, "secret-tool");
            File.WriteAllText(executable, string.Empty);
            string searchPath = string.Join(
                Path.PathSeparator,
                Path.Combine(directory, "missing"),
                directory);

            Assert.Equal(executable, LinuxSecretServiceTokenStore.FindExecutable("secret-tool", searchPath));
            Assert.Null(LinuxSecretServiceTokenStore.FindExecutable("other-tool", searchPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Store_is_unavailable_off_linux_or_without_the_binary()
    {
        var store = new LinuxSecretServiceTokenStore();

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(store.IsAvailable);
        }
    }
}
