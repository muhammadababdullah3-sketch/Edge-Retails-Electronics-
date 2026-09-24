using EdgeRetails.Infrastructure;
using EdgeRetails.Worker;
using EdgeRetails.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

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

var productionStateRoot = Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeRetails", "Production");

var workerDir = Path.Combine(productionStateRoot, "worker");
Directory.CreateDirectory(workerDir);

builder.Services.AddSingleton<IWorkerJobLock>(_ => new FileWorkerJobLock(Path.Combine(workerDir, "locks")));
builder.Services.AddSingleton(sp => new WorkerHeartbeatService(
    Path.Combine(workerDir, "worker.heartbeat"),
    sp.GetRequiredService<ILogger<WorkerHeartbeatService>>()));

builder.Services.AddSingleton<IWorkerJob, OutboxDispatcherJob>();
builder.Services.AddSingleton<IWorkerJob, ScheduledBackupJob>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
