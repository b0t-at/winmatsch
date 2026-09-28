using WinMatsch.Cli.Commands.Diagnostics;
using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.GitHub.Auth;
using WinMatsch.Workflows.GitHub;

namespace WinMatsch.Cli.Hosting;

/// <summary>Creates the complete production command tree over one consistent host environment.</summary>
public static class ProductionCliComposition
{
    public static CliHost CreateHost()
    {
        Func<string, string?> environment = Environment.GetEnvironmentVariable;
        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ITokenStore tokenStore = TokenStores.CreateDefault();
        string? configurationPath = UserConfigurationFile.GetDefaultPath(
            environment,
            homeDirectory);

        // CI runners are ephemeral: without a persisted directory the upstream-verdict gate
        // never sees the escalations `complete` recorded in an earlier run.
        string? feedbackDirectory = environment("WINMATSCH_FEEDBACK_DIRECTORY");
        IFeedbackStateStore feedbackState = !string.IsNullOrWhiteSpace(feedbackDirectory)
            ? new FileFeedbackStateStore(Path.GetFullPath(feedbackDirectory))
            : configurationPath is null
                ? new FileFeedbackStateStore()
                : new FileFeedbackStateStore(
                    Path.Combine(
                        Path.GetDirectoryName(configurationPath)!,
                        "feedback"));
        IReadOnlyList<ICommandModule> modules =
        [
            new DiagnosticsCommandModule(),
            new MutationCommandModule(
                new ProductionMutationWorkflowFactory(feedbackState),
                new ProductionSubmissionWorkflowFactory()),
            new MaintenanceCommandModule(feedbackStateStore: feedbackState),
            new TokenCommandModule(tokenStore),
            new ConfigCommandModule(environment, homeDirectory),
            new CacheCommandModule(),
            new CompletionCommandModule(),
        ];
        return new(new CliHostOptions
        {
            Output = Console.Out,
            Error = Console.Error,
            EnvironmentVariables = environment,
            HomeDirectory = homeDirectory,
            IsInputRedirected = Console.IsInputRedirected,
            IsOutputRedirected = Console.IsOutputRedirected,
            IsErrorRedirected = Console.IsErrorRedirected,
            TokenStore = tokenStore,
            Modules = modules,
        });
    }
}
