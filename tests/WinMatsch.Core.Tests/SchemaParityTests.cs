using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class SchemaParityTests
{
    private static readonly string _schemaDirectory = Path.Combine(AppContext.BaseDirectory, "Schemas");

    [Theory]
    [InlineData("manifest.defaultLocale.1.12.0.json", "B87EAA2252DAF0BC9B2D495A3F7F5547B8B36236BDCC0543CF9F7403BD56707E")]
    [InlineData("manifest.installer.1.12.0.json", "47DE5AEFDEA4E7CCBC8AB4E32E8230671300054BD4F688CD7CA8BDEBF459F006")]
    [InlineData("manifest.locale.1.12.0.json", "F049048815DC30599722F0419545C6DBFC8E51C2FB52FF4AC113A60069032AB7")]
    [InlineData("manifest.version.1.12.0.json", "0CDF9C17A0D19221A3612980C95A47D120C4CE532157177AA6AF4368A8A78273")]
    public void BundledSchema_IsPinnedOfficialCopy(string fileName, string expectedSha256)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(_schemaDirectory, fileName));

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void TopLevelSchemaProperties_MatchTypedModels()
    {
        AssertRootProperties<InstallerManifest>("manifest.installer.1.12.0.json");
        AssertRootProperties<DefaultLocaleManifest>("manifest.defaultLocale.1.12.0.json");
        AssertRootProperties<LocaleManifest>("manifest.locale.1.12.0.json");
        AssertRootProperties<VersionManifest>("manifest.version.1.12.0.json");
    }

    [Fact]
    public void NestedSchemaProperties_MatchTypedModels()
    {
        using JsonDocument installer = Load("manifest.installer.1.12.0.json");
        JsonElement definitions = installer.RootElement.GetProperty("definitions");
        AssertDefinitionProperties<NestedInstallerFile>(definitions, "NestedInstallerFiles");
        AssertDefinitionProperties<InstallerSwitches>(definitions, "InstallerSwitches");
        AssertDefinitionProperties<ExpectedReturnCode>(definitions, "ExpectedReturnCodes");
        AssertDefinitionProperties<Dependencies>(definitions, "Dependencies");
        AssertDefinitionProperties<AppsAndFeaturesEntry>(definitions, "AppsAndFeaturesEntry");
        AssertDefinitionProperties<InstallationMetadata>(definitions, "InstallationMetadata");
        AssertDefinitionProperties<Authentication>(definitions, "Authentication");
        AssertDefinitionProperties<Installer>(definitions, "Installer");
        AssertModelProperties<PackageDependency>(
            definitions.GetProperty("Dependencies")
                .GetProperty("properties")
                .GetProperty("PackageDependencies")
                .GetProperty("items")
                .GetProperty("properties"));
        AssertModelProperties<InstalledFile>(
            definitions.GetProperty("InstallationMetadata")
                .GetProperty("properties")
                .GetProperty("Files")
                .GetProperty("items")
                .GetProperty("properties"));
        AssertModelProperties<MicrosoftEntraIdAuthenticationInfo>(
            definitions.GetProperty("Authentication")
                .GetProperty("properties")
                .GetProperty("MicrosoftEntraIdAuthenticationInfo")
                .GetProperty("properties"));
        AssertModelPropertyNames<Markets>(
            definitions.GetProperty("Markets")
                .GetProperty("oneOf")
                .EnumerateArray()
                .SelectMany(static branch => branch.GetProperty("properties").EnumerateObject())
                .Select(static property => property.Name));

        using JsonDocument locale = Load("manifest.locale.1.12.0.json");
        definitions = locale.RootElement.GetProperty("definitions");
        AssertDefinitionProperties<PackageAgreement>(definitions, "Agreement");
        AssertDefinitionProperties<Documentation>(definitions, "Documentation");
        AssertDefinitionProperties<Icon>(definitions, "Icon");
    }

    [Fact]
    public void SchemaEnums_AreCoveredByTypedYamlMappings()
    {
        using JsonDocument installer = Load("manifest.installer.1.12.0.json");
        JsonElement definitions = installer.RootElement.GetProperty("definitions");

        Assert.Equal(
            GetEnum(definitions.GetProperty("InstallerType")),
            Enum.GetValues<InstallerType>().Select(static value => value.ToYaml()));
        Assert.All(GetEnum(definitions.GetProperty("NestedInstallerType")), static value =>
        {
            InstallerType parsed = YamlValues.ParseNestedInstallerType(value);
            Assert.Equal(value, parsed.ToNestedInstallerTypeYaml());
        });
        Assert.Equal(
            GetEnum(definitions.GetProperty("Architecture")),
            Enum.GetValues<Architecture>().Select(static value => value.ToYaml()));
        Assert.All(GetEnum(definitions.GetProperty("UnsupportedOSArchitectures").GetProperty("items")), static value =>
        {
            Architecture parsed = YamlValues.ParseUnsupportedOSArchitecture(value);
            Assert.Equal(value, parsed.ToUnsupportedOSArchitectureYaml());
        });
        Assert.Equal(
            GetEnum(definitions.GetProperty("InstallModes").GetProperty("items")),
            Enum.GetValues<InstallMode>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(definitions.GetProperty("UpgradeBehavior")),
            Enum.GetValues<UpgradeBehavior>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(definitions.GetProperty("UnsupportedArguments").GetProperty("items")),
            Enum.GetValues<UnsupportedArgument>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(definitions.GetProperty("ElevationRequirement")),
            Enum.GetValues<ElevationRequirement>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(definitions.GetProperty("RepairBehavior")),
            Enum.GetValues<RepairBehavior>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(definitions.GetProperty("Authentication").GetProperty("properties").GetProperty("AuthenticationType")),
            Enum.GetValues<AuthenticationType>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(
                definitions.GetProperty("ExpectedReturnCodes")
                    .GetProperty("items")
                    .GetProperty("properties")
                    .GetProperty("ReturnResponse")),
            Enum.GetValues<ReturnResponse>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(
                definitions.GetProperty("InstallationMetadata")
                    .GetProperty("properties")
                    .GetProperty("Files")
                    .GetProperty("items")
                    .GetProperty("properties")
                    .GetProperty("FileType")),
            Enum.GetValues<InstalledFileType>().Select(static value => value.ToYaml()));

        using JsonDocument locale = Load("manifest.locale.1.12.0.json");
        JsonElement icon = locale.RootElement.GetProperty("definitions").GetProperty("Icon").GetProperty("properties");
        Assert.Equal(
            GetEnum(icon.GetProperty("IconFileType")),
            Enum.GetValues<IconFileType>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(icon.GetProperty("IconResolution")),
            Enum.GetValues<IconResolution>().Select(static value => value.ToYaml()));
        Assert.Equal(
            GetEnum(icon.GetProperty("IconTheme")),
            Enum.GetValues<IconTheme>().Select(static value => value.ToYaml()));
    }

    [Fact]
    public void SchemaVersion_IsPinnedAtDefault()
    {
        Assert.Equal("1.12.0", ManifestVersion.Default.Value);

        foreach (string fileName in Directory.EnumerateFiles(_schemaDirectory, "*.json").Select(Path.GetFileName)!)
        {
            using JsonDocument schema = Load(fileName);
            string? version = schema.RootElement
                .GetProperty("properties")
                .GetProperty("ManifestVersion")
                .GetProperty("default")
                .GetString();
            Assert.Equal(ManifestVersion.Default.Value, version);
        }
    }

    private static void AssertRootProperties<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string fileName)
    {
        using JsonDocument schema = Load(fileName);
        AssertModelProperties<T>(schema.RootElement.GetProperty("properties"));
    }

    private static void AssertDefinitionProperties<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        JsonElement definitions,
        string definitionName)
    {
        JsonElement definition = definitions.GetProperty(definitionName);
        JsonElement properties = FindFirstProperties(definition);
        AssertModelProperties<T>(properties);
    }

    private static void AssertModelProperties<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(JsonElement properties)
    {
        string[] schemaProperties = properties.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        AssertModelPropertyNames<T>(schemaProperties);
    }

    private static void AssertModelPropertyNames<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        IEnumerable<string> schemaPropertyNames)
    {
        string[] schemaProperties = schemaPropertyNames.Order(StringComparer.Ordinal).ToArray();
        string[] modelProperties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.SetMethod?.IsPublic == true)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(schemaProperties, modelProperties);
    }

    private static JsonElement FindFirstProperties(JsonElement element)
    {
        if (element.TryGetProperty("properties", out JsonElement properties))
        {
            return properties;
        }

        if (element.TryGetProperty("items", out JsonElement items))
        {
            return FindFirstProperties(items);
        }

        throw new InvalidOperationException("Schema definition has no object properties.");
    }

    private static string[] GetEnum(JsonElement element)
        => element.GetProperty("enum")
            .EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString()!)
            .ToArray();

    private static JsonDocument Load(string fileName)
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_schemaDirectory, fileName)));
}
