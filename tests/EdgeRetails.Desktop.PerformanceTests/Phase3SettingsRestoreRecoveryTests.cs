using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3SettingsRestoreRecoveryTests
{
    [Fact]
    public async Task SettingsBackupHistory_ShowsSanitizedIntegrityIssuesForInvalidArtifacts()
    {
        using var api = CreateApiClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/backups/diagnostics", request.RequestUri!.AbsolutePath);
            return JsonResponse(new BackupHistoryDiagnosticsResponse(
                0,
                Array.Empty<SafeBackupRecord>(),
                2,
                [new BackupHistoryIssueCount("backup.artifact_checksum_mismatch", 2)]));
        });
        using var viewModel = new SettingsViewModel(
            new TestThemeService(),
            new TestDialogService(),
            new TestToastService(),
            backupRestoreService: new RemoteBackupRestoreService(
                api,
                new FileClientOperationIntentStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "intents.json"))));

        await WaitUntilAsync(() => viewModel.BackupVerificationStatus.Contains("checksum_mismatch", StringComparison.Ordinal));

        Assert.Empty(viewModel.Backups);
        Assert.Contains("2 artifact(s) failed integrity checks", viewModel.BackupVerificationStatus, StringComparison.Ordinal);
        Assert.Contains("unavailable for restore", viewModel.BackupVerificationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsRestart_ReconcilesSavedPrepareOperationWithoutStartingOrRecoveringIt()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-restore-restart-");
        try
        {
            var intentPath = Path.Combine(directory.FullName, "operation-intents.json");
            var backupId = Guid.CreateVersion7();
            Guid? operationId = null;

            using (var firstApi = CreateApiClient(request =>
                   {
                       if (request.Method == HttpMethod.Post)
                       {
                           using var requestJson = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                           operationId = requestJson.RootElement.GetProperty("clientOperationId").GetGuid();
                           return Error(HttpStatusCode.ServiceUnavailable, "system.not_ready");
                       }

                       return Error(HttpStatusCode.NotFound, "restore.operation_not_found");
                   }))
            {
                var firstService = new RemoteBackupRestoreService(
                    firstApi,
                    new FileClientOperationIntentStore(intentPath));
                await Assert.ThrowsAsync<BackendOperationException>(() => firstService.PrepareRestoreAsync(backupId));
            }

            Assert.NotNull(operationId);
            var restoreId = Guid.CreateVersion7();
            var restartPosts = 0;
            var lookedUpOperationIds = new List<Guid>();
            using var restartApi = CreateApiClient(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    restartPosts++;
                    throw new InvalidOperationException("Settings startup must not prepare or recover a restore automatically.");
                }

                if (request.RequestUri!.AbsolutePath == "/api/backups")
                {
                    return JsonResponse(Array.Empty<SafeBackupRecord>());
                }

                if (request.RequestUri.AbsolutePath.StartsWith("/api/backups/restore-operations/", StringComparison.Ordinal))
                {
                    lookedUpOperationIds.Add(Guid.Parse(request.RequestUri.AbsolutePath[("/api/backups/restore-operations/".Length)..]));
                    return JsonResponse(new RestoreStatusApiResponse(
                        new RestoreSessionSummary(restoreId, operationId, RestoreSessionState.Preparing, DateTimeOffset.UtcNow, null),
                        null,
                        null));
                }

                throw new InvalidOperationException($"Unexpected startup request: {request.Method} {request.RequestUri}");
            });

            using var viewModel = new SettingsViewModel(
                new TestThemeService(),
                new TestDialogService(),
                new TestToastService(),
                backupRestoreService: new RemoteBackupRestoreService(
                    restartApi,
                    new FileClientOperationIntentStore(intentPath)));

            await WaitUntilAsync(() => viewModel.IsRestorePreparing);

            Assert.Equal(operationId, lookedUpOperationIds.Single());
            Assert.Equal(0, restartPosts);
            Assert.True(viewModel.RecoverRestoreCommand.CanExecute(null));
            Assert.Contains(restoreId.ToString("D"), viewModel.RestoreStatusDisplay, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(operationId, new FileClientOperationIntentStore(intentPath)
                .FindPendingByPrefix("backup-restore-prepare:").Single().OperationId);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsRestart_ReloadsPreparedRestoreUntilServerConfirmsDiscard()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-prepared-restore-restart-");
        try
        {
            var intentPath = Path.Combine(directory.FullName, "operation-intents.json");
            var backupId = Guid.CreateVersion7();
            var restoreId = Guid.CreateVersion7();
            Guid? operationId = null;

            using var prepareApi = CreateApiClient(request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal($"/api/backups/{backupId:D}/restore/prepare", request.RequestUri!.AbsolutePath);
                using var requestJson = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                operationId = requestJson.RootElement.GetProperty("clientOperationId").GetGuid();
                return JsonResponse(new RestoreStatusApiResponse(
                    new RestoreSessionSummary(restoreId, operationId, RestoreSessionState.Prepared, DateTimeOffset.UtcNow, null),
                    "CUTOVER VERIFIED BACKUP",
                    "DISCARD VERIFIED BACKUP"));
            });
            var firstService = new RemoteBackupRestoreService(
                prepareApi,
                new FileClientOperationIntentStore(intentPath));

            var preparedResult = await firstService.PrepareRestoreAsync(backupId);

            Assert.Equal(RestoreSessionState.Prepared, preparedResult.Session.State);
            Assert.NotNull(operationId);
            Assert.Equal(operationId, new FileClientOperationIntentStore(intentPath)
                .FindPendingByPrefix("backup-restore-prepare:").Single().OperationId);
            var prepared = new RestoreStatusApiResponse(
                preparedResult.Session,
                "CUTOVER VERIFIED BACKUP",
                "DISCARD VERIFIED BACKUP");

            var discarded = prepared with
            {
                Session = prepared.Session with { State = RestoreSessionState.Discarded }
            };
            using var restartApi = CreateApiClient(request =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/backups/diagnostics")
                {
                    return JsonResponse(new BackupHistoryDiagnosticsResponse(
                        0, Array.Empty<SafeBackupRecord>(), 0, Array.Empty<BackupHistoryIssueCount>()));
                }

                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == $"/api/backups/restore-operations/{operationId:D}")
                {
                    return JsonResponse(prepared);
                }

                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"/api/backups/restore-sessions/{restoreId:D}/discard")
                {
                    return JsonResponse(discarded);
                }

                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            });
            var restartedService = new RemoteBackupRestoreService(
                restartApi,
                new FileClientOperationIntentStore(intentPath));
            using var viewModel = new SettingsViewModel(
                new TestThemeService(),
                new TestDialogService(),
                new TestToastService(),
                backupRestoreService: restartedService);

            await WaitUntilAsync(() => viewModel.IsRestorePrepared);

            Assert.Contains(restoreId.ToString("D"), viewModel.RestoreStatusDisplay, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.CutoverRestoreCommand.CanExecute(null));
            Assert.True(viewModel.DiscardRestoreCommand.CanExecute(null));

            var discardResult = await restartedService.DiscardAsync(restoreId, "DISCARD VERIFIED BACKUP");

            Assert.Equal(RestoreSessionState.Discarded, discardResult.Session.State);
            Assert.Empty(new FileClientOperationIntentStore(intentPath)
                .FindPendingByPrefix("backup-restore-prepare:"));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Settings did not reconcile the saved restore operation before the timeout.");
    }

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://127.0.0.1:7150") }, ownsClient: true);

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code, message = "test" }), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, options), Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class TestThemeService : IThemeService
    {
        public event EventHandler<AppTheme>? ThemeChanged;
        public AppTheme CurrentTheme => AppTheme.System;
        public AppTheme ResolvedTheme => AppTheme.Light;
        public void ApplyTheme(AppTheme theme) => ThemeChanged?.Invoke(this, theme);
    }

    private sealed class TestDialogService : IDialogService
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
