using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Output;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Validation;
using WinMatsch.Workflows.Diagnostics;

namespace WinMatsch.Cli.Commands.Diagnostics;

public sealed class DiagnosticsCommandModule : ICommandModule
{
    private readonly IInstallerDiagnosticService _installerDiagnostics;
    private readonly IManifestValidationService _manifestValidation;
    private readonly Func<string, IRepositoryDiagnosticService>? _repositoryServiceFactory;
    private readonly Func<GitHubClientOptions, string?, IRepositoryDiagnosticService>
        _publicRepositoryServiceFactory;

    public DiagnosticsCommandModule(
        IInstallerDiagnosticService? installerDiagnostics = null,
        IManifestValidationService? manifestValidation = null,
        Func<string, IRepositoryDiagnosticService>? repositoryServiceFactory = null,
        Func<GitHubClientOptions, string?, IRepositoryDiagnosticService>?
            publicRepositoryServiceFactory = null)
    {
        _installerDiagnostics = installerDiagnostics ?? new InstallerDiagnosticService();
        _manifestValidation = manifestValidation ?? new ManifestValidationService();
        _repositoryServiceFactory = repositoryServiceFactory;
        _publicRepositoryServiceFactory = publicRepositoryServiceFactory
            ?? CreatePublicRepositoryService;
    }

