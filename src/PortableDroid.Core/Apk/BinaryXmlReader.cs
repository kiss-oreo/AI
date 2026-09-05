using System.Text;

namespace PortableDroid.Core.Apk;

/// <summary>
/// Minimal reader for Android's binary XML (AXML) format, enough to read the
/// attributes we need out of AndroidManifest.xml without shipping aapt.
///
/// Format reference: AOSP frameworks/base/libs/androidfw/include/androidfw/ResourceTypes.h
/// We deliberately support only what a manifest uses: string pool, resource map,
/// start-element and end-element chunks.
/// </summary>
public static class BinaryXmlReader
{
    private const ushort ChunkStringPool = 0x0001;
    private const ushort ChunkXmlResourceMap = 0x0180;
    private const ushort ChunkXmlStartElement = 0x0102;

    private const int TypeReference = 0x01;
    private const int TypeString = 0x03;
    private const int TypeIntDec = 0x10;
    private const int TypeIntHex = 0x11;
    private const int TypeIntBool = 0x12;

    public sealed record XmlAttribute(string Namespace, string Name, uint ResourceId, string Value, int RawType, uint RawData);

    public sealed record XmlElement(string Name, IReadOnlyList<XmlAttribute> Attributes);

    /// <summary>Parses AXML bytes into a flat, ordered list of start elements.</summary>
    public static IReadOnlyList<XmlElement> Parse(byte[] data)
    {
        if (data is null || data.Length < 8)
            throw new InvalidDataException("Binary XML is empty or truncated.");

        using var ms = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        reader.ReadUInt16();               // file type
        reader.ReadUInt16();               // header size
        var fileSize = reader.ReadUInt32();
        if (fileSize > data.Length) fileSize = (uint)data.Length;

        string[] strings = Array.Empty<string>();
        uint[] resourceIds = Array.Empty<uint>();
        var elements = new List<XmlElement>();

        while (ms.Position + 8 <= fileSize)
        {
            var chunkStart = ms.Position;
            var chunkType = reader.ReadUInt16();
            var headerSize = reader.ReadUInt16();
            var chunkSize = reader.ReadUInt32();

            if (chunkSize < 8 || chunkStart + chunkSize > fileSize)
                break; // malformed; stop rather than throw so partial data is still usable

            switch (chunkType)
            {
                case ChunkStringPool:
                    strings = ReadStringPool(reader, chunkStart, headerSize, chunkSize);
                    break;

                case ChunkXmlResourceMap:
                {
                    var count = (int)((chunkSize - headerSize) / 4);
                    ms.Position = chunkStart + headerSize;
                    resourceIds = new uint[count];
                    for (var i = 0; i < count; i++) resourceIds[i] = reader.ReadUInt32();
                    break;
                }

                case ChunkXmlStartElement:
                {
                    ms.Position = chunkStart + headerSize;
                    var nsIndex = reader.ReadInt32();
                    var nameIndex = reader.ReadInt32();
                    reader.ReadUInt16();                       // attribute start offset
                    reader.ReadUInt16();                       // attribute size
                    var attributeCount = reader.ReadUInt16();
                    reader.ReadUInt16();                       // id index
                    reader.ReadUInt16();                       // class index
                    reader.ReadUInt16();                       // style index

                    _ = nsIndex;
                    var attributes = new List<XmlAttribute>(attributeCount);
                    for (var i = 0; i < attributeCount; i++)
                    {
                        var attrNs = reader.ReadInt32();
                        var attrName = reader.ReadInt32();
                        var attrRawValue = reader.ReadInt32();
                        reader.ReadUInt16();                   // value size
                        reader.ReadByte();                     // padding
                        var valueType = reader.ReadByte();
                        var valueData = reader.ReadUInt32();

                        var resId = attrName >= 0 && attrName < resourceIds.Length ? resourceIds[attrName] : 0u;
                        attributes.Add(new XmlAttribute(
                            Namespace: SafeString(strings, attrNs),
                            Name: SafeString(strings, attrName),
                            ResourceId: resId,
                            Value: FormatValue(strings, valueType, valueData, attrRawValue),
                            RawType: valueType,
                            RawData: valueData));
                    }

                    elements.Add(new XmlElement(SafeString(strings, nameIndex), attributes));
                    break;
                }
            }

            ms.Position = chunkStart + chunkSize;
        }

        return elements;
    }

    private static string[] ReadStringPool(BinaryReader reader, long chunkStart, ushort headerSize, uint chunkSize)
    {
        var ms = reader.BaseStream;
        ms.Position = chunkStart + 8;

        var stringCount = reader.ReadInt32();
        reader.ReadInt32();                       // style count
        var flags = reader.ReadUInt32();
        var stringsStart = reader.ReadUInt32();
        reader.ReadUInt32();                      // styles start

        var isUtf8 = (flags & 0x0100) != 0;
        if (stringCount <= 0 || stringCount > 500_000) return Array.Empty<string>();

        ms.Position = chunkStart + headerSize;
        var offsets = new uint[stringCount];
        for (var i = 0; i < stringCount; i++) offsets[i] = reader.ReadUInt32();

        var result = new string[stringCount];
        var poolBase = chunkStart + stringsStart;

        for (var i = 0; i < stringCount; i++)
        {
            var position = poolBase + offsets[i];
            if (position < 0 || position >= chunkStart + chunkSize)
            {
                result[i] = string.Empty;
                continue;
            }

            ms.Position = position;
            try
            {
                result[i] = isUtf8 ? ReadUtf8String(reader) : ReadUtf16String(reader);
            }
            catch (EndOfStreamException)
            {
                result[i] = string.Empty;
            }
        }

        return result;
    }

    private static string ReadUtf8String(BinaryReader reader)
    {
        ReadUtf8Length(reader);                     // character count (unused)
        var byteLength = ReadUtf8Length(reader);    // byte count
        var bytes = reader.ReadBytes(byteLength);
        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadUtf8Length(BinaryReader reader)
    {
        int length = reader.ReadByte();
        if ((length & 0x80) != 0)
            length = ((length & 0x7F) << 8) | reader.ReadByte();
        return length;
    }

    private static string ReadUtf16String(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        if ((length & 0x8000) != 0)
            length = ((length & 0x7FFF) << 16) | reader.ReadUInt16();

        var bytes = reader.ReadBytes(length * 2);
        return Encoding.Unicode.GetString(bytes);
    }

    private static string SafeString(string[] pool, int index) =>
        index >= 0 && index < pool.Length ? pool[index] : string.Empty;

    private static string FormatValue(string[] pool, int type, uint data, int rawValueIndex)
    {
        switch (type)
        {
            case TypeString:
                return SafeString(pool, rawValueIndex >= 0 ? rawValueIndex : (int)data);
            case TypeIntBool:
                return data != 0 ? "true" : "false";
            case TypeReference:
                return "@" + data.ToString("X");
            case TypeIntHex:
                return "0x" + data.ToString("X");
            case TypeIntDec:
                return ((int)data).ToString();
            default:
                return ((int)data).ToString();
        }
    }
}
