using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3HostileDesktopBoundaryTests
{
    [Fact]
    public async Task BackupCreationFailure_ShowsSafeMessageAndDoesNotExposeBackendDetails()
    {
        using var api = CreateApiClient(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Diagnostics()
            : Error(HttpStatusCode.InternalServerError, "backup.creation_failed",
                "pg_dump failed at C:\\private\\tenant\\backup.dump; password=secret-value")));
        using var toast = new TestToastService();
        using var viewModel = CreateViewModel(api, toast);

        viewModel.BackupNowCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBackupBusy &&
            viewModel.BackupOperationStatus.Contains("could not be completed", StringComparison.Ordinal));

        Assert.Equal("The backup could not be completed.", viewModel.BackupOperationStatus);
        Assert.DoesNotContain("private", viewModel.BackupOperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", viewModel.BackupOperationStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_dump", viewModel.BackupOperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(toast.Messages, message => message.Message == viewModel.BackupOperationStatus && message.Tone == ToastTone.Warning);
    }

    [Fact]
    public async Task BackupCreation_DoubleClickWhileRequestIsPendingDoesNotSendDuplicateRequest()
    {
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completePost = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postCount = 0;
        using var api = CreateApiClient(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return await Task.FromResult(Diagnostics());
            }

            Interlocked.Increment(ref postCount);
            postStarted.TrySetResult();
            return await completePost.Task;
        });
        using var viewModel = CreateViewModel(api, new TestToastService());

        Assert.True(viewModel.BackupNowCommand.CanExecute(null));
        viewModel.BackupNowCommand.Execute(null);
        await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.IsBackupBusy);

        Assert.False(viewModel.BackupNowCommand.CanExecute(null));
        if (viewModel.BackupNowCommand.CanExecute(null))
        {
            viewModel.BackupNowCommand.Execute(null);
        }

        Assert.Equal(1, Volatile.Read(ref postCount));
        completePost.SetResult(JsonResponse(new
        {
            backup = new
            {
                backupId = Guid.CreateVersion7(),
                createdAtUtc = DateTimeOffset.UtcNow,
                sizeBytes = 128L,
                postgreSqlServerVersion = "18.6",
                pgDumpVersion = "pg_dump 18.6",
                applicationVersion = "test",
                schemaVersion = "test-schema",
                protection = "encrypted",
                formatVersion = 1
            },
            retentionWarningCode = (string?)null
        }));

        await WaitUntilAsync(() => !viewModel.IsBackupBusy && viewModel.Backups.Count == 1);
        Assert.Equal(1, Volatile.Read(ref postCount));
    }

    private static SettingsViewModel CreateViewModel(DesktopApiClient api, TestToastService toast)
        => new(new TestThemeService(), new TestDialogService(), toast,
            backupRestoreService: new RemoteBackupRestoreService(
                api,
                new FileClientOperationIntentStore(Path.Combine(
                    Path.GetTempPath(), "edge-retails-hostile-desktop", Guid.NewGuid().ToString("N"), "intents.json"))));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The Settings operation did not reach the expected state before the timeout.");
    }

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        => new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://127.0.0.1:7150") }, ownsClient: true);

    private static HttpResponseMessage Diagnostics()
        => JsonResponse(new
        {
            verifiedBackupCount = 0,
            verifiedBackups = Array.Empty<object>(),
            invalidArtifactCount = 0,
            issues = Array.Empty<object>()
        });

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code, message }), Encoding.UTF8, "application/json")
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

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
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

    private sealed class TestToastService : IToastService, IDisposable
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
            => _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));

        public void Dispose() => _messages.Clear();
    }
}
