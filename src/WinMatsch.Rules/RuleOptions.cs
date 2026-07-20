using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>Options that influence how rules run.</summary>
public sealed class RuleOptions
{
    /// <summary>When set, rules record what they changed or found in <see cref="ManifestContext.Trace"/> (the <c>--explain</c> log).</summary>
    public bool Explain { get; init; }

    /// <summary>The locale the run targets, when relevant to a rule.</summary>
    public LanguageTag? Locale { get; init; }
}
