using System.Xml;
using System.Xml.Linq;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Burn;

/// <summary>
/// The fields this analyzer consumes from the Burn manifest — the XML file named "0" inside
/// the UX container. <c>&lt;Registration&gt;</c> carries the bundle's ARP identity (its Id
/// is the product code WinGet matches against the registry), the nested <c>&lt;Arp&gt;</c>
/// element the display strings, and <c>&lt;RelatedBundle Action="Upgrade"&gt;</c> the
/// upgrade code shared across bundle versions. WiX v3 writes the namespace
/// <c>http://schemas.microsoft.com/wix/2008/Burn</c> and WiX v4+
/// <c>http://wixtoolset.org/schemas/v4/2008/Burn</c>; elements are matched by local name so
/// both parse identically.
/// </summary>
internal sealed class BurnManifest
{
    /// <summary>The Registration Id — the bundle's ARP product code.</summary>
    public string? RegistrationId { get; private init; }

    /// <summary>The Registration Version — the bundle version in four-part form.</summary>
    public string? RegistrationVersion { get; private init; }

    /// <summary>
    /// Whether the bundle writes an ARP entry: an <c>&lt;Arp&gt;</c> element is present and
    /// does not opt out with <c>Register="no"</c> (the ARPSYSTEMCOMPONENT=1 equivalent).
    /// </summary>
    public bool RegistersArpEntry { get; private init; }

    /// <summary>The Arp DisplayName.</summary>
    public string? DisplayName { get; private init; }

    /// <summary>The Arp DisplayVersion.</summary>
    public string? DisplayVersion { get; private init; }

    /// <summary>The Arp Publisher.</summary>
    public string? Publisher { get; private init; }

    /// <summary>The Id of the first RelatedBundle with Action="Upgrade" — the upgrade code.</summary>
    public string? UpgradeCode { get; private init; }

    /// <summary>MSI packages in the chain, including ARP visibility and architecture evidence.</summary>
    public IReadOnlyList<BurnMsiPackage> MsiPackages { get; private init; } = [];

    /// <summary>Architecture targets inferred from every package type in the chain.</summary>
    public IReadOnlyList<Architecture> ChainTargetArchitectures { get; private init; } = [];

    /// <summary>Whether an architecture-bearing condition was negated or otherwise ambiguous.</summary>
    public bool HasAmbiguousArchitectureCondition { get; private init; }

    /// <summary>Parses the manifest XML.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed Burn manifest.</exception>
    public static BurnManifest Parse(byte[] manifestUtf8)
    {
        XDocument document;
        try
        {
            using var manifestStream = new MemoryStream(manifestUtf8);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(manifestStream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"The Burn manifest is not well-formed XML: {exception.Message}", exception);
        }

        XElement root = document.Root!;
        if (root.Name.LocalName != "BurnManifest")
        {
            throw new InvalidDataException($"The Burn manifest root element is '{root.Name.LocalName}', not 'BurnManifest'.");
        }

        XElement? registration = FirstByLocalName(root.Elements(), "Registration");
        XElement? arp = registration is null ? null : FirstByLocalName(registration.Elements(), "Arp");
        XElement? upgradeRelation = root.Descendants().FirstOrDefault(element
            => element.Name.LocalName == "RelatedBundle"
                && IsUpgradeRelation(element));
        List<BurnMsiPackage> msiPackages = ParseMsiPackages(root);
        (List<Architecture> chainTargets, bool ambiguousArchitecture) = ParseChainArchitectures(root);

        return new BurnManifest
        {
            RegistrationId = registration?.Attribute("Id")?.Value,
            RegistrationVersion = registration?.Attribute("Version")?.Value,
            RegistersArpEntry = arp is not null
                && !string.Equals(arp.Attribute("Register")?.Value, "no", StringComparison.OrdinalIgnoreCase),
            DisplayName = arp?.Attribute("DisplayName")?.Value,
            DisplayVersion = arp?.Attribute("DisplayVersion")?.Value,
            Publisher = arp?.Attribute("Publisher")?.Value,
            UpgradeCode = upgradeRelation?.Attribute("Id")?.Value,
            MsiPackages = msiPackages,
            ChainTargetArchitectures = chainTargets,
            HasAmbiguousArchitectureCondition = ambiguousArchitecture,
        };
    }

    private static XElement? FirstByLocalName(IEnumerable<XElement> elements, string localName)
        => elements.FirstOrDefault(element => element.Name.LocalName == localName);

