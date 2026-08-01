using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;

namespace WinMatsch.Rules.OverridePacks;

internal static class LearnedInstallerSelector
{
    public static string Create(
        PackageManifests manifests,
        int installerIndex,
        string correctedField)
    {
        InstallerManifest root = manifests.Installer;
        if (root.Installers is not { } installers
            || installerIndex < 0
            || installerIndex >= installers.Count)
        {
            throw new InvalidOperationException("Learned installer selector references an unavailable installer.");
        }

        Installer installer = installers[installerIndex];
        string version = root.PackageVersion?.Value ?? "";
        string normalizedUrl = NormalizeUrl(installer.InstallerUrl, version);
        string identity = string.Join(
            '\u001f',
            normalizedUrl,
            correctedField == "Architecture" ? "" : installer.Architecture?.ToString() ?? "",
            correctedField == "InstallerType"
                ? ""
                : (installer.InstallerType ?? root.InstallerType)?.ToString() ?? "",
            correctedField == "NestedInstallerType"
                ? ""
                : (installer.NestedInstallerType ?? root.NestedInstallerType)?.ToString() ?? "",
            correctedField == "Scope"
                ? ""
                : (installer.Scope ?? root.Scope)?.ToString() ?? "",
            correctedField == "InstallerLocale"
                ? ""
                : (installer.InstallerLocale ?? root.InstallerLocale)?.Value ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static string? GetValue(
        InstallerManifest root,
        Installer installer,
        string field)
        => field switch
        {
            "Architecture" => installer.Architecture?.ToYaml(),
            "InstallerType" => (installer.InstallerType ?? root.InstallerType)?.ToYaml(),
            "NestedInstallerType" => (installer.NestedInstallerType ?? root.NestedInstallerType)?.ToYaml(),
            "Scope" => (installer.Scope ?? root.Scope)?.ToYaml(),
            "InstallerLocale" => (installer.InstallerLocale ?? root.InstallerLocale)?.Value,
            _ => null,
        };

    private static string NormalizeUrl(string? value, string version)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return value ?? "";
        }

        string normalized = uri.GetLeftPart(UriPartial.Path);
        return string.IsNullOrEmpty(version)
            ? normalized
            : normalized.Replace(version, "{version}", StringComparison.OrdinalIgnoreCase);
    }
}
