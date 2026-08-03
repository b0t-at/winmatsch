using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinMatsch.Testing.Fixtures;

public static class FixtureCatalog
{
    private const string DescriptorSuffix = ".descriptor.json";
    private const string ExpectedManifestMarker = ".Fixtures.ExpectedManifests.";
    private static readonly Lazy<List<RegressionFixture>> _allFixtures = new(LoadAll);

    public static IReadOnlyList<RegressionFixture> All => _allFixtures.Value;

    public static RegressionFixture Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return All.Single(
            fixture => string.Equals(fixture.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static List<RegressionFixture> LoadAll()
    {
        Assembly assembly = typeof(FixtureCatalog).Assembly;
        var fixtures = new List<RegressionFixture>();

        foreach (string resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.EndsWith(DescriptorSuffix, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using Stream descriptorStream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Embedded descriptor '{resourceName}' could not be opened.");
            FixtureDescriptor descriptor = JsonSerializer.Deserialize(
                descriptorStream,
                FixtureJsonContext.Default.FixtureDescriptor)
                ?? throw new InvalidDataException($"Embedded descriptor '{resourceName}' is empty.");
            descriptor = Normalize(descriptor);

            IReadOnlyDictionary<string, byte[]> expected = LoadExpectedManifests(descriptor);

            Validate(descriptor, expected);
            fixtures.Add(new RegressionFixture(descriptor, expected));
        }

        return fixtures;
    }

    private static FixtureDescriptor Normalize(FixtureDescriptor descriptor)
    {
        FixtureScenario scenario = descriptor.Scenario ?? new();
        FixtureLocale locale = scenario.Locale ?? new();
        return descriptor with
        {
            Assets =
            [
                .. descriptor.Assets.Select(static asset =>
                {
                    FixtureSyntheticAsset synthetic = asset.Synthetic
                        ?? throw new InvalidDataException(
                            $"Fixture asset '{asset.FileName}' has no independent synthetic encoding.");
                    return asset with
                    {
                        Synthetic = synthetic with
                        {
                            NestedPayloadPaths = synthetic.NestedPayloadPaths ?? [],
                            Imports = synthetic.Imports ?? [],
                            PayloadArchitectures = synthetic.PayloadArchitectures ?? [],
                        },
                    };
                }),
            ],
            Scenario = scenario with
            {
                Operation = string.IsNullOrWhiteSpace(scenario.Operation) ? "new" : scenario.Operation,
                PreviousInstallers = scenario.PreviousInstallers ?? [],
                Locale = locale with
                {
                    PackageLocale = string.IsNullOrWhiteSpace(locale.PackageLocale)
                        ? "en-US"
                        : locale.PackageLocale,
                    Publisher = string.IsNullOrWhiteSpace(locale.Publisher)
                        ? "WinMatsch synthetic fixture"
                        : locale.Publisher,
                    License = string.IsNullOrWhiteSpace(locale.License) ? "MIT" : locale.License,
                },
            },
        };
    }

    private static Dictionary<string, byte[]> LoadExpectedManifests(FixtureDescriptor descriptor)
    {
        Assembly assembly = typeof(FixtureCatalog).Assembly;
        string resourceDirectory = descriptor.ExpectedManifestDirectory.Replace('-', '_');
        string marker = $"{ExpectedManifestMarker}{resourceDirectory}.";
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(marker, StringComparison.Ordinal)
                && name.EndsWith(".yaml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                name => name[(name.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..],
                name =>
                {
                    using Stream stream = assembly.GetManifestResourceStream(name)
                        ?? throw new InvalidDataException($"Embedded resource '{name}' could not be opened.");
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                },
                StringComparer.Ordinal);
    }

    private static void Validate(
        FixtureDescriptor descriptor,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        if (descriptor.SchemaVersion != 2)
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' uses unsupported schema version {descriptor.SchemaVersion}.");
        }

        bool updatingGoldens =
            Environment.GetEnvironmentVariable("WINMATSCH_UPDATE_REGRESSION_GOLDENS") == "1";
        if (!updatingGoldens
            && (expected.Count != 3
            || expected.Values.Any(static bytes => bytes.Length == 0)
            || !expected.Values.Any(static bytes => Contains(bytes, "ManifestType: installer"))
            || !expected.Values.Any(static bytes => Contains(bytes, "ManifestType: defaultLocale"))
            || !expected.Values.Any(static bytes => Contains(bytes, "ManifestType: version"))))
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' must embed a complete three-file merged-manifest golden.");
        }

        ValidateSha256(descriptor.Id, descriptor.Provenance.ManifestSha256);
        ValidateRegressionRules(descriptor);

        foreach (FixtureAsset asset in descriptor.Assets)
        {
            ValidateSha256(descriptor.Id, asset.UpstreamSha256);
            ValidateSha256(descriptor.Id, asset.SyntheticSha256);
            _ = FixtureSemantics.ParseArchitecture(asset.Synthetic.Architecture);
            _ = FixtureSemantics.ParseInstallerType(asset.Synthetic.Kind);
        }

        string[] duplicateUrls = descriptor.Assets
            .GroupBy(asset => asset.Url.AbsoluteUri, StringComparer.Ordinal)
            .Where(group => group.Select(asset => asset.UpstreamSha256).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateUrls.Length > 0)
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' assigns different hashes to the same URL.");
        }
    }

    private static void ValidateRegressionRules(FixtureDescriptor descriptor)
    {
        FixtureRegression regression = descriptor.Regression;
        string[] classifiedRuleIds = regression.ExpectedRuleExecutions
            .Concat(regression.NonExecutableRuleReasons.Keys)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!regression.RuleIds.Order(StringComparer.Ordinal).SequenceEqual(
                classifiedRuleIds,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' must classify every regression rule id exactly once as executable or owned elsewhere.");
        }

        if (regression.RuleIds.Distinct(StringComparer.Ordinal).Count() != regression.RuleIds.Count)
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' repeats a regression rule id.");
        }

        if (regression.NonExecutableRuleReasons.Values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' must explain every non-executable regression rule id.");
        }
    }

    private static bool Contains(byte[] bytes, string value)
        => System.Text.Encoding.UTF8.GetString(bytes).Contains(value, StringComparison.Ordinal);

    private static void ValidateSha256(string fixtureId, string value)
    {
        if (value.Length != SHA256.HashSizeInBytes * 2
            || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                $"Fixture '{fixtureId}' contains an invalid SHA-256 value '{value}'.");
        }
    }
}
