using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinMatsch.Testing.Fixtures;

public static class FixtureCatalog
{
    private const string DescriptorSuffix = ".descriptor.json";
    private static readonly Lazy<List<RegressionFixture>> AllFixtures = new(LoadAll);

    public static IReadOnlyList<RegressionFixture> All => AllFixtures.Value;

    public static RegressionFixture Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return All.Single(
            fixture => string.Equals(fixture.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<HttpInteractionRecording> LoadRecordings()
    {
        using Stream stream = OpenResource("Fixtures.Recordings.http-recordings.json");
        return JsonSerializer.Deserialize(
            stream,
            FixtureJsonContext.Default.ListHttpInteractionRecording)
            ?? throw new InvalidDataException("The embedded HTTP recording collection is empty.");
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

            using Stream snapshotStream = OpenResource(descriptor.ExpectedSnapshot.Replace('/', '.'));
            ExpectedManifestSnapshot expected = JsonSerializer.Deserialize(
                snapshotStream,
                FixtureJsonContext.Default.ExpectedManifestSnapshot)
                ?? throw new InvalidDataException(
                    $"Expected snapshot '{descriptor.ExpectedSnapshot}' is empty.");

            Validate(descriptor, expected);
            fixtures.Add(new RegressionFixture(descriptor, expected));
        }

        return fixtures;
    }

    private static Stream OpenResource(string resourceSuffix)
    {
        Assembly assembly = typeof(FixtureCatalog).Assembly;
        string resourceName = assembly.GetManifestResourceNames().Single(
            name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded resource '{resourceName}' could not be opened.");
    }

    private static void Validate(FixtureDescriptor descriptor, ExpectedManifestSnapshot expected)
    {
        if (descriptor.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' uses unsupported schema version {descriptor.SchemaVersion}.");
        }

        if (!string.Equals(
                descriptor.Package.Identifier,
                expected.PackageIdentifier,
                StringComparison.Ordinal)
            || !string.Equals(
                descriptor.Package.Version,
                expected.PackageVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' package coordinates do not match its expected snapshot.");
        }

        ValidateSha256(descriptor.Id, descriptor.Provenance.ManifestSha256);

        foreach (FixtureAsset asset in descriptor.Assets)
        {
            ValidateSha256(descriptor.Id, asset.Sha256);
        }

        foreach (ExpectedInstallerSnapshot installer in expected.Installers)
        {
            ValidateSha256(descriptor.Id, installer.InstallerSha256);
        }

        string[] duplicateUrls = descriptor.Assets
            .GroupBy(asset => asset.Url.AbsoluteUri, StringComparer.Ordinal)
            .Where(group => group.Select(asset => asset.Sha256).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateUrls.Length > 0)
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' assigns different hashes to the same URL.");
        }
    }

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
