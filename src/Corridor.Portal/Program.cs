using Corridor.Portal.Api;
using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Pdp;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Data;
using Corridor.Portal.Data.Memory;
using Corridor.Portal.Data.Sql;
using Corridor.Portal.Services;
using Corridor.Portal.Services.Scim;
using Corridor.Portal.Services.TraceLink;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Polly;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.Configure<OktaOptions>(builder.Configuration.GetSection("Okta"));
builder.Services.Configure<AdfsOptions>(builder.Configuration.GetSection("Adfs"));
builder.Services.Configure<PortalSiteOptions>(builder.Configuration.GetSection("Portal"));
builder.Services.Configure<LegacyOptions>(builder.Configuration.GetSection("Legacy"));

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<SamlValidator>();
builder.Services.AddSingleton<SamlAssertionFactory>();
builder.Services.AddSingleton<AdfsCertificateStore>();
builder.Services.AddSingleton<OktaSamlMetadataClient>();
builder.Services.AddSingleton<ITrustedCertificateProvider, TrustedCertificateProvider>();
builder.Services.AddScoped<TrustModeService>();
builder.Services.AddScoped<LegacyCredentialFactory>();
builder.Services.AddSingleton<ChecklistService>();
// The SOAP hop runs on two named clients, split by idempotency rather than a per-call flag
// through one shared pipeline: a flag puts the "is this safe to replay" decision at every
// call site, where a forgotten flag silently retries a mutating call. Read operations
// (SearchCases, GetCase) are safe to replay, so the read client retries once on a transient
// failure. Write operations (CreateTraceRequest, UpdateStatus) mutate the legacy system and
// replaying an ambiguous failure could double-create a case or re-apply a status transition,
// so the write client has no retry. Both cap a call at 8 seconds.
builder.Services.AddHttpClient(TraceLinkHttpClients.Read, client => client.Timeout = TimeSpan.FromSeconds(8))
    .AddResilienceHandler("tracelink-read", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 1,
        BackoffType = DelayBackoffType.Constant,
        Delay = TimeSpan.FromMilliseconds(500),
        UseJitter = false
    }));
builder.Services.AddHttpClient(TraceLinkHttpClients.Write, client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddTransient<SoapTraceLinkClient>();
builder.Services.AddTransient<ITraceLinkClient>(sp => sp.GetRequiredService<SoapTraceLinkClient>());
// The service credential fetch happens on the hot path of every SOAP call, so it gets one
// retry on transient failures and a 3 second cap; the token itself stays cached 10 minutes.
builder.Services.AddHttpClient<OktaServiceTokenClient>(client => client.Timeout = TimeSpan.FromSeconds(3))
    .AddResilienceHandler("okta-service-token", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 1,
        BackoffType = DelayBackoffType.Constant,
        Delay = TimeSpan.FromMilliseconds(500),
        UseJitter = false
    }));

// The portal is a policy enforcement point: every guarded API call asks okta-sim's XACML PDP
// for (role, resource, action). The pdp named client caps a decision at 3 seconds and retries
// once on a transient failure; real decisions are cached 15 minutes per triple, and every
// other outcome (unreachable, HTTP error, unparseable Decision) fails closed to a Deny with
// one warning logged (ADR 0007).
builder.Services.AddSingleton(TimeProvider.System);
var pdpBaseUrl = builder.Configuration["Portal:PdpBaseUrl"] is { Length: > 0 } configured
    ? configured
    : new PortalSiteOptions().PdpBaseUrl;
builder.Services.AddHttpClient(PdpHttpClient.ClientName, client =>
    {
        client.BaseAddress = new Uri(pdpBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(3);
    })
    .AddResilienceHandler("pdp", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 1,
        BackoffType = DelayBackoffType.Constant,
        Delay = TimeSpan.FromMilliseconds(500),
        UseJitter = false
    }));
builder.Services.AddSingleton<IPdpClient>(sp => new PdpHttpClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(PdpHttpClient.ClientName),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<PdpHttpClient>>()));

// The provisioning side of the cutover story (ADR 0006): the migration dashboard
// synchronizes idn.Users into the target directory over SCIM. The scim named client
// carries the bearer token and a 5 second cap so a stuck directory cannot pin the
// dashboard request; failures surface inline on the page, never as a fake success.
var scimBaseUrl = builder.Configuration["Portal:ScimBaseUrl"] is { Length: > 0 } configuredScimBase
    ? configuredScimBase
    : new PortalSiteOptions().ScimBaseUrl;
