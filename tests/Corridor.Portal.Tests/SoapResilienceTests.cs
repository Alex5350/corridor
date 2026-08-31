using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Corridor.Portal.Data;
using Corridor.Portal.Data.Memory;
using Corridor.Portal.Models;
using Corridor.Portal.Services.TraceLink;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Corridor.Portal.Tests;

/// <summary>
/// Scriptable SOAP endpoint behind the real SoapTraceLinkClient: counts attempts per SOAP
/// action and can be told to refuse connections, hang past the client timeout, or fail the
/// first N attempts transiently before answering with a valid envelope. The behaviors model
/// what SocketsHttpHandler actually raises: refusal is an HttpRequestException wrapping a
/// SocketException, and a hang is cut short by the client's own timeout token.
/// </summary>
public sealed class ScriptedSoapHandler : HttpMessageHandler
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Cor = "http://corridor.example/tracelink/2026/08";

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _remainingTransientFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _hangs = new(StringComparer.Ordinal);

    public void Reset()
    {
        lock (_gate)
        {
            _attempts.Clear();
            _remainingTransientFailures.Clear();
            _refused.Clear();
            _hangs.Clear();
        }
    }

    public int AttemptsFor(string action)
    {
        lock (_gate)
        {
            return _attempts.TryGetValue(action, out var count) ? count : 0;
        }
    }

    /// <summary>Fails the first <paramref name="count"/> attempts of an action with a transient connection error.</summary>
    public void FailTransientAttempts(string action, int count)
    {
        lock (_gate)
        {
            _remainingTransientFailures[action] = count;
        }
    }

    /// <summary>Every attempt of the action fails as if the TCP connection to the legacy service was refused.</summary>
    public void Refuse(string action)
    {
        lock (_gate)
        {
            _refused.Add(action);
        }
    }

    /// <summary>Every attempt of the action hangs, so the configured HttpClient timeout fires first.</summary>
    public void Hang(string action, TimeSpan delay)
    {
        lock (_gate)
        {
            _hangs[action] = delay;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var action = ReadAction(request);
        XElement responseBody;
        TimeSpan? hang = null;
        lock (_gate)
        {
            _attempts[action] = (_attempts.TryGetValue(action, out var count) ? count : 0) + 1;
            if (_refused.Contains(action))
            {
                throw new HttpRequestException($"Scripted connection refused for {action}.",
                    new SocketException((int)SocketError.ConnectionRefused));
            }
            if (_remainingTransientFailures.TryGetValue(action, out var remaining) && remaining > 0)
            {
                _remainingTransientFailures[action] = remaining - 1;
                throw new HttpRequestException($"Scripted transient failure for {action}, {remaining} left.");
            }
            if (_hangs.TryGetValue(action, out var delay))
            {
                hang = delay;
            }
            responseBody = ResponseFor(action);
        }
        if (hang is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }
        var envelope = new XElement(Soap + "Envelope", new XElement(Soap + "Body", responseBody));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml")
        };
    }

    private static string ReadAction(HttpRequestMessage request)
    {
        var soapAction = request.Headers.TryGetValues("SOAPAction", out var values)
            ? values.FirstOrDefault() ?? ""
            : "";
        var trimmed = soapAction.Trim('"');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static XElement ResponseFor(string action)
    {
        return action switch
        {
            "SearchCases" => new XElement(Cor + "SearchCasesResponse",
                new XElement(Cor + "SearchCasesResult", CaseElement("TRC-300101"))),
            "GetCase" => new XElement(Cor + "GetCaseResponse",
                new XElement(Cor + "GetCaseResult", CaseElement("TRC-300101"))),
            "CreateTraceRequest" => new XElement(Cor + "CreateTraceRequestResponse",
                new XElement(Cor + "CreateTraceRequestResult", "TRC-300201")),
            "UpdateStatus" => new XElement(Cor + "UpdateStatusResponse",
                new XElement(Cor + "UpdateStatusResult", "true")),
            _ => throw new InvalidOperationException($"The scripted SOAP handler has no response for {action}.")
        };
    }

    private static XElement CaseElement(string caseNumber)
    {
        return new XElement(Cor + "TraceCase",
            new XElement(Cor + "CaseNumber", caseNumber),
            new XElement(Cor + "LicenseeName", "Scripted Distributors"),
            new XElement(Cor + "ItemDescription", "Kalvin KB-7 .22 bolt rifle"),
            new XElement(Cor + "Serial", "KB7-0041999"),
            new XElement(Cor + "Status", "Received"),
            new XElement(Cor + "SubmittedAt", "2026-08-28T09:30:00Z"),
            new XElement(Cor + "SubmittedBy", "officer@corridor.example"),
            new XElement(Cor + "Disposition", ""));
    }
}

