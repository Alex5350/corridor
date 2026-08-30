using System.Net;
using System.Text.RegularExpressions;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>
/// A delegating handler that behaves like a real browser for the portal OIDC dance:
/// it stores and replays Set-Cookie values across redirects itself instead of using
/// HttpClientHandler's cookie jar. That matters here because the OIDC handler issues
/// its correlation and nonce cookies with the Secure attribute (form_post response
/// mode), which browsers still accept on http://localhost as a secure context but
/// .NET's CookieContainer refuses to send over plain http. Redirects are followed
/// manually so intermediate Set-Cookie headers (issued on 302s) are not lost.
/// </summary>
public sealed partial class BrowserLikeCookieHandler : DelegatingHandler
{
    private readonly Dictionary<string, string> _cookies = [];
    private readonly object _gate = new();

    public BrowserLikeCookieHandler() : base(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(request, cancellationToken);
        var redirects = 0;
        while (IsRedirect(response.StatusCode) && response.Headers.Location is not null && redirects < 10)
        {
            CaptureCookies(response);
            var target = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(request.RequestUri!, response.Headers.Location);
            response.Dispose();
            // Browsers switch to GET on 303 and, in practice, on 302 after a form POST.
            using var next = new HttpRequestMessage(HttpMethod.Get, target);
            response = await SendOnceAsync(next, cancellationToken);
            redirects++;
        }
        CaptureCookies(response);
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AttachCookies(request);
        return await base.SendAsync(request, cancellationToken);
    }

    private void AttachCookies(HttpRequestMessage request)
    {
        string header;
        lock (_gate)
        {
            if (_cookies.Count == 0)
            {
                return;
            }
            header = string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}"));
        }
        request.Headers.TryAddWithoutValidation("Cookie", header);
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }
        var setCookies = values.ToList();
        lock (_gate)
        {
            foreach (var raw in setCookies)
            {
                var match = CookieNameValue().Match(raw);
                if (!match.Success)
                {
                    continue;
                }
                var name = match.Groups["name"].Value;
                if (raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                {
                    _cookies.Remove(name);
                    continue;
                }
                _cookies[name] = match.Groups["value"].Value;
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.RedirectMethod;

    [GeneratedRegex(@"^\s*(?<name>[^=;,\s]+)=(?<value>[^;]*)")]
    private static partial Regex CookieNameValue();
}
