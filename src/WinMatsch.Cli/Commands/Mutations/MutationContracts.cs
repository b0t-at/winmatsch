using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using WinMatsch.Cli.Hosting;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Mutations;

public interface IMutationWorkflow
{
    public Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IVerifiedMutationWorkflow : IMutationWorkflow
{
    public Task<WorkflowOperationResult> ApplyVerifiedAsync(
        WorkflowOperationRequest request,
        string expectedPlanFingerprint,
        CancellationToken cancellationToken = default);
}

public interface IMutationWorkflowFactory
{
    public Task<IMutationWorkflow> CreateAsync(
        CommandContext context,
        CancellationToken cancellationToken = default);
}

public interface ISubmissionWorkflow
{
    public Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IJournaledSubmissionWorkflow : ISubmissionWorkflow
{
    public Task<SubmissionJournalHandle> PrepareAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default);

    public Task<GitHubLifecycleResult> ExecutePreparedAsync(
        SubmissionJournalHandle handle,
        CancellationToken cancellationToken = default);

    public Task<GitHubLifecycleResult?> ResumePendingAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        RepositoryCoordinates upstreamRepository,
        CancellationToken cancellationToken = default);

    public Task<ImmutableArray<SubmissionJournalEntry>> ListPendingAsync(
        CancellationToken cancellationToken = default);

