using System.Net;
using System.Xml.Linq;
using Corridor.Portal.Auth.Pdp;
using Corridor.Portal.Models;
using Corridor.Portal.Services.TraceLink;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Corridor.Portal.Tests;

/// <summary>Boots the real portal with in-memory stores, a fake SOAP client, a fake PDP, and test auth.</summary>
public class PortalFactory : WebApplicationFactory<Program>
{
    public FakeTraceLinkClient TraceClient { get; } = new();

    /// <summary>Fake PDP behind the pdp named client: permits everything by default, scriptable per triple.</summary>
    public FakePdpHandler Pdp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting flows into the WebApplicationBuilder before Program.cs reads it,
        // so the in-memory stores are selected at boot.
        builder.UseSetting("Data:UseInMemory", "true");
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:UseInMemory"] = "true",
                ["Okta:Authority"] = "http://localhost:59999",
                ["Adfs:BaseAddress"] = "http://localhost:59998",
                ["Portal:PdpBaseUrl"] = "http://localhost:59997"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITraceLinkClient>();
            services.AddSingleton<ITraceLinkClient>(TraceClient);
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.AddAuthorization(options =>
            {
                options.AddPolicy("AnyRole", policy =>
                {
                    policy.AddAuthenticationSchemes(TestAuthHandler.SchemeName);
                    policy.RequireRole("Admin", "Officer", "Clerk", "Inspector");
                });
                options.AddPolicy("SpaBearer", policy =>
                {
                    policy.AddAuthenticationSchemes(TestAuthHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                });
            });
            // The real pdp pipeline (3 second timeout, one retry) over the scripted fake, so
            // PEP tests exercise the actual client without a network.
            services.AddHttpClient(PdpHttpClient.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Pdp);
        });
    }
}

/// <summary>Fake TraceLink: no network, scriptable fault subcodes for problem-details tests.</summary>
public sealed class FakeTraceLinkClient : ITraceLinkClient
{
    public string? FaultSubcodeForNextCall { get; set; }

    public string NextCaseNumber { get; set; } = "TRC-200001";

    private static readonly DateTime BaseTime = new(2026, 8, 28, 9, 30, 0, DateTimeKind.Utc);

    public List<TraceCase> Cases { get; } =
    [
        new TraceCase("TRC-100101", "Riverside Sporting Goods", "Kalvin KB-7 .22 bolt rifle", "KB7-0041882",
            "Received", BaseTime, "officer@corridor.example", null),
        new TraceCase("TRC-100102", "Northgate Firearms Exchange", "Merrin M-12 shotgun 12ga", "M12-771204",
            "UnderReview", BaseTime.AddHours(-4), "officer@corridor.example", null)
    ];

    public Task<IReadOnlyList<TraceCase>> SearchCasesAsync(string requester, string? statusFilter, int maxRows, CancellationToken ct = default)
    {
        ThrowIfScripted();
        IEnumerable<TraceCase> query = Cases;
        if (!string.IsNullOrEmpty(statusFilter))
        {
            query = query.Where(c => c.Status == statusFilter);
        }
        IReadOnlyList<TraceCase> result = query.Take(maxRows).ToList();
        return Task.FromResult(result);
    }

    public Task<TraceCase?> GetCaseAsync(string caseNumber, CancellationToken ct = default)
    {
        ThrowIfScripted();
        return Task.FromResult(Cases.FirstOrDefault(c => c.CaseNumber == caseNumber));
    }

    public Task<string> CreateTraceRequestAsync(TraceRequestCreate request, CancellationToken ct = default)
    {
        ThrowIfScripted();
        return Task.FromResult(NextCaseNumber);
    }

    public Task<bool> UpdateStatusAsync(string caseNumber, string newStatus, string actor, CancellationToken ct = default)
    {
        ThrowIfScripted();
        var found = Cases.FirstOrDefault(c => c.CaseNumber == caseNumber);
        if (found is null)
        {
            return Task.FromResult(false);
        }
        Cases.Remove(found);
        Cases.Add(found with { Status = newStatus });
        return Task.FromResult(true);
    }

    private void ThrowIfScripted()
    {
        var subcode = FaultSubcodeForNextCall;
        if (subcode is not null)
        {
            FaultSubcodeForNextCall = null;
            throw new TraceLinkFaultException(subcode, $"Scripted fault {subcode} for tests.");
        }
    }
}

/// <summary>
/// Stands in for okta-sim's PDP in WebApplicationFactory tests. Parses the XACML request the
/// portal sends, records it, and answers Permit unless the (role, resource, action) triple is
/// scripted to Deny, a raw response is staged (garbage-response tests), or a number of next
/// requests is scripted to throw (unreachable tests; the count survives the client's retry).
/// </summary>
public sealed class FakePdpHandler : HttpMessageHandler
{
    private const string ContextNs = "urn:oasis:names:tc:xacml:2.0:context:schema:os";

    private readonly Dictionary<(string Role, string Resource, string Action), string> _scriptedStatusMessages = new();

    public int RequestsReceived { get; private set; }

    public List<string> RequestBodies { get; } = [];

    public List<(string Role, string Resource, string Action)> ReceivedTriples { get; } = [];

    /// <summary>Raw response body returned for the next request, used to stage unparseable payloads.</summary>
    public string? RawResponseForNextCall { get; set; }

    /// <summary>How many of the next requests throw (an unreachable PDP), decremented per attempt.</summary>
    public int FailNextRequestsWith { get; set; }

    public void ScriptDeny(string role, string resource, string action, string statusMessage = "denied by the scripted policy")
        => _scriptedStatusMessages[(role, resource, action)] = statusMessage;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestsReceived++;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);
        var triple = ParseTriple(body);
        ReceivedTriples.Add(triple);

        if (FailNextRequestsWith > 0)
        {
            FailNextRequestsWith--;
            throw new HttpRequestException("Scripted PDP unreachable.");
        }
        if (RawResponseForNextCall is { } raw)
        {
            RawResponseForNextCall = null;
            return Text(raw);
        }
        return _scriptedStatusMessages.TryGetValue(triple, out var statusMessage)
            ? Text(Response("Deny", statusMessage))
            : Text(Response("Permit", null));
    }

    private static HttpResponseMessage Text(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xacml+xml")
    };

    /// <summary>Mirrors the PdpEngine response shape the portal client parses.</summary>
    private static string Response(string decision, string? statusMessage) =>
        $"<Response xmlns=\"{ContextNs}\"><Result><Decision>{decision}</Decision><Status>" +
        "<StatusCode Value=\"urn:oasis:names:tc:xacml:1.0:status:ok\"/>" +
        (statusMessage is null ? string.Empty : $"<StatusMessage>{statusMessage}</StatusMessage>") +
        "</Status></Result></Response>";

    private static (string Role, string Resource, string Action) ParseTriple(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidOperationException("The request body has no root element.");
        XNamespace ns = ContextNs;
        string Category(string name) =>
            root.Element(ns + name)?.Element(ns + "Attribute")?.Element(ns + "AttributeValue")?.Value ?? string.Empty;
        return (Category("Subject"), Category("Resource"), Category("Action"));
    }
}
