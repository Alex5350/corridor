using Corridor.OktaSim.Endpoints;
using Corridor.OktaSim.Models;
using Corridor.OktaSim.Services;
using Corridor.OktaSim.Stores;
using Corridor.OktaSim.Xacml;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Signing material: committed demo PEM (kid okta-sim-2026-08) plus a generated
// retired kid so the JWKS demonstrates rotation.
builder.Services.AddSingleton<SigningKeys>();
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<AuthCodeStore>();
builder.Services.AddSingleton<RefreshTokenStore>();
builder.Services.AddSingleton<TokenService>();

// Directory: SQL-backed idn.Users when a connection string is configured,
// otherwise the in-memory seeded store (default for local runs and unit tests).
var connectionString = builder.Configuration.GetConnectionString("Corridor");
if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
}
else
{
    builder.Services.AddSingleton<IUserStore>(sp => new SqlUserStore(connectionString, sp.GetRequiredService<ILogger<SqlUserStore>>()));
}

// XACML PDP: policies from the repo's policies/ directory with an in-code fallback.
builder.Services.AddSingleton<PdpEngine>();

var app = builder.Build();

app.MapHealthEndpoints();
app.MapAdminEndpoints();
app.MapOidcEndpoints();
app.MapSamlEndpoints();
app.MapScimEndpoints();
app.MapXacmlEndpoints();

var startupLogger = Log.ForContext<Program>();
startupLogger.Information(
    "Corridor.OktaSim starting: issuer {Issuer}, user store {Store}, policies {PolicyCount}",
    app.Services.GetRequiredService<TokenService>().Issuer,
    app.Services.GetRequiredService<IUserStore>().StoreKind,
    app.Services.GetRequiredService<PdpEngine>().PolicyCount);

app.Run();

/// <summary>Exposed for WebApplicationFactory-based tests.</summary>
public partial class Program;
