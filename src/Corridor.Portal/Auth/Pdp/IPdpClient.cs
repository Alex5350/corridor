namespace Corridor.Portal.Auth.Pdp;

/// <summary>
/// The outcome of one policy decision: whether the request is permitted, plus the PDP
/// StatusMessage when the response carries one (problem-detail detail text).
/// </summary>
public sealed record PdpDecision(bool Permit, string StatusMessage);

/// <summary>
/// Client for the central XACML policy decision point. Implementations must fail closed:
/// any failure to obtain a real Decision resolves to a Deny, never an exception.
/// </summary>
public interface IPdpClient
{
    Task<PdpDecision> DecideAsync(string role, string resource, string action, CancellationToken cancellationToken = default);
}

/// <summary>Resource ids, action ids, and error codes the portal PEP and policies/ agree on.</summary>
public static class PdpAuthorization
{
    public const string TraceCasesResource = "trace-cases";

    public const string AssignmentsResource = "assignments";

    public const string CreateAction = "create";

    public const string ReadAction = "read";

    public const string WriteAction = "write";

    /// <summary>Stable error code carried on 403 problem details when the PDP denied the call.</summary>
    public const string PdpDeniedCode = "cor:PdpDenied";
}
