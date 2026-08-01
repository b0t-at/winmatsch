using System.IO.Compression;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>Builds in-memory MSIX/AppX packages and bundles (plain zips) for tests.</summary>
internal static class MsixFixtures
{
    /// <summary>The publisher subject whose publisher id is the well-known <c>8wekyb3d8bbwe</c>.</summary>
    public const string MicrosoftPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    /// <summary>Builds a package zip with an <c>AppxManifest.xml</c> and an optional signature entry.</summary>
    public static MemoryStream BuildPackage(string manifestXml, byte[]? signature = null)
        => BuildZip(("AppxManifest.xml", Encoding.UTF8.GetBytes(manifestXml)), ("AppxSignature.p7x", signature));

    /// <summary>Builds a bundle zip with an <c>AppxMetadata/AppxBundleManifest.xml</c> and an optional signature entry.</summary>
    public static MemoryStream BuildBundle(string bundleManifestXml, byte[]? signature = null)
        => BuildZip(
            ("AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes(bundleManifestXml)),
            ("AppxSignature.p7x", signature));

    /// <summary>Composes a package manifest; null sections are omitted.</summary>
    public static string PackageManifest(
        string identityName = "Contoso.Editor",
        string publisher = "CN=Contoso Ltd, O=Contoso Ltd, C=US",
        string version = "2.4.1.0",
        string? processorArchitecture = "x64",
        string? displayName = "Contoso Editor",
        string? publisherDisplayName = "Contoso Ltd",
        string? dependencies = """<TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />""",
        string? capabilities = null)
    {
        string architectureAttribute = processorArchitecture is null
            ? ""
            : $" ProcessorArchitecture=\"{processorArchitecture}\"";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
              <Identity Name="{identityName}" Publisher="{publisher}" Version="{version}"{architectureAttribute} />
              <Properties>
                <DisplayName>{displayName}</DisplayName>
                <PublisherDisplayName>{publisherDisplayName}</PublisherDisplayName>
              </Properties>
              <Dependencies>
                {dependencies}
              </Dependencies>
              {(capabilities is null ? "" : $"<Capabilities>{capabilities}</Capabilities>")}
            </Package>
            """;
    }

    private static MemoryStream BuildZip(params (string Name, byte[]? Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[]? content) in entries)
            {
                if (content is null)
                {
                    continue;
                }

                ZipArchiveEntry entry = archive.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
