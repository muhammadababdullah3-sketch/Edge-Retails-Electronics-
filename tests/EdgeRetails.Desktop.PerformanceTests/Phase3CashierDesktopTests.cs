using System.Collections.ObjectModel;
using System.ComponentModel;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Gateways;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3CashierDesktopTests
{
    [Fact]
    public void ProductionAddUser_OpensCashierEditor()
    {
        var dialogs = new Dialogs();
        using var settings = new SettingsViewModel(new Theme(), dialogs, new Toasts(), new Settings());
        settings.AddUserCommand.Execute(null);
        var editor = Assert.IsType<SettingsEditorViewModel>(dialogs.Content);
        Assert.Equal(new[] { "Cashier" }, editor.Roles);
        Assert.Equal(new[] { "Active" }, editor.Statuses);
    }

    [Fact]
    public async Task Save_IsSingleSubmissionClearsPinAndRefreshesOnlyAfterConfirmation()
    {
        var completion = new TaskCompletionSource<CreatedCashierDto>();
        var service = new Settings { Create = (_, _, _) => completion.Task };
        var closed = 0;
        var refreshed = 0;
        var editor = new SettingsEditorViewModel(service, new Toasts(), () => closed++,
            () => { refreshed++; return Task.CompletedTask; }) { Name = "Ali Cashier", NewPin = "1248" };
        editor.SaveCommand.Execute(null);
        Assert.True(editor.IsSaving);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.CancelCommand.CanExecute(null));
        Assert.Equal(string.Empty, editor.NewPin);
        editor.SaveCommand.Execute(null);
        editor.CancelCommand.Execute(null);
        Assert.Equal(1, service.CreateCount);
        Assert.Equal(0, closed);
        Assert.Equal(0, refreshed);
        completion.SetResult(new(Guid.CreateVersion7(), "Ali Cashier", "Cashier"));
        await Until(() => !editor.IsSaving);
        Assert.Equal(1, closed);
        Assert.Equal(1, refreshed);
        Assert.Equal(string.Empty, editor.NewPin);
    }

    [Fact]
    public async Task UnknownOutcome_RetainsIdentityAllowsScopedStatusAndClearsPin()
    {
        var service = new Settings
        {
            Create = (_, _, _) => throw new BackendOperationException("operation.outcome_unknown", "unsafe password=fixture-private"),
            Status = _ => Task.FromResult(new CashierCreationStatus("Succeeded", Guid.CreateVersion7()))
        };
        var closed = 0;
        var editor = new SettingsEditorViewModel(service, new Toasts(), () => closed++, () => Task.CompletedTask)
        { Name = "Ali Cashier", NewPin = "1248" };
        var operationId = editor.ClientOperationId;
        editor.SaveCommand.Execute(null);
        await Until(() => !editor.IsSaving);
        Assert.True(editor.HasUnknownOutcome);
        Assert.False(editor.IsUserFieldsEnabled);
        Assert.Equal(string.Empty, editor.NewPin);
        Assert.DoesNotContain("fixture-private", editor.UserOperationMessage!);
        Assert.Equal(0, closed);
        editor.CheckStatusCommand.Execute(null);
        await Until(() => !editor.IsSaving);
        Assert.Equal(operationId, service.LastStatusId);
        Assert.Equal(1, service.CreateCount);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task UnknownOutcome_RetryUsesSameOperationIdentity()
    {
        var ids = new List<Guid>();
        var service = new Settings { Create = (_, _, id) =>
        {
            ids.Add(id);
            if (ids.Count == 1)
            {
                throw new BackendOperationException("operation.outcome_unknown", "unconfirmed");
            }
            return Task.FromResult(new CreatedCashierDto(Guid.CreateVersion7(), "Ali Cashier", "Cashier"));
        } };
        var editor = new SettingsEditorViewModel(service, new Toasts(), () => { }, () => Task.CompletedTask)
        { Name = "Ali Cashier", NewPin = "1248" };
        editor.SaveCommand.Execute(null);
        await Until(() => !editor.IsSaving);
        editor.NewPin = "1248";
        editor.SaveCommand.Execute(null);
        await Until(() => !editor.IsSaving);
        Assert.Equal(2, ids.Count);
        Assert.All(ids, id => Assert.Equal(editor.ClientOperationId, id));
    }

    [Fact]
    public async Task ProductionEditor_RejectsRoleTamperingAndInvalidPinWithoutSending()
    {
        var service = new Settings();
        var editor = new SettingsEditorViewModel(service, new Toasts(), () => { }, () => Task.CompletedTask)
        { Name = "Ali Cashier", NewPin = "1248", Role = "Owner" };
        editor.SaveCommand.Execute(null);
        Assert.Equal(0, service.CreateCount);
        Assert.Equal(string.Empty, editor.NewPin);
        editor.Role = "Cashier";
        editor.NewPin = "１２３４";
        editor.SaveCommand.Execute(null);
        await Until(() => !editor.IsSaving);
        Assert.Equal(0, service.CreateCount);
        Assert.Equal(string.Empty, editor.NewPin);
    }

    [Fact]
    public async Task RemoteAdapter_LostPostResponseReconcilesAuthenticatedOperationWithoutResendingPin()
    {
        var actor = Guid.CreateVersion7();
        var terminal = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var posts = 0;
        var reads = 0;
        using var api = Api(async request =>
        {
            Assert.Equal(session.ToString("D"), request.Headers.GetValues("X-Session-Id").Single());
            Assert.Equal(terminal.ToString("D"), request.Headers.GetValues("X-Terminal-Id").Single());
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal("/api/users/cashiers", request.RequestUri!.AbsolutePath);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(operationId, payload.RootElement.GetProperty("clientOperationId").GetGuid());
                Assert.Equal("1248", payload.RootElement.GetProperty("pin").GetString());
                Assert.False(payload.RootElement.TryGetProperty("actorId", out _));
                Assert.False(payload.RootElement.TryGetProperty("roleId", out _));
                throw new HttpRequestException("synthetic lost response");
            }
            reads++;
            Assert.Equal($"/api/operations/{operationId:D}", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Json(new OperationStatusResult(operationId, true, CreateCashierHandler.OperationType,
                userId, null, true, Status: "Succeeded"));
        });
        api.SetSession(session);
        api.SetTerminalContext(terminal, "synthetic-fixture-terminal");
        var service = new RemoteBackendSettingsService(api, () => actor);
        var result = await service.CreateCashierAsync("Ali Cashier", "1248", operationId);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(1, posts);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task RemoteAdapter_UnrelatedOperationStatusCannotConfirmCreation()
    {
        var operationId = Guid.CreateVersion7();
        using var api = Api(_ => Task.FromResult(Json(new OperationStatusResult(operationId, true,
            "OtherOperation", Guid.CreateVersion7(), null, true, Status: "Succeeded"))));
        var service = new RemoteBackendSettingsService(api, () => Guid.CreateVersion7());
        var ex = await Assert.ThrowsAsync<BackendOperationException>(() => service.GetCashierCreationStatusAsync(operationId));
        Assert.Equal("idempotency.payload_mismatch", ex.Code);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
    private static DesktopApiClient Api(Func<HttpRequestMessage, Task<HttpResponseMessage>> response)
        => new(new HttpClient(new Handler(response)) { BaseAddress = new Uri("http://127.0.0.1:7150") }, ownsClient: true);
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => response(request);
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

    private sealed class Settings : IBackendSettingsService
    {
        public bool SupportsCashierCreation => true;
        public int CreateCount { get; private set; }
        public Guid? LastStatusId { get; private set; }
        public Func<string, string, Guid, Task<CreatedCashierDto>> Create { get; init; }
            = (_, _, _) => Task.FromResult(new CreatedCashierDto(Guid.CreateVersion7(), "Ali Cashier", "Cashier"));
        public Func<Guid, Task<CashierCreationStatus>> Status { get; init; }
            = _ => Task.FromResult(new CashierCreationStatus("OutcomeUnknown", null));
        public Task<CreatedCashierDto> CreateCashierAsync(string name, string pin, Guid operationId, CancellationToken cancellationToken = default)
        { CreateCount++; return Create(name, pin, operationId); }
        public Task<CashierCreationStatus> GetCashierCreationStatusAsync(Guid operationId, CancellationToken cancellationToken = default)
        { LastStatusId = operationId; return Status(operationId); }
        public Task<BackendSettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BackendSettingsSnapshot([], "Shop", "Amir", "", "", "", "", false, false, false,
                "PostgreSQL", "", "Ready", "Connected", "", "", "", "", "", "", "", "", "", []));
        public Task SaveShopAsync(string shopName, string? phone, string? address, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveReceiptTemplateAsync(string? header, string? footer, bool showCustomer, bool showCashier,
            bool autoPrintDefault, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Dialogs : IDialogService
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => Content is not null;
        public object? Content { get; private set; }
        public void Show(object content) => Content = content;
        public void Close() => Content = null;
    }
    private sealed class Theme : IThemeService
    {
        public event EventHandler<AppTheme>? ThemeChanged;
        public AppTheme CurrentTheme => AppTheme.System;
        public AppTheme ResolvedTheme => AppTheme.Light;
        public void ApplyTheme(AppTheme theme) => ThemeChanged?.Invoke(this, theme);
    }
    private sealed class Toasts : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }
        public Toasts() => Messages = new(_messages);
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
            => _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));
    }
}
