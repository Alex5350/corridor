using Corridor.AdfsSim;
using Corridor.AdfsSim.Identity;
using Corridor.AdfsSim.Saml;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Console logging via Serilog, levels from the Serilog configuration section.
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddRazorPages();
builder.Services.Configure<AdfsSimOptions>(builder.Configuration.GetSection(AdfsSimOptions.SectionName));
builder.Services.AddSingleton<SigningCertificate>();
builder.Services.AddSingleton<SamlResponseBuilder>();
builder.Services.AddSingleton<RelyingPartyRegistry>();

// User validation: SQL Server (idn.Users) when a connection string is configured,
// otherwise the in-memory demo seed so the app runs with no database.
var connectionString = builder.Configuration.GetConnectionString("Corridor");
if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
    Log.Information("No Corridor connection string configured: using the in-memory demo user store.");
}
else
{
    builder.Services.AddSingleton<IUserStore>(new SqlUserStore(connectionString));
    Log.Information("Corridor connection string configured: validating users against idn.Users.");
}

var app = builder.Build();

app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// Anonymous health probe (contract: JSON {status:"ok"}).
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

// ADFS-style federation metadata, served as application/xml.
app.MapGet("/federationmetadata/2007-06/federationmetadata.xml",
    (SigningCertificate signing, IOptions<AdfsSimOptions> options) =>
    {
        var opts = options.Value;
        var ssoEndpoint = opts.BaseUrl.TrimEnd('/') + opts.SsoPath;
        var xml = FederationMetadata.Build(opts.EntityId, ssoEndpoint, signing.Certificate);
        return Results.Bytes(FederationMetadata.ToUtf8Bytes(xml), "application/xml");
    });

app.Run();

// Exposed for WebApplicationFactory in the test project.
public partial class Program { }
