using System.Net;

namespace Corridor.Legacy.Tests.TestDoubles;

/// <summary>
/// HTTP handler that answers from a fixed in-memory response. Lets the JWT
/// tests serve a JWKS document generated in-test without any network access.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, string> responder) => _responder = responder;

    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responder(request))
        });
    }
}
