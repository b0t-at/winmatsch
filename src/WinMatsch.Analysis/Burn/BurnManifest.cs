using System.Xml;
using System.Xml.Linq;

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

    /// <summary>
    /// Whether any chain package's InstallCondition targets ARM64: it mentions
    /// <c>NativeMachine</c> together with the IMAGE_FILE_MACHINE_ARM64 value 0xAA64 (or an
    /// "arm64" token). Such a bundle installs ARM64 payloads even though its stub is x86.
    /// </summary>
    public bool ChainTargetsArm64 { get; private init; }

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
                && string.Equals(element.Attribute("Action")?.Value, "Upgrade", StringComparison.OrdinalIgnoreCase));

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
            ChainTargetsArm64 = root.Descendants().Any(element
                => element.Attribute("InstallCondition")?.Value is { } condition && ConditionTargetsArm64(condition)),
        };
    }

    private static XElement? FirstByLocalName(IEnumerable<XElement> elements, string localName)
        => elements.FirstOrDefault(element => element.Name.LocalName == localName);

    private static bool ConditionTargetsArm64(string condition)
        => condition.Contains("NativeMachine", StringComparison.OrdinalIgnoreCase)
            && (condition.Contains("0xAA64", StringComparison.OrdinalIgnoreCase)
                || condition.Contains("arm64", StringComparison.OrdinalIgnoreCase));
}
