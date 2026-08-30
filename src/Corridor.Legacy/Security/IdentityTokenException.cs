namespace Corridor.Legacy.Security;

/// <summary>
/// Thrown by token validation when a token is rejected. Carries the cor: fault
/// subcode the SOAP layer should surface.
/// </summary>
public sealed class IdentityTokenException : Exception
{
    public string Subcode { get; }

    public IdentityTokenException(string subcode, string message) : base(message)
    {
        Subcode = subcode;
    }
}
