using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class PolicyEvidenceTests
{
    [Fact]
    public void Scope_lookup_is_case_insensitive()
    {
        var expected = new PolicyScopeEvidence
        {
            Scope = Scope.Machine,
            Origin = PolicyScopeEvidenceOrigin.MsiAllUsersProperty,
            Source = "MSI ALLUSERS=1",
        };
        var evidence = new PolicyEvidence
        {
            InstallerScopes = new Dictionary<string, PolicyScopeEvidence>
            {
                ["https://EXAMPLE.test/App.msi"] = expected,
            },
        };

        Assert.Same(expected, evidence.FindScopeEvidence("https://example.TEST/app.msi"));
    }

    [Fact]
    public void Case_variant_scope_keys_are_rejected_deterministically()
    {
        var first = new PolicyScopeEvidence
        {
            Scope = Scope.User,
            Origin = PolicyScopeEvidenceOrigin.InnoPrivilegesRequired,
            Source = "first",
        };
        var second = first with { Scope = Scope.Machine, Source = "second" };
        var values = new Dictionary<string, PolicyScopeEvidence>(StringComparer.Ordinal)
        {
            ["https://example.test/App.exe"] = first,
            ["https://example.test/app.exe"] = second,
        };
        var reversedValues = new Dictionary<string, PolicyScopeEvidence>(StringComparer.Ordinal)
        {
            ["https://example.test/app.exe"] = second,
            ["https://example.test/App.exe"] = first,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new PolicyEvidence
        {
            InstallerScopes = values,
        });
        ArgumentException reversedException = Assert.Throws<ArgumentException>(() => new PolicyEvidence
        {
            InstallerScopes = reversedValues,
        });

        Assert.Contains("case-insensitive duplicate", exception.Message, StringComparison.Ordinal);
        Assert.Equal(exception.Message, reversedException.Message);
    }
}