    public Task CancelAsync(
        string id,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface ISubmissionWorkflowFactory
{
    public Task<ISubmissionWorkflow> CreateAsync(
        CommandContext context,
        CancellationToken cancellationToken = default);
}

public interface IRawManifestSetLoader
{
    public Task<ImmutableArray<RawManifestDocument>> LoadAsync(
        string path,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

public interface IEditorRunner
{
    public Task<EditorResult> EditAsync(
        ImmutableArray<RawManifestDocument> documents,
        CancellationToken cancellationToken = default);
}

public interface IEditorProcessRunner
{
    public Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public interface IUrlLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface IUrlProcessRunner
{
    public Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public enum UrlLauncherPlatform
{
    Windows,
    MacOS,
    Linux,
}

public enum EditorResultCode
{
    Accepted,
    Cancelled,
    MissingConfiguration,
    InvalidConfiguration,
    Failed,
}

public sealed record EditorResult(
    EditorResultCode Code,
    ImmutableArray<RawManifestDocument> Documents,
    string? ErrorMessage = null)
{
    public bool Accepted => Code == EditorResultCode.Accepted;
}

public sealed class FixedMutationWorkflowFactory(IMutationWorkflow workflow) : IMutationWorkflowFactory
{
    private readonly IMutationWorkflow _workflow =
        workflow ?? throw new ArgumentNullException(nameof(workflow));

    public Task<IMutationWorkflow> CreateAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_workflow);
}

public sealed class FixedSubmissionWorkflowFactory(ISubmissionWorkflow workflow)
    : ISubmissionWorkflowFactory
{
    private readonly ISubmissionWorkflow _workflow =
        workflow ?? throw new ArgumentNullException(nameof(workflow));

    public Task<ISubmissionWorkflow> CreateAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_workflow);
}

public sealed class LocalMutationWorkflow(LocalWorkflowEngine engine) : IVerifiedMutationWorkflow
{
    private readonly LocalWorkflowEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken = default)
        => request switch
        {
            NewOperationRequest value => _engine.NewAsync(value, cancellationToken),
            UpdateOperationRequest value => _engine.UpdateAsync(value, cancellationToken),
            RemoveOperationRequest value => _engine.RemoveAsync(value, cancellationToken),
            SubmitOperationRequest value => _engine.SubmitAsync(value, cancellationToken),
            NewLocaleOperationRequest value => _engine.NewLocaleAsync(value, cancellationToken),
            UpdateLocaleOperationRequest value => _engine.UpdateLocaleAsync(value, cancellationToken),
            _ => throw new ArgumentException("Unsupported mutation request.", nameof(request)),
        };

    public Task<WorkflowOperationResult> ApplyVerifiedAsync(
        WorkflowOperationRequest request,
        string expectedPlanFingerprint,
        CancellationToken cancellationToken = default)
        => _engine.ApplyVerifiedPlanAsync(
            request,
            expectedPlanFingerprint,
            cancellationToken);
}

public sealed class LifecycleSubmissionWorkflow(GitHubLifecycleWorkflow workflow) : ISubmissionWorkflow
{
    private readonly GitHubLifecycleWorkflow _workflow =
        workflow ?? throw new ArgumentNullException(nameof(workflow));

    public Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
        => _workflow.ExecuteAsync(request, cancellationToken);
}

public sealed class FileSystemRawManifestSetLoader : IRawManifestSetLoader
{
    public async Task<ImmutableArray<RawManifestDocument>> LoadAsync(
        string path,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string input = Path.GetFullPath(path);
        string root = Path.GetFullPath(outputDirectory);
        RejectLink(input);
        string[] files;
        if (File.Exists(input))
        {
            throw new CliUsageException(
                "Submitting one file is unsupported because a WinGet multi-file manifest "
                + "requires its installer, version, and default-locale siblings. "
                + "Pass the complete manifest directory.");
        }
        else if (Directory.Exists(input))
        {
            files = [.. EnumerateManifestFiles(input).Order(StringComparer.Ordinal)];
        }
        else
        {
            throw new FileNotFoundException($"Manifest input '{path}' was not found.", path);
        }

        var loaded = new List<(string File, byte[] Content, string? RepositoryPath)>(files.Length);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(file);
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string? repositoryPath = relative;
            if (Path.IsPathRooted(relative)
                || relative.StartsWith("../", StringComparison.Ordinal)
                || relative == "..")
            {
                repositoryPath = FindManifestSegment(file);
            }

            byte[] content = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            loaded.Add((file, content, repositoryPath));
        }

        bool preserveRepositoryPaths = loaded.All(static item =>
            item.RepositoryPath?.StartsWith("manifests/", StringComparison.Ordinal) == true);
        string? inferredDirectory = preserveRepositoryPaths
            ? null
            : InferVersionDirectory(loaded);
        var documents = ImmutableArray.CreateBuilder<RawManifestDocument>(files.Length);
        foreach ((string file, byte[] content, string? repositoryPath) in loaded)
        {
            documents.Add(new(
                preserveRepositoryPaths
                    ? repositoryPath!
                    : $"{inferredDirectory}/{Path.GetFileName(file)}",
                content));
        }

        return documents.ToImmutable();
    }

    private static IEnumerable<string> EnumerateManifestFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            RejectLink(directory);
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                RejectLink(child);
                pending.Push(child);
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetExtension(file).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(file).Equals(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static string InferVersionDirectory(
        IReadOnlyList<(string File, byte[] Content, string? RepositoryPath)> files)
    {
        foreach ((string _, byte[] content, string? _) in files)
        {
            string yaml = new UTF8Encoding(false, true).GetString(content);
            if (ManifestYamlReader.TryDetectType(yaml) == ManifestType.Version)
            {
                VersionManifest manifest = ManifestYamlReader.ReadVersion(yaml);
                return ManifestPaths.GetVersionDirectory(
                    manifest.PackageIdentifier
                        ?? throw new InvalidDataException("Version manifest package identifier is missing."),
                    manifest.PackageVersion
                        ?? throw new InvalidDataException("Version manifest package version is missing."));
            }
        }

        throw new InvalidDataException(
            "A manifest set outside the output repository must contain a version manifest so repository paths can be inferred.");
    }

    private static void RejectLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        if (info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Manifest input '{path}' must not be a symbolic link or reparse point.");
        }
    }

    private static string? FindManifestSegment(string path)
    {
        string normalized = path.Replace('\\', '/');
        int index = normalized.LastIndexOf("/manifests/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + 1)..];
    }
}

public sealed class ProcessEditorRunner : IEditorRunner
{
    private readonly Func<string, string?> _environment;
    private readonly IEditorProcessRunner _processes;
    private readonly Action<string> _cleanup;

    public ProcessEditorRunner(
        Func<string, string?>? environment = null,
        IEditorProcessRunner? processes = null,
        Action<string>? cleanup = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _processes = processes ?? new EditorProcessRunner();
        _cleanup = cleanup ?? (static path => Directory.Delete(path, recursive: true));
    }

    public async Task<EditorResult> EditAsync(
        ImmutableArray<RawManifestDocument> documents,
        CancellationToken cancellationToken = default)
    {
        string? command = _environment("VISUAL") ?? _environment("EDITOR");
        if (string.IsNullOrWhiteSpace(command))
        {
            return new(
                EditorResultCode.MissingConfiguration,
                documents,
                "Set VISUAL or EDITOR to enable manifest editing.");
        }

        IReadOnlyList<string> commandParts;
        try
        {
            commandParts = EditorCommandLine.Parse(command);
        }
        catch (FormatException exception)
        {
            return new(EditorResultCode.InvalidConfiguration, documents, exception.Message);
        }

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-editor-{Guid.NewGuid():N}");
        CreateSecureDirectory(temporaryRoot);
        EditorResult? result = null;
        Exception? primaryFailure = null;
        try
        {
            result = await EditInTemporaryDirectoryAsync(
                documents,
                commandParts,
                temporaryRoot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            _cleanup(temporaryRoot);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            if (primaryFailure is OperationCanceledException
                && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"{primaryFailure.Message} Cleanup of the isolated editor directory also "
                    + $"failed: {cleanupFailure.Message}",
                    new AggregateException(primaryFailure, cleanupFailure),
                    cancellationToken);
            }

            throw new IOException(
                $"Manifest editing failed: {primaryFailure.Message} "
                + $"Cleanup of the isolated editor directory also failed: {cleanupFailure.Message}",
                new AggregateException(primaryFailure, cleanupFailure));
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            string primaryResult = result?.ErrorMessage
                ?? "The editor completed, but its isolated files could not be removed.";
            return new(
                EditorResultCode.Failed,
                documents,
                $"{primaryResult} Cleanup of the isolated editor directory failed: "
                + cleanupFailure.Message);
        }

        return result!;
    }

    private async Task<EditorResult> EditInTemporaryDirectoryAsync(
        ImmutableArray<RawManifestDocument> documents,
        IReadOnlyList<string> commandParts,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>(documents.Length);
        foreach (RawManifestDocument document in documents)
        {
            string destination = Path.GetFullPath(
                Path.Combine(temporaryRoot, document.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(
                    temporaryRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    EditorResultCode.Failed,
                    documents,
                    "Manifest path escaped the isolated editor directory.");
            }

            CreateSecureDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(
                destination,
                document.Content.ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    destination,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            paths.Add(destination);
        }

        int exitCode = await _processes.RunAsync(
            commandParts[0],
            [.. commandParts.Skip(1), .. paths],
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return new(
                EditorResultCode.Failed,
                documents,
                $"The configured editor exited with code {exitCode}.");
        }

        var edited = ImmutableArray.CreateBuilder<RawManifestDocument>(documents.Length);
        for (int index = 0; index < documents.Length; index++)
        {
            byte[] content = await File.ReadAllBytesAsync(paths[index], cancellationToken)
                .ConfigureAwait(false);
            edited.Add(new(documents[index].RepositoryPath, content));
        }

        return new(EditorResultCode.Accepted, edited.ToImmutable());
    }

    private static void CreateSecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

public sealed class EditorProcessRunner : IEditorProcessRunner
{
    public async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The configured editor could not be started.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The editor exited between the state check and termination request.
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return process.ExitCode;
    }
}

public sealed class ProcessUrlLauncher : IUrlLauncher
{
    private readonly Func<UrlLauncherPlatform> _platform;
    private readonly IUrlProcessRunner _processes;

    public ProcessUrlLauncher(
        Func<UrlLauncherPlatform>? platform = null,
        IUrlProcessRunner? processes = null)
    {
        _platform = platform ?? DetectPlatform;
        _processes = processes ?? new UrlProcessRunner();
    }

    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        UrlLauncherPlatform platform = _platform();
        string executable = platform switch
        {
            UrlLauncherPlatform.Windows => "explorer.exe",
            UrlLauncherPlatform.MacOS => "open",
            UrlLauncherPlatform.Linux => "xdg-open",
            _ => throw new PlatformNotSupportedException(
                "Opening pull request URLs is unsupported on this platform."),
        };
        int exitCode = await _processes.RunAsync(
            executable,
            [uri.AbsoluteUri],
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0
            && !(platform == UrlLauncherPlatform.Windows && exitCode == 1))
        {
            throw new InvalidOperationException(
                $"The pull request URL launcher exited with code {exitCode}.");
        }
    }

    private static UrlLauncherPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return UrlLauncherPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return UrlLauncherPlatform.MacOS;
        }

        if (OperatingSystem.IsLinux())
        {
            return UrlLauncherPlatform.Linux;
        }

        throw new PlatformNotSupportedException(
            "Opening pull request URLs is unsupported on this platform.");
    }
}

public sealed class UrlProcessRunner : IUrlProcessRunner
{
    private const int StartupObservationMilliseconds = 2000;
    private const int PollMilliseconds = 100;

    public Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The pull request URL launcher could not be started.");
        }

        for (int elapsed = 0; elapsed < StartupObservationMilliseconds; elapsed += PollMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.WaitForExit(PollMilliseconds))
            {
                return Task.FromResult(process.ExitCode);
            }
        }

        // xdg-open/open/explorer may hand the URL to a long-lived browser. Once startup has been
        // observed without an immediate error, release our process handle without owning or
        // killing the browser process tree.
        return Task.FromResult(0);
    }
}

internal static class EditorCommandLine
{
    public static IReadOnlyList<string> Parse(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                AddPart();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quote != '\0')
        {
            throw new FormatException("The configured editor command contains an unmatched quote.");
        }

        AddPart();
        return parts.Count == 0
            ? throw new FormatException("The configured editor command is empty.")
            : parts;

        void AddPart()
        {
            if (current.Length == 0)
            {
                return;
            }

            parts.Add(current.ToString());
            current.Clear();
        }
    }
}