    private static bool IsUpgradeRelation(XElement element)
        => IsValue(element, "Action", "Upgrade")
            || IsValue(element, "RelationType", "Upgrade")
            || IsValue(element, "Type", "Upgrade");

    private static List<BurnMsiPackage> ParseMsiPackages(XElement root)
    {
        List<BurnMsiPackage> packages = [];
        foreach (XElement package in root.Descendants().Where(static element => element.Name.LocalName == "MsiPackage"))
        {
            bool arpSystemComponent = package.Descendants().Any(element
                => element.Name.LocalName == "MsiProperty"
                    && (IsValue(element, "Id", "ARPSYSTEMCOMPONENT")
                        || IsValue(element, "Name", "ARPSYSTEMCOMPONENT"))
                    && IsTruthy(element.Attribute("Value")?.Value));
            bool visible = IsTruthy(package.Attribute("Visible")?.Value) && !arpSystemComponent;
            string? condition = package.Attribute("InstallCondition")?.Value;
            ArchitectureEvidence evidence = ParseTargetArchitecture(condition);
            Architecture? architecture = evidence.Architecture;
            if (architecture is null && IsTruthy(package.Attribute("Win64")?.Value))
            {
                architecture = Architecture.X64;
            }

            packages.Add(new BurnMsiPackage(
                package.Attribute("Id")?.Value,
                package.Attribute("ProductCode")?.Value,
                package.Attribute("UpgradeCode")?.Value,
                package.Attribute("DisplayName")?.Value,
                package.Attribute("Version")?.Value,
                visible,
                architecture,
                condition));
        }

        return packages;
    }

    private static (List<Architecture> Architectures, bool Ambiguous) ParseChainArchitectures(XElement root)
    {
        List<Architecture> architectures = [];
        bool ambiguous = false;
        foreach (XElement package in root.Descendants().Where(static element
            => element.Name.LocalName.EndsWith("Package", StringComparison.Ordinal)))
        {
            ArchitectureEvidence evidence = ParseTargetArchitecture(package.Attribute("InstallCondition")?.Value);
            if (evidence.Architecture is { } architecture && !architectures.Contains(architecture))
            {
                architectures.Add(architecture);
            }

            ambiguous |= evidence.Ambiguous;
            if (evidence.Architecture is null
                && IsTruthy(package.Attribute("Win64")?.Value)
                && !architectures.Contains(Architecture.X64))
            {
                architectures.Add(Architecture.X64);
            }
        }

        return (architectures, ambiguous);
    }

    private static ArchitectureEvidence ParseTargetArchitecture(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return default;
        }

        bool arm64 = condition.Contains("arm64", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("0xAA64", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("43620", StringComparison.Ordinal);
        bool x64 = condition.Contains("amd64", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("x64", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("0x8664", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("34404", StringComparison.Ordinal);
        string compact = condition.Replace(" ", string.Empty, StringComparison.Ordinal);
        bool negatedNativeMachine = compact.Contains("NOTNativeMachine", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("NOT(NativeMachine", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("<>", StringComparison.Ordinal)
            || compact.Contains("!=", StringComparison.Ordinal);
        if ((arm64 || x64) && negatedNativeMachine)
        {
            return new ArchitectureEvidence(null, Ambiguous: true);
        }

        if (arm64)
        {
            return new ArchitectureEvidence(Architecture.Arm64, Ambiguous: false);
        }

        if (x64
            || (condition.Contains("VersionNT64", StringComparison.OrdinalIgnoreCase)
                && !condition.Contains("NOT VersionNT64", StringComparison.OrdinalIgnoreCase)))
        {
            return new ArchitectureEvidence(Architecture.X64, Ambiguous: false);
        }

        return default;
    }

    private static bool IsValue(XElement element, string attributeName, string expected)
        => string.Equals(element.Attribute(attributeName)?.Value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value)
        => value is not null
            && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.Ordinal));

    private readonly record struct ArchitectureEvidence(Architecture? Architecture, bool Ambiguous);
}

/// <summary>Install-relevant metadata for one MSI package in a Burn chain.</summary>
internal sealed record BurnMsiPackage(
    string? Id,
    string? ProductCode,
    string? UpgradeCode,
    string? DisplayName,
    string? Version,
    bool RegistersArpEntry,
    Architecture? TargetArchitecture,
    string? InstallCondition);
