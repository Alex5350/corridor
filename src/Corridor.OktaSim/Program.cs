using System.Threading.RateLimiting;
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

// Rate limiting on the credential-facing endpoints: a fixed window per IP for the
// authorize and token endpoints. Real providers throttle far harder and smarter; the
// point here is that the pattern is in place and observable (429 with a Retry-After)
// rather than left as an exercise.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("credential", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("OktaSim:CredentialPermitLimit", 60),
                Window = TimeSpan.FromMinutes(1),
            }));
});

// CORS for the browser-side OIDC flow: the SPA's oidc-client-ts fetches
// discovery, JWKS, token, and userinfo with XHR from its own origin, so the
// OIDC endpoint group must answer cross-origin. The "spa" policy is applied
// only to those endpoints (RequireCors in OidcEndpoints); SCIM, the PDP,
// SAML, admin, and health stay CORS-free. Origins are config-driven as a
// comma-separated list (OktaSim:SpaOrigins) and no credentials are allowed:
// the SPA is a public client and nothing here relies on cookies.
var spaOrigins = (builder.Configuration["OktaSim:SpaOrigins"] ?? OidcEndpoints.DefaultSpaOrigin)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (spaOrigins.Length == 0)
{
    spaOrigins = [OidcEndpoints.DefaultSpaOrigin];
}
builder.Services.AddCors(options => options.AddPolicy(OidcEndpoints.SpaCorsPolicy, policy => policy
    .WithOrigins(spaOrigins)
    .WithMethods("GET", "POST", "OPTIONS")
    .WithHeaders("Authorization", "Content-Type")
    .DisallowCredentials()));

var app = builder.Build();

// Required for endpoint-routing CORS (RequireCors) to terminate preflights.
app.UseRateLimiter();
app.UseCors();

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
