using System.Collections.Immutable;
using System.Diagnostics;
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
            files = [input];
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
            if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..")
            {
                repositoryPath = FindManifestSegment(file);
            }

            byte[] content = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            loaded.Add((file, content, repositoryPath));
        }

        string? inferredDirectory = loaded.Any(static item => item.RepositoryPath is null)
            ? InferVersionDirectory(loaded)
            : null;
        var documents = ImmutableArray.CreateBuilder<RawManifestDocument>(files.Length);
        foreach ((string file, byte[] content, string? repositoryPath) in loaded)
        {
            documents.Add(new(
                repositoryPath ?? $"{inferredDirectory}/{Path.GetFileName(file)}",
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

    public ProcessEditorRunner(
        Func<string, string?>? environment = null,
        IEditorProcessRunner? processes = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _processes = processes ?? new EditorProcessRunner();
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
        Directory.CreateDirectory(temporaryRoot);
        try
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

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(
                    destination,
                    document.Content.ToArray(),
                    cancellationToken).ConfigureAwait(false);
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
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
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
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return process.ExitCode;
    }
}

public sealed class ProcessUrlLauncher : IUrlLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        using Process? process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        if (process is null)
        {
            throw new InvalidOperationException("The pull request URL could not be opened.");
        }

        return Task.CompletedTask;
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
