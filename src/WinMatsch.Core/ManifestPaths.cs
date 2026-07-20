using System.Text;

namespace WinMatsch.Core;

/// <summary>
/// Builds the repository paths and file names used by winget-pkgs:
/// <c>manifests/&lt;p&gt;/&lt;Publisher&gt;/&lt;Package&gt;/.../&lt;version&gt;/&lt;Id&gt;.installer.yaml</c> etc.
/// All paths use forward slashes, matching Git object paths.
/// </summary>
public static class ManifestPaths
{
    public const string ManifestsRoot = "manifests";

    /// <summary>The directory containing all versions of a package, e.g. <c>manifests/m/Microsoft/PowerToys</c>.</summary>
    public static string GetPackageDirectory(PackageIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var builder = new StringBuilder(ManifestsRoot.Length + identifier.Value.Length + 4);
        builder.Append(ManifestsRoot);
        builder.Append('/');
        builder.Append(char.ToLowerInvariant(identifier.Segments[0][0]));
        foreach (string segment in identifier.Segments)
        {
            builder.Append('/');
            builder.Append(segment);
        }

        return builder.ToString();
    }

    /// <summary>The directory containing the manifests of one version, e.g. <c>manifests/m/Microsoft/PowerToys/0.75.1</c>.</summary>
    public static string GetVersionDirectory(PackageIdentifier identifier, PackageVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return $"{GetPackageDirectory(identifier)}/{version.Value}";
    }

    public static string GetInstallerFileName(PackageIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"{identifier.Value}.installer.yaml";
    }

    public static string GetVersionFileName(PackageIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"{identifier.Value}.yaml";
    }

    public static string GetLocaleFileName(PackageIdentifier identifier, LanguageTag locale)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(locale);
        return $"{identifier.Value}.locale.{locale.Value}.yaml";
    }
}
