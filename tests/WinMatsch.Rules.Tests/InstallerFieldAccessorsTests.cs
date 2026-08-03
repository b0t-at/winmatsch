using System.Reflection;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class InstallerFieldAccessorsTests
{
    [Fact]
    public void Accessor_table_covers_every_InstallerFieldsBase_property()
    {
        // Tests are not AOT-constrained, so reflection is fine here: this guards the
        // hand-written table in InstallerFieldAccessors against new model properties.
        var expected = new List<string>();
        foreach (PropertyInfo property in typeof(InstallerFieldsBase).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            expected.Add(property.Name);
        }

        var actual = new List<string>();
        foreach (InstallerFieldAccessor accessor in InstallerFieldAccessors.All)
        {
            actual.Add(accessor.Name);
        }

        expected.Sort(StringComparer.Ordinal);
        actual.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Getters_and_setters_round_trip()
    {
        var source = new Installer { InstallerType = InstallerType.Nullsoft, Commands = ["app"] };
        var target = new Installer();

        foreach (InstallerFieldAccessor accessor in InstallerFieldAccessors.All)
        {
            if (accessor.Get(source) is { } value)
            {
                accessor.Set(target, value);
                Assert.True(accessor.ValueEquals(value, accessor.Get(target)!), $"{accessor.Name} did not round-trip.");
                accessor.Set(target, null);
                Assert.Null(accessor.Get(target));
            }
        }
    }

    [Fact]
    public void Clones_of_composite_values_are_independent()
    {
        var switches = new InstallerSwitches { Silent = "/S" };
        var entries = new List<AppsAndFeaturesEntry> { new() { ProductCode = "{AB12CD34-EF56-7890-ABCD-EF1234567890}" } };
        var source = new Installer { InstallerSwitches = switches, AppsAndFeaturesEntries = entries };

        foreach (InstallerFieldAccessor accessor in InstallerFieldAccessors.All)
        {
            if (accessor.Get(source) is not { } value)
            {
                continue;
            }

            object clone = accessor.Clone(value);
            Assert.True(accessor.ValueEquals(value, clone), $"{accessor.Name} clone is not equal to the original.");
            Assert.NotSame(value, clone);
        }

        var switchesClone = (InstallerSwitches)InstallerFieldAccessors.All
            .First(a => a.Name == nameof(InstallerFieldsBase.InstallerSwitches)).Clone(switches);
        switchesClone.Silent = "/quiet";
        Assert.Equal("/S", switches.Silent);
    }
}
