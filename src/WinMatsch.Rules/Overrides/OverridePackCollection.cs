using System.Collections.ObjectModel;
using WinMatsch.Core;

namespace WinMatsch.Rules.OverridePacks;

/// <summary>An immutable, case-insensitive package override index.</summary>
public sealed class OverridePackSet
{
    private readonly ReadOnlyDictionary<string, OverridePack> _packs;
    private readonly IReadOnlyCollection<OverridePack> _packValues;

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
        _packValues = values.Values.ToArray();
    }

    public static OverridePackSet Empty { get; } = new();

    public static OverridePackSet BuiltIn { get; } = LoadBuiltIn();

    public IReadOnlyCollection<OverridePack> Packs => _packValues;

    public bool TryGet(PackageIdentifier? packageIdentifier, out OverridePack? pack)
    {
        if (packageIdentifier is null)
        {
            pack = null;
            return false;
        }

        return _packs.TryGetValue(packageIdentifier.Value, out pack);
    }

    private static OverridePackSet LoadBuiltIn()
    {
        const string resourceName = "WinMatsch.Rules.Overrides.BuiltIn.Google.Chrome.yaml";
        using Stream stream = typeof(OverridePackSet).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded override pack '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return new([OverridePackYaml.Read(reader.ReadToEnd())]);
    }
}
