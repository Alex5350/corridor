using System.Security.Cryptography.X509Certificates;
using CoreWCF;
using CoreWCF.Channels;
using CoreWCF.Configuration;
using CoreWCF.Description;
using Corridor.Legacy.Contracts;
using Corridor.Legacy.DataAccess;
using Corridor.Legacy.Security;
using Corridor.Legacy.Services;
using Serilog;

// TraceLink: the legacy SOAP 1.1 case service. Hosted with CoreWCF inside
// ASP.NET Core so the same process also serves /healthz and ?wsdl. Data access
// is raw ADO.NET against the trace procs; identity arrives in a simplified
// WS-Security-style cor:Security header validated per TrustMode (SAML from
// adfs-sim, JWT from okta-sim, both during the dual-trust cutover window).

var builder = WebApplication.CreateBuilder(args);

// Serilog console logging (Serilog section in appsettings.json).
// Secrets policy: appsettings.json carries no credentials. The dev SQL login
// (sa / CorridorDev1!) lives only in appsettings.Development.json or in user
// secrets for production-like runs:
//   dotnet user-secrets set "ConnectionStrings:Corridor" "Server=localhost,1433;Database=Corridor;User Id=sa;Password=CorridorDev1!;Encrypt=True;TrustServerCertificate=True"
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console());

builder.Services.AddHttpClient(CorridorOktaOptions.JwksHttpClientName);

builder.Services.AddOptions<CorridorAdfsOptions>()
    .Bind(builder.Configuration.GetSection("Corridor:Adfs"));
builder.Services.AddOptions<CorridorOktaOptions>()
    .Bind(builder.Configuration.GetSection("Corridor:Okta"));

// Raw ADO.NET data access. The connection factory is the seam unit tests
// replace; no test ever needs SQL Server.
builder.Services.AddSingleton<IDbConnectionFactory>(provider =>
{
    string? connectionString = builder.Configuration.GetConnectionString("Corridor");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Corridor is not configured.");
    }

    return new SqlConnectionFactory(connectionString);
});
builder.Services.AddSingleton<IMigrationState, SqlMigrationState>();
builder.Services.AddSingleton<TraceCaseRepository>();
builder.Services.AddScoped<TraceLinkService>();

// Identity validation: one TokenValidator facade, two strategies.
builder.Services.AddSingleton<IJwksProvider>(provider =>
{
    CorridorOktaOptions okta = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorridorOktaOptions>>().Value;
    HttpClient httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CorridorOktaOptions.JwksHttpClientName);
    return new CachedJwksProvider(httpClient, okta.JwksUrl, TimeSpan.FromSeconds(okta.CacheSeconds));
});
builder.Services.AddSingleton<ITokenValidationStrategy>(provider =>
{
    CorridorAdfsOptions adfs = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorridorAdfsOptions>>().Value;
    X509Certificate2? certificate = AdfsCertLoader.Load(adfs.SigningCertPath);
    if (certificate is null)
    {
        Log.Logger.Error("ADFS signing certificate not found (tried {Path} and fallback locations); SAML tokens will be rejected", adfs.SigningCertPath);
    }
    else
    {
        Log.Logger.Information("Loaded ADFS signing certificate {Subject} (expires {NotAfter})", certificate.Subject, certificate.NotAfter);
    }

    return new SamlTokenValidator(certificate, adfs.AudienceUri);
});
builder.Services.AddSingleton<ITokenValidationStrategy>(provider =>
{
    CorridorOktaOptions okta = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorridorOktaOptions>>().Value;
    return new JwtTokenValidator(provider.GetRequiredService<IJwksProvider>(), okta.Issuer, okta.Audience);
});
builder.Services.AddSingleton<ITokenValidator>(provider => new TokenValidator(
    provider.GetRequiredService<IMigrationState>(),
    provider.GetServices<ITokenValidationStrategy>(),
    appKey: "legacy"));

builder.Services.AddServiceModelServices();
builder.Services.AddServiceModelMetadata();

var app = builder.Build();

// Anonymous health endpoint, repo convention: JSON {"status":"ok"}.
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

app.UseServiceModel(serviceBuilder =>
{
    serviceBuilder.AddService<TraceLinkService>();
    // BasicHttpBinding is SOAP 1.1 with text encoding, the classic ASMX wire format.
    serviceBuilder.AddServiceEndpoint<TraceLinkService, ITraceLinkService>(new BasicHttpBinding(), "TraceLink.svc");

    serviceBuilder.ConfigureServiceHostBase<TraceLinkService>(host =>
    {
        ServiceEndpoint endpoint = host.Description.Endpoints.Single(e => e.Contract.ContractType == typeof(ITraceLinkService));
        endpoint.EndpointBehaviors.Add(new CorridorSecurityEndpointBehavior(app.Services.GetRequiredService<ITokenValidator>()));

        // WSDL at http://localhost:8000/TraceLink.svc?wsdl for SoapUI and the portal client.
        ServiceMetadataBehavior? metadata = host.Description.Behaviors.Find<ServiceMetadataBehavior>();
        if (metadata is null)
        {
            metadata = new ServiceMetadataBehavior();
            host.Description.Behaviors.Add(metadata);
        }

        metadata.HttpGetEnabled = true;
    });
});

app.Run();
