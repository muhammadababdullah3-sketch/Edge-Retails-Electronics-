// Cloud server (license/admin authority). Shop PC API = EdgeRetails.Server — see docs/SHOP_AND_CENTRAL_RUNTIME.md.
using EdgeRetails.Central.Api.Configuration;
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

builder.BootstrapCentralLocalEnvironment();

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
builder.Services.AddSingleton(_ => AdminAuthenticationPolicy.FromEnvironment());
builder.Services.AddScoped<CentralRequestAuthorizationFilter>();
builder.Services.AddScoped<InstallationRequestProofService>();
builder.Services.AddScoped<IStepUpService, StepUpService>();
builder.Services.AddSingleton(_ => SensitiveApprovalPolicy.FromEnvironment());
builder.Services.AddScoped<IOperationLedgerService, OperationLedgerService>();
builder.Services.AddScoped<ICentralAuditService, CentralAuditService>();
builder.Services.AddCentralOutboxDispatch();
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
app.UseMiddleware<W1DenialAuditMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseMiddleware<CsrfProtectionMiddleware>();
app.UseMiddleware<AnonymousPortalSessionGateMiddleware>();

// Denial auditing is scoped to /api and /internal, so portal assets never resolve Central services.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

app.MapControllers();
app.MapGet("/favicon.ico", (Microsoft.AspNetCore.Hosting.IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "favicon.svg"), "image/svg+xml"));

app.Run();

public partial class Program { }
