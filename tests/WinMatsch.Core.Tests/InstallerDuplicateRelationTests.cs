using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class InstallerDuplicateRelationTests
{
    [Fact]
    public void SurrealDb_style_absent_scope_is_a_wildcard_in_both_directions()
    {
        InstallerManifest manifest = CreateManifest();
        Installer unknown = CreateInstaller();
        Installer user = CreateInstaller(scope: Scope.User);

        AssertSymmetricMatch(manifest, unknown, user);
    }

    [Fact]
    public void Wildcard_bridge_does_not_make_known_scope_values_transitive()
    {
        InstallerManifest manifest = CreateManifest();
        Installer user = CreateInstaller(scope: Scope.User);
        Installer unknown = CreateInstaller();
        Installer machine = CreateInstaller(scope: Scope.Machine);

        AssertSymmetricMatch(manifest, user, unknown);
        AssertSymmetricMatch(manifest, unknown, machine);
        AssertSymmetricDistinct(manifest, user, machine);
    }

    [Fact]
    public void Multi_locale_entries_are_distinct_but_absent_locale_is_a_wildcard()
    {
        InstallerManifest manifest = CreateManifest();
        Installer english = CreateInstaller(locale: new LanguageTag("en-US"));
        Installer german = CreateInstaller(locale: new LanguageTag("de-DE"));
        Installer unknown = CreateInstaller();

        AssertSymmetricDistinct(manifest, english, german);
        AssertSymmetricMatch(manifest, english, unknown);
        AssertSymmetricMatch(manifest, unknown, german);
    }

    [Fact]
    public void Legal_archive_variants_with_different_nested_types_are_distinct()
    {
        InstallerManifest manifest = CreateManifest(installerType: InstallerType.Zip);
        Installer portable = CreateInstaller(nestedType: InstallerType.Portable);
        Installer msi = CreateInstaller(nestedType: InstallerType.Msi);

        AssertSymmetricDistinct(manifest, portable, msi);
    }

    [Fact]
    public void Root_values_participate_in_every_effective_identity_dimension()
    {
        InstallerManifest manifest = CreateManifest(
            installerType: InstallerType.Zip,
            nestedType: InstallerType.Portable,
            scope: Scope.User,
            locale: new LanguageTag("en-US"));
        Installer inherited = CreateInstaller(installerType: null);
        Installer explicitValues = CreateInstaller(
            installerType: InstallerType.Zip,
            nestedType: InstallerType.Portable,
            scope: Scope.User,
            locale: new LanguageTag("en-US"));

        AssertSymmetricMatch(manifest, inherited, explicitValues);
    }

    [Fact]
    public void Known_architecture_and_installer_type_differences_remain_distinct()
    {
        InstallerManifest manifest = CreateManifest();

        AssertSymmetricDistinct(
            manifest,
            CreateInstaller(Architecture.X64),
            CreateInstaller(Architecture.X86));
        AssertSymmetricDistinct(
            manifest,
            CreateInstaller(installerType: InstallerType.Exe),
            CreateInstaller(installerType: InstallerType.Msi));
    }

    private static InstallerManifest CreateManifest(
        InstallerType? installerType = null,
        InstallerType? nestedType = null,
        Scope? scope = null,
        LanguageTag? locale = null)
        => new()
        {
            InstallerType = installerType,
            NestedInstallerType = nestedType,
            Scope = scope,
            InstallerLocale = locale,
        };

    private static Installer CreateInstaller(
        Architecture architecture = Architecture.X64,
        InstallerType? installerType = InstallerType.Exe,
        InstallerType? nestedType = null,
        Scope? scope = null,
        LanguageTag? locale = null)
        => new()
        {
            Architecture = architecture,
            InstallerType = installerType,
            NestedInstallerType = nestedType,
            Scope = scope,
            InstallerLocale = locale,
        };

    private static void AssertSymmetricMatch(
        InstallerManifest manifest,
        Installer left,
        Installer right)
    {
        Assert.True(InstallerDuplicateRelation.AreDuplicates(manifest, left, right));
        Assert.True(InstallerDuplicateRelation.AreDuplicates(manifest, right, left));
    }

    private static void AssertSymmetricDistinct(
        InstallerManifest manifest,
        Installer left,
        Installer right)
    {
        Assert.False(InstallerDuplicateRelation.AreDuplicates(manifest, left, right));
        Assert.False(InstallerDuplicateRelation.AreDuplicates(manifest, right, left));
    }
}
