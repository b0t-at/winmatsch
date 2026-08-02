using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace WinMatsch.Validation.Tests;

/// <summary>
/// Guards THIRD-PARTY-NOTICES.txt against drift. The notice is checked against two
/// independent sources of truth: the central version pins, and the restored dependency
/// closure of the shipped CLI (so a *transitive* that appears or changes version is caught
/// too, without anyone maintaining a list by hand).
/// </summary>
public sealed class LicenseNoticeTests
{
    /// <summary>Packages that exist only in the test harness and are never shipped to users.</summary>
    private static readonly HashSet<string> _testOnlyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "xunit.v3",
        "xunit.runner.visualstudio",
    };

    /// <summary>
    /// Build-time-only packages in the CLI closure: they contribute MSBuild targets, never
    /// assemblies in the published output, so they need no attribution.
    /// </summary>
    private static readonly HashSet<string> _buildOnlyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.ILLink.Tasks",
    };

    /// <summary>
    /// .NET platform packages that runtime-identifier-specific targets resolve (shared
    /// framework, apphost, and ILCompiler packs). They are part of the platform we build on,
    /// covered by the .NET license rather than by third-party attribution.
    /// </summary>
    private static readonly string[] _platformPackagePrefixes =
    [
        "Microsoft.NETCore.App.",
        "Microsoft.AspNetCore.App.",
        "Microsoft.WindowsDesktop.App.",
        "Microsoft.DotNet.ILCompiler",
    ];

    private static readonly Regex _noticeEntry = new(
        @"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*\s+\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?$",
        RegexOptions.CultureInvariant);

    private static readonly string[] _attributionSeparators = [", and ", " and ", ", "];

    /// <summary>Asset sections that mean a package contributes files to the published output.</summary>
    private static readonly string[] _shippedAssetSections = ["runtime", "runtimeTargets", "native"];

    [Fact]
    public void Third_party_notice_is_shipped_with_validation_outputs()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

        Assert.True(File.Exists(path), $"Expected third-party notice at '{path}'.");
        string notice = File.ReadAllText(path);
        Assert.Contains("WinGet manifest schemas 1.12.0", notice, StringComparison.Ordinal);
        Assert.Contains("Mozilla Public License 2.0", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Third_party_notice_covers_every_pinned_shipped_package()
    {
        HashSet<string> noticed = ReadNoticedPackages();

        foreach ((string id, string version) in ReadPinnedPackages())
        {
            if (_testOnlyPackages.Contains(id))
            {
                continue;
            }

            Assert.True(
                noticed.Contains($"{id} {version}"),
                $"THIRD-PARTY-NOTICES.txt does not attribute '{id} {version}'. Update the notice whenever a pinned version changes.");
        }
    }

    [Fact]
    public void Third_party_notice_covers_the_restored_cli_dependency_closure()
    {
        HashSet<string> noticed = ReadNoticedPackages();
        List<(string Id, string Version)> closure = ReadShippedClosure();

        Assert.NotEmpty(closure);
        foreach ((string id, string version) in closure)
        {
            Assert.True(
                noticed.Contains($"{id} {version}"),
                $"THIRD-PARTY-NOTICES.txt does not attribute '{id} {version}', which ships inside the CLI. "
                + "Run 'dotnet list package --include-transitive', verify the license, and add it.");
        }
    }

    [Fact]
    public void Every_pinned_package_is_classified_as_shipped_or_test_only()
    {
        HashSet<string> noticedIds = ReadNoticedPackages()
            .Select(static entry => entry[..entry.LastIndexOf(' ')])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach ((string id, _) in ReadPinnedPackages())
        {
            bool isTestOnly = _testOnlyPackages.Contains(id);
            bool isNoticed = noticedIds.Contains(id);

            Assert.True(
                isTestOnly ^ isNoticed,
                $"Package '{id}' must be either attributed in THIRD-PARTY-NOTICES.txt (shipped) or declared test-only — not both, not neither.");
        }
    }

    /// <summary>
    /// Parses the notice into the set of "&lt;id&gt; &lt;version&gt;" attributions it makes.
    /// Only whole lines that consist purely of a comma/"and"-separated list of
    /// <c>&lt;PackageId&gt; &lt;version&gt;</c> pairs count, so surrounding prose (license text,
    /// URLs, copyright lines) can never be mistaken for an attribution.
    /// </summary>
    private static HashSet<string> ReadNoticedPackages()
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
        HashSet<string> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            string[] parts = line.Trim().Split(_attributionSeparators, StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && Array.TrueForAll(parts, static part => _noticeEntry.IsMatch(part)))
            {
                foreach (string part in parts)
                {
                    entries.Add(part);
                }
            }
        }

        Assert.NotEmpty(entries);
        return entries;
    }

    private static List<(string Id, string Version)> ReadPinnedPackages()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Directory.Packages.props");
        Assert.True(File.Exists(path), $"Expected central package pins at '{path}'.");

        List<(string Id, string Version)> packages = [];
        foreach (XElement element in XDocument.Load(path).Descendants("PackageVersion"))
        {
            string? id = element.Attribute("Include")?.Value;
            string? version = element.Attribute("Version")?.Value;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
            {
                packages.Add((id, version));
            }
        }

        Assert.NotEmpty(packages);
        return packages;
    }

    /// <summary>
    /// Reads the CLI's restored dependency closure (direct and transitive) from its NuGet
    /// assets file, keeping every package that contributes files to the published output —
    /// managed assemblies, RID-specific assets, or native libraries alike. Runtime-identifier
    /// targets are inspected too, so a package that only ever ships native or RID-scoped
    /// content cannot slip past the notice; only the .NET platform packs those targets pull in
    /// are excluded.
    /// </summary>
    private static List<(string Id, string Version)> ReadShippedClosure()
    {
        string assetsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WinMatsch.Cli",
            "obj",
            "project.assets.json");
        Assert.True(
            File.Exists(assetsPath),
            $"Expected the CLI restore assets at '{assetsPath}'. Build the whole solution (or 'dotnet restore') before running this test.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        HashSet<(string Id, string Version)> closure = [];
        foreach (JsonProperty target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out JsonElement type)
                    || type.GetString() != "package"
                    || !Array.Exists(
                        _shippedAssetSections,
                        section => library.Value.TryGetProperty(section, out _)))
                {
                    continue;
                }

                string[] parts = library.Name.Split('/');
                if (parts.Length == 2
                    && !_buildOnlyPackages.Contains(parts[0])
                    && !IsPlatformPackage(parts[0]))
                {
                    closure.Add((parts[0], parts[1]));
                }
            }
        }

        return [.. closure];
    }

    private static bool IsPlatformPackage(string id)
        => Array.Exists(
            _platformPackagePrefixes,
            prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinMatsch.slnx")))
            {
                return directory.FullName;
            }
        }

        Assert.Fail($"Could not locate the repository root (WinMatsch.slnx) above '{AppContext.BaseDirectory}'.");
        return string.Empty;
    }
}
