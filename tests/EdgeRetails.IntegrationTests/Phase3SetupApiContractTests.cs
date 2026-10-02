using System.Net;
using System.Net.Http.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production.Recovery;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3SetupApiContractTests
{
    [Fact]
    public async Task RecoveryApi_IsLoopbackOnlyAndFailsClosedWhenRecoveryTrustIsUnprovisioned()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var ownerId = Guid.NewGuid();
        const string fixtureLicenseId = "phase3-recovery-fixture-license";
        const string fixtureDeviceId = "phase3-recovery-fixture-device";
        var licensePayload = new LicensePayload(
            fixtureLicenseId,
            "Fixture Customer",
            "Fixture Shop",
            "Retail",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            fixtureDeviceId,
            null,
            ["Retail"]);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIdentityCredentialRecoveryRepository>();
                services.AddSingleton<IIdentityCredentialRecoveryRepository>(new RecoveryRepositoryStub(ownerId));
                services.RemoveAll<ILicenseStore>();
                services.AddSingleton<ILicenseStore>(new StubLicenseStore());
                services.RemoveAll<ILicenseValidator>();
                services.AddSingleton<ILicenseValidator>(new StubLicenseValidator(licensePayload));
                services.RemoveAll<IDeviceIdentityProvider>();
                services.AddSingleton<IDeviceIdentityProvider>(new StubDeviceIdentityProvider(fixtureDeviceId));
                services.RemoveAll<IRecoveryAuthorizationValidator>();
                services.AddSingleton<IRecoveryAuthorizationValidator, UnprovisionedRecoveryValidator>();
                services.AddTransient<IStartupFilter, LoopbackAddressStartupFilter>();
            }));
        using var client = factory.CreateClient();

        using var owners = await client.GetAsync("/api/recovery/owner-pin");
        Assert.Equal(HttpStatusCode.OK, owners.StatusCode);
        var ownerList = await owners.Content.ReadFromJsonAsync<PinRecoveryTarget[]>();
        Assert.Equal(ownerId, Assert.Single(ownerList!).UserId);

        using var recoveryContext = await client.GetAsync("/api/recovery/context");
        Assert.Equal(HttpStatusCode.OK, recoveryContext.StatusCode);
        var context = await recoveryContext.Content.ReadFromJsonAsync<RecoveryContextResponse>();
        Assert.Equal(fixtureLicenseId, context?.LicenseId);
        Assert.Equal(fixtureDeviceId, context?.DeviceId);
        Assert.Null(context?.RecoveryIssuerId);

        using var attempt = await client.PostAsJsonAsync("/api/recovery/owner-pin", new
        {
            signedAuthorization = "untrusted payload",
            targetUserId = ownerId,
            newPin = "7316"
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, attempt.StatusCode);
        Assert.Contains("recovery.authorization_not_provisioned", await attempt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryApi_HidesOwnerListFromNonLoopbackCaller()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIdentityCredentialRecoveryRepository>();
                services.AddSingleton<IIdentityCredentialRecoveryRepository>(new RecoveryRepositoryStub(Guid.NewGuid()));
                services.AddTransient<IStartupFilter, RemoteAddressStartupFilter>();
            }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/recovery/owner-pin");
        using var context = await client.GetAsync("/api/recovery/context");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, context.StatusCode);
    }

    [Fact]
    public async Task FirstRunState_IsServedByServerWithoutAUserSession()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var state = new StubInstallationStateReadService();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IInstallationStateReadService>();
                services.AddSingleton<IInstallationStateReadService>(state);
            }));
        using var client = factory.CreateClient();

        using var firstRun = await client.GetAsync("/api/setup/state");
        Assert.Equal(HttpStatusCode.OK, firstRun.StatusCode);
        var initial = await firstRun.Content.ReadFromJsonAsync<SetupStateResponse>();
        Assert.True(initial?.IsSetupRequired);

        state.State = new InstallationState { SetupStatus = SetupStatus.Complete };
        using var completed = await client.GetAsync("/api/setup/state");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var afterSetup = await completed.Content.ReadFromJsonAsync<SetupStateResponse>();
        Assert.False(afterSetup?.IsSetupRequired);
    }

    [Fact]
    public async Task Bootstrap_RejectsCompletedSetupBeforeTouchingLicense()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IInstallationStateReadService>();
                services.AddSingleton<IInstallationStateReadService>(
                    new StubInstallationStateReadService
                    {
                        State = new InstallationState { SetupStatus = SetupStatus.Complete }
                    });
            }));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/setup/bootstrap", new
        {
            shopName = "Shop",
            ownerName = "Owner",
            ownerPin = "1234",
            selectedModule = "Retail",
            signedLicenseContent = "invalid",
            clientOperationId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<SetupErrorResponse>();
        Assert.Equal("setup.already_complete", error?.Code);
    }

    [Fact]
    public async Task SetupEndpoints_RejectNonLoopbackCaller()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IInstallationStateReadService>();
                services.AddSingleton<IInstallationStateReadService>(
                    new StubInstallationStateReadService());
                services.AddTransient<IStartupFilter, RemoteAddressStartupFilter>();
            }));
        using var client = factory.CreateClient();

        using var state = await client.GetAsync("/api/setup/state");
        Assert.Equal(HttpStatusCode.Forbidden, state.StatusCode);

        using var bootstrap = await client.PostAsJsonAsync("/api/setup/bootstrap", new
        {
            shopName = "Shop",
            ownerName = "Owner",
            ownerPin = "1234",
            signedLicenseContent = "invalid",
            clientOperationId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
    }

    [Fact]
    public async Task Readiness_RejectsRestoreRecoveryState()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProductionMaintenanceBarrier>();
                services.AddSingleton<IProductionMaintenanceBarrier>(
                    new StubMaintenanceBarrier(ProductionMaintenanceState.RecoveryRequired));
            }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/system/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed record SetupStateResponse(bool IsSetupRequired);
    private sealed record SetupErrorResponse(string Code);
    private sealed record RecoveryContextResponse(string LicenseId, string DeviceId, string? RecoveryIssuerId);

    private sealed class UnprovisionedRecoveryValidator : IRecoveryAuthorizationValidator
    {
        public Task<RecoveryAuthorizationValidation> ValidateAsync(
            string? signedAuthorization,
            Guid targetUserId,
            CancellationToken cancellationToken) =>
            Task.FromResult(RecoveryAuthorizationValidation.Failed("recovery.authorization_not_provisioned"));
    }

    private sealed class StubLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("fixture-license");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubLicenseValidator(LicensePayload payload) : ILicenseValidator
    {
        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseValidationResult(LicenseValidationStatus.Valid, payload, null));
    }

    private sealed class StubDeviceIdentityProvider(string deviceId) : IDeviceIdentityProvider
    {
        public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(deviceId);
    }

    private sealed class RecoveryRepositoryStub(Guid ownerId) : IIdentityCredentialRecoveryRepository
    {
        public Task<IReadOnlyList<PinRecoveryTarget>> GetActiveOwnerTargetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PinRecoveryTarget>>([new PinRecoveryTarget(ownerId, "Fixture Owner")]);
        public Task<User?> GetUserForRecoveryUpdateAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<User?>(null);
        public Task<bool> HasRecoveryOperationAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task RevokeActiveSessionsAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TrySaveRecoveryChangesAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StubInstallationStateReadService : IInstallationStateReadService
    {
        public InstallationState? State { get; set; }

        public Task<InstallationState?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(State);
    }

    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, middlewareNext) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                return middlewareNext();
            });
            next(app);
        };
    }

    private sealed class LoopbackAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, middlewareNext) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return middlewareNext();
            });
            next(app);
        };
    }

    private sealed class StubMaintenanceBarrier(ProductionMaintenanceState state)
        : IProductionMaintenanceBarrier
    {
        public Task<ProductionMaintenanceState> GetStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(state);

        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(
            ProductionMaintenanceState requested,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
