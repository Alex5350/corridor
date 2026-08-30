using Corridor.Portal.Api;
using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Data;
using Corridor.Portal.Data.Memory;
using Corridor.Portal.Data.Sql;
using Corridor.Portal.Services;
using Corridor.Portal.Services.TraceLink;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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
builder.Services.AddHttpClient<SoapTraceLinkClient>();
builder.Services.AddHttpClient<OktaServiceTokenClient>();
builder.Services.AddTransient<ITraceLinkClient>(sp => sp.GetRequiredService<SoapTraceLinkClient>());

var useInMemory = builder.Configuration.GetValue<bool>("Data:UseInMemory");
var connectionString = builder.Configuration.GetConnectionString("Corridor");
if (useInMemory || string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<IPermitRepository>(new InMemoryPermitRepository());
    builder.Services.AddSingleton<IMigrationAppRepository>(new InMemoryMigrationAppRepository());
    builder.Services.AddSingleton<IAuditEventRepository, InMemoryAuditEventRepository>();
    builder.Services.AddSingleton<IAssignmentRepository>(new InMemoryAssignmentRepository());
}
else
{
    builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
    builder.Services.AddScoped<IPermitRepository, SqlPermitRepository>();
    builder.Services.AddScoped<IMigrationAppRepository, SqlMigrationAppRepository>();
    builder.Services.AddScoped<IAuditEventRepository, SqlAuditEventRepository>();
    builder.Services.AddScoped<IAssignmentRepository, SqlAssignmentRepository>();
}

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
