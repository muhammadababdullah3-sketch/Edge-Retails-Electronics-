using EdgeRetails.Application.Production.Diagnostics;

namespace EdgeRetails.UnitTests;

public sealed class Phase5DiagnosticsContractTests
{
    [Fact]
    public void Overall_UsesUnavailableBeforeActionRequiredAndDegraded()
    {
        var checks = new[]
        {
            new DiagnosticValue("db.latency.healthy", HealthClassification.HEALTHY, "", ""),
            new DiagnosticValue("backup.age.action_required", HealthClassification.ACTION_REQUIRED, "", ""),
            new DiagnosticValue("worker.heartbeat.action_required", HealthClassification.ACTION_REQUIRED, "", ""),
            new DiagnosticValue("reconciliation.action_required", HealthClassification.UNAVAILABLE, "", "")
        };

        Assert.Equal(
            HealthClassification.UNAVAILABLE,
            DiagnosticsClassifier.Overall(checks));
    }

    [Fact]
    public void Overall_ReturnsActionRequiredWhenNoUnavailableExists()
    {
        var checks = new[]
        {
            new DiagnosticValue("db.latency.degraded", HealthClassification.DEGRADED, "", ""),
            new DiagnosticValue("failed_jobs.action_required", HealthClassification.ACTION_REQUIRED, "", "")
        };

        Assert.Equal(
            HealthClassification.ACTION_REQUIRED,
            DiagnosticsClassifier.Overall(checks));
    }

    [Fact]
    public void Overall_ReturnsDegradedWhenAllAvailableChecksAreAtLeastDegraded()
    {
        var checks = new[]
        {
            new DiagnosticValue("db.latency.degraded", HealthClassification.DEGRADED, "", ""),
            new DiagnosticValue("disk.healthy", HealthClassification.HEALTHY, "", "")
        };

        Assert.Equal(
            HealthClassification.DEGRADED,
            DiagnosticsClassifier.Overall(checks));
    }

    [Fact]
    public void Overall_ReturnsHealthyOnlyWhenEveryCheckIsHealthy()
    {
        var checks = new[]
        {
            new DiagnosticValue("db.latency.healthy", HealthClassification.HEALTHY, "", ""),
            new DiagnosticValue("disk.healthy", HealthClassification.HEALTHY, "", "")
        };

        Assert.Equal(
            HealthClassification.HEALTHY,
            DiagnosticsClassifier.Overall(checks));
    }

    [Fact]
    public void Overall_ReturnsUnavailableForEmptySnapshot()
    {
        Assert.Equal(
            HealthClassification.UNAVAILABLE,
            DiagnosticsClassifier.Overall(Array.Empty<DiagnosticValue>()));
    }

    [Fact]
    public void PolicyReadsConfiguredThresholdsFromEnvironment()
    {
        var names = new[]
        {
            "EDGE_RETAILS_DIAG_BACKUP_MAX_AGE_HOURS",
            "EDGE_RETAILS_DIAG_WORKER_MAX_AGE_MINUTES",
            "EDGE_RETAILS_DIAG_DISK_FREE_WARNING_BYTES",
            "EDGE_RETAILS_DIAG_PRINT_BACKLOG_WARNING_COUNT",
            "EDGE_RETAILS_DIAG_OUTCOME_UNKNOWN_WARNING_COUNT",
            "EDGE_RETAILS_DIAG_ACTION_REQUIRED_WARNING_COUNT",
            "EDGE_RETAILS_DIAG_FAILED_JOBS_WARNING_COUNT",
            "EDGE_RETAILS_DIAG_RECONCILIATION_FAILURE_WARNING_COUNT",
            "EDGE_RETAILS_DIAG_DB_LATENCY_WARNING_MS"
        };
        var previous = names.ToDictionary(x => x, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(names[0], "48");
            Environment.SetEnvironmentVariable(names[1], "5");
            Environment.SetEnvironmentVariable(names[2], "123456");
            Environment.SetEnvironmentVariable(names[3], "7");
            Environment.SetEnvironmentVariable(names[4], "8");
            Environment.SetEnvironmentVariable(names[5], "9");
            Environment.SetEnvironmentVariable(names[6], "10");
            Environment.SetEnvironmentVariable(names[7], "11");
            Environment.SetEnvironmentVariable(names[8], "12.5");

            var policy = DiagnosticsPolicy.FromEnvironment();

            Assert.Equal(TimeSpan.FromHours(48), policy.BackupMaxAge);
            Assert.Equal(TimeSpan.FromMinutes(5), policy.WorkerHeartbeatMaxAge);
            Assert.Equal(123456L, policy.DiskFreeWarningBytes);
            Assert.Equal(7, policy.PrintBacklogWarningCount);
            Assert.Equal(8, policy.OutcomeUnknownWarningCount);
            Assert.Equal(9, policy.ActionRequiredWarningCount);
            Assert.Equal(10, policy.FailedJobsWarningCount);
            Assert.Equal(11, policy.ReconciliationFailureWarningCount);
            Assert.Equal(12.5, policy.DbLatencyWarningMilliseconds);
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
