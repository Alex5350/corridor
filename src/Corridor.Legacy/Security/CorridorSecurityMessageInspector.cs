using System.Xml;
using CoreWCF;
using CoreWCF.Channels;
using CoreWCF.Description;
using CoreWCF.Dispatcher;

namespace Corridor.Legacy.Security;

/// <summary>
/// Dispatch inspector enforcing the demo's identity profile on every TraceLink
/// call. This is a WS-Security-style profile simplified for the demo: a SOAP
/// header &lt;cor:Security xmlns:cor="http://corridor.example/security"&gt; carries
/// EITHER a &lt;saml:Assertion&gt; (adfs-sim) OR a &lt;jwt&gt; element (okta-sim). A
/// production deployment would use a real WS-Trust issued-token channel and
/// negotiate service credentials instead of this hand-rolled header.
/// Validation order: parse header, gate token kind against the app's current
/// TrustMode, then run the matching strategy. Rejections become SOAP faults
/// with cor: subcodes.
/// </summary>
public sealed class CorridorSecurityMessageInspector : IDispatchMessageInspector
{
    public const string IdentityPropertyName = "CorridorValidatedIdentity";

    private readonly ITokenValidator _tokenValidator;

    public CorridorSecurityMessageInspector(ITokenValidator tokenValidator)
    {
        _tokenValidator = tokenValidator;
    }

    public object AfterReceiveRequest(ref Message request, IClientChannel channel, InstanceContext instanceContext)
    {
        int headerIndex = request.Headers.FindHeader("Security", CorridorSecurityNamespaces.Security);
        if (headerIndex < 0)
        {
            throw CorridorFault.Sender(CorridorFaultSubcodes.MissingSecurityHeader,
                "Every TraceLink call must carry a cor:Security header containing a saml:Assertion or a jwt element.");
        }

        IdentityToken token = ReadIdentityToken(request, headerIndex);
        try
        {
            ValidatedIdentity identity = _tokenValidator.Validate(token);
            request.Properties[IdentityPropertyName] = identity;
            return identity; // flows to BeforeSendReply as correlationState
        }
        catch (IdentityTokenException exception)
        {
            throw CorridorFault.Sender(exception.Subcode, exception.Message);
        }
        catch (Microsoft.Data.SqlClient.SqlException exception)
        {
            // The trust mode lives in SQL; a database outage must still produce
            // a cor: fault rather than an unhandled channel error.
            throw DataAccess.SqlFaultMapper.Map(exception);
        }
        catch (InvalidOperationException exception)
        {
            throw CorridorFault.Receiver(CorridorFaultSubcodes.DataAccessError,
                $"Could not read the migration trust mode: {exception.Message}");
        }
    }

    public void BeforeSendReply(ref Message reply, object correlationState)
    {
        // Nothing to enrich on the reply path; the correlation state (the
        // validated identity) is only needed for audit logging upstream.
    }

    private static IdentityToken ReadIdentityToken(Message request, int headerIndex)
    {
        using XmlReader reader = request.Headers.GetReaderAtHeader(headerIndex);
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);

        XmlElement? security = document.DocumentElement;
        if (security is null || security.LocalName != "Security" || security.NamespaceURI != CorridorSecurityNamespaces.Security)
        {
            throw CorridorFault.Sender(CorridorFaultSubcodes.InvalidTokenFormat,
                $"Expected a {CorridorSecurityNamespaces.Security}:Security header.");
        }

        XmlElement? assertion = security["Assertion", SamlTokenValidator.AssertionNamespace];
        if (assertion is not null)
        {
            return new IdentityToken(IdentityTokenKind.SamlAssertion, assertion.OuterXml);
        }

        // Local-name match: tolerate the jwt element with or without the cor prefix.
        XmlElement? jwt = security.GetElementsByTagName("jwt").OfType<XmlElement>().FirstOrDefault();
        string token = jwt?.InnerText.Trim() ?? string.Empty;
        if (token.Length > 0)
        {
            return new IdentityToken(IdentityTokenKind.Jwt, token);
        }

        throw CorridorFault.Sender(CorridorFaultSubcodes.InvalidTokenFormat,
            "The cor:Security header must contain either a saml:Assertion element or a jwt element.");
    }
}

/// <summary>
/// Wires the inspector into the dispatch runtime of the TraceLink endpoint.
/// </summary>
public sealed class CorridorSecurityEndpointBehavior : IEndpointBehavior
{
    private readonly ITokenValidator _tokenValidator;

    public CorridorSecurityEndpointBehavior(ITokenValidator tokenValidator)
    {
        _tokenValidator = tokenValidator;
    }

    public void Validate(ServiceEndpoint endpoint)
    {
    }

    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters)
    {
    }

    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
    {
        endpointDispatcher.DispatchRuntime.MessageInspectors.Add(new CorridorSecurityMessageInspector(_tokenValidator));
    }

    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime)
    {
        throw new NotSupportedException("CorridorSecurityEndpointBehavior is a server-side behavior.");
    }
}
