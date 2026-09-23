using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Infrastructure;
using EdgeRetails.Server.Middleware;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
    ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_DB")
    ?? (builder.Environment.IsEnvironment("Testing")
        ? "Host=localhost;Database=edge_retails_test;Username=postgres;Password=postgres"
        : throw new InvalidOperationException(
            "No database connection string configured. " +
            "Set ConnectionStrings:DefaultConnection, EDGE_RETAILS_TEST_DB, or EDGE_RETAILS_DB."));

builder.Services.AddEdgeRetailsInfrastructure(connectionString);

builder.Services.AddScoped<RuntimeLicenseService>(sp =>
{
    var store = sp.GetService<ILicenseStore>() ?? new FallbackLicenseStore();
    var validator = sp.GetService<ILicenseValidator>() ?? new FallbackLicenseValidator();
    return new RuntimeLicenseService(store, validator);
});

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

internal sealed class FallbackLicenseStore : ILicenseStore
{
    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FallbackLicenseValidator : ILicenseValidator
{
    public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        => Task.FromResult(new LicenseValidationResult(LicenseValidationStatus.Missing, null, "No license installed.", "license.missing"));
}
