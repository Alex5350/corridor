using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Corridor.Portal.Tests;

/// <summary>
/// The portal as a policy enforcement point over a scripted fake PDP: the permit path flows
/// through in the PDP's XACML dialect, deny and every fail closed path (unreachable PDP,
/// unparseable response) become 403 problem details with errorCode cor:PdpDenied plus one
/// warning, decisions are cached per (role, resource, action) until the TTL, and the
/// assignments PATCH asks the PDP only after its own ownership check passes.
/// </summary>
public sealed class PdpEnforcementTests
{
    [Fact]
    public async Task CasesApi_PermitPathFlowsThrough_AndSpeaksThePdpDialect()
    {
        using var factory = new PdpPortalFactory();
        using var client = factory.CreateClient();

        var list = await client.GetAsync("/api/cases");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var cases = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, cases.GetArrayLength());

        // The create verb carries its own action, permitted by policy 15.
        var created = await client.PostAsJsonAsync("/api/cases", new
        {
            licenseeName = "Pdp Test Licensee",
            itemDescription = "Kalvin KB-7 .22 bolt rifle",
            serial = "KB7-7700077"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Reads share one cached triple; create is its own triple: two distinct requests.
        Assert.Equal(2, factory.Pdp.RequestsReceived);
        var createBody = factory.Pdp.RequestBodies[1];
        Assert.Contains(">create</AttributeValue>", createBody);
        Assert.Contains(">trace-cases</AttributeValue>", createBody);
        var body = factory.Pdp.RequestBodies[0];
        Assert.Contains("xmlns=\"urn:oasis:names:tc:xacml:2.0:context:schema:os\"", body);
        Assert.Contains("AttributeId=\"urn:oasis:names:tc:xacml:2.0:subject:role\"", body);
        Assert.Contains("AttributeId=\"urn:oasis:names:tc:xacml:1.0:resource:resource-id\"", body);
        Assert.Contains("AttributeId=\"urn:oasis:names:tc:xacml:1.0:action:action-id\"", body);
        Assert.Contains(">trace-cases</AttributeValue>", body);
        Assert.Contains(">read</AttributeValue>", body);
        // The role claim travels as attribute VALUE text, never concatenated into markup.
        Assert.Contains(">Officer</AttributeValue>", body);
    }

    [Fact]
    public async Task CasesApi_DenyPath_Returns403ProblemWithCorPdpDenied()
    {
        using var factory = new PdpPortalFactory();
        factory.Pdp.ScriptDeny("Clerk", "trace-cases", "read", "clerks may not read trace cases");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Clerk");

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("cor:PdpDenied", body.GetProperty("errorCode").GetString());
        Assert.Contains("clerks may not read trace cases", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CasesApi_UnreachablePdp_FailsClosedWith403AndOneWarning()
    {
        using var factory = new PdpPortalFactory();
        // Enough failures to survive the named client's single retry.
        factory.Pdp.FailNextRequestsWith = 5;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cor:PdpDenied", body.GetProperty("errorCode").GetString());
        Assert.True(factory.Pdp.RequestsReceived >= 2, "the client should have retried the transient failure once");
        Assert.Contains(factory.Warnings, warning => warning.Contains("failing closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CasesApi_UnparseablePdpResponse_FailsClosedWith403()
    {
        using var factory = new PdpPortalFactory();
        factory.Pdp.RawResponseForNextCall = "<html><body>scheduled maintenance</body></html>";
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cor:PdpDenied", body.GetProperty("errorCode").GetString());
        Assert.Contains(factory.Warnings, warning => warning.Contains("failing closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PdpDecisions_AreCachedPerRoleResourceAction_UntilTheTtl()
    {
        using var factory = new PdpPortalFactory();
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/api/cases");
        var second = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // The second call within the TTL is served from the cache: still one PDP request.
        Assert.Equal(1, factory.Pdp.RequestsReceived);

        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        var third = await client.GetAsync("/api/cases");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, factory.Pdp.RequestsReceived);
    }

    [Fact]
    public async Task AssignmentsPatch_InspectorOwnerWithPdpPermit_FlowsThrough()
    {
        using var factory = new PdpPortalFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "inspector@corridor.example");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Inspector");

        var response = await client.PatchAsJsonAsync("/api/assignments/1", new { itemIndex = 0, done = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inspector@corridor.example", body.GetProperty("inspectorUpn").GetString());
        Assert.Equal(("Inspector", "assignments", "write"), Assert.Single(factory.Pdp.ReceivedTriples));
    }

    [Fact]
    public async Task AssignmentsPatch_PdpDenyAfterOwnershipPasses_Returns403()
    {
        using var factory = new PdpPortalFactory();
        // Admin passes the endpoint's ownership check (the reassignment override), then the
        // PDP denies assignments:write, so the response must carry the PDP error code.
        factory.Pdp.ScriptDeny("Admin", "assignments", "write");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "admin@corridor.example");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        var response = await client.PatchAsJsonAsync("/api/assignments/3", new { itemIndex = 2, done = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cor:PdpDenied", body.GetProperty("errorCode").GetString());
        Assert.False(body.TryGetProperty("faultSubcode", out _), "the deny must come from the PDP gate, not a SOAP fault");
    }
}

/// <summary>Portal factory for PEP tests: fake PDP plus a manual clock and a warning recorder.</summary>
internal sealed class PdpPortalFactory : PortalFactory
{
    public ManualTimeProvider Clock { get; } = new();

    public IReadOnlyList<string> Warnings => WarningSink.Messages;

    private WarningLog WarningSink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(WarningSink);
            services.AddSingleton(typeof(ILogger<>), typeof(WarningLogger<>));
        });
    }
}

/// <summary>Test clock the decision cache consults, so the TTL can elapse instantly.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan forward) => _utcNow = _utcNow.Add(forward);

    public override DateTimeOffset GetUtcNow() => _utcNow;
}

/// <summary>Collects every Warning or worse logged through ILogger&lt;T&gt; during a test.</summary>
internal sealed class WarningLog
{
    private readonly object _gate = new();

    public List<string> Messages { get; } = [];

    public void Add(string message)
    {
        lock (_gate)
        {
            Messages.Add(message);
        }
    }
}

internal sealed class WarningLogger<T>(WarningLog sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            sink.Add($"{typeof(T).Name}: {formatter(state, exception)}");
        }
    }
}
