using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3BackupRestoreAdapterTests
{
    [Fact]
    public async Task BackupHistoryDiagnostics_UsesSafeDiagnosticsRouteAndDeserializesAggregates()
    {
        var backup = new SafeBackupRecord(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1024, "18", "pg_dump 18", "test-version", "schema-1", "AES-256-GCM", 2);
        var expected = new BackupHistoryDiagnosticsResponse(
            1,
            [backup],
            2,
            [new BackupHistoryIssueCount("backup.artifact_checksum_mismatch", 2)]);
        string? requestedPath = null;
        using var api = CreateApiClient(request =>
        {
            requestedPath = request.RequestUri!.AbsolutePath;
            Assert.Equal(HttpMethod.Get, request.Method);
            return JsonResponse(expected);
        });
        var service = new RemoteBackupRestoreService(
            api,
            new FileClientOperationIntentStore(Path.Combine(Path.GetTempPath(), "unused-diagnostics-" + Guid.CreateVersion7().ToString("N"), "intents.json")));

        var result = await service.LoadHistoryDiagnosticsAsync();

        Assert.Equal("/api/backups/diagnostics", requestedPath);
        Assert.Equal(1, result.VerifiedBackupCount);
        Assert.Equal(new[] { backup }, result.VerifiedBackups);
        Assert.Equal(2, result.InvalidArtifactCount);
        Assert.Equal(expected.Issues, result.Issues);
    }

    [Fact]
    public async Task PrepareRetryAfterRestart_ReusesOperationIdAndSendsNoFilesystemAuthority()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-backup-adapter-");
        try
        {
            var operationStorePath = Path.Combine(directory.FullName, "operation-intents.json");
            var backupId = Guid.CreateVersion7();
            Guid? firstOperationId = null;
            string? firstRequestBody = null;

            using (var api = CreateApiClient(request =>
                   {
                       if (request.Method == HttpMethod.Post)
                       {
                           firstRequestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                           using var document = JsonDocument.Parse(firstRequestBody);
                           firstOperationId = document.RootElement.GetProperty("clientOperationId").GetGuid();
                           return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                           {
                               Content = new StringContent("{\"code\":\"system.not_ready\",\"message\":\"retry\"}")
                           };
                       }

                       return new HttpResponseMessage(HttpStatusCode.NotFound)
                       {
                           Content = new StringContent("{\"code\":\"restore.operation_not_found\",\"message\":\"missing\"}")
                       };
                   }))
            {
                var firstService = new RemoteBackupRestoreService(
                    api,
                    new FileClientOperationIntentStore(operationStorePath));

                await Assert.ThrowsAsync<BackendOperationException>(() => firstService.PrepareRestoreAsync(backupId));
            }

            Assert.NotNull(firstOperationId);
            Assert.NotNull(firstRequestBody);
            Assert.DoesNotContain("BackupFilePath", firstRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("backupDirectory", firstRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory.FullName, firstRequestBody, StringComparison.OrdinalIgnoreCase);

            Guid? retryOperationId = null;
            using var retryApi = CreateApiClient(request =>
            {
                if (request.Method != HttpMethod.Post)
                {
                    throw new InvalidOperationException("A confirmed prepare retry should not require a status request.");
                }

                using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                retryOperationId = document.RootElement.GetProperty("clientOperationId").GetGuid();
                return JsonResponse(new RestoreStatusApiResponse(
                    new RestoreSessionSummary(
                        Guid.CreateVersion7(), retryOperationId, RestoreSessionState.Prepared, DateTimeOffset.UtcNow, null),
                    "CUTOVER retail restore-id",
                    "DISCARD retail restore-id"));
            });
            var retryService = new RemoteBackupRestoreService(
                retryApi,
                new FileClientOperationIntentStore(operationStorePath));

            var result = await retryService.PrepareRestoreAsync(backupId);

            Assert.Equal(firstOperationId, retryOperationId);
            Assert.Equal(RestoreSessionState.Prepared, result.Session.State);
            Assert.Equal(retryOperationId, result.Session.ClientOperationId);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedRestoreMutation_EchoesServerIssuedTypedConfirmation()
    {
        var restoreId = Guid.CreateVersion7();
        string? sentConfirmation = null;
        using var api = CreateApiClient(request =>
        {
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            sentConfirmation = document.RootElement.GetProperty("confirmation").GetString();
            return JsonResponse(new RestoreStatusApiResponse(
                new RestoreSessionSummary(restoreId, Guid.CreateVersion7(), RestoreSessionState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                null,
                null));
        });
        var service = new RemoteBackupRestoreService(api, new FileClientOperationIntentStore(
            Path.Combine(Path.GetTempPath(), "unused-restore-adapter-" + Guid.CreateVersion7().ToString("N"), "intents.json")));
        var phrase = $"CUTOVER retail {restoreId:N}";

        var result = await service.CutoverAsync(restoreId, phrase);

        Assert.Equal(phrase, sentConfirmation);
        Assert.Equal(RestoreSessionState.Completed, result.Session.State);
    }

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, options), System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
