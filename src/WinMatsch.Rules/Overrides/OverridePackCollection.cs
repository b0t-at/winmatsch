using System.Collections.Immutable;
using System.Collections.ObjectModel;
using WinMatsch.Core;

namespace WinMatsch.Rules.OverridePacks;

/// <summary>An immutable, case-insensitive package override index.</summary>
public sealed class OverridePackSet
{
    private readonly ReadOnlyDictionary<string, OverridePack> _packs;
    private readonly ImmutableArray<OverridePack> _packValues;

    public OverridePackSet(IEnumerable<OverridePack>? packs = null)
    {
        var values = new Dictionary<string, OverridePack>(StringComparer.OrdinalIgnoreCase);
        if (packs is not null)
        {
            foreach (OverridePack pack in packs)
            {
                ArgumentNullException.ThrowIfNull(pack);
                values.Add(pack.PackageIdentifier.Value, pack);
            }
        }

        _packs = new ReadOnlyDictionary<string, OverridePack>(values);
        _packValues = [.. values.Values];
    }

    public static OverridePackSet Empty { get; } = new();

    public static OverridePackSet BuiltIn { get; } = LoadBuiltIn();

    public ImmutableArray<OverridePack> Packs => _packValues;

    public bool TryGet(PackageIdentifier? packageIdentifier, out OverridePack? pack)
    {
        if (packageIdentifier is null)
        {
            pack = null;
            return false;
        }

        return _packs.TryGetValue(packageIdentifier.Value, out pack);
    }

    /// <summary>
    /// Composes lower-precedence learned/built-in packs with explicit request packs without
    /// mutating either source. Explicit scalar and keyed values win; safety lists are merged.
    /// </summary>
    public static OverridePackSet Compose(
        OverridePackSet? lowerPrecedence,
        OverridePackSet? higherPrecedence)
    {
        var values = new Dictionary<string, OverridePack>(StringComparer.OrdinalIgnoreCase);
        foreach (OverridePack pack in lowerPrecedence?.Packs ?? [])
        {
            values.Add(pack.PackageIdentifier.Value, pack);
        }

        foreach (OverridePack pack in higherPrecedence?.Packs ?? [])
        {
            values[pack.PackageIdentifier.Value] = values.TryGetValue(
                pack.PackageIdentifier.Value,
                out OverridePack? lower)
                ? Merge(lower, pack)
                : pack;
        }

        return new(values.Values);
    }

    public static OverridePack Merge(OverridePack lower, OverridePack higher)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(higher);
        if (lower.PackageIdentifier != higher.PackageIdentifier)
        {
            throw new ArgumentException("Override packs can only be merged for the same package identifier.");
        }

        return new()
        {
            FormatVersion = OverridePack.CurrentFormatVersion,
            PackageIdentifier = higher.PackageIdentifier,
            RuleModes = lower.RuleModes.SetItems(higher.RuleModes),
            ForcedArchitectures = higher.ForcedArchitectures.IsDefaultOrEmpty
                ? lower.ForcedArchitectures
                : higher.ForcedArchitectures,
            AssetMappings = higher.AssetMappings.IsDefaultOrEmpty
                ? lower.AssetMappings
                : higher.AssetMappings,
            ScopeLayout = higher.ScopeLayout ?? lower.ScopeLayout,
            VersionSource = higher.VersionSource ?? lower.VersionSource,
            MetadataUrlReplacements = lower.MetadataUrlReplacements.SetItems(
                higher.MetadataUrlReplacements),
            PreservedFields = Union(lower.PreservedFields, higher.PreservedFields),
            DroppedFields = Union(lower.DroppedFields, higher.DroppedFields),
            LearnedFields = MergeLearnedFields(lower.LearnedFields, higher.LearnedFields),
            VanityUrls = Union(lower.VanityUrls, higher.VanityUrls),
            ManualOnly = lower.ManualOnly || higher.ManualOnly,
            Policies =
            [
                .. lower.Policies
                    .Concat(higher.Policies)
                    .Distinct()
                    .OrderBy(static item => item.Id, StringComparer.Ordinal)
                    .ThenBy(static item => item.Annotation, StringComparer.Ordinal),
            ],
            Quirks = new()
            {
                DisplayVersionFromEvidenceProperty =
                    higher.Quirks.DisplayVersionFromEvidenceProperty
                    ?? lower.Quirks.DisplayVersionFromEvidenceProperty,
            },
        };
    }

    private static ImmutableArray<string> Union(
        ImmutableArray<string> lower,
        ImmutableArray<string> higher)
        =>
        [
            .. lower
                .Concat(higher)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

    private static ImmutableArray<LearnedFieldOverride> MergeLearnedFields(
        ImmutableArray<LearnedFieldOverride> lower,
        ImmutableArray<LearnedFieldOverride> higher)
        =>
        [
            .. lower
                .Concat(higher)
                .GroupBy(
                    static item => $"{item.DocumentKey}\u001f{item.SemanticPath}",
                    StringComparer.Ordinal)
                .Select(static group => group.Last())
                .OrderBy(static item => item.DocumentKey, StringComparer.Ordinal)
                .ThenBy(static item => item.SemanticPath, StringComparer.Ordinal),
        ];

    private static OverridePackSet LoadBuiltIn()
    {
        const string resourceName = "WinMatsch.Rules.Overrides.BuiltIn.Google.Chrome.yaml";
        using Stream stream = typeof(OverridePackSet).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded override pack '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return new([OverridePackYaml.Read(reader.ReadToEnd())]);
    }
}
