using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Api.Services;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.Infrastructure.Storage;
using EdgeRetails.ProtectedSigner.Keys;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

// Add Controllers
builder.Services.AddControllers(options =>
    {
        options.Filters.AddService<CentralRequestAuthorizationFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    });

// Core services
builder.Services.AddSingleton<IDatabaseTargetGuard, DatabaseTargetGuard>();
builder.Services.AddSingleton<ICanonicalJsonSerializer, CanonicalJsonSerializer>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<IPrivateKeyStore>(_ =>
{
    var root = Environment.GetEnvironmentVariable("CENTRAL_SIGNER_KEY_STORE_ROOT");
    if (string.IsNullOrWhiteSpace(root))
    {
        throw new InvalidOperationException("An explicit protected Central signer key-store root is required; in-memory signer fallback is prohibited.");
    }
    return new PersistentDevelopmentPrivateKeyStore(root);
});
builder.Services.AddSingleton<IObjectStorageProvider, DiskObjectStorageProvider>();

// Scoped services
builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();
builder.Services.AddScoped<CentralRequestAuthorizationFilter>();
builder.Services.AddScoped<InstallationRequestProofService>();
builder.Services.AddScoped<IStepUpService, StepUpService>();
builder.Services.AddScoped<IOperationLedgerService, OperationLedgerService>();
builder.Services.AddScoped<ICentralAuditService, CentralAuditService>();
builder.Services.AddSingleton(_ => CentralOutboxOptions.FromEnvironment());
builder.Services.AddSingleton<CentralOutboxWorkerState>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<CentralOutboxOptions>();
    if (!options.Enabled)
    {
        throw new InvalidOperationException("Explicit Central outbox configuration is required.");
    }
    return options.Policy;
});
builder.Services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
builder.Services.AddScoped<IOutboxConsumerSink, DurableOutboxConsumerSink>();
builder.Services.AddHostedService<CentralOutboxHostedService>();
builder.Services.AddSingleton(_ => ActivationDeliveryCleanupOptions.FromEnvironment());
builder.Services.AddHostedService<ActivationDeliveryCleanupHostedService>();
builder.Services.AddSingleton(_ => CentralStorageOptions.FromEnvironment());
builder.Services.AddScoped<StorageReconciliationService>();
builder.Services.AddHostedService<CentralStorageHostedService>();
builder.Services.AddScoped<IProtectedSigningEngine, ProtectedSigningEngine>();
builder.Services.AddScoped<IProtectedSignerClient, ProtectedSignerClient>();
builder.Services.AddScoped<ICommercialService, CommercialService>();
builder.Services.AddScoped<IActivationService, ActivationService>();
builder.Services.AddSingleton<IActivationDeliverySecretProtector, ConfiguredActivationDeliverySecretProtector>();
builder.Services.AddScoped<ISensitiveLifecycleService, SensitiveLifecycleService>();
builder.Services.AddScoped<Wave3StorageAndSupportService>();
builder.Services.AddScoped<Wave4DashboardAndPortalService>();

// Resolve and guard the database target only when a request actually resolves CentralDbContext.
// Static portal requests do not need database access; API/database paths still fail closed without an owned target.
builder.Services.AddDbContext<CentralDbContext>((sp, options) =>
{
    var connectionString = builder.Configuration.GetConnectionString("CentralDatabase")
        ?? Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
        ?? throw new InvalidOperationException("An explicit owned CentralDatabase connection is required; database fallback is prohibited.");
    var guard = sp.GetRequiredService<IDatabaseTargetGuard>();
    guard.ValidateConnectionString(connectionString);

    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema);
    });
});

builder.Services.AddScoped<ICentralDbContext>(sp => sp.GetRequiredService<CentralDbContext>());

var app = builder.Build();

// Middleware pipeline
app.UseMiddleware<CentralExceptionHandlingMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseMiddleware<CsrfProtectionMiddleware>();

// Static portal assets do not participate in the admin denial audit or resolve the Central database.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<W1DenialAuditMiddleware>();

app.MapControllers();

app.Run();

public partial class Program { }
