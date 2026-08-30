using System.Xml;

namespace Corridor.AdfsSim.Saml;

/// <summary>XML loading helpers. DTD processing is prohibited and external entities are
/// never resolved: SAML payloads arrive from other services and must not be trusted as
/// parsers.</summary>
public static class SamlXml
{
    public const string ProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string MetadataNs = "urn:oasis:names:tc:SAML:2.0:metadata";
    public const string DsNs = "http://www.w3.org/2000/09/xmldsig#";

    public static readonly XmlReaderSettings SafeReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
    };

    public static XmlDocument LoadDocument(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = false };
        using var reader = XmlReader.Create(new StringReader(xml), SafeReaderSettings);
        doc.Load(reader);
        return doc;
    }

    public static XmlDocument LoadDocument(byte[] bytes)
    {
        var doc = new XmlDocument { PreserveWhitespace = false };
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeReaderSettings);
        doc.Load(reader);
        return doc;
    }

    public static string ToXmlString(XmlDocument document)
    {
        var buffer = new System.Text.StringBuilder();
        var settings = new XmlWriterSettings
        {
            Encoding = System.Text.Encoding.UTF8,
            Indent = false,
            OmitXmlDeclaration = true,
        };

        using (var writer = XmlWriter.Create(buffer, settings))
        {
            document.Save(writer);
        }

        return buffer.ToString();
    }
}
