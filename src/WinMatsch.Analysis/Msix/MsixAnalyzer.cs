using System.Xml;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Msix;

/// <summary>
/// Analyzes MSIX and AppX application packages (.msix, .appx): zip archives whose
/// <c>AppxManifest.xml</c> declares the package identity, target device families and
/// capabilities, and whose <c>AppxSignature.p7x</c> yields the <c>SignatureSha256</c>.
/// </summary>
public sealed class MsixAnalyzer : IInstallerAnalyzer
{
    private const string ManifestEntryName = "AppxManifest.xml";
    private const string RestrictedCapabilitiesNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    public bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".appx", StringComparison.OrdinalIgnoreCase);
    }

    public InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using IDisposable scope = AnalysisLimits.EnterArchive($"'{fileName}'");
        using var archive = new SupportedZipArchive(
            stream,
            fileName,
            $"'{fileName}'");
        AnalysisLimits.ValidateArchive(archive, $"'{fileName}'");

        SupportedZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException($"'{fileName}' is not an MSIX/AppX package: it has no {ManifestEntryName} entry.");
        byte[] manifestBytes = MsixReader.ReadEntryBytes(manifestEntry);
        ParsedManifest manifest = ParseManifest(manifestBytes);

        var installer = new Installer
        {
            Architecture = MsixReader.ParseArchitecture(manifest.ProcessorArchitecture),
            InstallerType = DetermineInstallerType(fileName),
            PackageFamilyName = manifest.IdentityName is not null && manifest.IdentityPublisher is not null
                ? MsixPackageFamilyName.Create(manifest.IdentityName, manifest.IdentityPublisher)
                : null,
            SignatureSha256 = MsixReader.ComputeSignatureHash(archive),
            Platform = manifest.Platforms.Count > 0 ? manifest.Platforms : null,
            MinimumOSVersion = manifest.MinimumOSVersion,
            Capabilities = manifest.Capabilities.Count > 0 ? manifest.Capabilities : null,
            RestrictedCapabilities = manifest.RestrictedCapabilities.Count > 0 ? manifest.RestrictedCapabilities : null,
        };

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Msix,
            Installers = [installer],
            // DisplayName may be an "ms-resource:" reference; it is passed through as-is and
            // resolved (or discarded) by later rules.
            ProductName = manifest.DisplayName,
            Publisher = manifest.PublisherDisplayName,
            ProductVersion = manifest.IdentityVersion,
        };
    }

    private static InstallerType DetermineInstallerType(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".appx", StringComparison.OrdinalIgnoreCase)
            ? InstallerType.Appx
            : InstallerType.Msix;

    private static ParsedManifest ParseManifest(byte[] manifestBytes)
    {
        var manifest = new ParsedManifest();
        try
        {
            using var manifestStream = new MemoryStream(manifestBytes);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(manifestStream, settings);
            bool inProperties = false;
            bool hasNode = reader.Read();
            while (hasNode)
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (string.Equals(reader.LocalName, "Properties", StringComparison.Ordinal))
                    {
                        inProperties = false;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "Identity" when manifest.IdentityName is null:
                            manifest.IdentityName = MsixReader.GetAttribute(reader, "Name");
                            manifest.IdentityPublisher = MsixReader.GetAttribute(reader, "Publisher");
                            manifest.IdentityVersion = MsixReader.GetAttribute(reader, "Version");
                            manifest.ProcessorArchitecture = MsixReader.GetAttribute(reader, "ProcessorArchitecture");
                            break;

                        case "Properties":
                            inProperties = !reader.IsEmptyElement;
                            break;

                        case "DisplayName" when inProperties:
                            manifest.DisplayName = reader.ReadElementContentAsString();
                            continue; // The reader already advanced to the node after the element.

                        case "PublisherDisplayName" when inProperties:
                            manifest.PublisherDisplayName = reader.ReadElementContentAsString();
                            continue; // The reader already advanced to the node after the element.

                        case "TargetDeviceFamily":
                            ParseTargetDeviceFamily(reader, manifest);
                            break;

                        case "Capability":
                            ParseCapability(reader, manifest);
                            break;

                        default:
                            break; // Not an element the analyzer consumes.
                    }
                }

                hasNode = reader.Read();
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"The package's {ManifestEntryName} is not well-formed XML.", exception);
        }

        return manifest;
    }

    private static void ParseTargetDeviceFamily(XmlReader reader, ParsedManifest manifest)
    {
        // The minimum OS version is the smallest MinVersion across all families; the
        // per-family values also feed the AppX-versus-MSIX heuristic.
        string? minVersionText = MsixReader.GetAttribute(reader, "MinVersion");
        manifest.TargetFamilyMinVersions.Add(minVersionText);
        if (MinimumOSVersion.TryCreate(minVersionText, out MinimumOSVersion? minVersion)
            && (manifest.MinimumOSVersion is null || minVersion! < manifest.MinimumOSVersion))
        {
            manifest.MinimumOSVersion = minVersion;
        }

        Platform? platform = MsixReader.GetAttribute(reader, "Name") switch
        {
            "Windows.Desktop" => Platform.WindowsDesktop,
            "Windows.Universal" => Platform.WindowsUniversal,
            _ => null, // Other families (Team, Holographic, ...) have no manifest platform value.
        };

        if (platform is { } value && !manifest.Platforms.Contains(value))
        {
            manifest.Platforms.Add(value);
        }
    }

    private static void ParseCapability(XmlReader reader, ParsedManifest manifest)
    {
        if (MsixReader.GetAttribute(reader, "Name") is not { } name)
        {
            return;
        }

        bool restricted = string.Equals(reader.NamespaceURI, RestrictedCapabilitiesNamespace, StringComparison.Ordinal)
            || string.Equals(reader.Prefix, "rescap", StringComparison.Ordinal);
        List<string> target = restricted ? manifest.RestrictedCapabilities : manifest.Capabilities;
        if (!target.Contains(name, StringComparer.Ordinal))
        {
            target.Add(name);
        }
    }

    /// <summary>The manifest values the analyzer extracts, in document order.</summary>
    private sealed class ParsedManifest
    {
        public string? IdentityName { get; set; }

        public string? IdentityPublisher { get; set; }

        public string? IdentityVersion { get; set; }

        public string? ProcessorArchitecture { get; set; }

        public string? DisplayName { get; set; }

        public string? PublisherDisplayName { get; set; }

        public MinimumOSVersion? MinimumOSVersion { get; set; }

        public List<string?> TargetFamilyMinVersions { get; } = [];

        public List<Platform> Platforms { get; } = [];

        public List<string> Capabilities { get; } = [];

        public List<string> RestrictedCapabilities { get; } = [];
    }
}
