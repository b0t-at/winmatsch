using System.Xml;
using System.Xml.Linq;

namespace WinMatsch.Analysis.Squirrel;

/// <summary>
/// Package identity harvested from a NuGet <c>.nuspec</c> manifest: the fields Squirrel copies
/// into its per-user Apps &amp; Features entry.
/// </summary>
/// <param name="Id">Package id — Squirrel uses it as the uninstall registry key name.</param>
/// <param name="Version">Package (semantic) version.</param>
/// <param name="Title">Display title, when the package sets one.</param>
/// <param name="Authors">Author list, Squirrel's Publisher value.</param>
internal sealed record NuspecMetadata(string? Id, string? Version, string? Title, string? Authors)
{
    /// <summary>True when at least one identity field is present.</summary>
    public bool HasAnyValue => Id is not null || Version is not null || Title is not null || Authors is not null;
}

/// <summary>
/// Minimal, hardened reader for the <c>.nuspec</c> manifest inside a Squirrel release package.
/// Namespace-agnostic (nuspec schema versions differ only by namespace URI); DTD processing is
/// prohibited and external resolution disabled, so hostile manifests cannot trigger entity
/// expansion or fetch anything.
/// </summary>
internal static class NuspecReader
{
    /// <summary>Parses <c>package/metadata</c> and returns the identity fields.</summary>
    /// <param name="stream">Stream over the nuspec XML document.</param>
    /// <exception cref="InvalidDataException">The document is not well-formed XML.</exception>
    public static NuspecMetadata Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
        };

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The package's nuspec manifest is not well-formed XML.", ex);
        }

        XElement? metadata = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "metadata");

        return new NuspecMetadata(
            Id: ReadValue(metadata, "id"),
            Version: ReadValue(metadata, "version"),
            Title: ReadValue(metadata, "title"),
            Authors: ReadValue(metadata, "authors"));
    }

    private static string? ReadValue(XElement? metadata, string localName)
    {
        string? value = metadata?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