/// <summary>Scriptable okta-sim token endpoint: instant success, or transient failures on demand.</summary>
public sealed class ScriptedTokenHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private int _attempts;
    private int _remainingTransientFailures;

    public void Reset()
    {
        lock (_gate)
        {
            _attempts = 0;
            _remainingTransientFailures = 0;
        }
    }

    /// <summary>Fails the first <paramref name="count"/> token requests with a transient connection error.</summary>
    public void FailTransientAttempts(int count)
    {
        lock (_gate)
        {
            _remainingTransientFailures = count;
        }
    }

    public int Attempts
    {
        get
        {
            lock (_gate)
            {
                return _attempts;
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _attempts++;
            if (_remainingTransientFailures > 0)
            {
                _remainingTransientFailures--;
                throw new HttpRequestException("Scripted transient token endpoint failure.");
            }
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"test-service-token\"}", Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Boots the portal with the REAL SoapTraceLinkClient over scripted handlers, so the named
/// client pipelines (timeouts, retry) are exercised end to end. The legacy app is flipped to
/// Okta trust mode so the credential hop is the scripted token endpoint rather than the ADFS
/// certificate files.
/// </summary>
public class SoapPortalFactory : PortalFactory
{
    public ScriptedSoapHandler SoapHandler { get; } = new();

    public ScriptedTokenHandler TokenHandler { get; } = new();

    /// <summary>Shrinks the SOAP client timeouts so timeout paths are testable without waiting 8 seconds.</summary>
    public TimeSpan? ClientTimeout { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // The real client instead of the base factory's fake: these tests target the
            // HTTP pipeline, so the SOAP envelopes must actually be built, sent, and parsed.
            services.RemoveAll<ITraceLinkClient>();
            services.AddTransient<ITraceLinkClient>(sp => sp.GetRequiredService<SoapTraceLinkClient>());

            services.RemoveAll<IMigrationAppRepository>();
            var apps = InMemoryMigrationAppRepository.DefaultApps()
                .Select(a => a.AppKey == "legacy" ? a with { TrustMode = TrustMode.Okta } : a);
            services.AddSingleton<IMigrationAppRepository>(new InMemoryMigrationAppRepository(apps));

            services.AddHttpClient<OktaServiceTokenClient>()
                .ConfigurePrimaryHttpMessageHandler(() => TokenHandler);
            services.AddHttpClient(TraceLinkHttpClients.Read)
                .ConfigurePrimaryHttpMessageHandler(() => SoapHandler);
            services.AddHttpClient(TraceLinkHttpClients.Write)
                .ConfigurePrimaryHttpMessageHandler(() => SoapHandler);
            if (ClientTimeout is { } timeout)
            {
                services.AddHttpClient(TraceLinkHttpClients.Read)
                    .ConfigureHttpClient(client => client.Timeout = timeout);
                services.AddHttpClient(TraceLinkHttpClients.Write)
                    .ConfigureHttpClient(client => client.Timeout = timeout);
            }
        });
    }
}

/// <summary>Portal with 400 ms SOAP timeouts: a handler that hangs a few seconds is effectively forever.</summary>
public sealed class FastTimeoutSoapFactory : SoapPortalFactory
{
    public FastTimeoutSoapFactory()
    {
        ClientTimeout = TimeSpan.FromMilliseconds(400);
    }
}

/// <summary>Timeout, unreachable, and retry behavior of the SOAP hop through the real pipeline.</summary>
public class SoapResilienceTests : IClassFixture<SoapPortalFactory>
{
    private readonly SoapPortalFactory _factory;

    public SoapResilienceTests(SoapPortalFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RefusedConnection_YieldsTraceServiceUnreachableProblem()
    {
        _factory.SoapHandler.Reset();
        _factory.SoapHandler.Refuse("SearchCases");
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(502, body.GetProperty("status").GetInt32());
        Assert.Equal("cor:TraceServiceUnreachable", body.GetProperty("faultSubcode").GetString());
    }

    [Fact]
    public async Task FlakySearch_RetriesOnceThenSucceedsWithExactlyTwoAttempts()
    {
        _factory.SoapHandler.Reset();
        _factory.SoapHandler.FailTransientAttempts("SearchCases", 1);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cases = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TRC-300101", cases[0].GetProperty("caseNumber").GetString());
        Assert.Equal(2, _factory.SoapHandler.AttemptsFor("SearchCases"));
    }

    [Fact]
    public async Task CreateIsNeverRetried_RefusedCreateReportsUnreachableAfterExactlyOneAttempt()
    {
        _factory.SoapHandler.Reset();
        _factory.SoapHandler.Refuse("CreateTraceRequest");
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/cases", new
        {
            licenseeName = "Scripted Distributors",
            itemDescription = "Kalvin KB-7 .22 bolt rifle",
            serial = "KB7-0041999"
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cor:TraceServiceUnreachable", body.GetProperty("faultSubcode").GetString());
        Assert.Equal(1, _factory.SoapHandler.AttemptsFor("CreateTraceRequest"));
    }

    [Fact]
    public async Task ServiceTokenClient_RetriesOnceOnTransientFailure()
    {
        _factory.TokenHandler.Reset();
        _factory.TokenHandler.FailTransientAttempts(1);
        var attemptsBefore = _factory.TokenHandler.Attempts;
        using var scope = _factory.Services.CreateScope();
        var tokenClient = scope.ServiceProvider.GetRequiredService<OktaServiceTokenClient>();

        var token = await tokenClient.GetAccessTokenAsync();

        Assert.Equal("test-service-token", token);
        Assert.Equal(attemptsBefore + 2, _factory.TokenHandler.Attempts);
    }
}

/// <summary>Slow legacy service: timeouts map to cor:TraceServiceTimeout and degrade the page, never a 500.</summary>
public class SoapTimeoutTests : IClassFixture<FastTimeoutSoapFactory>
{
    private readonly FastTimeoutSoapFactory _factory;

    public SoapTimeoutTests(FastTimeoutSoapFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SlowSearch_YieldsTraceServiceTimeoutProblem()
    {
        _factory.SoapHandler.Reset();
        _factory.SoapHandler.Hang("SearchCases", TimeSpan.FromSeconds(30));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(502, body.GetProperty("status").GetInt32());
        Assert.Equal("cor:TraceServiceTimeout", body.GetProperty("faultSubcode").GetString());
    }

    [Fact]
    public async Task SlowSearch_RendersDegradedCasesPageInsteadOf500()
    {
        _factory.SoapHandler.Reset();
        _factory.SoapHandler.Hang("SearchCases", TimeSpan.FromSeconds(30));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/Cases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("notice error", html);
        Assert.Contains("The trace service timed out", html);
        Assert.Contains("No trace cases match this filter.", html);
        Assert.DoesNotContain("TRC-300101", html);
        // The forms still render, so officers can keep working while the legacy service is slow.
        Assert.Contains("Create trace request", html);
        Assert.Contains("Update case status", html);
    }
}
