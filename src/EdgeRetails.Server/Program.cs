using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Recovery;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Production.Recovery;
using EdgeRetails.Server.Middleware;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "EdgeRetailsServer");

var commonConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EdgeRetails", "config.json");
if (File.Exists(commonConfig))
{
    builder.Configuration.AddJsonFile(commonConfig, optional: true, reloadOnChange: false);
}
var localConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeRetails", "config.json");
// Per-user development settings must never redirect the production shop authority.
if (builder.Environment.IsDevelopment() && File.Exists(localConfig))
{
    builder.Configuration.AddJsonFile(localConfig, optional: true, reloadOnChange: false);
}
var recoveryTrustConfig = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "EdgeRetails", "recovery", "trust.json");

// Integration factories that exercise the real server pipeline against isolated PostgreSQL
// provide this value explicitly. Prefer it in Testing even when workstation config files exist.
// Integration factories that exercise the real server pipeline against isolated PostgreSQL
// provide this value explicitly. Prefer it in Testing even when workstation config files exist.
var isolatedTestConnectionString = builder.Environment.IsEnvironment("Testing")
    ? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
    : null;
var connectionString = isolatedTestConnectionString
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["EDGE_RETAILS_DB"]
    ?? builder.Configuration["DatabaseConnectionString"]
    ?? (builder.Environment.IsEnvironment("Testing") ? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB") : null)
    ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_DB")
    ?? throw new InvalidOperationException(
        "No database connection string configured. " +
        "Set ConnectionStrings:DefaultConnection or EDGE_RETAILS_DB.");

builder.Services.AddEdgeRetailsInfrastructure(connectionString);
builder.Services.RemoveAll<IRecoveryAuthorizationTrustProvider>();
builder.Services.AddSingleton<IRecoveryAuthorizationTrustProvider>(
    builder.Environment.IsEnvironment("Testing")
        ? new UnprovisionedRecoveryAuthorizationTrustProvider()
        : new ProtectedFileRecoveryAuthorizationTrustProvider(recoveryTrustConfig));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Single-machine runtime boundary: Default to loopback 127.0.0.1:7150
if (!builder.Environment.IsEnvironment("Testing"))
{
    var configuredUrls = builder.Configuration["ASPNETCORE_URLS"] ?? builder.Configuration["urls"];
    if (string.IsNullOrWhiteSpace(configuredUrls))
    {
        builder.WebHost.UseUrls("http://127.0.0.1:7150");
    }
}

var app = builder.Build();

app.UseMiddleware<ProtocolCompatibilityMiddleware>();
app.UseMiddleware<MaintenanceModeGuardMiddleware>();
app.UseMiddleware<TerminalAuthenticationMiddleware>();
app.UseMiddleware<UserSessionAuthenticationMiddleware>();

app.MapControllers();

app.Run();

public partial class Program { }

