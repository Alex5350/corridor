using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Corridor.AdfsSim.Saml;

/// <summary>Builds the SAML 2.0 federation metadata document (EntityDescriptor with
/// IDPSSODescriptor, signing KeyDescriptor, POST binding SSO endpoint).</summary>
public static class FederationMetadata
{
    public static string Build(string entityId, string singleSignOnEndpoint, X509Certificate2 certificate)
    {
        var certBase64 = Convert.ToBase64String(certificate.RawData);
        var sso = System.Net.WebUtility.HtmlEncode(singleSignOnEndpoint);
        var entity = System.Net.WebUtility.HtmlEncode(entityId);

        return $"""
            <EntityDescriptor xmlns="{SamlXml.MetadataNs}" entityID="{entity}">
              <IDPSSODescriptor protocolSupportEnumeration="{SamlXml.ProtocolNs}" WantAuthnRequestsSigned="false">
                <KeyDescriptor use="signing">
                  <KeyInfo xmlns="{SamlXml.DsNs}">
                    <X509Data>
                      <X509Certificate>{certBase64}</X509Certificate>
                    </X509Data>
                  </KeyInfo>
                </KeyDescriptor>
                <NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</NameIDFormat>
                <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{sso}"/>
              </IDPSSODescriptor>
            </EntityDescriptor>
            """;
    }

    /// <summary>The document serialized without the leading indentation, for HTTP delivery.</summary>
    public static byte[] ToUtf8Bytes(string xml)
    {
        var trimmed = string.Join('\n', xml.Split('\n').Select(line => line.TrimStart()).Where(line => line.Length > 0));
        return Encoding.UTF8.GetBytes(trimmed);
    }
}
