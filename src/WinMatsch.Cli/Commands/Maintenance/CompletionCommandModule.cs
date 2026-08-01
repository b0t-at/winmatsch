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
        IReadOnlyList<string> GlobalOptionsWithValues,
        IReadOnlyList<string> GlobalBooleanOptions,
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
            var globalOptions = SanitizeNames(root.Options.Select(static option => option.Name));
            var globalOptionsWithValues = SanitizeNames(root.Options
                .Where(static option => option.ValueType != typeof(bool))
                .Select(static option => option.Name));
            var globalBooleanOptions = SanitizeNames(root.Options
                .Where(static option => option.ValueType == typeof(bool))
                .Select(static option => option.Name));
            var commands = new List<CompletionCommand>();
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
                    SanitizeDescription(command.Description),
                    subcommands,
                    options));
            }

            return new CompletionTree(
                ProductExecutableName,
                globalOptions,
                globalOptionsWithValues,
                globalBooleanOptions,
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

        internal static string SanitizeDescription(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            return new string(value
                    .Where(static character => !char.IsControl(character))
                    .Take(256)
                    .ToArray())
                .Trim();
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
        builder.Append("    local value_opts=\"").Append(Join(tree.GlobalOptionsWithValues)).Append("\"\n");
        builder.Append("    local bool_opts=\"").Append(Join(tree.GlobalBooleanOptions)).Append("\"\n");
        builder.Append("    local command=\"\" word option i skip_next=0\n");
        builder.Append("    for ((i=1; i<COMP_CWORD; i++)); do\n");
        builder.Append("        word=\"${COMP_WORDS[i]}\"\n");
        builder.Append("        if [ \"$skip_next\" -eq 1 ]; then skip_next=0; continue; fi\n");
        builder.Append("        option=\"${word%%=*}\"\n");
        builder.Append("        if [[ \" $value_opts \" == *\" $option \"* ]]; then\n");
        builder.Append("            [[ \"$word\" != *=* ]] && skip_next=1\n");
        builder.Append("            continue\n");
        builder.Append("        fi\n");
        builder.Append("        if [[ \" $bool_opts \" == *\" $option \"* ]]; then\n");
        builder.Append("            if [[ \"$word\" != *=* && \"${COMP_WORDS[i+1]}\" =~ ^(true|false)$ ]]; then ((i++)); fi\n");
        builder.Append("            continue\n");
        builder.Append("        fi\n");
        builder.Append("        [[ \"$word\" == -* ]] && continue\n");
        builder.Append("        command=\"$word\"\n");
        builder.Append("        break\n");
        builder.Append("    done\n");
        builder.Append("    if [ -z \"$command\" ]; then\n");
        builder.Append("        COMPREPLY=( $(compgen -W \"$commands $global_opts\" -- \"$cur\") )\n");
        builder.Append("        return 0\n");
        builder.Append("    fi\n");
        builder.Append("    case \"$command\" in\n");
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
        builder.Append("    local -a value_opts\n");
        builder.Append("    value_opts=(").Append(Join(tree.GlobalOptionsWithValues)).Append(")\n");
        builder.Append("    local -a bool_opts\n");
        builder.Append("    bool_opts=(").Append(Join(tree.GlobalBooleanOptions)).Append(")\n");
        builder.Append("    local command=\"\" word option\n");
        builder.Append("    local skip_next=0 i\n");
        builder.Append("    for ((i=2; i<CURRENT; i++)); do\n");
        builder.Append("        word=\"${words[i]}\"\n");
        builder.Append("        if (( skip_next )); then skip_next=0; continue; fi\n");
        builder.Append("        option=\"${word%%=*}\"\n");
        builder.Append("        if (( ${value_opts[(Ie)$option]} )); then\n");
        builder.Append("            [[ \"$word\" != *=* ]] && skip_next=1\n");
        builder.Append("            continue\n");
        builder.Append("        fi\n");
        builder.Append("        if (( ${bool_opts[(Ie)$option]} )); then\n");
        builder.Append("            if [[ \"$word\" != *=* && \"${words[i+1]}\" == (true|false) ]]; then ((i++)); fi\n");
        builder.Append("            continue\n");
        builder.Append("        fi\n");
        builder.Append("        [[ \"$word\" == -* ]] && continue\n");
        builder.Append("        command=\"$word\"\n");
        builder.Append("        break\n");
        builder.Append("    done\n");
        builder.Append("    if [[ -z \"$command\" ]]; then\n");
        builder.Append("        compadd -- ")
            .Append(Join(tree.Commands.Select(static c => c.Name).Concat(tree.GlobalOptions)))
            .Append('\n');
        builder.Append("        return\n");
        builder.Append("    fi\n");
        builder.Append("    case \"$command\" in\n");
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
        builder.Append("# ").Append(name).Append(" fish completion (generated; static)\n");
        builder.Append("function __").Append(name).Append("_command\n");
        builder.Append("    set -l tokens (commandline -opc)\n");
        builder.Append("    set -e tokens[1]\n");
        builder.Append("    set -l value_opts ").Append(Join(tree.GlobalOptionsWithValues)).Append('\n');
        builder.Append("    set -l bool_opts ").Append(Join(tree.GlobalBooleanOptions)).Append('\n');
        builder.Append("    set -l i 1\n");
        builder.Append("    while test $i -le (count $tokens)\n");
        builder.Append("        set -l word $tokens[$i]\n");
        builder.Append("        set -l option (string split -m 1 '=' -- $word)[1]\n");
        builder.Append("        if contains -- $option $value_opts\n");
        builder.Append("            if string match -q '*=*' -- $word; set i (math $i + 1); else; set i (math $i + 2); end\n");
        builder.Append("            continue\n");
        builder.Append("        end\n");
        builder.Append("        if contains -- $option $bool_opts\n");
        builder.Append("            if not string match -q '*=*' -- $word; and test (math $i + 1) -le (count $tokens); and contains -- $tokens[(math $i + 1)] true false\n");
        builder.Append("                set i (math $i + 2)\n");
        builder.Append("            else\n");
        builder.Append("                set i (math $i + 1)\n");
        builder.Append("            end\n");
        builder.Append("            continue\n");
        builder.Append("        end\n");
        builder.Append("        if string match -q -- '-*' $word; set i (math $i + 1); continue; end\n");
        builder.Append("        echo $word\n");
        builder.Append("        return 0\n");
        builder.Append("    end\n");
        builder.Append("    return 1\n");
        builder.Append("end\n");
        builder.Append("function __").Append(name).Append("_is_command\n");
        builder.Append("    test (__").Append(name).Append("_command) = $argv[1]\n");
        builder.Append("end\n");
        builder.Append("complete -c ").Append(name).Append(" -f\n");
        foreach (CompletionCommand command in tree.Commands)
        {
            builder.Append("complete -c ").Append(name)
                .Append(" -n \"not __").Append(name).Append("_command >/dev/null\"")
                .Append(" -a ").Append(command.Name);
            if (command.Description.Length > 0)
            {
                builder.Append(" -d ").Append(QuoteFish(command.Description));
            }

            builder.Append('\n');
            if (command.Subcommands.Count > 0)
            {
                builder.Append("complete -c ").Append(name)
                    .Append(" -n \"__").Append(name).Append("_is_command ").Append(command.Name).Append('"')
                    .Append(" -a \"").Append(Join(command.Subcommands)).Append("\"\n");
            }

            foreach (string option in command.Options.Where(static option =>
                option.StartsWith("--", StringComparison.Ordinal)))
            {
                builder.Append("complete -c ").Append(name)
                    .Append(" -n \"__").Append(name).Append("_is_command ").Append(command.Name).Append('"')
                    .Append(" -l ").Append(option[2..]).Append('\n');
            }
        }

        foreach (string option in tree.GlobalOptions.Where(static option =>
            option.StartsWith("--", StringComparison.Ordinal)))
        {
            builder.Append("complete -c ").Append(name).Append(" -l ").Append(option[2..]);
            if (tree.GlobalOptionsWithValues.Contains(option, StringComparer.Ordinal))
            {
                builder.Append(" -r");
            }

            builder.Append('\n');
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
        builder.Append("    $tree = @{}\n");
        builder.Append("    $tree[''] = @(").Append(JoinQuoted(
            tree.Commands.Select(static c => c.Name).Concat(tree.GlobalOptions))).Append(")\n");
        foreach (CompletionCommand command in tree.Commands)
        {
            builder.Append("    $tree['").Append(command.Name).Append("'] = @(")
                .Append(JoinQuoted(command.Subcommands.Concat(command.Options).Concat(tree.GlobalOptions)))
                .Append(")\n");
        }

        builder.Append("    $elements = $commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() }\n");
        builder.Append("    $valueOptions = @(").Append(JoinQuoted(tree.GlobalOptionsWithValues)).Append(")\n");
        builder.Append("    $booleanOptions = @(").Append(JoinQuoted(tree.GlobalBooleanOptions)).Append(")\n");
        builder.Append("    $first = $null\n");
        builder.Append("    for ($i = 0; $i -lt $elements.Count; $i++) {\n");
        builder.Append("        $element = $elements[$i]\n");
        builder.Append("        $option = ($element -split '=', 2)[0]\n");
        builder.Append("        if ($valueOptions -contains $option) {\n");
        builder.Append("            if ($element -notlike '*=*') { $i++ }\n");
        builder.Append("            continue\n");
        builder.Append("        }\n");
        builder.Append("        if ($booleanOptions -contains $option) {\n");
        builder.Append("            if ($element -notlike '*=*' -and $i + 1 -lt $elements.Count -and $elements[$i + 1] -in @('true', 'false')) { $i++ }\n");
        builder.Append("            continue\n");
        builder.Append("        }\n");
        builder.Append("        if ($element -like '-*') { continue }\n");
        builder.Append("        $first = $element\n");
        builder.Append("        break\n");
        builder.Append("    }\n");
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

    private static string QuoteFish(string value)
        => "'" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal) + "'";
}
