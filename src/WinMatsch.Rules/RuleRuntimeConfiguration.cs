using System.Collections.ObjectModel;

namespace WinMatsch.Rules;

/// <summary>
/// Immutable inputs used to resolve each rule's mode. Resolution is always command override,
/// package override, user configuration, then the default.
/// </summary>
public sealed class RuleRuntimeConfiguration
{
    private readonly ReadOnlyDictionary<string, RuleMode> _commandOverrides;
    private readonly ReadOnlyDictionary<string, RuleMode> _userOverrides;

    public RuleRuntimeConfiguration(
        RuleMode defaultMode = RuleMode.Apply,
        IReadOnlyDictionary<string, RuleMode>? userOverrides = null,
        IReadOnlyDictionary<string, RuleMode>? commandOverrides = null)
    {
        DefaultMode = defaultMode;
        _userOverrides = Copy(userOverrides);
        _commandOverrides = Copy(commandOverrides);
    }

    public RuleMode DefaultMode { get; }

    public IReadOnlyDictionary<string, RuleMode> UserOverrides => _userOverrides;

    public IReadOnlyDictionary<string, RuleMode> CommandOverrides => _commandOverrides;

    internal RuleModeResolution Resolve(
        string ruleId,
        IReadOnlyDictionary<string, RuleMode>? packageOverrides)
    {
        if (_commandOverrides.TryGetValue(ruleId, out RuleMode command))
        {
            return new(command, RuleModeSource.CommandOverride);
        }

        if (packageOverrides is not null && packageOverrides.TryGetValue(ruleId, out RuleMode package))
        {
            return new(package, RuleModeSource.PackageOverride);
        }

        if (_userOverrides.TryGetValue(ruleId, out RuleMode user))
        {
            return new(user, RuleModeSource.UserConfig);
        }

        return new(DefaultMode, RuleModeSource.Default);
    }

    internal static RuleRuntimeConfiguration FromDisabled(IEnumerable<string>? disabledRuleIds)
    {
        if (disabledRuleIds is null)
        {
            return new();
        }

        var commandOverrides = new Dictionary<string, RuleMode>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in disabledRuleIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            commandOverrides[id] = RuleMode.Disabled;
        }

        return new(commandOverrides: commandOverrides);
    }

    private static ReadOnlyDictionary<string, RuleMode> Copy(IReadOnlyDictionary<string, RuleMode>? source)
    {
        var copy = new Dictionary<string, RuleMode>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach ((string id, RuleMode mode) in source)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                copy.Add(id, mode);
            }
        }

        return new ReadOnlyDictionary<string, RuleMode>(copy);
    }
}

/// <summary>The effective mode and the layer that supplied it.</summary>
public sealed record RuleModeResolution(RuleMode Mode, RuleModeSource Source);
