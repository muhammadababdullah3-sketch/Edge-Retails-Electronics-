using EdgeRetails.Application.Features.Setup;
using EdgeRetails.Application.Production.Licensing;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public interface IBackendSetupService
{
    Task CompleteFirstSetupAsync(
        string shopName,
        string ownerName,
        string phone,
        string address,
        string ownerPin,
        string selectedModule,
        string signedLicenseContent,
        CancellationToken cancellationToken = default);
}

public sealed class BackendSetupService : IBackendSetupService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BackendSetupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task CompleteFirstSetupAsync(
        string shopName,
        string ownerName,
        string phone,
        string address,
        string ownerPin,
        string selectedModule,
        string signedLicenseContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signedLicenseContent))
        {
            throw new InvalidOperationException(
                "A valid signed Edge Retails license (.erlic) is required to complete first-time setup.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // 1. Authoritative backend license validation & persistence
        var validator = scope.ServiceProvider.GetRequiredService<ILicenseValidator>();
        var validationResult = await validator.ValidateAsync(signedLicenseContent, cancellationToken);

        if (!validationResult.IsValid)
        {
            throw new InvalidOperationException(
                $"License verification failed: {validationResult.Message ?? validationResult.Status.ToString()}");
        }

        var store = scope.ServiceProvider.GetRequiredService<ILicenseStore>();
        var persisted = await store.TryPersistInitialRawAsync(signedLicenseContent, cancellationToken);
        if (!persisted)
        {
            await store.PersistRawAsync(signedLicenseContent, cancellationToken);
        }

        // 2. Complete identity and business bootstrap
        var handler = scope.ServiceProvider
            .GetRequiredService<FirstSetupBootstrapHandler>();

        var result = await handler.HandleAsync(
            new FirstSetupBootstrapCommand(
                shopName,
                Normalize(phone),
                Normalize(address),
                ownerName,
                ownerPin,
                Normalize(selectedModule),
                Guid.CreateVersion7()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "First Setup could not be completed.");
        }
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
