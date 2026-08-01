using WinMatsch.Core;
using WinMatsch.Testing.Fixtures;
using Xunit;

namespace WinMatsch.Workflows.Tests.Mapping;

public sealed class AssetMappingFixtureTests
{
    [Fact]
    public void Mapping_and_e2e_consume_the_same_descriptor_semantics()
    {
        foreach (FixtureAsset asset in FixtureCatalog.All.SelectMany(static fixture => fixture.Descriptor.Assets))
        {
            Architecture architecture = FixtureSemantics.ParseArchitecture(asset.ExpectedArchitecture);
            InstallerType installerType = FixtureSemantics.ParseInstallerType(asset.ExpectedInstallerType);

            Assert.True(Enum.IsDefined(architecture));
            Assert.True(Enum.IsDefined(installerType));
        }
    }

    [Fact]
    public void Explicit_ambiguous_architecture_resolution_is_not_a_hidden_package_carve_out()
    {
        FixtureAsset ambiguous = FixtureCatalog.Get("uhk-agent").Descriptor.Assets
            .Single(static asset => asset.Synthetic.PayloadArchitectures.Count > 1);

        Assert.Equal("neutral", ambiguous.Synthetic.ExplicitArchitecture);
        Assert.Equal(Architecture.Neutral, FixtureSemantics.ParseArchitecture(
            ambiguous.Synthetic.ExplicitArchitecture!));
        Assert.DoesNotContain(
            FixtureCatalog.Get("super-productivity").Descriptor.Assets,
            static asset => asset.Synthetic.ExplicitArchitecture is not null);
    }
}
