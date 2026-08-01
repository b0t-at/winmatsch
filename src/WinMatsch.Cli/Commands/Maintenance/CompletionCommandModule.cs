using System.CommandLine;
using System.Text;
using WinMatsch.Cli.Hosting;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>
/// Generates static shell completion scripts (<c>completion bash|zsh|fish|powershell</c>) from
/// the composed command tree at generation time. The scripts embed only sanitized literal
/// command and option names — no dynamic callback, network, or token access — and are written
/// to standard output only. The workflow lifecycle command keeps the name <c>complete</c>;
/// shell scripts live under <c>completion</c>.
/// </summary>
public sealed class CompletionCommandModule : ICommandModule
{
    private static readonly string[] _shells = ["bash", "zsh", "fish", "powershell"];

    public string Name => "completion";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var shell = new Argument<string>("shell")
        {
            Description = "The shell to generate a completion script for: bash, zsh, fish, or powershell.",
        };
        shell.AcceptOnlyFromAmong(_shells);
        var command = new Command(
            "completion",
            "Write a static shell completion script to standard output.")
        {
            Arguments = { shell },
        };

        registry.AddCommand(command);
        registry.SetHandler(command, context =>
        {
            string shellName = context.ParseResult.GetValue(shell)
                ?? throw new CliUsageException("A shell name is required.");
            Command root = context.ParseResult.RootCommandResult.Command;
            CompletionTree tree = CompletionTree.From(root);
            string script = shellName switch
            {
                "bash" => GenerateBash(tree),
                "zsh" => GenerateZsh(tree),
                "fish" => GenerateFish(tree),
                "powershell" => GeneratePowerShell(tree),
                _ => throw new CliUsageException(
                    $"'{shellName}' is not a supported shell. Use bash, zsh, fish, or powershell."),
            };

            context.Output.WriteFormatted(
                writer => writer.Write(script),
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("shell", shellName);
                    writer.WriteString("script", script);
                    writer.WriteEndObject();
                });
            return Task.FromResult(ExitCodes.Success);
        });
    }

    /// <summary>A sanitized, sorted snapshot of the visible command tree.</summary>
    internal sealed record CompletionTree(
        string ExecutableName,
        IReadOnlyList<string> GlobalOptions,
        IReadOnlyList<CompletionCommand> Commands)
    {
        /// <summary>
        /// The published executable name. Fixed so generated scripts stay deterministic and
        /// correct regardless of the process the generator happens to run in.
        /// </summary>
        internal const string ProductExecutableName = "winmatsch";

        public static CompletionTree From(Command root)
        {
            ArgumentNullException.ThrowIfNull(root);
            var globalOptions = SanitizeNames(root.Options.Select(static option => option.Name)); var commands = new List<CompletionCommand>();
            foreach (Command command in root.Subcommands.Where(static command => !command.Hidden))
            {
                var subcommands = SanitizeNames(command.Subcommands
                    .Where(static subcommand => !subcommand.Hidden)
                    .Select(static subcommand => subcommand.Name));
                var options = SanitizeNames(command.Options
                    .Concat(command.Subcommands
                        .Where(static subcommand => !subcommand.Hidden)
                        .SelectMany(static subcommand => subcommand.Options))
                    .Select(static option => option.Name));
                commands.Add(new CompletionCommand(
                    Sanitize(command.Name) ?? "",
                    Sanitize(command.Description) ?? "",
                    subcommands,
                    options));
            }

            return new CompletionTree(
                ProductExecutableName,
                globalOptions,
                [.. commands
                    .Where(static command => command.Name.Length > 0)
                    .OrderBy(static command => command.Name, StringComparer.Ordinal)]);
        }

        private static IReadOnlyList<string> SanitizeNames(IEnumerable<string> names)
            =>
            [
                .. names
                    .Select(Sanitize)
                    .OfType<string>()
                    .Where(static name => name.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static name => name, StringComparer.Ordinal),
            ];

        /// <summary>
        /// Keeps only characters that are inert in every generated shell context; a name that
        /// loses characters is dropped entirely rather than emitted mangled.
        /// </summary>
        internal static string? Sanitize(string? value)
        {
            if (value is null)
            {
                return null;
            }

            return value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
                ? value
                : null;
        }
    }

    internal sealed record CompletionCommand(
        string Name,
        string Description,
        IReadOnlyList<string> Subcommands,
        IReadOnlyList<string> Options);

    private static string GenerateBash(CompletionTree tree)
    {
        var builder = new StringBuilder();
        string name = tree.ExecutableName;
        builder.Append("# ").Append(name).Append(" bash completion (generated; static)\n");
        builder.Append('_').Append(name).Append("()\n{\n");
        builder.Append("    local cur=\"${COMP_WORDS[COMP_CWORD]}\"\n");
        builder.Append("    local commands=\"").Append(Join(tree.Commands.Select(static c => c.Name))).Append("\"\n");
        builder.Append("    local global_opts=\"").Append(Join(tree.GlobalOptions)).Append("\"\n");
        builder.Append("    if [ \"$COMP_CWORD\" -eq 1 ]; then\n");
        builder.Append("        COMPREPLY=( $(compgen -W \"$commands $global_opts\" -- \"$cur\") )\n");
        builder.Append("        return 0\n");
        builder.Append("    fi\n");
        builder.Append("    case \"${COMP_WORDS[1]}\" in\n");
        foreach (CompletionCommand command in tree.Commands)
        {
            builder.Append("        ").Append(command.Name).Append(")\n");
            builder.Append("            COMPREPLY=( $(compgen -W \"")
                .Append(Join(command.Subcommands.Concat(command.Options).Concat(tree.GlobalOptions)))
                .Append("\" -- \"$cur\") )\n");
            builder.Append("            ;;\n");
        }

        builder.Append("        *)\n");
        builder.Append("            COMPREPLY=( $(compgen -W \"$global_opts\" -- \"$cur\") )\n");
        builder.Append("            ;;\n");
        builder.Append("    esac\n");
        builder.Append("    return 0\n");
        builder.Append("}\n");
        builder.Append("complete -F _").Append(name).Append(' ').Append(name).Append('\n');
        return builder.ToString();
    }

    private static string GenerateZsh(CompletionTree tree)
    {
        var builder = new StringBuilder();
        string name = tree.ExecutableName;
        builder.Append("#compdef ").Append(name).Append('\n');
        builder.Append("# ").Append(name).Append(" zsh completion (generated; static)\n");
        builder.Append('_').Append(name).Append("()\n{\n");
        builder.Append("    if (( CURRENT == 2 )); then\n");
        builder.Append("        compadd -- ")
            .Append(Join(tree.Commands.Select(static c => c.Name).Concat(tree.GlobalOptions)))
            .Append('\n');
        builder.Append("        return\n");
        builder.Append("    fi\n");
        builder.Append("    case \"${words[2]}\" in\n");
        foreach (CompletionCommand command in tree.Commands)
        {
            builder.Append("        ").Append(command.Name).Append(")\n");
            builder.Append("            compadd -- ")
                .Append(Join(command.Subcommands.Concat(command.Options).Concat(tree.GlobalOptions)))
                .Append('\n');
            builder.Append("            ;;\n");
        }

        builder.Append("        *)\n");
        builder.Append("            compadd -- ").Append(Join(tree.GlobalOptions)).Append('\n');
        builder.Append("            ;;\n");
        builder.Append("    esac\n");
        builder.Append("}\n");
        builder.Append('_').Append(name).Append(" \"$@\"\n");
        return builder.ToString();
    }

    private static string GenerateFish(CompletionTree tree)
    {
        var builder = new StringBuilder();
        string name = tree.ExecutableName;
        string allCommands = Join(tree.Commands.Select(static c => c.Name));
        builder.Append("# ").Append(name).Append(" fish completion (generated; static)\n");
        builder.Append("complete -c ").Append(name).Append(" -f\n");
        foreach (CompletionCommand command in tree.Commands)
        {
            builder.Append("complete -c ").Append(name)
                .Append(" -n \"not __fish_seen_subcommand_from ").Append(allCommands).Append('"')
                .Append(" -a ").Append(command.Name);
            if (command.Description.Length > 0)
            {
                builder.Append(" -d \"").Append(command.Description).Append('"');
            }

            builder.Append('\n');
            if (command.Subcommands.Count > 0)
            {
                builder.Append("complete -c ").Append(name)
                    .Append(" -n \"__fish_seen_subcommand_from ").Append(command.Name).Append('"')
                    .Append(" -a \"").Append(Join(command.Subcommands)).Append("\"\n");
            }

            foreach (string option in command.Options.Where(static option =>
                option.StartsWith("--", StringComparison.Ordinal)))
            {
                builder.Append("complete -c ").Append(name)
                    .Append(" -n \"__fish_seen_subcommand_from ").Append(command.Name).Append('"')
                    .Append(" -l ").Append(option[2..]).Append('\n');
            }
        }

        foreach (string option in tree.GlobalOptions.Where(static option =>
            option.StartsWith("--", StringComparison.Ordinal)))
        {
            builder.Append("complete -c ").Append(name).Append(" -l ").Append(option[2..]).Append('\n');
        }

        return builder.ToString();
    }

    private static string GeneratePowerShell(CompletionTree tree)
    {
        var builder = new StringBuilder();
        string name = tree.ExecutableName;
        builder.Append("# ").Append(name).Append(" PowerShell completion (generated; static)\n");
        builder.Append("Register-ArgumentCompleter -Native -CommandName '").Append(name)
            .Append("' -ScriptBlock {\n");
        builder.Append("    param($wordToComplete, $commandAst, $cursorPosition)\n");
        builder.Append("    $tree = @{\n");
        builder.Append("        '' = @(").Append(JoinQuoted(
            tree.Commands.Select(static c => c.Name).Concat(tree.GlobalOptions))).Append(")\n");
        foreach (CompletionCommand command in tree.Commands)
        {
            builder.Append("        '").Append(command.Name).Append("' = @(")
                .Append(JoinQuoted(command.Subcommands.Concat(command.Options).Concat(tree.GlobalOptions)))
                .Append(")\n");
        }

        builder.Append("    }\n");
        builder.Append("    $elements = $commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() }\n");
        builder.Append("    $first = ($elements | Where-Object { $_ -notlike '-*' } | Select-Object -First 1)\n");
        builder.Append("    $key = if ($null -ne $first -and $tree.ContainsKey($first) -and $first -ne $wordToComplete) { $first } else { '' }\n");
        builder.Append("    $tree[$key] | Where-Object { $_ -like \"$wordToComplete*\" } | Sort-Object | ForEach-Object {\n");
        builder.Append("        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)\n");
        builder.Append("    }\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    private static string Join(IEnumerable<string> values)
        => string.Join(' ', values.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal));

    private static string JoinQuoted(IEnumerable<string> values)
        => string.Join(", ", values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Select(static value => "'" + value + "'"));
}
