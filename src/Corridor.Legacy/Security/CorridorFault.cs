using CoreWCF;

namespace Corridor.Legacy.Security;

/// <summary>Namespace for the cor: SOAP fault subcodes and the cor:Security header.</summary>
public static class CorridorSecurityNamespaces
{
    public const string Security = "http://corridor.example/security";
}

/// <summary>
/// Builds the SOAP faults this service raises. Every fault carries a subcode in
/// the cor: namespace (http://corridor.example/security) per the repo-wide
/// error discipline: SOAP Faults with subcodes, problem details on REST.
/// </summary>
public static class CorridorFault
{
    public const string Namespace = CorridorSecurityNamespaces.Security;

    /// <summary>Fault for caller error: Sender code with a cor: subcode.</summary>
    public static FaultException Sender(string subcode, string reason) =>
        new(reason, FaultCode.CreateSenderFaultCode(subcode, Namespace));

    /// <summary>Fault for server-side error: Receiver code with a cor: subcode.</summary>
    public static FaultException Receiver(string subcode, string reason) =>
        new(reason, FaultCode.CreateReceiverFaultCode(subcode, Namespace));
}

/// <summary>
/// Names of the cor: fault subcodes used by TraceLink.
/// </summary>
public static class CorridorFaultSubcodes
{
    public const string MissingSecurityHeader = "MissingSecurityHeader";
    public const string InvalidTokenFormat = "InvalidTokenFormat";
    public const string InvalidIdentityMode = "InvalidIdentityMode";
    public const string InvalidToken = "InvalidToken";
    public const string InvalidRequest = "InvalidRequest";
    public const string CaseNotFound = "CaseNotFound";
    public const string IllegalTransition = "IllegalTransition";
    public const string UnknownStatus = "UnknownStatus";
    public const string DataAccessError = "DataAccessError";
}
