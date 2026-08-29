using System.Text;
using System.Xml;

namespace Corridor.OktaSim.Saml;

/// <summary>
/// XML loading with hardened settings shared by the SAML and XACML paths:
/// DTD prohibited, no resolver, so no entity expansion or external fetches.
/// </summary>
public static class SafeXml
{
    public static XmlReaderSettings ReaderSettings { get; } = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = true,
    };

    public static XmlDocument CreateDocument()
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        return doc;
    }

    public static XmlDocument LoadDocument(string xml)
    {
        var doc = CreateDocument();
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        doc.Load(reader);
        return doc;
    }

    public static XmlElement? SingleElement(XmlDocument doc, string localName, string ns)
    {
        var nodes = doc.GetElementsByTagName(localName, ns);
        return nodes.Count == 0 ? null : (XmlElement)nodes[0]!;
    }
}
