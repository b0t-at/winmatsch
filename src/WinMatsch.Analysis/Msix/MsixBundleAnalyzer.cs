using System.IO.Compression;
using System.Xml;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Msix;

/// <summary>
/// Analyzes MSIX and AppX bundles (.msixbundle, .appxbundle): zip archives whose
/// <c>AppxMetadata/AppxBundleManifest.xml</c> lists per-architecture application packages
/// (plus language/scale resource packages, which are not installers). One installer entry is
/// produced per distinct application-package architecture; all entries share the bundle's
/// package family name and signature hash. The installer type is always <c>msix</c>: the
/// nested packages are not unpacked, and bundles predating MSIX are practically extinct.
/// </summary>
public sealed class MsixBundleAnalyzer : IInstallerAnalyzer
{
    private const string BundleManifestEntryName = "AppxMetadata/AppxBundleManifest.xml";

    public bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".msixbundle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".appxbundle", StringComparison.OrdinalIgnoreCase);
    }

    public InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using IDisposable scope = AnalysisLimits.EnterArchive($"'{fileName}'");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        AnalysisLimits.ValidateArchive(archive, $"'{fileName}'");

        ZipArchiveEntry manifestEntry = archive.GetEntry(BundleManifestEntryName)
            ?? throw new InvalidDataException(
                $"'{fileName}' is not an MSIX/AppX bundle: it has no {BundleManifestEntryName} entry.");
        ParsedBundleManifest manifest = ParseManifest(MsixReader.ReadEntryBytes(manifestEntry));

        if (manifest.ApplicationArchitectures.Count == 0)
        {
            throw new InvalidDataException($"'{fileName}' contains no application packages, only resource packages.");
        }

        string? packageFamilyName = manifest.IdentityName is not null && manifest.IdentityPublisher is not null
            ? MsixPackageFamilyName.Create(manifest.IdentityName, manifest.IdentityPublisher)
            : null;
        Sha256Hash? signatureSha256 = MsixReader.ComputeSignatureHash(archive);

        List<Installer> installers = [];
        foreach (Architecture architecture in manifest.ApplicationArchitectures)
        {
            installers.Add(new Installer
            {
                Architecture = architecture,
                InstallerType = InstallerType.Msix,
                PackageFamilyName = packageFamilyName,
                SignatureSha256 = signatureSha256,
            });
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.MsixBundle,
            Installers = installers,
            ProductVersion = manifest.IdentityVersion,
        };
    }

    private static ParsedBundleManifest ParseManifest(byte[] manifestBytes)
    {
        var manifest = new ParsedBundleManifest();
        try
        {
            using var manifestStream = new MemoryStream(manifestBytes);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(manifestStream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (string.Equals(reader.LocalName, "Identity", StringComparison.Ordinal) && manifest.IdentityName is null)
                {
                    manifest.IdentityName = MsixReader.GetAttribute(reader, "Name");
                    manifest.IdentityPublisher = MsixReader.GetAttribute(reader, "Publisher");
                    manifest.IdentityVersion = MsixReader.GetAttribute(reader, "Version");
                }
                else if (string.Equals(reader.LocalName, "Package", StringComparison.Ordinal))
                {
                    // Only Type="application" packages install; resource packages (the
                    // schema default when the attribute is absent) carry assets only.
                    if (!string.Equals(MsixReader.GetAttribute(reader, "Type"), "application", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Architecture architecture = MsixReader.ParseArchitecture(MsixReader.GetAttribute(reader, "Architecture"));
                    if (!manifest.ApplicationArchitectures.Contains(architecture))
                    {
                        manifest.ApplicationArchitectures.Add(architecture);
                    }
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"The bundle's {BundleManifestEntryName} is not well-formed XML.", exception);
        }

        return manifest;
    }

    /// <summary>The bundle manifest values the analyzer extracts, in document order.</summary>
    private sealed class ParsedBundleManifest
    {
        public string? IdentityName { get; set; }

        public string? IdentityPublisher { get; set; }

        public string? IdentityVersion { get; set; }

        public List<Architecture> ApplicationArchitectures { get; } = [];
    }
}
