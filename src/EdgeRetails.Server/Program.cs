using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Infrastructure;
using EdgeRetails.Server.Middleware;

var builder = WebApplication.CreateBuilder(args);

var commonConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EdgeRetails", "config.json");
if (File.Exists(commonConfig))
{
    builder.Configuration.AddJsonFile(commonConfig, optional: true, reloadOnChange: false);
}
var localConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeRetails", "config.json");
if (File.Exists(localConfig))
{
    builder.Configuration.AddJsonFile(localConfig, optional: true, reloadOnChange: false);
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["EDGE_RETAILS_DB"]
    ?? builder.Configuration["DatabaseConnectionString"]
    ?? (builder.Environment.IsEnvironment("Testing") ? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB") : null)
    ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_DB")
    ?? throw new InvalidOperationException(
        "No database connection string configured. " +
        "Set ConnectionStrings:DefaultConnection or EDGE_RETAILS_DB.");

builder.Services.AddEdgeRetailsInfrastructure(connectionString);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

var app = builder.Build();

app.UseMiddleware<ProtocolCompatibilityMiddleware>();
app.UseMiddleware<MaintenanceModeGuardMiddleware>();
app.UseMiddleware<TerminalAuthenticationMiddleware>();

app.MapControllers();

app.Run();

public partial class Program { }

