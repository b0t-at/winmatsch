using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WinMatsch.Analysis.Msi;

/// <summary>
/// A minimal, defensive reader for the <c>\x05SummaryInformation</c> OLE property set stream
/// of an MSI (MS-OLEPS layout). Only the properties WinMatsch needs are surfaced: Template
/// (PID 7, for example <c>x64;1033</c>), Creating Application (PID 18) and Comments (PID 6).
/// The hand-rolled reader exists because the OpenMcdf OLE companion package has no stable,
/// AOT-vetted release; the subset needed here is tiny.
/// </summary>
internal sealed class MsiSummaryInformation
{
    private const ushort LittleEndianByteOrderMark = 0xFFFE;
    private const int VtI2 = 2;
    private const int VtI4 = 3;
    private const int VtLpstr = 30;
    private const int CommentsPropertyId = 6;
    private const int TemplatePropertyId = 7;
    private const int CreatingApplicationPropertyId = 18;

    /// <summary>The FMTID of the SummaryInformation property set.</summary>
    private static readonly Guid _summaryInformationFormatId = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    private MsiSummaryInformation(string? template, string? creatingApplication, string? comments)
    {
        Template = template;
        CreatingApplication = creatingApplication;
        Comments = comments;
    }

    /// <summary>A summary with no properties, used when the stream is absent.</summary>
    public static MsiSummaryInformation Empty { get; } = new(null, null, null);

    /// <summary>PID 7: the target platform and languages, for example <c>x64;1033</c>.</summary>
    public string? Template { get; }

    /// <summary>PID 18: the tool that created the package, for example <c>WiX Toolset v4</c>.</summary>
    public string? CreatingApplication { get; }

    /// <summary>PID 6: free-form comments.</summary>
    public string? Comments { get; }

    /// <summary>Parses the raw <c>\x05SummaryInformation</c> stream contents.</summary>
    /// <exception cref="InvalidDataException">The property set structure is malformed.</exception>
    public static MsiSummaryInformation Read(ReadOnlySpan<byte> stream)
    {
        // Property set stream header: byte order (2), version (2), system identifier (4),
        // CLSID (16), number of property sets (4), then per set an FMTID (16) + offset (4).
        if (stream.Length < 28 || BinaryPrimitives.ReadUInt16LittleEndian(stream) != LittleEndianByteOrderMark)
        {
            throw new InvalidDataException("The MSI SummaryInformation stream is not a little-endian OLE property set.");
        }

        uint setCount = BinaryPrimitives.ReadUInt32LittleEndian(stream[24..]);
        int sectionOffset = -1;
        for (int i = 0; i < setCount; i++)
        {
            int entryOffset = 28 + (i * 20);
            if (entryOffset + 20 > stream.Length)
            {
                throw new InvalidDataException("The MSI SummaryInformation stream is truncated inside its property set table.");
            }

            var formatId = new Guid(stream.Slice(entryOffset, 16));
            if (formatId == _summaryInformationFormatId)
            {
                sectionOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(stream[(entryOffset + 16)..]));
                break;
            }
        }

        if (sectionOffset < 0)
        {
            return Empty; // No SummaryInformation set in the stream; treat as absent.
        }

        // Section: size (4), property count (4), then (property id, offset from section start) pairs.
        if (sectionOffset + 8 > stream.Length)
        {
            throw new InvalidDataException("The MSI SummaryInformation section offset points outside the stream.");
        }

        uint propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(stream[(sectionOffset + 4)..]);
        string? template = null;
        string? creatingApplication = null;
        string? comments = null;
        for (int i = 0; i < propertyCount; i++)
        {
            int entryOffset = sectionOffset + 8 + (i * 8);
            if (entryOffset + 8 > stream.Length)
            {
                throw new InvalidDataException("The MSI SummaryInformation section is truncated inside its property table.");
            }

            uint propertyId = BinaryPrimitives.ReadUInt32LittleEndian(stream[entryOffset..]);
            int valueOffset = sectionOffset + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(stream[(entryOffset + 4)..]));
            switch (propertyId)
            {
                case CommentsPropertyId:
                    comments = ReadValue(stream, valueOffset);
                    break;
                case TemplatePropertyId:
                    template = ReadValue(stream, valueOffset);
                    break;
                case CreatingApplicationPropertyId:
                    creatingApplication = ReadValue(stream, valueOffset);
                    break;
                default:
                    break; // Not a property WinMatsch consumes.
            }
        }

        return new MsiSummaryInformation(template, creatingApplication, comments);
    }

    /// <summary>Reads a typed property value; types other than VT_LPSTR/VT_I2/VT_I4 yield null.</summary>
    private static string? ReadValue(ReadOnlySpan<byte> stream, int valueOffset)
    {
        if (valueOffset < 0 || valueOffset + 4 > stream.Length)
        {
            throw new InvalidDataException("An MSI SummaryInformation property offset points outside the stream.");
        }

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(stream[valueOffset..]);
        switch (type)
        {
            case VtLpstr:
                {
                    if (valueOffset + 8 > stream.Length)
                    {
                        throw new InvalidDataException("An MSI SummaryInformation string property is truncated.");
                    }

                    int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(stream[(valueOffset + 4)..]));
                    if (length < 0 || valueOffset + 8 + length > stream.Length)
                    {
                        throw new InvalidDataException("An MSI SummaryInformation string property is truncated.");
                    }

                    // The declared length includes the terminating NUL; the properties WinMatsch
                    // consumes (Template, Creating Application, Comments) are ASCII in practice,
                    // so decoding as UTF-8 and trimming at the first NUL is sufficient.
                    ReadOnlySpan<byte> bytes = stream.Slice(valueOffset + 8, length);
                    int nul = bytes.IndexOf((byte)0);
                    return Encoding.UTF8.GetString(nul >= 0 ? bytes[..nul] : bytes);
                }

            case VtI2 when valueOffset + 6 <= stream.Length:
                return BinaryPrimitives.ReadInt16LittleEndian(stream[(valueOffset + 4)..]).ToString(CultureInfo.InvariantCulture);

            case VtI4 when valueOffset + 8 <= stream.Length:
                return BinaryPrimitives.ReadInt32LittleEndian(stream[(valueOffset + 4)..]).ToString(CultureInfo.InvariantCulture);

            default:
                return null;
        }
    }
}