    public string Name => "diagnostics";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RegisterAnalyze(registry);
        RegisterValidate(registry);
        RegisterShow(registry);
        RegisterListVersions(registry);
    }

    private void RegisterAnalyze(ICommandRegistry registry)
    {
        var source = new Argument<string>("source")
        {
            Description = "Local installer path or HTTPS URL.",
        };
        var command = new Command(
            "analyze",
            "Analyze an installer without generating or changing manifests.")
        {
            Arguments = { source },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            string input = context.ParseResult.GetValue(source)
                ?? throw new CliUsageException("An installer path or HTTPS URL is required.");
            try
            {
                InstallerDiagnosticResult result = await context.Interaction.RunProgressAsync(
                    "Downloading and analyzing installer",
                    cancellation => _installerDiagnostics.AnalyzeAsync(
                        new InstallerAnalysisRequest(
                            input,
                            context.Configuration.CacheEnabled,
                            context.Configuration.CacheDirectory),
                        cancellation),
                    context.CancellationToken)
                    .ConfigureAwait(false);
                WriteAnalyzeResult(context, result);
                return ExitCodes.Success;
            }
            catch (OperationCanceledException exception)
                when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new CliOperationException(
                    $"Installer analysis failed: the remote request timed out. {exception.Message}",
                    exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                throw new CliOperationException(
                    $"Installer analysis failed: {exception.Message}",
                    exception);
            }
        });
    }

    private void RegisterValidate(ICommandRegistry registry)
    {
        var paths = new Argument<string[]>("paths")
        {
            Description = "Manifest directory or one or more local YAML manifest files.",
            Arity = ArgumentArity.OneOrMore,
        };
        var offline = new Option<bool>("--offline")
        {
            Description = "Skip optional live metadata checks; mandatory origin/hash validation remains blocking.",
        };
        var warningsAsErrors = new Option<bool>("--warnings-as-errors")
        {
            Description = "Treat validation warnings as blocking findings.",
        };
        var command = new Command(
            "validate",
            "Validate a local multi-file manifest set without changing files or repositories.")
        {
            Arguments = { paths },
            Options = { offline, warningsAsErrors },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            string[] inputs = context.ParseResult.GetValue(paths) ?? [];
            WarningPolicy warningPolicy = context.ParseResult.GetValue(warningsAsErrors)
                ? WarningPolicy.TreatAsErrors
                : WarningPolicy.Allow;
            try
            {
                ManifestValidationResult result = await context.Interaction.RunProgressAsync(
                    "Downloading and validating manifests",
                    cancellation => _manifestValidation.ValidateAsync(
                        new ManifestValidationRequest(
                            inputs,
                            context.ParseResult.GetValue(offline),
                            warningPolicy,
                            context.Configuration.CacheEnabled,
                            context.Configuration.CacheDirectory,
                            context.Configuration.ConcurrentDownloads),
                        cancellation),
                    context.CancellationToken)
                    .ConfigureAwait(false);
                WriteValidationResult(context, result);
                return result.Report.CanProceed(warningPolicy)
                    ? ExitCodes.Success
                    : ExitCodes.OperationFailed;
            }
            catch (OperationCanceledException exception)
                when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new CliOperationException(
                    $"Manifest validation failed: the remote request timed out. {exception.Message}",
                    exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                throw new CliOperationException(
                    $"Manifest validation failed: {exception.Message}",
                    exception);
            }
        });
    }

    private void RegisterShow(ICommandRegistry registry)
    {
        var identifier = new Argument<string>("package")
        {
            Description = "Exact package identifier, including repository casing.",
        };
        var version = new Argument<string>("version")
        {
            Description = "Exact package version, including repository casing.",
        };
        var raw = new Option<bool>("--raw")
        {
            Description = "Display repository bytes instead of normalized readable YAML.",
        };
        var command = new Command(
            "show",
            "Read one exact package version from the configured repository.")
        {
            Arguments = { identifier, version },
            Options = { raw },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            PackageIdentifier package = ParseIdentifier(context.ParseResult.GetValue(identifier));
            PackageVersion packageVersion = ParseVersion(context.ParseResult.GetValue(version));
            using IRepositoryDiagnosticService service = await CreateRepositoryServiceAsync(context)
                .ConfigureAwait(false);
            try
            {
                PackageVersionResult result = await service
                    .GetPackageVersionAsync(
                        context.Configuration.Repository,
                        package,
                        packageVersion,
                        normalize: !context.ParseResult.GetValue(raw),
                        context.CancellationToken)
                    .ConfigureAwait(false);
                WriteShowResult(context, result);
                return ExitCodes.Success;
            }
            catch (OperationCanceledException exception)
                when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new CliOperationException(
                    $"Repository read failed: the remote request timed out. {exception.Message}",
                    exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                throw new CliOperationException(
                    $"Repository read failed: {exception.Message}",
                    exception);
            }
        });
    }

    private void RegisterListVersions(ICommandRegistry registry)
    {
        var identifier = new Argument<string>("package")
        {
            Description = "Exact package identifier, including repository casing.",
        };
        var skip = new Option<int?>("--skip")
        {
            Description = "Number of newest versions to skip (default: 0).",
            HelpName = "count",
        };
        var limit = new Option<int?>("--limit")
        {
            Description = "Maximum versions to return (default: 100).",
            HelpName = "count",
        };
        var command = new Command(
            "list-versions",
            "List versions of one package from the configured repository.")
        {
            Arguments = { identifier },
            Options = { skip, limit },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, async context =>
        {
            PackageIdentifier package = ParseIdentifier(context.ParseResult.GetValue(identifier));
            int pageSkip = context.ParseResult.GetValue(skip) ?? 0;
            int pageLimit = context.ParseResult.GetValue(limit) ?? 100;
            if (pageSkip < 0)
            {
                throw new CliUsageException("--skip must be zero or greater.");
            }

            if (pageLimit is < 1 or > 1000)
            {
                throw new CliUsageException("--limit must be between 1 and 1000.");
            }

            using IRepositoryDiagnosticService service = await CreateRepositoryServiceAsync(context)
                .ConfigureAwait(false);
            try
            {
                PackageVersionsResult result = await service
                    .ListVersionsAsync(
                        context.Configuration.Repository,
                        package,
                        pageSkip,
                        pageLimit,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                WriteVersionsResult(context, result);
                return ExitCodes.Success;
            }
            catch (OperationCanceledException exception)
                when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new CliOperationException(
                    $"Repository read failed: the remote request timed out. {exception.Message}",
                    exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                throw new CliOperationException(
                    $"Repository read failed: {exception.Message}",
                    exception);
            }
        });
    }

    private async Task<IRepositoryDiagnosticService> CreateRepositoryServiceAsync(
        CommandContext context)
    {
        if (_repositoryServiceFactory is not null)
        {
            ResolvedToken required = await context.Tokens
                .RequireAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return _repositoryServiceFactory(required.Token.RevealValue());
        }

        ResolvedToken? optional;
        try
        {
            optional = await context.Tokens
                .ResolveAsync(context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (TokenStoreException exception)
        {
            context.Output.WriteDiagnostic(
                $"The token keyring is unavailable; continuing with an anonymous public read: "
                + exception.Message);
            optional = null;
        }

        return _publicRepositoryServiceFactory(
            context.GitHubOptions,
            optional?.Token.RevealValue());
    }

    private static RepositoryDiagnosticService CreatePublicRepositoryService(
        GitHubClientOptions options,
        string? token)
        => new(new PublicReadOnlyGitHubClient(options, token));

    private static PackageIdentifier ParseIdentifier(string? value)
    {
        try
        {
            return new PackageIdentifier(
                value ?? throw new ArgumentException("A package identifier is required."));
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException($"Invalid package identifier: {exception.Message}", exception);
        }
    }

    private static PackageVersion ParseVersion(string? value)
    {
        try
        {
            return new PackageVersion(
                value ?? throw new ArgumentException("A package version is required."));
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException($"Invalid package version: {exception.Message}", exception);
        }
    }

    private static bool IsOperationalFailure(Exception exception)
        => exception is FormatException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or HttpRequestException
            or JsonException
            or DownloadException
            or GitHubApiException
            or DiagnosticNotFoundException;

    private static void WriteAnalyzeResult(
        CommandContext context,
        InstallerDiagnosticResult result)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Source: {RedactInput(result)}");
                writer.WriteLine($"File: {result.FileName}");
                writer.WriteLine($"SHA-256: {result.Sha256}");
                writer.WriteLine($"Size: {result.SizeInBytes.ToString(CultureInfo.InvariantCulture)} bytes");
                writer.WriteLine($"Format: {ToCamelCase(result.Analysis.Format)}");
                writer.WriteLine($"Confidence: {result.Confidence}");
                writer.WriteLine($"Product: {result.Analysis.ProductName ?? "-"}");
                writer.WriteLine($"Publisher: {result.Analysis.Publisher ?? "-"}");
                writer.WriteLine($"Version: {result.Analysis.ProductVersion ?? "-"}");
                writer.WriteLine("Installers:");
                for (int index = 0; index < result.Analysis.Installers.Count; index++)
                {
                    Installer installer = result.Analysis.Installers[index];
                    writer.WriteLine(
                        $"  {index + 1}: architecture={installer.Architecture?.ToString().ToLowerInvariant() ?? "unknown"}, "
                        + $"type={installer.InstallerType?.ToString().ToLowerInvariant() ?? "unknown"}, "
                        + $"productCode={installer.ProductCode ?? "-"}, "
                        + $"packageFamilyName={installer.PackageFamilyName ?? "-"}");
                }

                writer.WriteLine("Dependencies:");
                foreach (DependencyEvidence evidence in result.Dependencies.Evidence)
                {
                    writer.WriteLine(
                        $"  {evidence.PayloadPath}: {ToCamelCase(evidence.Kind)}={ToCamelCase(evidence.Status)}"
                        + (evidence.RuntimeMajor is { } major ? $" runtimeMajor={major}" : ""));
                }

                writer.WriteLine("Diagnostics:");
                foreach (AnalysisDiagnostic diagnostic in result.Analysis.Diagnostics)
                {
                    writer.WriteLine(
                        $"  {diagnostic.Code}: {diagnostic.Message}"
                        + (diagnostic.RequiresManualAnalysis ? " [manual analysis required]" : ""));
                }
            },
            writer => WriteAnalyzeJson(writer, result));

    private static void WriteAnalyzeJson(Utf8JsonWriter writer, InstallerDiagnosticResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("input", RedactInput(result));
        writer.WriteString("fileName", result.FileName);
        writer.WriteBoolean("remote", result.IsRemote);
        writer.WriteBoolean("fromCache", result.IsFromCache);
        writer.WriteString("sha256", result.Sha256);
        writer.WriteNumber("sizeInBytes", result.SizeInBytes);
        CliJson.WriteEnum(writer, "format", result.Analysis.Format);
        writer.WriteString("confidence", result.Confidence);
        writer.WriteStartObject("product");
        WriteNullableString(writer, "name", result.Analysis.ProductName);
        WriteNullableString(writer, "publisher", result.Analysis.Publisher);
        WriteNullableString(writer, "version", result.Analysis.ProductVersion);
        WriteNullableString(writer, "copyright", result.Analysis.Copyright);
        writer.WriteEndObject();
        writer.WriteStartArray("installers");
        foreach (Installer installer in result.Analysis.Installers)
        {
            writer.WriteStartObject();
            WriteNullableEnum(writer, "architecture", installer.Architecture);
            WriteNullableEnum(writer, "installerType", installer.InstallerType);
            WriteNullableEnum(writer, "nestedInstallerType", installer.NestedInstallerType);
            WriteNullableString(writer, "productCode", installer.ProductCode);
            WriteNullableString(writer, "packageFamilyName", installer.PackageFamilyName);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("dependencies");
        foreach (DependencyEvidence evidence in result.Dependencies.Evidence)
        {
            writer.WriteStartObject();
            writer.WriteString("payloadPath", evidence.PayloadPath);
            WriteNullableEnum(writer, "architecture", evidence.Architecture);
            CliJson.WriteEnum(writer, "kind", evidence.Kind);
            CliJson.WriteEnum(writer, "status", evidence.Status);
            if (evidence.RuntimeMajor is { } major)
            {
                writer.WriteNumber("runtimeMajor", major);
            }
            else
            {
                writer.WriteNull("runtimeMajor");
            }

            writer.WriteStartArray("signals");
            foreach (string signal in evidence.Signals)
            {
                writer.WriteStringValue(signal);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("diagnostics");
        foreach (AnalysisDiagnostic diagnostic in result.Analysis.Diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("message", diagnostic.Message);
            writer.WriteBoolean("requiresManualAnalysis", diagnostic.RequiresManualAnalysis);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteValidationResult(
        CommandContext context,
        ManifestValidationResult result)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Network mode: {result.NetworkMode.ToString().ToLowerInvariant()}");
                writer.WriteLine($"Warning policy: {ToCamelCase(result.WarningPolicy)}");
                writer.WriteLine($"Files: {result.Files.Count.ToString(CultureInfo.InvariantCulture)}");
                if (result.Report.Findings.Count == 0)
                {
                    writer.WriteLine("valid: no findings");
                }
                else
                {
                    writer.Write(result.Report.ToText());
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("isValid", result.Report.IsValid);
                writer.WriteBoolean("canProceed", result.Report.CanProceed(result.WarningPolicy));
                CliJson.WriteEnum(
                    writer,
                    "networkMode",
                    result.NetworkMode,
                    result.NetworkMode.ToString().ToLowerInvariant());
                CliJson.WriteEnum(writer, "warningPolicy", result.WarningPolicy);
                writer.WriteStartArray("files");
                foreach (string file in result.Files)
                {
                    writer.WriteStringValue(file);
                }

                writer.WriteEndArray();
                WriteFindings(writer, result.Report);
                writer.WriteEndObject();
            });

    private static void WriteFindings(Utf8JsonWriter writer, ValidationReport report)
    {
        writer.WriteStartArray("findings");
        foreach (ValidationFinding finding in report.Findings)
        {
            writer.WriteStartObject();
            writer.WriteString("code", finding.Code);
            CliJson.WriteEnum(writer, "severity", finding.Severity);
            writer.WriteString("message", finding.Message);
            if (finding.Path is null)
            {
                writer.WriteNull("path");
            }
            else
            {
                writer.WriteString("path", finding.Path);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteShowResult(CommandContext context, PackageVersionResult result)
        => context.Output.WriteFormatted(
            writer =>
            {
                foreach (RepositoryManifestFile file in result.Files)
                {
                    writer.WriteLine($"--- {file.Path} ---");
                    writer.Write(file.Content);
                    if (!file.Content.EndsWith('\n'))
                    {
                        writer.WriteLine();
                    }
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("repository", result.Repository.ToString());
                writer.WriteString("reference", result.Reference);
                writer.WriteString("packageIdentifier", result.Identifier.Value);
                writer.WriteString("packageVersion", result.Version.Value);
                writer.WriteBoolean("normalized", result.Normalized);
                writer.WriteStartArray("files");
                foreach (RepositoryManifestFile file in result.Files)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", file.Path);
                    writer.WriteString("content", file.Content);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });

    private static void WriteVersionsResult(CommandContext context, PackageVersionsResult result)
        => context.Output.WriteFormatted(
            writer =>
            {
                foreach (PackageVersion version in result.Versions)
                {
                    writer.WriteLine(version.Value);
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("repository", result.Repository.ToString());
                writer.WriteString("reference", result.Reference);
                writer.WriteString("packageIdentifier", result.Identifier.Value);
                writer.WriteNumber("skip", result.Skip);
                writer.WriteNumber("limit", result.Limit);
                writer.WriteNumber("total", result.Total);
                writer.WriteStartArray("versions");
                foreach (PackageVersion version in result.Versions)
                {
                    writer.WriteStringValue(version.Value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableEnum<T>(Utf8JsonWriter writer, string name, T? value)
        where T : struct, Enum
    {
        CliJson.WriteNullableEnum(writer, name, value);
    }

    private static string ToCamelCase<T>(T value)
        where T : struct, Enum
        => CliJson.EnumValue(value);

    private static string RedactInput(InstallerDiagnosticResult result)
        => result.IsRemote
            ? CliRedactor.RedactUrl(result.Input, redactAllQueryValues: true)
            : CliRedactor.Redact(result.Input);
}