var scimToken = builder.Configuration["Portal:ScimToken"] is { Length: > 0 } configuredScimToken
    ? configuredScimToken
    : new PortalSiteOptions().ScimToken;
builder.Services.AddHttpClient(ScimClient.ClientName, client =>
{
    client.BaseAddress = new Uri(scimBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<IScimProvisioner>(sp => new ScimClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ScimClient.ClientName),
    scimToken));

var useInMemory = builder.Configuration.GetValue<bool>("Data:UseInMemory");
var connectionString = builder.Configuration.GetConnectionString("Corridor");
if (useInMemory || string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<IPermitRepository>(new InMemoryPermitRepository());
    builder.Services.AddSingleton<IMigrationAppRepository>(new InMemoryMigrationAppRepository());
    builder.Services.AddSingleton<IAuditEventRepository, InMemoryAuditEventRepository>();
    builder.Services.AddSingleton<IAssignmentRepository>(new InMemoryAssignmentRepository());
    builder.Services.AddSingleton<IDirectoryUserRepository>(new InMemoryDirectoryUserRepository());
}
else
{
    builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
    builder.Services.AddScoped<IPermitRepository, SqlPermitRepository>();
    builder.Services.AddScoped<IMigrationAppRepository, SqlMigrationAppRepository>();
    builder.Services.AddScoped<IAuditEventRepository, SqlAuditEventRepository>();
    builder.Services.AddScoped<IAssignmentRepository, SqlAssignmentRepository>();
    builder.Services.AddScoped<IDirectoryUserRepository, SqlDirectoryUserRepository>();
}
builder.Services.AddScoped<DirectoryProvisioner>();

var oktaOptions = builder.Configuration.GetSection("Okta").Get<OktaOptions>() ?? new OktaOptions();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
})
// Selector that lets the JSON APIs accept either the portal cookie or an okta-sim bearer token.
.AddPolicyScheme("ApiOrSpa", "Portal cookie or Okta bearer", options =>
{
    options.ForwardDefaultSelector = context =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? JwtBearerDefaults.AuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, "okta-sim", options =>
{
    options.Authority = oktaOptions.Authority;
    options.ClientId = oktaOptions.ClientId;
    options.ClientSecret = oktaOptions.ClientSecret ?? string.Empty;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.CallbackPath = new PathString("/signin-oidc");
    options.SaveTokens = true;
    options.RequireHttpsMetadata = false; // local simulation IdP on plain http
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "upn";
    options.TokenValidationParameters.RoleClaimType = "role";
    options.ClaimActions.MapJsonKey("upn", "upn");
    options.ClaimActions.MapJsonKey("role", "role");
    options.Events.OnTokenValidated = context =>
    {
        // Fold the okta claims into the portal principal shape: upn -> name, role -> role.
        if (context.Principal is not null)
        {
            context.Principal = PortalClaims.Transform(context.Principal, "okta", OpenIdConnectDefaults.AuthenticationScheme);
        }
        return Task.CompletedTask;
    };
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Authority = oktaOptions.Authority;
    options.RequireHttpsMetadata = false; // local simulation IdP on plain http
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "upn";
    options.TokenValidationParameters.RoleClaimType = "role";
    // okta-sim issues demo tokens whose audience is the OAuth client id; there is no per-API audience to pin.
    options.TokenValidationParameters.ValidateAudience = false;
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AnyRole", policy =>
    {
        policy.AddAuthenticationSchemes("ApiOrSpa");
        policy.RequireRole("Admin", "Officer", "Clerk", "Inspector");
    });
    options.AddPolicy("SpaBearer", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Spa", policy => policy
        .WithOrigins(builder.Configuration["Portal:SpaOrigin"] ?? "http://localhost:5173")
        .WithHeaders("Authorization", "Content-Type")
        .WithMethods("GET", "PATCH"));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseSerilogRequestLogging();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapSamlAcs();
app.MapCasesApi();
app.MapAssignmentsApi();
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

app.Run();

/// <summary>Exposed for WebApplicationFactory based tests.</summary>
public partial class Program;
