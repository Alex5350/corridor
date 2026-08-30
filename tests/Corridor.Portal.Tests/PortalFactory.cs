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

/// <summary>Boots the real portal with in-memory stores, a fake SOAP client, and test auth.</summary>
public class PortalFactory : WebApplicationFactory<Program>
{
    public FakeTraceLinkClient TraceClient { get; } = new();

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
                ["Adfs:BaseAddress"] = "http://localhost:59998"
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
